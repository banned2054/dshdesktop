using DshDesktop.Core.Models;
using DshDesktop.Core.Services;
using DshDesktop.Infrastructure.Services;
using DshDesktop.ViewModels;
using NUnit.Framework;
using NUnit.Framework.Legacy;

namespace DshDesktop.Tests;

/// <summary>
///     Composer 纯行为测试：上下文由 SetSession/SetBackendConnected 显式推送，
///     生效选型由 ApplyCurrentModel 模拟 follow 回声，不依赖窗口组装。
/// </summary>
public sealed class ComposerViewModelTests
{
    [Test]
    public void SendRequiresSessionDraftAndConnectedBackend()
    {
        var composer = CreateComposer();

        // 三个前提逐项到位：会话、草稿、后端连接。
        composer.SetBackendConnected(true);
        composer.SetSession("session-1", false, string.Empty);
        ClassicAssert.IsFalse(composer.SendMessageCommand.CanExecute(null));

        composer.DraftMessage = "你好";
        ClassicAssert.IsTrue(composer.SendMessageCommand.CanExecute(null));

        // 草稿清空后回到不可发送。
        composer.DraftMessage = "   ";
        ClassicAssert.IsFalse(composer.SendMessageCommand.CanExecute(null));
    }

    [Test]
    public void SendIsUnavailableWithoutSession()
    {
        var composer = CreateComposer();

        composer.SetBackendConnected(true);
        composer.DraftMessage = "未选中会话时的草稿";

        ClassicAssert.IsFalse(composer.SendMessageCommand.CanExecute(null));
    }

    [Test]
    public void SendIsUnavailableWhenBackendDisconnected()
    {
        var composer = CreateComposer();

        composer.SetSession("session-1", false, string.Empty);
        composer.SetBackendConnected(false);
        composer.DraftMessage = "后端未连接时的草稿";

        ClassicAssert.IsFalse(composer.SendMessageCommand.CanExecute(null));

        // 恢复连接后即可发送。
        composer.SetBackendConnected(true);
        ClassicAssert.IsTrue(composer.SendMessageCommand.CanExecute(null));
    }

    [Test]
    public async Task CancelIsAvailableWhileSessionRunningAndCallsService()
    {
        var sessionService = new ControllableSessionService();
        var composer       = new ComposerViewModel(sessionService, _ => { });

        ClassicAssert.IsFalse(composer.CancelCommand.CanExecute(null));

        composer.SetSession("session-1", true, string.Empty);
        ClassicAssert.IsTrue(composer.CancelCommand.CanExecute(null));

        composer.CancelCommand.Execute(null);
        await WaitUntilAsync(() => sessionService.CancelledSessions.Count == 1);
        ClassicAssert.AreEqual("session-1", sessionService.CancelledSessions.Single());

        // 运行结束回到不可取消。
        composer.SetSessionRunning(false);
        ClassicAssert.IsFalse(composer.CancelCommand.CanExecute(null));
    }

    [Test]
    public async Task SendCompletionDoesNotClearDraftEnteredWhileRequestIsInFlight()
    {
        var sessionService = new ControllableSessionService();
        var composer       = new ComposerViewModel(sessionService, _ => { });
        composer.SetSession("session-1", false, string.Empty);
        composer.SetBackendConnected(true);

        composer.DraftMessage = "第一条消息";
        composer.SendMessageCommand.Execute(null);
        await sessionService.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // 请求未完成期间用户继续输入：旧请求完成时不得清掉新草稿。
        composer.DraftMessage = "发送期间的新草稿";
        sessionService.ReleaseSend.TrySetResult();
        await WaitUntilAsync(() => !composer.IsSending);

        ClassicAssert.AreEqual("发送期间的新草稿", composer.DraftMessage);
    }

    [Test]
    public async Task FailedSendReportsErrorThroughCallback()
    {
        var     sessionService = new ControllableSessionService { FailSend = true };
        string? reported       = null;
        var     composer       = new ComposerViewModel(sessionService, text => reported = text);
        composer.SetSession("session-1", false, string.Empty);
        composer.SetBackendConnected(true);

        composer.DraftMessage = "会失败的发送";
        composer.SendMessageCommand.Execute(null);

        await WaitUntilAsync(() => reported is not null);
        Assert.That(reported, Does.Contain("发送失败"));
        ClassicAssert.IsFalse(composer.IsSending);
    }

