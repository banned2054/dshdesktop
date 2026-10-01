using DshDesktop.Core.Models;
using DshDesktop.Core.Services;
using DshDesktop.ViewModels;
using Xunit;

namespace DshDesktop.Tests;

/// <summary>
///     执行权限选择器纯行为测试：目录来自注入服务，投影权威值由 ApplyPermission 显式推送
///     （模拟 root 转发的 permissions 投影回流），不依赖窗口组装与 Avalonia 平台。
/// </summary>
public sealed class PermissionSelectorViewModelTests
{
    [Fact]
    public async Task CatalogMapsToOptionItemsWithDisplayNames()
    {
        var (selector, _) = await CreateReadySelector(service =>
        {
            service.Catalog = new PermissionCatalog(
            [
                new PermissionPresetOption("read-only", "read-only"),
                new PermissionPresetOption("workspace-write", "workspace-write"),
                new PermissionPresetOption("danger-full-access", "danger-full-access"),
                new PermissionPresetOption("team-preset", "Team preset", "团队定制预设")
            ], "workspace-write");
        });

        Assert.Equal(4, selector.Options.Count);
        Assert.Equal("仅可查看", selector.Options[0].DisplayName);
        Assert.Equal("工作区内修改", selector.Options[1].DisplayName);
        Assert.Equal("完全权限", selector.Options[2].DisplayName);
        // 宿主定制名透传展示，不按 id 推导。
        Assert.Equal("Team preset", selector.Options[3].DisplayName);
        Assert.Equal("团队定制预设", selector.Options[3].Description);
    }

    [Fact]
    public async Task ProjectionIsTheAuthoritativeCurrentPermission()
    {
        var (selector, _) = await CreateReadySelector();

        selector.ApplyPermission(9, "workspace-write");
        Assert.Equal("工作区内修改", selector.PermissionPickerLabel);
        Assert.True(selector.Options.Single(option => option.Value == "workspace-write").IsSelected);

        // 整值替换：新投影到达后勾选与文案整体迁移。
        selector.ApplyPermission(10, "danger-full-access");
        Assert.Equal("完全权限", selector.PermissionPickerLabel);
        Assert.True(selector.Options.Single(option => option.Value  == "danger-full-access").IsSelected);
        Assert.False(selector.Options.Single(option => option.Value == "workspace-write").IsSelected);
    }

    [Fact]
    public async Task SelectingPresetSendsSwitchAndWaitsForProjection()
    {
        var (selector, service) = await CreateReadySelector();

        var option = selector.Options.Single(item => item.Value == "read-only");
        option.SelectCommand.Execute(null);

        var (sessionId, preset) = Assert.Single(service.Switches);
        Assert.Equal("session-1", sessionId);
        Assert.Equal("read-only", preset);

        // 不做乐观更新：投影回流前当前权限保持旧值。
        Assert.Equal("workspace-write", selector.CurrentValue);
        Assert.Equal("工作区内修改", selector.PermissionPickerLabel);

        selector.ApplyPermission(11, "read-only");
        Assert.Equal("仅可查看", selector.PermissionPickerLabel);
    }

    [Fact]
    public async Task SwitchFailureKeepsOldStateAndReenablesSelector()
    {
        var errors = new List<string?>();
        var (selector, service) = await CreateReadySelector(errors : errors);
        service.SwitchError     = new InvalidOperationException("连接中断（模拟）");

        SelectOptionByValue(selector, "read-only");
        await WaitUntilAsync(() => !selector.IsSwitching);

        Assert.Equal("workspace-write", selector.CurrentValue);
        Assert.Equal("工作区内修改", selector.PermissionPickerLabel);
        Assert.True(selector.IsSelectorEnabled);

        // 首条是请求前的错误清除（null），第二条是真实失败信息。
        var error = Assert.Single(errors.Where(text => text is not null));
        Assert.StartsWith("权限切换失败：", error);
    }

    [Fact]
    public async Task MissingPermissionCommandReportsWithoutStateChange()
    {
        var errors = new List<string?>();
        var (selector, service) = await CreateReadySelector(errors : errors);
        service.CommandMissing  = true;

        SelectOptionByValue(selector, "read-only");
        await WaitUntilAsync(() => !selector.IsSwitching);

        Assert.Equal("workspace-write", selector.CurrentValue);
        Assert.Contains("未提供权限切换命令", Assert.Single(errors.Where(text => text is not null)));
    }

