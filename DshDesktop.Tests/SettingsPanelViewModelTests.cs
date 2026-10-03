using DshDesktop.Core.Models;
using DshDesktop.Core.Services;
using DshDesktop.Infrastructure.Services;
using DshDesktop.Utils;
using DshDesktop.ViewModels.Settings;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using System.Text.Json;

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

        ClassicAssert.IsTrue(condition(), "预期的异步 ViewModel 状态未在超时前出现。");
    }

    private static SettingsNamespaceView DescribeNamespace(ISettingsService service, string ns)
    {
        return service.DescribeAsync().GetAwaiter().GetResult().Namespaces.Single(view => view.Ns == ns);
    }

    [Test]
    public async Task OpenProjectsSectionsRows()
    {
        var panel = CreatePanel();
        await panel.OpenAsync();

        ClassicAssert.IsTrue(panel.IsReady);
        ClassicAssert.AreEqual(new[] { "general", "models", "plugins" }, panel.Sections.Select(section => section.Id));
        ClassicAssert.AreEqual(panel.General, panel.ActiveSection);

        // 通用行投影：主题 dark、遗留 label 映射、权限候选来自 schema.choices。
        var general = panel.General;
        ClassicAssert.IsTrue(general.AppearanceRow.IsVisible);
        ClassicAssert.IsTrue(general.AppearanceRow.IsDarkSelected);
        ClassicAssert.AreEqual("标准", general.TranscriptViewRow.CurrentLabel);
        ClassicAssert.AreEqual("简洁", general.PerformanceUsageRow.CurrentLabel);
        ClassicAssert.AreEqual("排队发送", general.BusyEnterRow.CurrentLabel);
        ClassicAssert.IsTrue(general.PermissionRow.IsVisible);
        ClassicAssert.AreEqual(2, general.PermissionRow.Options.Count);
        ClassicAssert.AreEqual("默认", general.PermissionRow.CurrentLabel);
        ClassicAssert.IsFalse(general.CodeWorkViewRow.IsChecked);
        ClassicAssert.IsTrue(general.SessionLogRow.IsChecked);

        // 模型卡与插件卡。
        ClassicAssert.IsNotNull(panel.Models.Card);
        ClassicAssert.AreEqual("DeepSeek", panel.Models.Card!.Title);
        ClassicAssert.IsTrue(panel.Models.Card.IsCredentialSet);
        ClassicAssert.AreEqual("https://api.deepseek.com", panel.Models.Card.BaseUrlDraft);
        ClassicAssert.AreEqual(2, panel.Models.Card.ModelEntries.Count);
        ClassicAssert.AreEqual(new[] { "终端", "Agent 循环", "子代理", "网页搜索" },
                               panel.Plugins.Cards.Select(card => card.Title));

        // 网页搜索密钥：凭据查询返回未配置（空凭据服务），圆点为空心灰。
        var webSearch   = panel.Plugins.Cards.Single(card => card.Ns == "web-search-deepseek");
        var apiKeyField = webSearch.Fields.Single(field => field.Key == "apiKeyEnv");
        ClassicAssert.IsTrue(apiKeyField.ShowCredentialDot);
        ClassicAssert.IsTrue(apiKeyField.IsCredentialUnset);
    }

    [Test]
    public async Task ProviderRowsProjectConfiguredRoutesWithCredentialDots()
    {
        // 行列表：已配置路由按官方排序（DeepSeek → 其余按目录声明序）；账户路由在会话目录
        // 无 deepseek-account 组时隐藏；显式命名 apiKeyEnv 且已配置的行画实心绿点。
        var panel = CreatePanel(catalogService : new SimulatedLlmCatalogService());
        await panel.OpenAsync();

        ClassicAssert.IsTrue(panel.IsReady);
        var rows = panel.Models.Providers;
        ClassicAssert.AreEqual(new[] { "deepseek-official", "glm" }, rows.Select(row => row.ProviderId).ToArray());
        ClassicAssert.AreEqual(new[] { "DeepSeek", "GLM" }, rows.Select(row => row.DisplayName).ToArray());
        Assert.That(rows.Any(row => row.ProviderId == "deepseek-account"), Is.False);
        foreach (var row in rows) ClassicAssert.IsTrue(row.ShowCredentialDot);
        foreach (var row in rows) ClassicAssert.IsTrue(row.IsCredentialSet);
        ClassicAssert.IsEmpty(rows[0].SettingsPath);
        ClassicAssert.AreEqual(new[] { "providers", "glm" }, rows[1].SettingsPath);
        ClassicAssert.AreEqual("llm-pi-ai", rows[1].SettingsNs);
        // llm-pi-ai 命名空间挂载 → 可添加；展开态默认关闭（无行编辑、无添加卡）。
        ClassicAssert.IsTrue(panel.Models.CanAdd);
        ClassicAssert.IsFalse(panel.Models.IsAddOpen);
        foreach (var row in rows) ClassicAssert.IsFalse(row.IsEditing);
    }

    [Test]
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
        ClassicAssert.AreEqual("DeepSeek 账号", account.DisplayName);
        // 账户路由无 apiKeyEnv 槽位（凭据由登录态承载）→ 不画圆点。
        ClassicAssert.IsFalse(account.ShowCredentialDot);
        // 账户行排在 DeepSeek 官方行之前（官方排序）。
        ClassicAssert.AreEqual("deepseek-account",
                               panel.Models.Providers.Select(row => row.ProviderId).First());
    }

    [Test]
    public async Task CatalogFailureKeepsRowsFromSettingsFallback()
    {
        // 目录查询失败：面板不进错误态；行从设置文档兜底（DeepSeek 整段路由 + pi-ai 路由）。
        var panel = CreatePanel(catalogService : new SimulatedLlmCatalogService(),
                                sessionService :
                                new FixedCatalogSessionService(null,
                                                               new InvalidOperationException("目录查询失败")));
        await panel.OpenAsync();

        ClassicAssert.IsTrue(panel.IsReady);
        ClassicAssert.AreEqual(new[] { "deepseek-official", "glm" },
                               panel.Models.Providers.Select(row => row.ProviderId).ToArray());
    }

    [Test]
    public async Task DirectoryFailureStillBuildsRowsFromSettings()
    {
        // 目录服务不可用（旧版后端）：行显示名回退（DeepSeek 固定名 / profile.displayName / 路由 id）。
        var panel = CreatePanel();
        await panel.OpenAsync();

        ClassicAssert.IsTrue(panel.IsReady);
        ClassicAssert.AreEqual(new[] { "deepseek-official", "glm" },
                               panel.Models.Providers.Select(row => row.ProviderId).ToArray());
        ClassicAssert.AreEqual("DeepSeek", panel.Models.Providers[0].DisplayName);
        ClassicAssert.AreEqual("GLM", panel.Models.Providers[1].DisplayName);
    }

    [Test]
    public async Task DeepSeekRowEditExpandsInlineAndCancelCollapses()
    {
        var panel = CreatePanel(catalogService : new SimulatedLlmCatalogService());
        await panel.OpenAsync();

        var row = panel.Models.Providers.Single(candidate => candidate.ProviderId == "deepseek-official");
        row.EditCommand.Execute(null);
        ClassicAssert.IsTrue(row.IsEditing);
        ClassicAssert.IsTrue(row.HasDeepSeekCard);
        ClassicAssert.AreSame(panel.Models.Card, row.DeepSeekCard);

        panel.Models.Card!.CancelCommand.Execute(null);
        ClassicAssert.IsFalse(row.IsEditing);
        ClassicAssert.IsNull(row.DeepSeekCard);
    }

    [Test]
    public async Task ProviderRowEditExpandsInlineOneRowAtATime()
    {
        var panel = CreatePanel(catalogService : new SimulatedLlmCatalogService());
        await panel.OpenAsync();

        // 点 pi-ai 路由行的编辑：仅该行行内展开（官方同构），DeepSeek 行保持折叠。
        var glm      = panel.Models.Providers.Single(candidate => candidate.ProviderId == "glm");
        var deepSeek = panel.Models.Providers.Single(candidate => candidate.ProviderId == "deepseek-official");
        glm.EditCommand.Execute(null);
        ClassicAssert.IsTrue(glm.IsEditing);
        ClassicAssert.IsTrue(glm.HasProviderCard);
        ClassicAssert.IsFalse(deepSeek.IsEditing);
        ClassicAssert.IsNull(deepSeek.DeepSeekCard);

        // 取消收起后改编辑 DeepSeek 行：仅 DeepSeek 行展开，互不叠显。
        glm.ProviderCard!.CancelCommand.Execute(null);
        ClassicAssert.IsFalse(glm.IsEditing);

        deepSeek.EditCommand.Execute(null);
        ClassicAssert.IsTrue(deepSeek.IsEditing);
        ClassicAssert.IsTrue(deepSeek.HasDeepSeekCard);
        ClassicAssert.IsNull(glm.ProviderCard);

        deepSeek.DeepSeekCard!.CancelCommand.Execute(null);
        ClassicAssert.IsFalse(deepSeek.IsEditing);
        ClassicAssert.IsNull(deepSeek.DeepSeekCard);
    }

    [Test]
    public async Task EditingRowToggleCollapsesAndAddCardIsExclusive()
    {
        var panel = CreatePanel(catalogService : new SimulatedLlmCatalogService());
        await panel.OpenAsync();

        // 再点同一行的编辑：收起（官方 toggle 语义）。
        var glm = panel.Models.Providers.Single(candidate => candidate.ProviderId == "glm");
        glm.EditCommand.Execute(null);
        ClassicAssert.IsTrue(glm.IsEditing);
        glm.EditCommand.Execute(null);
        ClassicAssert.IsFalse(glm.IsEditing);
        ClassicAssert.IsNull(panel.Models.Editor);

        // 行编辑展开时打开添加卡：行编辑收起，一次一卡。
        glm.EditCommand.Execute(null);
        ClassicAssert.IsTrue(glm.IsEditing);
        panel.Models.OpenAddCommand.Execute(null);
        ClassicAssert.IsFalse(glm.IsEditing);
        ClassicAssert.IsTrue(panel.Models.IsAddOpen);
        ClassicAssert.IsNotNull(panel.Models.Editor);

        // 添加卡取消：收起且不展开任何行。
        panel.Models.Editor!.CancelCommand.Execute(null);
        ClassicAssert.IsFalse(panel.Models.IsAddOpen);
        ClassicAssert.IsNull(panel.Models.Editor);
        foreach (var row in panel.Models.Providers) ClassicAssert.IsFalse(row.IsEditing);
    }

    [Test]
    public async Task ImmediateToggleWritesUserSegmentAndAdvancesRevision()
    {
        var settings = new SimulatedSettingsService();
        var panel    = CreatePanel(settings);
        await panel.OpenAsync();
        var revisionBefore = DescribeNamespace(settings, "ui-settings").Revision;

        panel.General.CodeWorkViewRow.IsChecked = true;

        var view = DescribeNamespace(settings, "ui-settings");
        ClassicAssert.IsTrue(view.User is { } user                        &&
                             user.TryGetProperty("enabled", out var flag) &&
                             flag.ValueKind == JsonValueKind.True);
        ClassicAssert.IsTrue(view.Value.GetProperty("enabled").GetBoolean());
        ClassicAssert.IsTrue(panel.General.CodeWorkViewRow.IsChecked);
        ClassicAssert.IsTrue(view.Revision > revisionBefore);
    }

    [Test]
    public async Task StaleRevisionConflictRollsBackToBackendTruthWithRowError()
    {
        var settings = new SimulatedSettingsService();
        var panel    = CreatePanel(settings);
        await panel.OpenAsync();

        // 卡片快照过期：直接写服务推进 revision 后再经面板写（值取 true，与面板打开时的 false 不同）。
        await settings.MutateAsync("ui-settings",
                                   [SettingsMutationOp.Set(["enabled"], JsonElementFactory.FromBoolean(true))]);
        panel.General.CodeWorkViewRow.IsChecked = true;

        ClassicAssert.IsTrue(panel.General.CodeWorkViewRow.IsChecked);
        ClassicAssert.IsTrue(panel.General.CodeWorkViewRow.HasError);
        ClassicAssert.AreEqual("这些设置已被其他地方改动，已恢复为最新值。",
                               panel.General.CodeWorkViewRow.ErrorText);
        // 冲突写未落库：revision 仍是外部推进后的值。
        ClassicAssert.AreEqual(1, DescribeNamespace(settings, "ui-settings").Revision);
    }

    [Test]
    public async Task PluginCardSaveWritesUserLayerAndSwitchSectionDiscardsDraft()
    {
        var settings = new SimulatedSettingsService();
        var panel    = CreatePanel(settings);
        await panel.OpenAsync();
        var terminal = panel.Plugins.Cards.Single(card => card.Title == "终端");
        var timeout  = terminal.Fields.Single(field => field.Key     == "timeoutMs");
        ClassicAssert.AreEqual("120000", timeout.DraftText);
        ClassicAssert.IsFalse(terminal.IsDirty);

        timeout.DraftText = "30000";
        ClassicAssert.IsTrue(terminal.IsDirty);
        ClassicAssert.IsTrue(terminal.CanSave);
        terminal.SaveCommand.Execute(null);

        var view = DescribeNamespace(settings, "pwsh-sandbox");
        ClassicAssert.IsTrue(view.User is { } user && user.GetProperty("timeoutMs").GetInt64() == 30000);
        ClassicAssert.IsTrue(view.Value.GetProperty("timeoutMs").GetInt64() == 30000);
        ClassicAssert.IsFalse(terminal.IsDirty);
        ClassicAssert.IsTrue(terminal.HasSaveSuccess);

        // 切换分区丢弃草稿：重新投影回保存后的生效值。
        timeout.DraftText = "99000";
        panel.SelectSection(panel.Models);
        panel.SelectSection(panel.Plugins);
        var terminalAfter = panel.Plugins.Cards.Single(card => card.Title              == "终端");
        ClassicAssert.AreEqual("30000", terminalAfter.Fields.Single(field => field.Key == "timeoutMs").DraftText);
        ClassicAssert.IsFalse(terminalAfter.IsDirty);
    }

    [Test]
    public async Task SavedFieldShowsOverrideMarkAndRestoreDefaultUnsets()
    {
        var settings = new SimulatedSettingsService();
        var panel    = CreatePanel(settings);
        await panel.OpenAsync();
        var terminal = panel.Plugins.Cards.Single(card => card.Title == "终端");
        var timeout  = terminal.Fields.Single(field => field.Key     == "timeoutMs");
        ClassicAssert.IsFalse(timeout.IsOverridden);

        timeout.DraftText = "30000";
        terminal.SaveCommand.Execute(null);
        ClassicAssert.IsTrue(timeout.IsOverridden);

        // 恢复默认：暂存 unset，保存后回到 base 层并清除覆盖标记。
        timeout.RestoreDefaultCommand.Execute(null);
        ClassicAssert.IsTrue(timeout.IsRestoreStaged);
        ClassicAssert.IsTrue(terminal.IsDirty);
        terminal.SaveCommand.Execute(null);

        var view = DescribeNamespace(settings, "pwsh-sandbox");
        ClassicAssert.IsFalse(view.User is { } user && user.TryGetProperty("timeoutMs", out _));
        ClassicAssert.AreEqual(120000, view.Value.GetProperty("timeoutMs").GetInt64());
        ClassicAssert.IsFalse(timeout.IsOverridden);
        ClassicAssert.IsFalse(terminal.IsDirty);
    }

    [Test]
    public async Task ModelCardSaveUpdatesNamespaceAndCredentialReference()
    {
        var settings    = new SimulatedSettingsService();
        var credentials = new SimulatedCredentialsService();
        var panel       = CreatePanel(settings, credentials);
        await panel.OpenAsync();
        var card = panel.Models.Card!;
        card.BaseUrlDraft = "https://example.com/anthropic";
        card.ApiKeyDraft  = "sk-test-value";
        ClassicAssert.IsTrue(card.IsDirty);
        ClassicAssert.IsTrue(card.CanSave);

        card.SaveCommand.Execute(null);

        var view = DescribeNamespace(settings, "llm-deepseek");
        ClassicAssert.AreEqual("https://example.com/anthropic", view.Value.GetProperty("baseURL").GetString());
        ClassicAssert.IsTrue(card.HasSaveSuccess);
        ClassicAssert.IsFalse(card.IsDirty);
        var statuses = await credentials.DescribeAsync(["DEEPSEEK_API_KEY"]);
        ClassicAssert.IsTrue(statuses["DEEPSEEK_API_KEY"].Configured);
    }

    [Test]
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

        ClassicAssert.IsTrue(card.HasSaveError);
        ClassicAssert.AreEqual("这张卡片打开期间，这些设置已被其他地方改动。请关闭后重新打开，在当前值上编辑。",
                               card.SaveError);
        ClassicAssert.AreEqual("https://mine.example", card.BaseUrlDraft);
    }

    [Test]
    public async Task ModelEntriesProjectModalitiesCollapseAndKmDisplay()
    {
        var settings = new SimulatedSettingsService();
        var panel    = CreatePanel(settings);
        await panel.OpenAsync();
        var card = panel.Models.Card!;
        ClassicAssert.AreEqual(2, card.ModelEntries.Count);

        // 条目默认折叠；flash 携带 text+image，v4-pro 仅 text；整百万缩写为 1M。
        var flash = card.ModelEntries[0];
        ClassicAssert.AreEqual("deepseek-flash", flash.IdDraft);
        ClassicAssert.IsFalse(flash.IsExpanded);
        ClassicAssert.IsTrue(flash.IsTextSelected);
        ClassicAssert.IsTrue(flash.IsImageSelected);
        ClassicAssert.AreEqual("1M", flash.ContextWindowDraft);
        var pro = card.ModelEntries[1];
        ClassicAssert.IsTrue(pro.IsTextSelected);
        ClassicAssert.IsFalse(pro.IsImageSelected);
        ClassicAssert.AreEqual("1M", pro.ContextWindowDraft);
        ClassicAssert.AreEqual(string.Empty, pro.MaxTokensDraft);

        flash.ToggleExpandCommand.Execute(null);
        ClassicAssert.IsTrue(flash.IsExpanded);
        flash.ToggleExpandCommand.Execute(null);
        ClassicAssert.IsFalse(flash.IsExpanded);
    }

    [Test]
    public async Task ModelCardTokenDraftsValidateKmSuffixAndBlank()
    {
        var settings = new SimulatedSettingsService();
        var panel    = CreatePanel(settings);
        await panel.OpenAsync();
        var card = panel.Models.Card!;
        var pro  = card.ModelEntries[1];

        // K/M 后缀合法；小数/任意文本/负数不合法；留空合法。
        pro.MaxTokensDraft = "256K";
        ClassicAssert.IsFalse(card.HasValidationError);
        pro.MaxTokensDraft = "1.5M";
        ClassicAssert.IsTrue(card.HasValidationError);
        ClassicAssert.AreEqual("最大输出 token 数必须是正整数（可带 K/M 后缀）或留空。", card.FirstValidationError);
        pro.MaxTokensDraft = "abc";
        ClassicAssert.IsTrue(card.HasValidationError);
        pro.MaxTokensDraft = "-5";
        ClassicAssert.IsTrue(card.HasValidationError);
        pro.MaxTokensDraft = string.Empty;
        ClassicAssert.IsFalse(card.HasValidationError);
        pro.ContextWindowDraft = "1M";
        ClassicAssert.IsFalse(card.HasValidationError);
    }

    [Test]
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
        ClassicAssert.IsTrue(card.IsDirty);
        ClassicAssert.IsTrue(card.CanSave);
        card.SaveCommand.Execute(null);

        var models = DescribeNamespace(settings, "llm-deepseek").Value.GetProperty("models");
        var saved  = models[1];
        ClassicAssert.AreEqual(64000, saved.GetProperty("maxTokens").GetInt64());
        ClassicAssert
           .AreEqual("Stronger agentic coding, knowledge, and difficult reasoning; suited to complex or quality-critical tasks at higher cost.",
                     saved.GetProperty("description").GetString());
        ClassicAssert.IsFalse(saved.TryGetProperty("inputModalities", out _));
        ClassicAssert.IsFalse(card.IsDirty);

        // 保存重投影后勾选图片：写入 inputModalities（text+image）。
        pro = card.ModelEntries[1];
        ClassicAssert.AreEqual("64K", pro.MaxTokensDraft);
        pro.IsImageSelected = true;
        ClassicAssert.IsTrue(card.IsDirty);
        card.SaveCommand.Execute(null);

        saved = DescribeNamespace(settings, "llm-deepseek").Value.GetProperty("models")[1];
        ClassicAssert.AreEqual(new[] { "text", "image" },
                               saved.GetProperty("inputModalities").EnumerateArray()
                                    .Select(value => value.GetString()).ToArray());
    }

    [Test]
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
        ClassicAssert.IsTrue(card.CanSave);
        card.SaveCommand.Execute(null);

        var saved = DescribeNamespace(settings, "llm-deepseek").Value.GetProperty("models")[0];
        ClassicAssert.AreEqual(new[] { "text" },
                               saved.GetProperty("inputModalities").EnumerateArray()
                                    .Select(value => value.GetString()).ToArray());
        ClassicAssert.IsFalse(saved.TryGetProperty("imageMaxBytes", out _));
    }

    [Test]
    public async Task ModelEntryExpansionSurvivesSaveReprojection()
    {
        var settings = new SimulatedSettingsService();
        var panel    = CreatePanel(settings);
        await panel.OpenAsync();
        var card = panel.Models.Card!;
        card.ModelEntries[1].ToggleExpandCommand.Execute(null);
        ClassicAssert.IsTrue(card.ModelEntries[1].IsExpanded);

        card.ModelEntries[1].MaxTokensDraft = "64K";
        card.SaveCommand.Execute(null);

        // 保存重投影按 ID 保持展开状态。
        ClassicAssert.AreEqual(2, card.ModelEntries.Count);
        ClassicAssert.IsTrue(card.ModelEntries[1].IsExpanded);
    }

    [Test]
    public async Task ModelEntryDeleteClearsStaleValidation()
    {
        var settings = new SimulatedSettingsService();
        var panel    = CreatePanel(settings);
        await panel.OpenAsync();
        var card = panel.Models.Card!;

        // 新增空条目立即报 ID 必填；删除该条目后校验与脏标记同步消除。
        card.AddModelCommand.Execute(null);
        ClassicAssert.IsTrue(card.HasValidationError);
        ClassicAssert.AreEqual("模型 ID 不能为空。", card.FirstValidationError);

        card.ModelEntries.Last().DeleteCommand!.Execute(null);
        ClassicAssert.IsFalse(card.HasValidationError);
        ClassicAssert.IsFalse(card.IsDirty);
        ClassicAssert.AreEqual(2, card.ModelEntries.Count);
    }

    [Test]
    public async Task ThemePreferenceWriteInvokesCallbackWithLight()
    {
        var themeLog = new List<string?>();
        var settings = new SimulatedSettingsService();
        var panel    = CreatePanel(settings, themeLog : themeLog);
        await panel.OpenAsync();

        // 打开投影即应用主题（种子 dark），切浅色后回调收到 light 且存储同步。
        ClassicAssert.AreEqual("dark", themeLog.LastOrDefault());
        panel.General.AppearanceRow.SelectLightCommand.Execute(null);
        ClassicAssert.AreEqual("light", themeLog.Last());
        ClassicAssert.AreEqual("light",
                               DescribeNamespace(settings, "ui-theme").Value.GetProperty("preference").GetString());
    }

    [Test]
    public async Task MissingNamespacesHideCorrespondingRowsAndCards()
    {
        var settings = new SimulatedSettingsService();
        var filtered = new FilteredSettingsService(settings, "subagent", "ui-conversation");
        var panel    = CreatePanel(service : filtered);
        await panel.OpenAsync();

        ClassicAssert.IsFalse(panel.General.BusyEnterRow.IsVisible);
        ClassicAssert.IsTrue(panel.General.TranscriptViewRow.IsVisible);
        Assert.That(panel.Plugins.Cards.Any(card => card.Ns == "subagent"), Is.False);
        ClassicAssert.AreEqual(new[] { "终端", "Agent 循环", "网页搜索" }, panel.Plugins.Cards.Select(card => card.Title));
    }

    [Test]
    public async Task DocumentUpdatedExternalChangeReprojectsGeneralRows()
    {
        var settings = new SimulatedSettingsService();
        var panel    = CreatePanel(settings);
        await panel.OpenAsync();
        ClassicAssert.AreEqual("标准", panel.General.TranscriptViewRow.CurrentLabel);

        var view = await settings.MutateAsync("ui-chat",
        [
            SettingsMutationOp.Set(["transcriptView"],
                                   JsonElementFactory.FromString("detailed"))
        ]);
        panel.HandleDocumentUpdated(new SettingsDocumentUpdate("ui-chat", view.Revision));
        await WaitUntilAsync(() => panel.General.TranscriptViewRow.CurrentLabel == "详细");
    }

    [Test]
    public async Task PermissionFullAccessRequiresAcknowledgementAndCancelReverts()
    {
        var settings = new SimulatedSettingsService();
        var panel    = CreatePanel(settings);
        await panel.OpenAsync();
        var permission = panel.General.PermissionRow;
        var fullAccess = permission.Options.Single(option => option.Value == "full-access");

        // 点选完全权限：不直接写，先弹风险确认；取消则回到原值。
        fullAccess.SelectCommand!.Execute(null);
        ClassicAssert.IsTrue(permission.IsConfirmOpen);
        permission.CancelConfirmCommand.Execute(null);
        ClassicAssert.IsFalse(permission.IsConfirmOpen);
        ClassicAssert.AreEqual("default",
                               DescribeNamespace(settings, "permission").Value.GetProperty("defaultPreset")
                                                                        .GetString());

        // 必勾确认后允许：写入 full-access。
        fullAccess.SelectCommand!.Execute(null);
        permission.IsAcknowledged = true;
        permission.ConfirmSelectionCommand.Execute(null);
        ClassicAssert.AreEqual("full-access",
                               DescribeNamespace(settings, "permission").Value.GetProperty("defaultPreset")
                                                                        .GetString());
        ClassicAssert.AreEqual("完全权限", permission.CurrentLabel);
    }

    [Test]
    public async Task AddDeclaredProviderValidatesAndSavesProfileWithDerivedCredential()
    {
        var settings    = new SimulatedSettingsService();
        var credentials = new SimulatedCredentialsService();
        var panel       = CreatePanel(settings, credentials, catalogService : new SimulatedLlmCatalogService());
        await panel.OpenAsync();

        panel.Models.OpenAddCommand.Execute(null);
        var editor = panel.Models.Editor!;
        ClassicAssert.IsTrue(panel.Models.IsAddOpen);
        ClassicAssert.IsTrue(editor.IsAddMode);
        editor.ShowDeclaredTabCommand.Execute(null);
        editor = panel.Models.Editor!;
        ClassicAssert.IsFalse(editor.IsCatalogTabSelected);
        // 自定义创建 tab：字段直出无折叠头（官方同构，添加流程不走折叠）。
        ClassicAssert.IsTrue(editor.IsDeclaredCreateMode);
        ClassicAssert.IsFalse(editor.ShowsCustomHeader);
        ClassicAssert.IsTrue(editor.ShowsCatalogBlock);

        // 校验链：id 正则 → id 占用 → 地址必填 → ≥1 模型。
        editor.RouteIdDraft = "Acme";
        ClassicAssert.AreEqual("Provider ID 必须以小写字母开头，只能包含小写字母、数字与连字符。",
                               editor.FirstValidationError);
        editor.RouteIdDraft = "glm";
        ClassicAssert.AreEqual("该 Provider ID 已被其他提供商使用。", editor.FirstValidationError);
        editor.RouteIdDraft = "acme-gateway";
        ClassicAssert.AreEqual("API 地址必填，且需以 http:// 或 https:// 开头。", editor.FirstValidationError);
        editor.BaseUrlDraft = "https://gateway.example/v1";
        ClassicAssert.AreEqual("至少添加一个模型。", editor.FirstValidationError);
        ClassicAssert.IsFalse(editor.CanSave);

        editor.AddModelCommand.Execute(null);
        editor.ModelEntries[0].IdDraft = "acme-large";
        editor.ApiKeyDraft             = "sk-acme";
        ClassicAssert.IsNull(editor.FirstValidationError);
        ClassicAssert.IsTrue(editor.CanSave);
        editor.SaveCommand.Execute(null);

        var profile = DescribeNamespace(settings, "llm-pi-ai").Value.GetProperty("providers")
                                                              .GetProperty("acme-gateway");
        ClassicAssert.AreEqual("openai-completions", profile.GetProperty("api").GetString());
        ClassicAssert.AreEqual("https://gateway.example/v1", profile.GetProperty("baseURL").GetString());
        ClassicAssert.AreEqual("ACME_GATEWAY_API_KEY", profile.GetProperty("apiKeyEnv").GetString());
        ClassicAssert.AreEqual("acme-large", profile.GetProperty("models")[0].GetProperty("id").GetString());
        // 纯文本新条目不补写缺省 input 键（与基线零差异策略一致）。
        ClassicAssert.IsFalse(profile.GetProperty("models")[0].TryGetProperty("input", out _));
        var statuses = await credentials.DescribeAsync(["ACME_GATEWAY_API_KEY"]);
        ClassicAssert.IsTrue(statuses["ACME_GATEWAY_API_KEY"].Configured);
        // 保存成功收起添加卡，新行出现（目录无此路由 → 显示名回退路由 id）。
        ClassicAssert.IsNull(panel.Models.Editor);
        ClassicAssert.IsFalse(panel.Models.IsAddOpen);
        var row = panel.Models.Providers.Single(candidate => candidate.ProviderId == "acme-gateway");
        ClassicAssert.AreEqual("acme-gateway", row.DisplayName);
        ClassicAssert.IsTrue(row.ShowCredentialDot);
    }

    [Test]
    public async Task AddCatalogProviderSavesMinimalProfileAndCredential()
    {
        var settings    = new SimulatedSettingsService();
        var credentials = new SimulatedCredentialsService();
        var panel       = CreatePanel(settings, credentials, catalogService : new SimulatedLlmCatalogService());
        await panel.OpenAsync();

        // 第三方 tab：添加下拉仅含未配置的目录厂商（declared 的 glm 不在）。
        panel.Models.OpenAddCommand.Execute(null);
        var editor = panel.Models.Editor!;
        ClassicAssert.IsTrue(editor.IsCatalogTabSelected);
        Assert.That(editor.ProviderOptions.Any(option => option.Label == "glm"), Is.False);

        // 带密钥保存：最小 profile（仅 apiKeyEnv 引用），密钥写凭据域，行画绿点。
        editor.ProviderOptions.Single(option => option.Label == "mistral").SelectCommand.Execute(null);
        ClassicAssert.IsNull(editor.FirstValidationError);
        editor.ApiKeyDraft = "sk-mistral";
        editor.SaveCommand.Execute(null);

        var profile = DescribeNamespace(settings, "llm-pi-ai").Value.GetProperty("providers")
                                                              .GetProperty("mistral");
        ClassicAssert.AreEqual("MISTRAL_API_KEY", profile.GetProperty("apiKeyEnv").GetString());
        ClassicAssert.IsFalse(profile.TryGetProperty("baseURL", out _));
        var statuses = await credentials.DescribeAsync(["MISTRAL_API_KEY"]);
        ClassicAssert.IsTrue(statuses["MISTRAL_API_KEY"].Configured);
        ClassicAssert.IsFalse(panel.Models.IsAddOpen);
        ClassicAssert.IsTrue(panel.Models.Providers.Single(candidate => candidate.ProviderId == "mistral")
                                  .IsCredentialSet);

        // 无任何字段保存：物化空 profile（收养适配器默认），无显式引用不画圆点。
        panel.Models.OpenAddCommand.Execute(null);
        editor = panel.Models.Editor!;
        editor.ProviderOptions.Single(option => option.Label == "groq").SelectCommand.Execute(null);
        editor.SaveCommand.Execute(null);

        var empty = DescribeNamespace(settings, "llm-pi-ai").Value.GetProperty("providers")
                                                            .GetProperty("groq");
        ClassicAssert.AreEqual(JsonValueKind.Object, empty.ValueKind);
        ClassicAssert.IsFalse(empty.TryGetProperty("apiKeyEnv", out _));
        ClassicAssert.IsFalse(panel.Models.Providers.Single(candidate => candidate.ProviderId == "groq")
                                   .ShowCredentialDot);
    }

    [Test]
    public async Task EditDeclaredProviderSubmitsFieldLevelDiff()
    {
        var settings = new SimulatedSettingsService();
        var panel    = CreatePanel(settings, catalogService : new SimulatedLlmCatalogService());
        await panel.OpenAsync();

        var glmRow = panel.Models.Providers.Single(candidate => candidate.ProviderId == "glm");
        glmRow.EditCommand.Execute(null);
        var editor = panel.Models.Editor!;
        ClassicAssert.IsFalse(editor.IsAddMode);
        ClassicAssert.IsTrue(editor.IsDeclaredMode);
        // 编辑态默认折叠自定义设置（官方 details 同构）：仅密钥常显，折叠区收起。
        ClassicAssert.IsTrue(editor.ShowsCustomHeader);
        ClassicAssert.IsTrue(editor.IsCustomExpanded is false);
        ClassicAssert.IsFalse(editor.ShowsCatalogBlock);
        // 密钥占位双态：glm 已配置 → 提示可替换。
        ClassicAssert.AreEqual("已配置——输入新值可替换", editor.ApiKeyPlaceholder);
        var notified = new List<string>();
        editor.PropertyChanged += (_, e) => notified.Add(e.PropertyName);
        editor.ToggleCustomCommand.Execute(null);
        ClassicAssert.IsTrue(editor.ShowsCatalogBlock);
        // 计算属性须随展开态发通知，否则绑定不重算、折叠区展开后仍不可见。
        Assert.That(notified, Does.Contain("ShowsCatalogBlock"));
        // 编辑态预填：显示名/协议/模型条目来自 profile，Provider ID 只读展示路由 id。
        ClassicAssert.AreEqual("GLM", editor.DisplayNameDraft);
        ClassicAssert.AreEqual("openai-completions", editor.ApiDraft);
        ClassicAssert.AreEqual(3, editor.ModelEntries.Count);
        ClassicAssert.AreEqual("glm", editor.RouteIdDraft);
        ClassicAssert.IsFalse(editor.IsRouteIdEditable);
        ClassicAssert.IsFalse(editor.IsDirty);
        ClassicAssert.IsFalse(editor.CanSave);

        // 仅改 API 地址：保存后仅 baseURL 变化，其余字段与条目保持。
        editor.BaseUrlDraft = "https://relay2.example.com/v1";
        ClassicAssert.IsTrue(editor.IsDirty);
        ClassicAssert.IsTrue(editor.CanSave);
        editor.SaveCommand.Execute(null);

        ClassicAssert.IsNull(panel.Models.Editor);
        foreach (var candidate in panel.Models.Providers) ClassicAssert.IsFalse(candidate.IsEditing);
        var profile = DescribeNamespace(settings, "llm-pi-ai").Value.GetProperty("providers")
                                                              .GetProperty("glm");
        ClassicAssert.AreEqual("https://relay2.example.com/v1", profile.GetProperty("baseURL").GetString());
        ClassicAssert.AreEqual("GLM", profile.GetProperty("displayName").GetString());
        ClassicAssert.AreEqual("GLM_API_KEY", profile.GetProperty("apiKeyEnv").GetString());
        ClassicAssert.AreEqual(3, profile.GetProperty("models").GetArrayLength());
    }

    [Test]
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
                                             Secrets :
                                             [new SettingsSecretInfo(["providers", "glm", "apiKeyEnv"], true)]);
        var editor = ProviderEditorViewModel.ForEdit(null!, null, view, "glm", null, _ => { });

        var glm53 = editor.ModelEntries.Single(entry => entry.IdDraft == "glm-5.3");
        ClassicAssert.IsTrue(glm53.IsTextSelected);
        ClassicAssert.IsFalse(glm53.IsImageSelected);
        ClassicAssert.IsNull(editor.FirstValidationError);
    }

    [Test]
    public async Task DiscoverFillsDraftEntriesFromCatalogService()
    {
        var settings = new SimulatedSettingsService();
        var panel    = CreatePanel(settings, catalogService : new SimulatedLlmCatalogService());
        await panel.OpenAsync();

        panel.Models.OpenAddCommand.Execute(null);
        var editor = panel.Models.Editor!;
        editor.ProviderOptions.Single(option => option.Label == "mistral").SelectCommand.Execute(null);

        ClassicAssert.IsTrue(editor.CanDiscover);
        editor.DiscoverCommand.Execute(null);
        ClassicAssert.IsTrue(editor.IsDiscoverPopupOpen);
        var option = editor.DiscoverOptions.Single();
        ClassicAssert.AreEqual("Mistral Large", option.Label);

        editor.AdoptDiscoveredCommand.Execute(null);
        ClassicAssert.IsFalse(editor.IsDiscoverPopupOpen);
        Assert.That(editor.ModelEntries, Has.Count.EqualTo(1));
        ClassicAssert.AreEqual("mistral-large-latest", editor.ModelEntries[0].IdDraft);
        ClassicAssert.AreEqual("128K", editor.ModelEntries[0].ContextWindowDraft);
        ClassicAssert.IsTrue(editor.IsDirty);
        // 采纳只改草稿：设置文档中 mistral 路由尚未写入。
        ClassicAssert.IsFalse(DescribeNamespace(settings, "llm-pi-ai").Value.GetProperty("providers")
                                                                      .TryGetProperty("mistral", out _));
    }

    [Test]
    public async Task DiscoverFailureSurfacesInlineError()
    {
        var panel = CreatePanel(catalogService : new SimulatedLlmCatalogService());
        await panel.OpenAsync();

        // 未知厂商且无端点：模拟服务按上游 DISCOVERY_FAILED 语义拒绝，行内呈现不弹层。
        panel.Models.OpenAddCommand.Execute(null);
        var editor = panel.Models.Editor!;
        editor.ProviderOptions.Single(option => option.Label == "groq").SelectCommand.Execute(null);
        editor.DiscoverCommand.Execute(null);
        ClassicAssert.IsFalse(editor.IsDiscoverPopupOpen);
        ClassicAssert.IsTrue(editor.HasDiscoverError);
    }

    [Test]
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
        ClassicAssert.IsTrue(editor.CanDiscover);

        editor.DiscoverCommand.Execute(null);
        ClassicAssert.AreEqual("glm", catalog.LastRequest!.Provider);
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

    /// <summary>
    ///     固定返回给定目录（或注入失败）的会话服务替身；设置面板仅消费目录查询，
    ///     其余成员显式抛出，新增消费时须补桩。
    /// </summary>
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

    /// <summary>目录服务 Fake：记录发现请求参数（目录最小化为两条行路由）。</summary>
    private sealed class RecordingCatalogService : ILlmCatalogService
    {
        public LlmDiscoveryRequest? LastRequest { get; private set; }

        public Task<IReadOnlyList<LlmConfigurableProvider>> GetConfigurableProvidersAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LlmConfigurableProvider>>(
            [
                new LlmConfigurableProvider("deepseek-official", "DeepSeek", "llm-deepseek", []),
                new LlmConfigurableProvider("glm", "GLM", "llm-pi-ai", ["providers", "glm"], true)
            ]);
        }

        public Task<IReadOnlyList<LlmDiscoveredModel>> DiscoverModelsAsync(
            string settingsNs, LlmDiscoveryRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult<IReadOnlyList<LlmDiscoveredModel>>([]);
        }
    }
}