    [Test]
    public async Task ModelCatalogRefreshBuildsFlatOptionsWithDefaultFallback()
    {
        var composer = CreateComposer();

        await composer.RefreshModelCatalogAsync();

        // 目录扁平投影：两个提供方共三个模型。
        ClassicAssert.AreEqual((string[])["model-x", "model-y", "model-z"],
                               composer.ModelOptions.Select(option => option.Model).ToArray());

        // 会话未显式选型：生效选型为空，下拉回退目录默认。
        ClassicAssert.IsNull(composer.CurrentModel);
        ClassicAssert.AreEqual("model-x", composer.SelectedModelOption?.Model);
    }

    [Test]
    public async Task ModelPickerRequiresCatalogSessionAndBackendConnection()
    {
        var composer = CreateComposer();
        ClassicAssert.IsFalse(composer.IsModelPickerEnabled);

        // 目录、会话、连接三个条件逐项到位，并验证各变化点触发可用性重算。
        var raised = new List<string?>();
        composer.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await composer.RefreshModelCatalogAsync();
        composer.SetBackendConnected(true);
        Assert.That(raised, Does.Contain(nameof(ComposerViewModel.IsModelPickerEnabled)));
        ClassicAssert.IsFalse(composer.IsModelPickerEnabled); // 尚无会话。

        composer.SetSession("session-1", false, string.Empty);
        ClassicAssert.IsTrue(composer.IsModelPickerEnabled);

        composer.SetBackendConnected(false);
        ClassicAssert.IsFalse(composer.IsModelPickerEnabled);

        composer.SetBackendConnected(true);
        composer.SetSession(null, false, string.Empty);
        ClassicAssert.IsFalse(composer.IsModelPickerEnabled);
    }

    [Test]
    public async Task DraftTargetEnablesLocalModelPreselectionWithoutRpc()
    {
        var sessionService = new ControllableSessionService();
        var draftSelections = new List<ModelSelection>();
        var composer = new ComposerViewModel(sessionService, _ => { }, onDraftModelChanged : draftSelections.Add);
        ClassicAssert.IsFalse(composer.IsModelPickerEnabled);

        // 草稿目标模式：无 SessionId 时目录 + 连接到位即启用，选型只走本地预选回调，
        // 不向后端发任何选型请求。
        await composer.RefreshModelCatalogAsync();
        composer.SetBackendConnected(true);
        composer.SetDraftTarget(true);
        ClassicAssert.IsTrue(composer.IsModelPickerEnabled);

        composer.SelectedModelOption = composer.ModelOptions.Single(option => option.Model == "model-z");
        ClassicAssert.AreEqual(new ModelSelection("prov-b", "model-z"), composer.CurrentModel);
        Assert.That(draftSelections, Has.Count.EqualTo(1));
        var selection = draftSelections.Single();
        ClassicAssert.AreEqual("prov-b", selection.Provider);
        ClassicAssert.AreEqual("model-z", selection.Model);
        ClassicAssert.IsNull(composer.SessionId);
        ClassicAssert.IsEmpty(sessionService.SelectionRequests);

        // 离开草稿页：恢复会话语义（无会话时不可用）。
        composer.SetDraftTarget(false);
        ClassicAssert.IsFalse(composer.IsModelPickerEnabled);
    }

    [Test]
    public async Task ApplyCurrentModelEchoUpdatesPickerSelection()
    {
        var composer = CreateComposer();
        await composer.RefreshModelCatalogAsync();
        composer.SetSession("session-1", false, string.Empty);

        composer.ApplyCurrentModel(new ModelSelection("prov-b", "model-z"));

        ClassicAssert.AreEqual(new ModelSelection("prov-b", "model-z"), composer.CurrentModel);
        ClassicAssert.AreEqual("model-z", composer.SelectedModelOption?.Model);
    }

    [Test]
    public async Task UserSelectionSendsRequestToSessionAndTakesEffectViaEcho()
    {
        var sessionService = new ControllableSessionService();
        var composer       = new ComposerViewModel(sessionService, _ => { });
        await composer.RefreshModelCatalogAsync();
        composer.SetSession("session-1", false, string.Empty);

        composer.SelectedModelOption = composer.ModelOptions.Single(option => option.Model == "model-y");

        // 请求发往当前会话；回声到达前本地生效值不变。
        ClassicAssert.AreEqual(("session-1", "prov-a", "model-y", (string?)null),
                               sessionService.SelectionRequests.Single());
        ClassicAssert.IsNull(composer.CurrentModel);

        // 与当前生效选型的守卫在下次请求时生效；同一项重复赋值不触发新请求。
        composer.SelectedModelOption = composer.ModelOptions.Single(option => option.Model == "model-y");
        Assert.That(sessionService.SelectionRequests, Has.Count.EqualTo(1));

        // 生效值以后端回声为准。
        composer.ApplyCurrentModel(new ModelSelection("prov-a", "model-y"));
        ClassicAssert.AreEqual("model-y", composer.SelectedModelOption?.Model);
    }