    [Fact]
    public async Task FullAccessRequiresAcknowledgedConfirmationBeforeRequest()
    {
        var (selector, service) = await CreateReadySelector();

        SelectOptionByValue(selector, "danger-full-access");

        Assert.True(selector.IsConfirmOpen);
        Assert.Empty(service.Switches);
        Assert.Equal("确认启用完全权限？", selector.ConfirmTitle);
        Assert.Contains("减少确认步骤", selector.ConfirmDescription);
        Assert.Equal("启用完全权限", selector.ConfirmEnableText);

        // 未勾选确认不可启用（按钮禁用 + 命令守卫双保险）。
        selector.ConfirmSwitchCommand.Execute(null);
        Assert.Empty(service.Switches);
        Assert.True(selector.IsConfirmOpen);

        selector.IsAcknowledged = true;
        selector.ConfirmSwitchCommand.Execute(null);

        Assert.False(selector.IsConfirmOpen);
        var (_, preset) = Assert.Single(service.Switches);
        Assert.Equal("danger-full-access", preset);
        // 确认后同样不乐观更新：仍以投影回流为准。
        Assert.Equal("workspace-write", selector.CurrentValue);
    }

    [Fact]
    public async Task CancellingConfirmationSendsNothing()
    {
        var (selector, service) = await CreateReadySelector();

        SelectOptionByValue(selector, "danger-full-access");
        Assert.True(selector.IsConfirmOpen);

        selector.CancelSwitchCommand.Execute(null);

        Assert.False(selector.IsConfirmOpen);
        Assert.Empty(service.Switches);

        // 弹层已收起：后续普通切换不再被确认门拦住。
        SelectOptionByValue(selector, "read-only");
        Assert.Single(service.Switches);
    }

    [Fact]
    public async Task AutoPresetUsesExperimentalConfirmationCopy()
    {
        var (selector, service) = await CreateReadySelector(service =>
        {
            service.Catalog = new PermissionCatalog(
            [
                new PermissionPresetOption("read-only", "read-only"),
                new PermissionPresetOption("workspace-write", "workspace-write"),
                new PermissionPresetOption("danger-full-access", "danger-full-access"),
                new PermissionPresetOption("auto", "auto")
            ], "workspace-write");
        });

        var autoOption = selector.Options.Single(item => item.Value == "auto");
        Assert.True(autoOption.IsExperimental);

        SelectOptionByValue(selector, "auto");

        Assert.True(selector.IsConfirmOpen);
        Assert.Equal("确认启用 Auto review（实验）？", selector.ConfirmTitle);
        Assert.Equal("我已了解这些风险，并愿意继续", selector.ConfirmAcknowledgeText);
        Assert.Equal("启用 Auto review", selector.ConfirmEnableText);

        selector.IsAcknowledged = true;
        selector.ConfirmSwitchCommand.Execute(null);

        var (_, preset) = Assert.Single(service.Switches);
        Assert.Equal("auto", preset);
    }

    [Fact]
    public async Task SelectingAlreadyCurrentPresetSkipsConfirmationAndRequest()
    {
        var (selector, service) = await CreateReadySelector();
        selector.ApplyPermission(5, "danger-full-access");

        SelectOptionByValue(selector, "danger-full-access");

        Assert.False(selector.IsConfirmOpen);
        Assert.Empty(service.Switches);
    }

    [Fact]
    public async Task UnknownPresetValueFallsBackWithoutCrashing()
    {
        var (selector, _) = await CreateReadySelector();

        selector.ApplyPermission(4, "custom");
        Assert.Equal("Custom", selector.PermissionPickerLabel);
        Assert.True(selector.IsSelectorEnabled);

        // 非惯例键原样展示；目录中无匹配行时不勾任何选项。
        selector.ApplyPermission(5, "Some Exotic Value");
        Assert.Equal("Some Exotic Value", selector.PermissionPickerLabel);
        Assert.DoesNotContain(selector.Options, option => option.IsSelected);
    }

    [Fact]
    public async Task StaleProjectionSeqIsIgnored()
    {
        var (selector, _) = await CreateReadySelector();

        selector.ApplyPermission(10, "read-only");
        selector.ApplyPermission(5, "workspace-write");

        Assert.Equal("read-only", selector.CurrentValue);
        Assert.Equal("仅可查看", selector.PermissionPickerLabel);
    }

    [Fact]
    public async Task InFlightSwitchDisablesSelectorUntilRequestSettles()
    {
        var (selector, service) = await CreateReadySelector();
        service.PendingSwitch   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        SelectOptionByValue(selector, "read-only");
        Assert.True(selector.IsSwitching);
        Assert.False(selector.IsSelectorEnabled);

        // 在途期间不能提交相互冲突的切换请求。
        SelectOptionByValue(selector, "danger-full-access");
        Assert.Single(service.Switches);

        service.PendingSwitch.TrySetResult();
        await WaitUntilAsync(() => !selector.IsSwitching);
        Assert.True(selector.IsSelectorEnabled);
    }

