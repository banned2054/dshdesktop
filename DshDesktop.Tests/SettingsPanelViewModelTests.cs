using DshDesktop.Core.Models;
using DshDesktop.Core.Services;
using DshDesktop.Infrastructure.Services;
using DshDesktop.Utils;
using DshDesktop.ViewModels.Settings;
using System.Text.Json;
using Xunit;

namespace DshDesktop.Tests;

/// <summary>
///     设置面板 ViewModel 级测试：使用 SimulatedSettingsService/SimulatedCredentialsService
///     真实实现验证投影、即时写引擎（冲突回滚）、模型/插件卡保存语义、主题回调与外部刷新。
/// </summary>
public sealed class SettingsPanelViewModelTests
{
    private static SettingsPanelViewModel CreatePanel(SimulatedSettingsService?    settings       = null,
                                                      SimulatedCredentialsService? credentials    = null,
                                                      List<string?>?               themeLog       = null,
                                                      ISettingsService?            service        = null,
                                                      ISessionService?             sessionService = null,
                                                      ILlmCatalogService?          catalogService = null)
    {
        return new SettingsPanelViewModel(service     ?? settings ?? new SimulatedSettingsService(),
                                          credentials ?? new SimulatedCredentialsService(),
                                          preference => themeLog?.Add(preference),
                                          sessionService : sessionService,
                                          llmCatalogService : catalogService);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds = 2000)
    {
        var timeout = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (DateTime.UtcNow < timeout)
        {
            if (condition()) return;

            await Task.Delay(10);
        }

        Assert.True(condition(), "预期的异步 ViewModel 状态未在超时前出现。");
    }

    private static SettingsNamespaceView DescribeNamespace(ISettingsService service, string ns)
    {
        return service.DescribeAsync().GetAwaiter().GetResult().Namespaces.Single(view => view.Ns == ns);
    }

    /// <summary>包装模拟服务并从 describe 结果中过滤指定 ns，模拟后端未提供的命名空间。</summary>
    private sealed class FilteredSettingsService(
        ISettingsService inner,
        params string[]  hiddenNamespaces) : ISettingsService
    {
        public event EventHandler<SettingsDocumentUpdate>? DocumentUpdated
        {
            add => inner.DocumentUpdated += value;
            remove => inner.DocumentUpdated -= value;
        }

        public async Task<SettingsDescribeValue> DescribeAsync(CancellationToken cancellationToken = default)
        {
            var describe = await inner.DescribeAsync(cancellationToken);
            return describe with
            {
                Namespaces = describe.Namespaces
                                     .Where(view => !hiddenNamespaces.Contains(view.Ns))
                                     .ToArray()
            };
        }

        public Task<SettingsNamespaceView> UpdateAsync(string            ns, JsonElement patch,
                                                       long?             expectedRevision  = null,
                                                       CancellationToken cancellationToken = default)
        {
            return inner.UpdateAsync(ns, patch, expectedRevision, cancellationToken);
        }

        public Task<SettingsNamespaceView> ReplaceAsync(string            ns, JsonElement section,
                                                        long?             expectedRevision  = null,
                                                        CancellationToken cancellationToken = default)
        {
            return inner.ReplaceAsync(ns, section, expectedRevision, cancellationToken);
        }

        public Task<SettingsNamespaceView> MutateAsync(string            ns, IReadOnlyList<SettingsMutationOp> ops,
                                                       long?             expectedRevision  = null,
                                                       CancellationToken cancellationToken = default)
        {
            return inner.MutateAsync(ns, ops, expectedRevision, cancellationToken);
        }

        public Task OpenSettingsDocumentAsync(CancellationToken cancellationToken = default)
        {
            return inner.OpenSettingsDocumentAsync(cancellationToken);
        }
    }

    /// <summary>固定返回给定目录（或注入失败）的会话服务替身；设置面板仅消费目录查询，
    /// 其余成员显式抛出，新增消费时须补桩。</summary>
    private sealed class FixedCatalogSessionService(ModelCatalog? catalog, Exception? failure = null) : ISessionService
    {
        public event EventHandler? SessionsChanged
        {
            add { }
            remove { }
        }

        public Task<ModelCatalog> GetModelCatalogAsync(CancellationToken cancellationToken = default)
        {
            return failure is { } exception
                ? Task.FromException<ModelCatalog>(exception)
                : Task.FromResult(catalog!);
        }