    [Test]
    public async Task EffortMenuFollowsSelectedModelReasoningMetadata()
    {
        var sessionService = new ControllableSessionService();
        var composer       = new ComposerViewModel(sessionService, _ => { });
        await composer.RefreshModelCatalogAsync();
        composer.SetSession("session-1", false, string.Empty);

        // 生效选型为目录默认 model-x：菜单只有其声明的 off/high。
        composer.ApplyCurrentModel(new ModelSelection("prov-a", "model-x"));
        ClassicAssert.AreEqual((string[])["off", "high"],
                               composer.EffortOptions.Select(option => option.Value).ToArray());

        // 切到 model-y：菜单变为其声明的 low/high。
        composer.ApplyCurrentModel(new ModelSelection("prov-a", "model-y"));
        ClassicAssert.AreEqual((string[])["low", "high"],
                               composer.EffortOptions.Select(option => option.Value).ToArray());
    }

    [Test]
    public async Task ModelWithoutReasoningMetadataShowsEmptyEffortMenu()
    {
        var sessionService = new ControllableSessionService();
        var composer       = new ComposerViewModel(sessionService, _ => { });
        await composer.RefreshModelCatalogAsync();
        composer.SetSession("session-1", false, string.Empty);

        composer.ApplyCurrentModel(new ModelSelection("prov-b", "model-z"));

        // 无 reasoning 元数据的模型：任何显式档位都会被后端拒绝，菜单为空。
        ClassicAssert.IsEmpty(composer.EffortOptions);
    }

    [Test]
    public async Task SwitchingModelKeepsEffortSupportedByTarget()
    {
        var sessionService = new ControllableSessionService();
        var composer       = new ComposerViewModel(sessionService, _ => { });
        await composer.RefreshModelCatalogAsync();
        composer.SetSession("session-1", false, string.Empty);
        composer.ApplyCurrentModel(new ModelSelection("prov-a", "model-x", "high"));

        composer.SelectedModelOption = composer.ModelOptions.Single(option => option.Model == "model-y");

        // 目标模型支持 high：原样携带。
        ClassicAssert.AreEqual(("session-1", "prov-a", "model-y", "high"), sessionService.SelectionRequests.Single());
    }

    [Test]
    public async Task SwitchingModelFallsBackToTargetDefaultEffortWhenUnsupported()
    {
        var sessionService = new ControllableSessionService();
        var composer       = new ComposerViewModel(sessionService, _ => { });
        await composer.RefreshModelCatalogAsync();
        composer.SetSession("session-1", false, string.Empty);
        composer.ApplyCurrentModel(new ModelSelection("prov-a", "model-y", "low"));

        composer.SelectedModelOption = composer.ModelOptions.Single(option => option.Model == "model-x");

        // model-x 不支持 low：回退其默认档位 high（对齐后端「不支持即拒绝」的语义）。
        ClassicAssert.AreEqual(("session-1", "prov-a", "model-x", "high"), sessionService.SelectionRequests.Single());
    }

    [Test]
    public async Task FailedSelectionRollsBackPickerAndReportsError()
    {
        var     sessionService = new ControllableSessionService { FailSelect = true };
        string? reported       = null;
        var     composer       = new ComposerViewModel(sessionService, text => reported = text);
        await composer.RefreshModelCatalogAsync();
        composer.SetSession("session-1", false, string.Empty);

        composer.SelectedModelOption = composer.ModelOptions.Single(option => option.Model == "model-z");
        await WaitUntilAsync(() => reported is not null);

        Assert.That(reported, Does.Contain("选型失败"));
        // 回退到当前生效选型（会话未选型 → 目录默认），不停留在失败项。
        ClassicAssert.AreEqual("model-x", composer.SelectedModelOption?.Model);
    }