    [Fact]
    public async Task SessionSwitchResetsProjectionStateUntilNextBaseline()
    {
        var (selector, _) = await CreateReadySelector();

        selector.SetSession("session-2");

        Assert.Null(selector.CurrentValue);
        Assert.Equal("权限…", selector.PermissionPickerLabel);
        Assert.False(selector.IsSelectorEnabled);

        // 新会话投影基线到达后恢复可用。
        selector.ApplyPermission(1, "workspace-write");
        Assert.Equal("工作区内修改", selector.PermissionPickerLabel);
        Assert.True(selector.IsSelectorEnabled);
    }

    [Fact]
    public async Task DraftPageWithoutSessionHidesSelector()
    {
        var (selector, _) = await CreateReadySelector();

        selector.SetSession(null);

        Assert.False(selector.HasSession);
        Assert.False(selector.IsSelectorEnabled);
    }

    [Fact]
    public async Task DisconnectedBackendDisablesSelector()
    {
        var (selector, _) = await CreateReadySelector();

        selector.SetBackendConnected(false);
        Assert.False(selector.IsSelectorEnabled);

        selector.SetBackendConnected(true);
        Assert.True(selector.IsSelectorEnabled);
    }

    [Fact]
    public async Task CatalogChangedBroadcastReloadsOptions()
    {
        var (selector, service) = await CreateReadySelector();
        Assert.Equal(1, service.CatalogRequests);

        service.Catalog = new PermissionCatalog(
        [
            new PermissionPresetOption("workspace-write", "workspace-write"),
            new PermissionPresetOption("danger-full-access", "danger-full-access"),
            new PermissionPresetOption("auto", "auto")
        ], "workspace-write");
        service.RaiseCatalogChanged();

        Assert.Equal(2, service.CatalogRequests);
        Assert.Equal(3, selector.Options.Count);
        // 目录重读不改写当前权限：仍由投影决定。
        Assert.Equal("workspace-write", selector.CurrentValue);
        Assert.Equal("工作区内修改", selector.PermissionPickerLabel);
    }

    [Fact]
    public async Task CatalogFailureKeepsSelectorUnavailableWithoutErrorLeak()
    {
        var errors   = new List<string?>();
        var service  = new FakePermissionPresetService { CatalogError = new InvalidOperationException("目录失败") };
        var selector = new PermissionSelectorViewModel(service, text => errors.Add(text));

        selector.SetBackendConnected(true);
        selector.SetSession("session-1");
        // 目录加载失败：保持不可用态，不向窗口级错误条泄漏（不影响正常聊天）。
        await selector.ReloadCatalogSafeAsync();

        Assert.False(selector.IsSelectorEnabled);
        Assert.Equal("权限不可用", selector.PermissionPickerLabel);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task DraftPageShowsCatalogDefaultAndPreselectsLocally()
    {
        var (selector, service, draftPresets) = await CreateDraftSelector();

        // 草稿页无会话也可见（本地预选，不发 RPC）；未预选时显示目录默认——即新会话将被后端播种的预设。
        Assert.False(selector.HasSession);
        Assert.True(selector.IsVisible);
        Assert.True(selector.IsSelectorEnabled);
        Assert.Equal("工作区内修改", selector.PermissionPickerLabel);
        Assert.True(selector.Options.Single(option => option.Value == "workspace-write").IsSelected);

        SelectOptionByValue(selector, "read-only");

        Assert.Empty(service.Switches);
        Assert.Equal("仅可查看", selector.PermissionPickerLabel);
        Assert.Equal("read-only", Assert.Single(draftPresets));
    }

    [Fact]
    public async Task DraftFullAccessRequiresConfirmationBeforeLocalPreset()
    {
        var (selector, service, draftPresets) = await CreateDraftSelector();

        SelectOptionByValue(selector, "danger-full-access");

        // 确认门在草稿页同样生效：确认前不记账。
        Assert.True(selector.IsConfirmOpen);
        Assert.Empty(draftPresets);

        selector.IsAcknowledged = true;
        selector.ConfirmSwitchCommand.Execute(null);

        Assert.False(selector.IsConfirmOpen);
        Assert.Equal("完全权限", selector.PermissionPickerLabel);
        Assert.Equal("danger-full-access", Assert.Single(draftPresets));
        Assert.Empty(service.Switches);
    }

    [Fact]
    public async Task DraftSelectingCatalogDefaultSkipsCallback()
    {
        var (selector, _, draftPresets) = await CreateDraftSelector();

        // 目录默认即当前生效值：与 WebUI 一致直接返回，不产生草稿改动。
        SelectOptionByValue(selector, "workspace-write");

        Assert.Empty(draftPresets);
    }

    [Fact]
    public async Task DraftPreselectionRestoresAndLeavingDraftClearsIt()
    {
        var (selector, _, _) = await CreateDraftSelector();

        // root 在进入草稿页时推送既有草稿的预选（null=未预选，显示目录默认）。
        selector.ApplyDraftPreset("read-only");
        Assert.Equal("仅可查看", selector.PermissionPickerLabel);

        // 离开草稿页清除预选：改由会话投影驱动（投影基线未到时显示「权限…」）。
        selector.SetDraftTarget(false);
        selector.SetSession("session-2");
        Assert.True(selector.IsVisible);
        Assert.Equal("权限…", selector.PermissionPickerLabel);
        Assert.False(selector.IsSelectorEnabled);
    }

    [Fact]
    public void SetSessionFromDraftReRaisesIsVisibleForBinding()
    {
        var selector = new PermissionSelectorViewModel(new FakePermissionPresetService(), _ => { });
        selector.SetDraftTarget(true);
        Assert.True(selector.IsVisible);

        var isVisibleRaised = false;
        selector.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(PermissionSelectorViewModel.IsVisible)) isVisibleRaised = true;
        };