        public Task<IReadOnlyList<SessionSummary>> GetSessionsAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<SessionSummary> CreateSessionAsync(
            string?           workspaceId       = null, string? sessionId = null, string? agentPreset = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<string> ForkSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<string> RenameSessionAsync(
            string sessionId, string title, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public void MarkSessionEngaged(string sessionId)
        {
            throw new NotSupportedException();
        }

        public Task<ModelSelection> SelectModelAsync(
            string  sessionId,              string            provider, string model,
            string? reasoningEffort = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(
            string sessionId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<SessionHistoryPage> LoadOlderAsync(
            string sessionId, long throughSeq, long beforeSeq, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task SendPromptAsync(
            string sessionId, string requestId, string content, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task CancelAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public IAsyncEnumerable<SessionUpdate> FollowSessionAsync(
            string sessionId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    [Fact]
    public async Task OpenProjectsSectionsRows()
    {
        var panel = CreatePanel();
        await panel.OpenAsync();

        Assert.True(panel.IsReady);
        Assert.Equal(["general", "models", "plugins"], panel.Sections.Select(section => section.Id));
        Assert.Equal(panel.General, panel.ActiveSection);

        // 通用行投影：主题 dark、遗留 label 映射、权限候选来自 schema.choices。
        var general = panel.General;
        Assert.True(general.AppearanceRow.IsVisible);
        Assert.True(general.AppearanceRow.IsDarkSelected);
        Assert.Equal("标准", general.TranscriptViewRow.CurrentLabel);
        Assert.Equal("简洁", general.PerformanceUsageRow.CurrentLabel);
        Assert.Equal("排队发送", general.BusyEnterRow.CurrentLabel);
        Assert.True(general.PermissionRow.IsVisible);
        Assert.Equal(2, general.PermissionRow.Options.Count);
        Assert.Equal("默认", general.PermissionRow.CurrentLabel);
        Assert.False(general.CodeWorkViewRow.IsChecked);
        Assert.True(general.SessionLogRow.IsChecked);

        // 模型卡与插件卡。
        Assert.NotNull(panel.Models.Card);
        Assert.Equal("DeepSeek", panel.Models.Card!.Title);
        Assert.True(panel.Models.Card.IsCredentialSet);
        Assert.Equal("https://api.deepseek.com", panel.Models.Card.BaseUrlDraft);
        Assert.Equal(2, panel.Models.Card.ModelEntries.Count);
        Assert.Equal(["终端", "Agent 循环", "子代理", "网页搜索"],
                     panel.Plugins.Cards.Select(card => card.Title));

        // 网页搜索密钥：凭据查询返回未配置（空凭据服务），圆点为空心灰。
        var webSearch   = panel.Plugins.Cards.Single(card => card.Ns == "web-search-deepseek");
        var apiKeyField = webSearch.Fields.Single(field => field.Key == "apiKeyEnv");
        Assert.True(apiKeyField.ShowCredentialDot);
        Assert.True(apiKeyField.IsCredentialUnset);
    }

    [Fact]
    public async Task ProviderRowsProjectConfiguredRoutesWithCredentialDots()
    {
        // 行列表：已配置路由按官方排序（DeepSeek → 其余按目录声明序）；账户路由在会话目录
        // 无 deepseek-account 组时隐藏；显式命名 apiKeyEnv 且已配置的行画实心绿点。
        var panel = CreatePanel(catalogService : new SimulatedLlmCatalogService());
        await panel.OpenAsync();

        Assert.True(panel.IsReady);
        var rows = panel.Models.Providers;
        Assert.Equal(["deepseek-official", "glm"], rows.Select(row => row.ProviderId).ToArray());
        Assert.Equal(["DeepSeek", "GLM"], rows.Select(row => row.DisplayName).ToArray());
        Assert.DoesNotContain(rows, row => row.ProviderId == "deepseek-account");
        Assert.All(rows, row => Assert.True(row.ShowCredentialDot));
        Assert.All(rows, row => Assert.True(row.IsCredentialSet));
        Assert.Empty(rows[0].SettingsPath);
        Assert.Equal(["providers", "glm"], rows[1].SettingsPath);
        Assert.Equal("llm-pi-ai", rows[1].SettingsNs);
        // llm-pi-ai 命名空间挂载 → 可添加；展开态默认关闭（无行编辑、无添加卡）。
        Assert.True(panel.Models.CanAdd);
        Assert.False(panel.Models.IsAddOpen);
        Assert.All(rows, row => Assert.False(row.IsEditing));
    }

    [Fact]
    public async Task AccountRowRequiresCatalogGroup()
    {
        // 会话目录含非空 deepseek-account 组且命名空间挂载 → 「DeepSeek 账号」行出现（官方覆盖名）。
        var catalog = new ModelCatalog(new ModelSelection("deepseek", "deepseek-chat"),
        [
            new ModelProviderGroup("deepseek-account", "DeepSeek 账号",
                                   [new ModelCatalogEntry("deepseek-chat", "DeepSeek Chat")])
        ], []);
        var panel = CreatePanel(catalogService : new SimulatedLlmCatalogService(),
                                sessionService : new FixedCatalogSessionService(catalog));
        await panel.OpenAsync();

        var account = panel.Models.Providers.Single(row => row.ProviderId == "deepseek-account");
        Assert.Equal("DeepSeek 账号", account.DisplayName);
        // 账户路由无 apiKeyEnv 槽位（凭据由登录态承载）→ 不画圆点。
        Assert.False(account.ShowCredentialDot);
        // 账户行排在 DeepSeek 官方行之前（官方排序）。
        Assert.Equal("deepseek-account",
                     panel.Models.Providers.Select(row => row.ProviderId).First());
    }

    [Fact]
    public async Task CatalogFailureKeepsRowsFromSettingsFallback()
    {
        // 目录查询失败：面板不进错误态；行从设置文档兜底（DeepSeek 整段路由 + pi-ai 路由）。
        var panel = CreatePanel(catalogService : new SimulatedLlmCatalogService(),
                                sessionService :
                                new FixedCatalogSessionService(null,
                                                               failure : new InvalidOperationException("目录查询失败")));
        await panel.OpenAsync();

        Assert.True(panel.IsReady);
        Assert.Equal(["deepseek-official", "glm"],
                     panel.Models.Providers.Select(row => row.ProviderId).ToArray());
    }

    [Fact]
    public async Task DirectoryFailureStillBuildsRowsFromSettings()
    {
        // 目录服务不可用（旧版后端）：行显示名回退（DeepSeek 固定名 / profile.displayName / 路由 id）。
        var panel = CreatePanel();
        await panel.OpenAsync();

        Assert.True(panel.IsReady);
        Assert.Equal(["deepseek-official", "glm"],
                     panel.Models.Providers.Select(row => row.ProviderId).ToArray());
        Assert.Equal("DeepSeek", panel.Models.Providers[0].DisplayName);
        Assert.Equal("GLM", panel.Models.Providers[1].DisplayName);
    }

    [Fact]
    public async Task DeepSeekRowEditExpandsInlineAndCancelCollapses()
    {
        var panel = CreatePanel(catalogService : new SimulatedLlmCatalogService());
        await panel.OpenAsync();

        var row = panel.Models.Providers.Single(candidate => candidate.ProviderId == "deepseek-official");
        row.EditCommand.Execute(null);
        Assert.True(row.IsEditing);
        Assert.True(row.HasDeepSeekCard);
        Assert.Same(panel.Models.Card, row.DeepSeekCard);

        panel.Models.Card!.CancelCommand.Execute(null);
        Assert.False(row.IsEditing);
        Assert.Null(row.DeepSeekCard);
    }

    [Fact]
    public async Task ProviderRowEditExpandsInlineOneRowAtATime()
    {
        var panel = CreatePanel(catalogService : new SimulatedLlmCatalogService());
        await panel.OpenAsync();

        // 点 pi-ai 路由行的编辑：仅该行行内展开（官方同构），DeepSeek 行保持折叠。
        var glm = panel.Models.Providers.Single(candidate => candidate.ProviderId == "glm");
        var deepSeek = panel.Models.Providers.Single(candidate => candidate.ProviderId == "deepseek-official");
        glm.EditCommand.Execute(null);
        Assert.True(glm.IsEditing);
        Assert.True(glm.HasProviderCard);
        Assert.False(deepSeek.IsEditing);
        Assert.Null(deepSeek.DeepSeekCard);

        // 取消收起后改编辑 DeepSeek 行：仅 DeepSeek 行展开，互不叠显。
        glm.ProviderCard!.CancelCommand.Execute(null);
        Assert.False(glm.IsEditing);

        deepSeek.EditCommand.Execute(null);
        Assert.True(deepSeek.IsEditing);
        Assert.True(deepSeek.HasDeepSeekCard);
        Assert.Null(glm.ProviderCard);

        deepSeek.DeepSeekCard!.CancelCommand.Execute(null);
        Assert.False(deepSeek.IsEditing);
        Assert.Null(deepSeek.DeepSeekCard);
    }

    [Fact]
    public async Task EditingRowToggleCollapsesAndAddCardIsExclusive()
    {
        var panel = CreatePanel(catalogService : new SimulatedLlmCatalogService());
        await panel.OpenAsync();

        // 再点同一行的编辑：收起（官方 toggle 语义）。
        var glm = panel.Models.Providers.Single(candidate => candidate.ProviderId == "glm");
        glm.EditCommand.Execute(null);
        Assert.True(glm.IsEditing);
        glm.EditCommand.Execute(null);
        Assert.False(glm.IsEditing);
        Assert.Null(panel.Models.Editor);

        // 行编辑展开时打开添加卡：行编辑收起，一次一卡。
        glm.EditCommand.Execute(null);
        Assert.True(glm.IsEditing);
        panel.Models.OpenAddCommand.Execute(null);
        Assert.False(glm.IsEditing);
        Assert.True(panel.Models.IsAddOpen);
        Assert.NotNull(panel.Models.Editor);

        // 添加卡取消：收起且不展开任何行。
        panel.Models.Editor!.CancelCommand.Execute(null);
        Assert.False(panel.Models.IsAddOpen);
        Assert.Null(panel.Models.Editor);
        Assert.All(panel.Models.Providers, row => Assert.False(row.IsEditing));
    }

    [Fact]
    public async Task ImmediateToggleWritesUserSegmentAndAdvancesRevision()
    {
        var settings = new SimulatedSettingsService();
        var panel    = CreatePanel(settings);
        await panel.OpenAsync();
        var revisionBefore = DescribeNamespace(settings, "ui-settings").Revision;

        panel.General.CodeWorkViewRow.IsChecked = true;

        var view = DescribeNamespace(settings, "ui-settings");
        Assert.True(view.User is { } user                        &&
                    user.TryGetProperty("enabled", out var flag) &&
                    flag.ValueKind == JsonValueKind.True);
        Assert.True(view.Value.GetProperty("enabled").GetBoolean());
        Assert.True(panel.General.CodeWorkViewRow.IsChecked);
        Assert.True(view.Revision > revisionBefore);
    }

    [Fact]
    public async Task StaleRevisionConflictRollsBackToBackendTruthWithRowError()
    {
        var settings = new SimulatedSettingsService();
        var panel    = CreatePanel(settings);
        await panel.OpenAsync();

        // 卡片快照过期：直接写服务推进 revision 后再经面板写（值取 true，与面板打开时的 false 不同）。
        await settings.MutateAsync("ui-settings",
                                   [SettingsMutationOp.Set(["enabled"], JsonElementFactory.FromBoolean(true))]);
        panel.General.CodeWorkViewRow.IsChecked = true;

        Assert.True(panel.General.CodeWorkViewRow.IsChecked);
        Assert.True(panel.General.CodeWorkViewRow.HasError);
        Assert.Equal("这些设置已被其他地方改动，已恢复为最新值。",
                     panel.General.CodeWorkViewRow.ErrorText);
        // 冲突写未落库：revision 仍是外部推进后的值。
        Assert.Equal(1, DescribeNamespace(settings, "ui-settings").Revision);
    }

    [Fact]
    public async Task PluginCardSaveWritesUserLayerAndSwitchSectionDiscardsDraft()
    {
        var settings = new SimulatedSettingsService();
        var panel    = CreatePanel(settings);
        await panel.OpenAsync();
        var terminal = panel.Plugins.Cards.Single(card => card.Title == "终端");
        var timeout  = terminal.Fields.Single(field => field.Key     == "timeoutMs");
        Assert.Equal("120000", timeout.DraftText);
        Assert.False(terminal.IsDirty);

        timeout.DraftText = "30000";
        Assert.True(terminal.IsDirty);
        Assert.True(terminal.CanSave);
        terminal.SaveCommand.Execute(null);

        var view = DescribeNamespace(settings, "pwsh-sandbox");
        Assert.True(view.User is { } user && user.GetProperty("timeoutMs").GetInt64() == 30000);
        Assert.True(view.Value.GetProperty("timeoutMs").GetInt64() == 30000);
        Assert.False(terminal.IsDirty);
        Assert.True(terminal.HasSaveSuccess);

        // 切换分区丢弃草稿：重新投影回保存后的生效值。
        timeout.DraftText = "99000";
        panel.SelectSection(panel.Models);
        panel.SelectSection(panel.Plugins);
        var terminalAfter = panel.Plugins.Cards.Single(card => card.Title    == "终端");
        Assert.Equal("30000", terminalAfter.Fields.Single(field => field.Key == "timeoutMs").DraftText);
        Assert.False(terminalAfter.IsDirty);
    }

    [Fact]
    public async Task SavedFieldShowsOverrideMarkAndRestoreDefaultUnsets()
    {
        var settings = new SimulatedSettingsService();
        var panel    = CreatePanel(settings);
        await panel.OpenAsync();
        var terminal = panel.Plugins.Cards.Single(card => card.Title == "终端");
        var timeout  = terminal.Fields.Single(field => field.Key     == "timeoutMs");
        Assert.False(timeout.IsOverridden);

        timeout.DraftText = "30000";
        terminal.SaveCommand.Execute(null);
        Assert.True(timeout.IsOverridden);

        // 恢复默认：暂存 unset，保存后回到 base 层并清除覆盖标记。
        timeout.RestoreDefaultCommand.Execute(null);
        Assert.True(timeout.IsRestoreStaged);
        Assert.True(terminal.IsDirty);
        terminal.SaveCommand.Execute(null);

        var view = DescribeNamespace(settings, "pwsh-sandbox");
        Assert.False(view.User is { } user && user.TryGetProperty("timeoutMs", out _));
        Assert.Equal(120000, view.Value.GetProperty("timeoutMs").GetInt64());
        Assert.False(timeout.IsOverridden);
        Assert.False(terminal.IsDirty);
    }

    [Fact]
    public async Task ModelCardSaveUpdatesNamespaceAndCredentialReference()
    {
        var settings    = new SimulatedSettingsService();
        var credentials = new SimulatedCredentialsService();
        var panel       = CreatePanel(settings, credentials);
        await panel.OpenAsync();
        var card = panel.Models.Card!;
        card.BaseUrlDraft = "https://example.com/anthropic";
        card.ApiKeyDraft  = "sk-test-value";
        Assert.True(card.IsDirty);
        Assert.True(card.CanSave);

        card.SaveCommand.Execute(null);

        var view = DescribeNamespace(settings, "llm-deepseek");
        Assert.Equal("https://example.com/anthropic", view.Value.GetProperty("baseURL").GetString());
        Assert.True(card.HasSaveSuccess);
        Assert.False(card.IsDirty);
        var statuses = await credentials.DescribeAsync(["DEEPSEEK_API_KEY"]);
        Assert.True(statuses["DEEPSEEK_API_KEY"].Configured);
    }

    [Fact]
    public async Task ModelCardConflictKeepsDraftAndShowsConflictMessage()
    {
        var settings = new SimulatedSettingsService();
        var panel    = CreatePanel(settings);
        await panel.OpenAsync();
        var card = panel.Models.Card!;
        card.BaseUrlDraft = "https://mine.example";

        // 卡片打开期间外部改动推进 revision：保存按打开时的 revision 乐观锁冲突。
        await settings.MutateAsync("llm-deepseek",
        [
            SettingsMutationOp.Set(["baseURL"],
                                   JsonElementFactory.FromString("https://elsewhere.example"))
        ]);
        card.SaveCommand.Execute(null);

        Assert.True(card.HasSaveError);
        Assert.Equal("这张卡片打开期间，这些设置已被其他地方改动。请关闭后重新打开，在当前值上编辑。",
                     card.SaveError);
        Assert.Equal("https://mine.example", card.BaseUrlDraft);
    }

    [Fact]
    public async Task ModelEntriesProjectModalitiesCollapseAndKmDisplay()
    {
        var settings = new SimulatedSettingsService();
        var panel    = CreatePanel(settings);
        await panel.OpenAsync();
        var card = panel.Models.Card!;
        Assert.Equal(2, card.ModelEntries.Count);

        // 条目默认折叠；flash 携带 text+image，v4-pro 仅 text；整百万缩写为 1M。
        var flash = card.ModelEntries[0];
        Assert.Equal("deepseek-flash", flash.IdDraft);
        Assert.False(flash.IsExpanded);
        Assert.True(flash.IsTextSelected);
        Assert.True(flash.IsImageSelected);
        Assert.Equal("1M", flash.ContextWindowDraft);
        var pro = card.ModelEntries[1];
        Assert.True(pro.IsTextSelected);
        Assert.False(pro.IsImageSelected);
        Assert.Equal("1M", pro.ContextWindowDraft);
        Assert.Equal(string.Empty, pro.MaxTokensDraft);

        flash.ToggleExpandCommand.Execute(null);
        Assert.True(flash.IsExpanded);
        flash.ToggleExpandCommand.Execute(null);
        Assert.False(flash.IsExpanded);
    }

    [Fact]
    public async Task ModelCardTokenDraftsValidateKmSuffixAndBlank()
    {
        var settings = new SimulatedSettingsService();
        var panel    = CreatePanel(settings);
        await panel.OpenAsync();
        var card = panel.Models.Card!;
        var pro  = card.ModelEntries[1];

        // K/M 后缀合法；小数/任意文本/负数不合法；留空合法。
        pro.MaxTokensDraft = "256K";
        Assert.False(card.HasValidationError);
        pro.MaxTokensDraft = "1.5M";
        Assert.True(card.HasValidationError);
        Assert.Equal("最大输出 token 数必须是正整数（可带 K/M 后缀）或留空。", card.FirstValidationError);
        pro.MaxTokensDraft = "abc";
        Assert.True(card.HasValidationError);
        pro.MaxTokensDraft = "-5";
        Assert.True(card.HasValidationError);
        pro.MaxTokensDraft = string.Empty;
        Assert.False(card.HasValidationError);
        pro.ContextWindowDraft = "1M";
        Assert.False(card.HasValidationError);
    }

    [Fact]
    public async Task ModelCardSavePreservesUnmodeledFieldsAndWritesKmValue()
    {
        var settings = new SimulatedSettingsService();
        var panel    = CreatePanel(settings);
        await panel.OpenAsync();
        var card = panel.Models.Card!;
        var pro  = card.ModelEntries[1];

        // K/M 草稿解析为数字写入；未建模的 description 逐字保留；
        // 来源缺 inputModalities 且纯文本时不补写缺省键。
        pro.MaxTokensDraft = "64K";
        Assert.True(card.IsDirty);
        Assert.True(card.CanSave);
        card.SaveCommand.Execute(null);

        var models = DescribeNamespace(settings, "llm-deepseek").Value.GetProperty("models");
        var saved  = models[1];
        Assert.Equal(64000, saved.GetProperty("maxTokens").GetInt64());
        Assert.Equal("Stronger agentic coding, knowledge, and difficult reasoning; suited to complex or quality-critical tasks at higher cost.",
                     saved.GetProperty("description").GetString());
        Assert.False(saved.TryGetProperty("inputModalities", out _));
        Assert.False(card.IsDirty);

        // 保存重投影后勾选图片：写入 inputModalities（text+image）。
        pro = card.ModelEntries[1];
        Assert.Equal("64K", pro.MaxTokensDraft);
        pro.IsImageSelected = true;
        Assert.True(card.IsDirty);
        card.SaveCommand.Execute(null);

        saved = DescribeNamespace(settings, "llm-deepseek").Value.GetProperty("models")[1];
        Assert.Equal(new[] { "text", "image" },
                     saved.GetProperty("inputModalities").EnumerateArray()
                          .Select(value => value.GetString()).ToArray());
    }

    [Fact]
    public async Task ModelCardSaveDropsImageLimitsWhenImageModalityUnchecked()
    {
        var settings = new SimulatedSettingsService();

        // 预置一条携带 imageMaxBytes 的目录（上游允许图片模型附带限额）。
        await settings.MutateAsync("llm-deepseek",
        [
            SettingsMutationOp.Set(["models"], JsonElementFactory.FromArray(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("id", "deepseek-flash");
                writer.WriteString("name", "DeepSeek-V41-Flash");
                writer.WriteNumber("imageMaxBytes", 20971520);
                writer.WriteStartArray("inputModalities");
                writer.WriteStringValue("text");
                writer.WriteStringValue("image");
                writer.WriteEndArray();
                writer.WriteEndObject();
            }))
        ]);
        var panel = CreatePanel(settings);
        await panel.OpenAsync();

        // 取消图片：inputModalities 收敛为 text，图片限额被丢弃（上游禁止纯文本模型携带）。
        var card  = panel.Models.Card!;
        var flash = card.ModelEntries.Single(entry => entry.IdDraft == "deepseek-flash");
        flash.IsImageSelected = false;
        Assert.True(card.CanSave);
        card.SaveCommand.Execute(null);

        var saved = DescribeNamespace(settings, "llm-deepseek").Value.GetProperty("models")[0];
        Assert.Equal(new[] { "text" },
                     saved.GetProperty("inputModalities").EnumerateArray()
                          .Select(value => value.GetString()).ToArray());
        Assert.False(saved.TryGetProperty("imageMaxBytes", out _));
    }

    [Fact]
    public async Task ModelEntryExpansionSurvivesSaveReprojection()
    {
        var settings = new SimulatedSettingsService();
        var panel    = CreatePanel(settings);
        await panel.OpenAsync();
        var card = panel.Models.Card!;
        card.ModelEntries[1].ToggleExpandCommand.Execute(null);
        Assert.True(card.ModelEntries[1].IsExpanded);

        card.ModelEntries[1].MaxTokensDraft = "64K";
        card.SaveCommand.Execute(null);

        // 保存重投影按 ID 保持展开状态。
        Assert.Equal(2, card.ModelEntries.Count);
        Assert.True(card.ModelEntries[1].IsExpanded);
    }

    [Fact]
    public async Task ModelEntryDeleteClearsStaleValidation()
    {
        var settings = new SimulatedSettingsService();
        var panel    = CreatePanel(settings);
        await panel.OpenAsync();
        var card = panel.Models.Card!;

        // 新增空条目立即报 ID 必填；删除该条目后校验与脏标记同步消除。
        card.AddModelCommand.Execute(null);
        Assert.True(card.HasValidationError);
        Assert.Equal("模型 ID 不能为空。", card.FirstValidationError);

        card.ModelEntries.Last().DeleteCommand!.Execute(null);
        Assert.False(card.HasValidationError);
        Assert.False(card.IsDirty);
        Assert.Equal(2, card.ModelEntries.Count);
    }

    [Fact]
    public async Task ThemePreferenceWriteInvokesCallbackWithLight()
    {
        var themeLog = new List<string?>();
        var settings = new SimulatedSettingsService();
        var panel    = CreatePanel(settings, themeLog : themeLog);
        await panel.OpenAsync();

        // 打开投影即应用主题（种子 dark），切浅色后回调收到 light 且存储同步。
        Assert.Equal("dark", themeLog.LastOrDefault());
        panel.General.AppearanceRow.SelectLightCommand.Execute(null);
        Assert.Equal("light", themeLog.Last());
        Assert.Equal("light",
                     DescribeNamespace(settings, "ui-theme").Value.GetProperty("preference").GetString());
    }

    [Fact]
    public async Task MissingNamespacesHideCorrespondingRowsAndCards()
    {
        var settings = new SimulatedSettingsService();
        var filtered = new FilteredSettingsService(settings, "subagent", "ui-conversation");
        var panel    = CreatePanel(service : filtered);
        await panel.OpenAsync();

        Assert.False(panel.General.BusyEnterRow.IsVisible);
        Assert.True(panel.General.TranscriptViewRow.IsVisible);
        Assert.DoesNotContain(panel.Plugins.Cards, card => card.Ns == "subagent");
        Assert.Equal(["终端", "Agent 循环", "网页搜索"], panel.Plugins.Cards.Select(card => card.Title));
    }

    [Fact]
    public async Task DocumentUpdatedExternalChangeReprojectsGeneralRows()
    {
        var settings = new SimulatedSettingsService();
        var panel    = CreatePanel(settings);
        await panel.OpenAsync();
        Assert.Equal("标准", panel.General.TranscriptViewRow.CurrentLabel);

        var view = await settings.MutateAsync("ui-chat",
        [
            SettingsMutationOp.Set(["transcriptView"],
                                   JsonElementFactory.FromString("detailed"))
        ]);
        panel.HandleDocumentUpdated(new SettingsDocumentUpdate("ui-chat", view.Revision));
        await WaitUntilAsync(() => panel.General.TranscriptViewRow.CurrentLabel == "详细");
    }

    [Fact]
    public async Task PermissionFullAccessRequiresAcknowledgementAndCancelReverts()
    {
        var settings = new SimulatedSettingsService();
        var panel    = CreatePanel(settings);
        await panel.OpenAsync();
        var permission = panel.General.PermissionRow;
        var fullAccess = permission.Options.Single(option => option.Value == "full-access");

        // 点选完全权限：不直接写，先弹风险确认；取消则回到原值。
        fullAccess.SelectCommand!.Execute(null);
        Assert.True(permission.IsConfirmOpen);
        permission.CancelConfirmCommand.Execute(null);
        Assert.False(permission.IsConfirmOpen);
        Assert.Equal("default",
                     DescribeNamespace(settings, "permission").Value.GetProperty("defaultPreset").GetString());

        // 必勾确认后允许：写入 full-access。
        fullAccess.SelectCommand!.Execute(null);
        permission.IsAcknowledged = true;
        permission.ConfirmSelectionCommand.Execute(null);
        Assert.Equal("full-access",
                     DescribeNamespace(settings, "permission").Value.GetProperty("defaultPreset").GetString());
        Assert.Equal("完全权限", permission.CurrentLabel);
    }

    [Fact]
    public async Task AddDeclaredProviderValidatesAndSavesProfileWithDerivedCredential()
    {
        var settings    = new SimulatedSettingsService();
        var credentials = new SimulatedCredentialsService();
        var panel       = CreatePanel(settings, credentials, catalogService : new SimulatedLlmCatalogService());
        await panel.OpenAsync();

        panel.Models.OpenAddCommand.Execute(null);
        var editor = panel.Models.Editor!;
        Assert.True(panel.Models.IsAddOpen);
        Assert.True(editor.IsAddMode);
        editor.ShowDeclaredTabCommand.Execute(null);
        editor = panel.Models.Editor!;
        Assert.False(editor.IsCatalogTabSelected);
        // 自定义创建 tab：字段直出无折叠头（官方同构，添加流程不走折叠）。
        Assert.True(editor.IsDeclaredCreateMode);
        Assert.False(editor.ShowsCustomHeader);
        Assert.True(editor.ShowsCatalogBlock);

        // 校验链：id 正则 → id 占用 → 地址必填 → ≥1 模型。
        editor.RouteIdDraft = "Acme";
        Assert.Equal("Provider ID 必须以小写字母开头，只能包含小写字母、数字与连字符。",
                     editor.FirstValidationError);
        editor.RouteIdDraft = "glm";
        Assert.Equal("该 Provider ID 已被其他提供商使用。", editor.FirstValidationError);
        editor.RouteIdDraft = "acme-gateway";
        Assert.Equal("API 地址必填，且需以 http:// 或 https:// 开头。", editor.FirstValidationError);
        editor.BaseUrlDraft = "https://gateway.example/v1";
        Assert.Equal("至少添加一个模型。", editor.FirstValidationError);
        Assert.False(editor.CanSave);

        editor.AddModelCommand.Execute(null);
        editor.ModelEntries[0].IdDraft = "acme-large";
        editor.ApiKeyDraft             = "sk-acme";
        Assert.Null(editor.FirstValidationError);
        Assert.True(editor.CanSave);
        editor.SaveCommand.Execute(null);

        var profile = DescribeNamespace(settings, "llm-pi-ai").Value.GetProperty("providers")
                                                              .GetProperty("acme-gateway");
        Assert.Equal("openai-completions", profile.GetProperty("api").GetString());
        Assert.Equal("https://gateway.example/v1", profile.GetProperty("baseURL").GetString());
        Assert.Equal("ACME_GATEWAY_API_KEY", profile.GetProperty("apiKeyEnv").GetString());
        Assert.Equal("acme-large", profile.GetProperty("models")[0].GetProperty("id").GetString());
        // 纯文本新条目不补写缺省 input 键（与基线零差异策略一致）。
        Assert.False(profile.GetProperty("models")[0].TryGetProperty("input", out _));
        var statuses = await credentials.DescribeAsync(["ACME_GATEWAY_API_KEY"]);
        Assert.True(statuses["ACME_GATEWAY_API_KEY"].Configured);
        // 保存成功收起添加卡，新行出现（目录无此路由 → 显示名回退路由 id）。
        Assert.Null(panel.Models.Editor);
        Assert.False(panel.Models.IsAddOpen);
        var row = panel.Models.Providers.Single(candidate => candidate.ProviderId == "acme-gateway");
        Assert.Equal("acme-gateway", row.DisplayName);
        Assert.True(row.ShowCredentialDot);
    }

    [Fact]
    public async Task AddCatalogProviderSavesMinimalProfileAndCredential()
    {
        var settings    = new SimulatedSettingsService();
        var credentials = new SimulatedCredentialsService();
        var panel       = CreatePanel(settings, credentials, catalogService : new SimulatedLlmCatalogService());
        await panel.OpenAsync();

        // 第三方 tab：添加下拉仅含未配置的目录厂商（declared 的 glm 不在）。
        panel.Models.OpenAddCommand.Execute(null);
        var editor = panel.Models.Editor!;
        Assert.True(editor.IsCatalogTabSelected);
        Assert.DoesNotContain(editor.ProviderOptions, option => option.Label == "glm");

        // 带密钥保存：最小 profile（仅 apiKeyEnv 引用），密钥写凭据域，行画绿点。
        editor.ProviderOptions.Single(option => option.Label == "mistral").SelectCommand.Execute(null);
        Assert.Null(editor.FirstValidationError);
        editor.ApiKeyDraft = "sk-mistral";
        editor.SaveCommand.Execute(null);

        var profile = DescribeNamespace(settings, "llm-pi-ai").Value.GetProperty("providers")
                                                              .GetProperty("mistral");
        Assert.Equal("MISTRAL_API_KEY", profile.GetProperty("apiKeyEnv").GetString());
        Assert.False(profile.TryGetProperty("baseURL", out _));
        var statuses = await credentials.DescribeAsync(["MISTRAL_API_KEY"]);
        Assert.True(statuses["MISTRAL_API_KEY"].Configured);
        Assert.False(panel.Models.IsAddOpen);
        Assert.True(panel.Models.Providers.Single(candidate => candidate.ProviderId == "mistral")
                         .IsCredentialSet);

        // 无任何字段保存：物化空 profile（收养适配器默认），无显式引用不画圆点。
        panel.Models.OpenAddCommand.Execute(null);
        editor = panel.Models.Editor!;
        editor.ProviderOptions.Single(option => option.Label == "groq").SelectCommand.Execute(null);
        editor.SaveCommand.Execute(null);

        var empty = DescribeNamespace(settings, "llm-pi-ai").Value.GetProperty("providers")
                                                            .GetProperty("groq");
        Assert.Equal(JsonValueKind.Object, empty.ValueKind);
        Assert.False(empty.TryGetProperty("apiKeyEnv", out _));
        Assert.False(panel.Models.Providers.Single(candidate => candidate.ProviderId == "groq")
                          .ShowCredentialDot);
    }

    [Fact]
    public async Task EditDeclaredProviderSubmitsFieldLevelDiff()
    {
        var settings = new SimulatedSettingsService();
        var panel    = CreatePanel(settings, catalogService : new SimulatedLlmCatalogService());
        await panel.OpenAsync();

        var glmRow = panel.Models.Providers.Single(candidate => candidate.ProviderId == "glm");
        glmRow.EditCommand.Execute(null);
        var editor = panel.Models.Editor!;
        Assert.False(editor.IsAddMode);
        Assert.True(editor.IsDeclaredMode);
        // 编辑态默认折叠自定义设置（官方 details 同构）：仅密钥常显，折叠区收起。
        Assert.True(editor.ShowsCustomHeader);
        Assert.True(editor.IsCustomExpanded is false);
        Assert.False(editor.ShowsCatalogBlock);
        // 密钥占位双态：glm 已配置 → 提示可替换。
        Assert.Equal("已配置——输入新值可替换", editor.ApiKeyPlaceholder);
        var notified = new List<string>();
        editor.PropertyChanged += (_, e) => notified.Add(e.PropertyName);
        editor.ToggleCustomCommand.Execute(null);
        Assert.True(editor.ShowsCatalogBlock);
        // 计算属性须随展开态发通知，否则绑定不重算、折叠区展开后仍不可见。
        Assert.Contains("ShowsCatalogBlock", notified);
        // 编辑态预填：显示名/协议/模型条目来自 profile，Provider ID 只读展示路由 id。
        Assert.Equal("GLM", editor.DisplayNameDraft);
        Assert.Equal("openai-completions", editor.ApiDraft);
        Assert.Equal(3, editor.ModelEntries.Count);
        Assert.Equal("glm", editor.RouteIdDraft);
        Assert.False(editor.IsRouteIdEditable);
        Assert.False(editor.IsDirty);
        Assert.False(editor.CanSave);

        // 仅改 API 地址：保存后仅 baseURL 变化，其余字段与条目保持。
        editor.BaseUrlDraft = "https://relay2.example.com/v1";
        Assert.True(editor.IsDirty);
        Assert.True(editor.CanSave);
        editor.SaveCommand.Execute(null);

        Assert.Null(panel.Models.Editor);
        Assert.All(panel.Models.Providers, candidate => Assert.False(candidate.IsEditing));
        var profile = DescribeNamespace(settings, "llm-pi-ai").Value.GetProperty("providers")
                                                              .GetProperty("glm");
        Assert.Equal("https://relay2.example.com/v1", profile.GetProperty("baseURL").GetString());
        Assert.Equal("GLM", profile.GetProperty("displayName").GetString());
        Assert.Equal("GLM_API_KEY", profile.GetProperty("apiKeyEnv").GetString());
        Assert.Equal(3, profile.GetProperty("models").GetArrayLength());
    }

    [Fact]
    public void EmptyInputArrayFallsBackToTextAndDoesNotBlockSave()
    {
        // 合并层会把「无 input」规范成空数组（生效解析值）。官方 ModelInputTypes 视空数组为
        // 未声明（缺省勾 text），本端不得打开编辑即报「输入类型至少勾选一项」锁死保存。
        var json = JsonDocument.Parse("""
            {"providers":{"glm":{"displayName":"智谱","apiKeyEnv":"GLM_API_KEY","api":"openai-completions",
              "baseURL":"https://api.iruidong.com/v1",
              "models":[{"id":"glm-5.3","name":"glm-5.3","input":[]},
                        {"id":"glm-5.3-flash","name":"glm-5.3-flash","input":["text","image"]}]}}}
            """);
        var view = new SettingsNamespaceView("llm-pi-ai", false, default, json.RootElement.Clone(), "profile", 1,
                                             Secrets:
                                             [new SettingsSecretInfo(["providers", "glm", "apiKeyEnv"], true)]);
        var editor = ProviderEditorViewModel.ForEdit(null!, null, view, "glm", null, _ => { });

        var glm53 = editor.ModelEntries.Single(entry => entry.IdDraft == "glm-5.3");
        Assert.True(glm53.IsTextSelected);
        Assert.False(glm53.IsImageSelected);
        Assert.Null(editor.FirstValidationError);
    }

    [Fact]
    public async Task DiscoverFillsDraftEntriesFromCatalogService()
    {
        var settings = new SimulatedSettingsService();
        var panel    = CreatePanel(settings, catalogService : new SimulatedLlmCatalogService());
        await panel.OpenAsync();

        panel.Models.OpenAddCommand.Execute(null);
        var editor = panel.Models.Editor!;
        editor.ProviderOptions.Single(option => option.Label == "mistral").SelectCommand.Execute(null);

        Assert.True(editor.CanDiscover);
        editor.DiscoverCommand.Execute(null);
        Assert.True(editor.IsDiscoverPopupOpen);
        var option = editor.DiscoverOptions.Single();
        Assert.Equal("Mistral Large", option.Label);

        editor.AdoptDiscoveredCommand.Execute(null);
        Assert.False(editor.IsDiscoverPopupOpen);
        Assert.Single(editor.ModelEntries);
        Assert.Equal("mistral-large-latest", editor.ModelEntries[0].IdDraft);
        Assert.Equal("128K", editor.ModelEntries[0].ContextWindowDraft);
        Assert.True(editor.IsDirty);
        // 采纳只改草稿：设置文档中 mistral 路由尚未写入。
        Assert.False(DescribeNamespace(settings, "llm-pi-ai").Value.GetProperty("providers")
                                                             .TryGetProperty("mistral", out _));
    }

    [Fact]
    public async Task DiscoverFailureSurfacesInlineError()
    {
        var panel = CreatePanel(catalogService : new SimulatedLlmCatalogService());
        await panel.OpenAsync();

        // 未知厂商且无端点：模拟服务按上游 DISCOVERY_FAILED 语义拒绝，行内呈现不弹层。
        panel.Models.OpenAddCommand.Execute(null);
        var editor = panel.Models.Editor!;
        editor.ProviderOptions.Single(option => option.Label == "groq").SelectCommand.Execute(null);
        editor.DiscoverCommand.Execute(null);
        Assert.False(editor.IsDiscoverPopupOpen);
        Assert.True(editor.HasDiscoverError);
    }

    [Fact]
    public async Task DeclaredEditProbeCarriesRouteSoStoredCredentialApplies()
    {
        // 官方编辑卡探测恒带路由 id：后端可从适配器 registry 应答（含已存凭据），
        // 编辑已配置供应商无需重输密钥；裸端点探测会被后端要求密钥。
        var catalog = new RecordingCatalogService();
        var panel   = CreatePanel(catalogService : catalog);
        await panel.OpenAsync();

        var glm = panel.Models.Providers.Single(row => row.ProviderId == "glm");
        glm.EditCommand.Execute(null);
        var editor = panel.Models.Editor!;
        Assert.True(editor.CanDiscover);

        editor.DiscoverCommand.Execute(null);
        Assert.Equal("glm", catalog.LastRequest!.Provider);
    }

    /// <summary>目录服务 Fake：记录发现请求参数（目录最小化为两条行路由）。</summary>
    private sealed class RecordingCatalogService : ILlmCatalogService
    {
        public LlmDiscoveryRequest? LastRequest { get; private set; }

        public Task<IReadOnlyList<LlmConfigurableProvider>> GetConfigurableProvidersAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<LlmConfigurableProvider>>(
            [
                new("deepseek-official", "DeepSeek", "llm-deepseek", []),
                new("glm", "GLM", "llm-pi-ai", ["providers", "glm"], true)
            ]);

        public Task<IReadOnlyList<LlmDiscoveredModel>> DiscoverModelsAsync(
            string settingsNs, LlmDiscoveryRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult<IReadOnlyList<LlmDiscoveredModel>>([]);
        }
    }
}