    [Test]
    public async Task CurrentModelOutsideCatalogGetsPlaceholderOption()
    {
        var composer = CreateComposer();
        await composer.RefreshModelCatalogAsync();
        composer.SetSession("session-1", false, string.Empty);

        composer.ApplyCurrentModel(new ModelSelection("prov-ghost", "model-ghost"));

        // 目录不含该选型：补占位项并选中，下拉仍显示真实后端选型。
        ClassicAssert.AreEqual("model-ghost", composer.SelectedModelOption?.Model);
        Assert.That(composer.ModelOptions.Any(option => option.Model == "model-ghost"), Is.True);
        ClassicAssert.AreEqual(4, composer.ModelOptions.Count);
    }

    [Test]
    public async Task SessionSwitchClearsCurrentModelAndFallsBackToCatalogDefault()
    {
        var composer = CreateComposer();
        await composer.RefreshModelCatalogAsync();
        composer.SetSession("session-a", false, string.Empty);
        composer.ApplyCurrentModel(new ModelSelection("prov-b", "model-z"));

        // 切换会话：保留已加载目录，清除上一会话选型，下拉回退目录默认。
        composer.SetSession("session-b", false, string.Empty);
        ClassicAssert.IsNull(composer.CurrentModel);
        ClassicAssert.AreEqual("model-x", composer.SelectedModelOption?.Model);
        ClassicAssert.AreEqual(3, composer.ModelOptions.Count);

        // 新会话的真实选型由其快照携带，随后接管显示。
        composer.ApplyCurrentModel(new ModelSelection("prov-a", "model-y"));
        ClassicAssert.AreEqual("model-y", composer.SelectedModelOption?.Model);
    }

    [Test]
    public async Task StaleSelectionCompletionDoesNotPolluteSwitchedSessionPicker()
    {
        var     sessionService = new ControllableSessionService { BlockSelect = true, FailSelect = true };
        string? reported       = null;
        var     composer       = new ComposerViewModel(sessionService, text => reported = text);
        await composer.RefreshModelCatalogAsync();
        composer.SetSession("session-a", false, string.Empty);
        composer.SetBackendConnected(true);

        // session A 发起选型（生效默认 model-x → 目标 model-y），请求在途。
        composer.SelectedModelOption = composer.ModelOptions.Single(option => option.Model == "model-y");
        await sessionService.SelectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // 切到 session B，其快照选型到达。
        composer.SetSession("session-b", false, string.Empty);
        composer.ApplyCurrentModel(new ModelSelection("prov-b", "model-z"));
        ClassicAssert.AreEqual("model-z", composer.SelectedModelOption?.Model);

        // A 的请求此时失败：错误照常上报，但回退读取当前生效选型（B 的），
        // 不得把 B 的下拉拉回 A 请求时的目标或目录默认。
        sessionService.ReleaseSelect.TrySetResult();
        await WaitUntilAsync(() => reported is not null);
        Assert.That(reported, Does.Contain("选型失败"));
        ClassicAssert.AreEqual("model-z", composer.SelectedModelOption?.Model);
    }

    [Test]
    public void UsageUpdateSetsStateAndDerivedDisplayTexts()
    {
        var composer = CreateComposer();
        composer.SetSession("session-1", false, string.Empty);
        ClassicAssert.IsFalse(composer.HasStatsData);

        composer.ApplyUsage(10, new SessionUsage(400, 100, 100, 0));

        ClassicAssert.AreEqual(new SessionUsage(400, 100, 100, 0), composer.Usage);
        ClassicAssert.IsTrue(composer.HasStatsData);
        // 总量 = 计费输入（400+100+0）+ 输出 100；命中率 = 100/500。
        ClassicAssert.AreEqual("Token 用量 600", composer.UsageValueText);
        ClassicAssert.AreEqual("缓存命中 20%", composer.CacheHitValueText);
        ClassicAssert.AreEqual("未命中输入 400 · 缓存读 100 · 缓存写 0 · 输出 100", composer.UsageDetailText);
    }

    [Test]
    public void StaleUsageSeqDoesNotOverwriteNewerUsage()
    {
        var composer = CreateComposer();
        composer.SetSession("session-1", false, string.Empty);

        composer.ApplyUsage(20, new SessionUsage(400, 100, 100, 0));
        // 乱序到达的旧 seq（重连竞态）被忽略。
        composer.ApplyUsage(15, new SessionUsage(9, 9, 9, 9));
        ClassicAssert.AreEqual(new SessionUsage(400, 100, 100, 0), composer.Usage);

        // 同 seq 重复到达照常接受（整值幂等重放）。
        composer.ApplyUsage(20, new SessionUsage(500, 100, 100, 0));
        ClassicAssert.AreEqual(new SessionUsage(500, 100, 100, 0), composer.Usage);
    }