        // 复刻 root 选中会话的调用顺序：先离开草稿——此刻 SessionId 仍为 null，IsVisible
        // 求值为 false；再 SetSession 使属性转为 true。转 true 时必须重发通知，否则
        // XAML 绑定不重估，选择器在已有会话中卡在隐藏态。
        selector.SetDraftTarget(false);
        Assert.False(selector.IsVisible);
        isVisibleRaised = false;

        selector.SetSession("session-1");

        Assert.True(selector.IsVisible);
        Assert.True(isVisibleRaised);
    }

    private static void SelectOptionByValue(PermissionSelectorViewModel selector, string value)
    {
        selector.SelectOptionCommand.Execute(selector.Options.Single(option => option.Value == value));
    }

    /// <summary>构造草稿页就绪选择器：已连接、草稿目标态、目录已加载（本地预选模式，无会话）。</summary>
    private static async
        Task<(PermissionSelectorViewModel Selector, FakePermissionPresetService Service, List<string>DraftPresets)>
        CreateDraftSelector()
    {
        var service      = new FakePermissionPresetService();
        var draftPresets = new List<string>();
        var selector = new PermissionSelectorViewModel(service, _ => { },
                                                       onDraftPresetChanged : draftPresets.Add);
        selector.SetBackendConnected(true);
        selector.SetDraftTarget(true);
        await selector.ReloadCatalogAsync();
        return (selector, service, draftPresets);
    }

    /// <summary>构造就绪选择器：已连接、选中会话、目录已加载、投影基线 workspace-write。</summary>
    private static async Task<(PermissionSelectorViewModel Selector, FakePermissionPresetService Service)>
        CreateReadySelector(
            Action<FakePermissionPresetService>? configure = null, List<string?>? errors = null)
    {
        var service = new FakePermissionPresetService();
        configure?.Invoke(service);
        var selector = new PermissionSelectorViewModel(service, text => errors?.Add(text));
        selector.SetBackendConnected(true);
        selector.SetSession("session-1");
        await selector.ReloadCatalogAsync();
        selector.ApplyPermission(3, "workspace-write");
        return (selector, service);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds = 2000)
    {
        var timeout = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (DateTime.UtcNow < timeout)
        {
            if (condition()) return;

            await Task.Delay(10);
        }

        Assert.True(condition(), "预期的异步状态未在超时前出现。");
    }

    private sealed class FakePermissionPresetService : IPermissionPresetService
    {
        public PermissionCatalog Catalog { get; set; } = new(
        [
            new PermissionPresetOption("read-only", "read-only"),
            new PermissionPresetOption("workspace-write", "workspace-write"),
            new PermissionPresetOption("danger-full-access", "danger-full-access")
        ], "workspace-write");

        public List<(string SessionId, string Preset)> Switches { get; } = [];

        public int CatalogRequests { get; private set; }

        public Exception? SwitchError { get; set; }

        public Exception? CatalogError { get; set; }

        public bool CommandMissing { get; set; }

        public TaskCompletionSource? PendingSwitch { get; set; }

        public event EventHandler? CatalogChanged;

        public Task<PermissionCatalog> GetCatalogAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CatalogError is { } error) throw error;

            CatalogRequests++;
            return Task.FromResult(Catalog);
        }

        public async Task<bool> SwitchPresetAsync(
            string sessionId, string preset, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Switches.Add((sessionId, preset));
            if (PendingSwitch is { } gate) await gate.Task;

            if (SwitchError is { } error) throw error;

            return !CommandMissing;
        }

        public void RaiseCatalogChanged()
        {
            CatalogChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