    [Test]
    public void StatsUpdateSetsStateAndDerivedDisplayTexts()
    {
        var composer = CreateComposer();
        composer.SetSession("session-1", false, string.Empty);

        // 仅凭 stats 的步数即满足统计条显示口径。
        composer.ApplyStats(10, new SessionStats(2, 3, 2400, 800, 0, 0, 2000, 300));

        ClassicAssert.AreEqual(new SessionStats(2, 3, 2400, 800, 0, 0, 2000, 300), composer.Stats);
        ClassicAssert.IsTrue(composer.HasStatsData);
        ClassicAssert.AreEqual("生成速度 150 tok/s", composer.SpeedValueText);
        ClassicAssert.AreEqual("2 轮 · 3 步 · 模型耗时 2.4s · 工具耗时 0.8s", composer.StatsDetailText);
    }

    [Test]
    public void StaleStatsSeqDoesNotOverwriteNewerStats()
    {
        var composer = CreateComposer();
        composer.SetSession("session-1", false, string.Empty);

        composer.ApplyStats(30, new SessionStats(2, 3, 2400, 800, 0, 0, 2000, 300));
        composer.ApplyStats(25, new SessionStats(9, 9, 9, 9, 9, 9, 9, 9));

        ClassicAssert.AreEqual(new SessionStats(2, 3, 2400, 800, 0, 0, 2000, 300), composer.Stats);
    }

    [Test]
    public void SessionSwitchClearsUsageStatsAndRestartsSeqGating()
    {
        var composer = CreateComposer();
        composer.SetSession("session-a", false, string.Empty);
        composer.ApplyUsage(100, new SessionUsage(400, 100, 100, 0));
        composer.ApplyStats(100, new SessionStats(2, 3, 2400, 800, 0, 0, 2000, 300));

        composer.SetSession("session-b", false, string.Empty);

        // 上一会话的统计与 seq gating 一并清零，统计条隐藏。
        ClassicAssert.IsNull(composer.Usage);
        ClassicAssert.IsNull(composer.Stats);
        ClassicAssert.IsFalse(composer.HasStatsData);

        // 新会话的首批整值 seq 从头计（小于上一会话的 100）也可正常接受。
        composer.ApplyUsage(1, new SessionUsage(10, 5, 0, 0));
        composer.ApplyStats(1, new SessionStats(1, 1, 100, 0, 0, 0, 0, 0));
        ClassicAssert.AreEqual(new SessionUsage(10, 5, 0, 0), composer.Usage);
        ClassicAssert.AreEqual(new SessionStats(1, 1, 100, 0, 0, 0, 0, 0), composer.Stats);
    }

    [Test]
    public void RunningAndConnectionChangesDoNotClearUsageStats()
    {
        var composer = CreateComposer();
        composer.SetSession("session-a", false, string.Empty);
        var usage = new SessionUsage(400, 100, 100, 0);
        composer.ApplyUsage(10, usage);
        composer.ApplyStats(10, new SessionStats(2, 3, 2400, 800, 0, 0, 2000, 300));

        // 运行状态、后端连接与同会话的上下文刷新（SetSession 同 id）都不得清除统计。
        composer.SetSessionRunning(true);
        composer.SetSessionRunning(false);
        composer.SetBackendConnected(false);
        composer.SetBackendConnected(true);
        composer.SetSession("session-a", true, string.Empty);

        ClassicAssert.AreEqual(usage, composer.Usage);
        ClassicAssert.IsNotNull(composer.Stats);
        ClassicAssert.IsTrue(composer.HasStatsData);
    }

    private static ComposerViewModel CreateComposer()
    {
        return new ComposerViewModel(new ControllableSessionService(), _ => { });
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds = 2000)
    {
        var timeout = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (DateTime.UtcNow < timeout)
        {
            if (condition()) return;

            await Task.Delay(10);
        }

        ClassicAssert.IsTrue(condition(), "预期的异步 Composer 状态未在超时前出现。");
    }

    /// <summary>
    ///     可控桩：其余行为走模拟实现。发送/选型可阻塞到手动放行并可注入失败，
    ///     取消与选型请求记录目标会话，模型目录可整体替换。
    /// </summary>
    private sealed class ControllableSessionService : ISessionService
    {
        private readonly SimulatedSessionService _inner = new();

        public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseSend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SelectStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseSelect { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> CancelledSessions { get; } = [];

        public List<(string SessionId, string Provider, string Model, string? ReasoningEffort)> SelectionRequests
        {
            get;
        } = [];

        public bool FailSend { get; set; }

        public bool FailSelect { get; set; }

        /// <summary>选型请求是否阻塞到手动放行；默认立即完成。</summary>
        public bool BlockSelect { get; set; }

        /// <summary>
        ///     测试目录：默认 prov-a/model-x，两个提供方共三个模型。model-x/model-y 带档位
        ///     元数据（model-x 含默认档 high），model-z 无声明——显式档位必被后端拒绝的形态。
        /// </summary>
        public ModelCatalog Catalog { get; } = new(new ModelSelection("prov-a", "model-x"),
        [
            new ModelProviderGroup("prov-a", "Provider A",
            [
                new ModelCatalogEntry("model-x", "Model X", new ModelReasoningInfo(
                [
                    new ReasoningEffortInfo("off", "Off"), new ReasoningEffortInfo("high", "High")
                ], "high")),
                new ModelCatalogEntry("model-y", "Model Y", new ModelReasoningInfo(
                [
                    new ReasoningEffortInfo("low", "Low"), new ReasoningEffortInfo("high", "High")
                ]))
            ]),
            new ModelProviderGroup("prov-b", "Provider B", [new ModelCatalogEntry("model-z", "Model Z")])
        ], []);

        public event EventHandler? SessionsChanged
        {
            add => _inner.SessionsChanged += value;
            remove => _inner.SessionsChanged -= value;
        }

        public Task<IReadOnlyList<SessionSummary>> GetSessionsAsync(CancellationToken cancellationToken = default)
        {
            return _inner.GetSessionsAsync(cancellationToken);
        }

        public Task<SessionSummary> CreateSessionAsync(
            string?           workspaceId       = null, string? sessionId = null, string? agentPreset = null,
            CancellationToken cancellationToken = default)
        {
            return _inner.CreateSessionAsync(workspaceId, sessionId, cancellationToken : cancellationToken);
        }

        public Task<string> ForkSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            return _inner.ForkSessionAsync(sessionId, cancellationToken);
        }

        public Task<string> RenameSessionAsync(
            string sessionId, string title, CancellationToken cancellationToken = default)
        {
            return _inner.RenameSessionAsync(sessionId, title, cancellationToken);
        }

        public void MarkSessionEngaged(string sessionId)
        {
            _inner.MarkSessionEngaged(sessionId);
        }

        public Task<ModelCatalog> GetModelCatalogAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Catalog);
        }

        public async Task<ModelSelection> SelectModelAsync(
            string            sessionId, string provider, string model, string? reasoningEffort = null,
            CancellationToken cancellationToken = default)
        {
            // 只记录请求并模拟后端应答，不落到模拟实现：目标会话不必存在于演示数据，
            // Composer 级测试不依赖选型副作用（回声由测试显式 ApplyCurrentModel 模拟）。
            SelectionRequests.Add((sessionId, provider, model, reasoningEffort));
            SelectStarted.TrySetResult();
            if (BlockSelect) await ReleaseSelect.Task.WaitAsync(cancellationToken);

            if (FailSelect) throw new InvalidOperationException("选型失败（模拟）");

            cancellationToken.ThrowIfCancellationRequested();
            return new ModelSelection(provider, model);
        }

        public Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(
            string sessionId, CancellationToken cancellationToken = default)
        {
            return _inner.GetMessagesAsync(sessionId, cancellationToken);
        }

        public Task<SessionHistoryPage> LoadOlderAsync(
            string sessionId, long throughSeq, long beforeSeq, CancellationToken cancellationToken = default)
        {
            return _inner.LoadOlderAsync(sessionId, throughSeq, beforeSeq, cancellationToken);
        }

        public async Task SendPromptAsync(
            string sessionId, string requestId, string content, CancellationToken cancellationToken = default)
        {
            SendStarted.TrySetResult();
            if (FailSend) throw new InvalidOperationException("发送失败（模拟）");

            await ReleaseSend.Task.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }

        public Task CancelAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            CancelledSessions.Add(sessionId);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<SessionUpdate> FollowSessionAsync(
            string sessionId, CancellationToken cancellationToken = default)
        {
            return _inner.FollowSessionAsync(sessionId, cancellationToken);
        }
    }
}
