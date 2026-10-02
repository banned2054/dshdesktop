using DshDesktop.Core.Exceptions;
using DshDesktop.Core.Models;
using DshDesktop.Core.Services;
using DshDesktop.Utils;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows.Input;

namespace DshDesktop.ViewModels.Settings;

/// <summary>模型/插件卡的服务调用入口：面板壳承担 RPC 与 revision 记账，卡负责草稿与校验。</summary>
public interface ISettingsMutationRunner
{
    /// <summary>按乐观锁提交路径操作；成功返回写后最新脱敏视图。</summary>
    Task<SettingsNamespaceView> MutateAsync(string ns, IReadOnlyList<SettingsMutationOp> ops, long expectedRevision);

    /// <summary>写入凭据值（不可读取回）。</summary>
    Task SetCredentialAsync(string reference, string value);
}

/// <summary>模型分区：提供方行列表（对齐官方圆角行卡）+ 添加卡（目录/自定义双入口）+
/// 编辑卡。DeepSeek 官方编辑器由 <see cref="DeepSeekModelCardViewModel" /> 承担；pi-ai 路由
/// （llm-pi-ai ns 的 providers.&lt;route&gt;）由 <see cref="ProviderEditorViewModel" /> 承担。
/// 另有当前生效模型目录的只读展示与账户路由可见性数据源。</summary>
public sealed class ModelsSettingsSectionViewModel : SettingsSectionViewModel
{
    public const string DeepSeekNs = "llm-deepseek";
    public const string PiAiNs     = "llm-pi-ai";

    private static readonly Dictionary<string, SettingsNamespaceView> EmptyNamespaces = new();

    private readonly ISettingsMutationRunner _runner;

    private DeepSeekModelCardViewModel? _card;
    private ProviderEditorViewModel?    _editor;

    private bool _editorOpen;
    private bool _canEdit = true;
    private bool _canAdd;

    // 行投影输入缓存：目录与账户可用性晚于 describe 到达（异步），到达后按缓存重建行。
    private IReadOnlyDictionary<string, SettingsNamespaceView> _lastNamespaces = EmptyNamespaces;
    private IReadOnlyList<LlmConfigurableProvider>?            _lastDirectory;

    private bool _accountAvailable;

    public ModelsSettingsSectionViewModel(ISettingsMutationRunner runner)
        : base("models", "模型", "填入各提供商的 API 密钥即可使用其模型。")
    {
        _runner        = runner;
        OpenAddCommand = new RelayCommand(OpenAdd);
    }

    /// <summary>已配置提供方行（圆角行卡）：DeepSeek 官方/账户路由与 pi-ai 路由统一呈现。</summary>
    public ObservableCollection<ProviderRowViewModel> Providers { get; } = [];

    public bool HasProviders => Providers.Count > 0;

    /// <summary>「添加模型提供商」可用性：llm-pi-ai 命名空间挂载（写入目标存在）且分区可编辑。</summary>
    public bool CanAdd
    {
        get => _canAdd;
        private set => SetProperty(ref _canAdd, value);
    }

    /// <summary>内容区视图：列表（行卡 + 添加入口）或编辑器（添加卡 / DeepSeek 编辑卡）。</summary>
    public bool IsListView => !_editorOpen;

    public bool IsEditorOpen => _editorOpen;

    public bool IsEditingDeepSeek => _editorOpen && Card is not null;

    public bool IsEditingProvider => _editorOpen && Editor is not null;

    /// <summary>DeepSeek 官方编辑器；ns 不存在时为 null。常驻投影，编辑态由 IsEditingDeepSeek 驱动。</summary>
    public DeepSeekModelCardViewModel? Card
    {
        get => _card;
        private set
        {
            if (!SetProperty(ref _card, value)) return;
            OnPropertyChanged(nameof(HasCard));
            OnPropertyChanged(nameof(IsEditingDeepSeek));
        }
    }

    public bool HasCard => Card is not null;

    /// <summary>pi-ai 路由的添加/编辑卡；仅编辑态非空（视图切换即丢弃）。</summary>
    public ProviderEditorViewModel? Editor
    {
        get => _editor;
        private set
        {
            if (!SetProperty(ref _editor, value)) return;
            OnPropertyChanged(nameof(HasEditor));
            OnPropertyChanged(nameof(IsEditingProvider));
        }
    }

    public bool HasEditor => Editor is not null;

    public RelayCommand OpenAddCommand { get; }

    /// <summary>全量投影：重建 DeepSeek 卡与行集合；编辑器丢弃回列表（打开面板/切换分区/外部全量刷新）。
    /// 账户行可见性（accountAvailable）由 <see cref="SetCatalog" /> 随会话目录异步刷新。</summary>
    internal void Project(IReadOnlyDictionary<string, SettingsNamespaceView> namespaces,
                          IReadOnlyList<LlmConfigurableProvider>?            directory)
    {
        _lastNamespaces = namespaces;
        _lastDirectory  = directory;

        Card = namespaces.TryGetValue(DeepSeekNs, out var view) ? new DeepSeekModelCardViewModel(_runner, view) : null;
        if (Card is not null)
        {
            Card.EditFinished += OnCardEditFinished;
            Card.CanEdit      =  _canEdit;
        }

        Editor = null;
        SetEditorOpen(false);
        RebuildRows();
    }

    /// <summary>外部改动重投影：卡与编辑器仅更新基线（草稿不动），行集合按新值重建（圆点/新增行）。</summary>
    internal void Rebase(IReadOnlyDictionary<string, SettingsNamespaceView> namespaces)
    {
        _lastNamespaces = namespaces;
        if (namespaces.TryGetValue(DeepSeekNs, out var view)) Card?.Rebase(view);
        if (namespaces.TryGetValue(PiAiNs, out var piAi)) Editor?.Rebase(piAi);
        RebuildRows();
    }

    /// <summary>会话模型目录到达：刷新 deepseek-account 行可见性（官方以目录组非空判定账户可用）。</summary>
    internal void SetCatalog(ModelCatalog? catalog)
    {
        _accountAvailable = catalog?.Groups.Any(group => group.Id == "deepseek-account" && group.Models.Count > 0)
                         == true;
        RebuildRows();
    }

    internal void SetCanEdit(bool value)
    {
        _canEdit = value;
        foreach (var row in Providers) row.CanEdit = value;
        Card?.CanEdit = value;
        CanAdd        = CanAdd && value;
    }

    private void OnCardEditFinished(object? sender, EventArgs e)
    {
        SetEditorOpen(false);
    }

    /// <summary>行集合：目录条目中已配置者（整段路由=ns 挂载；pi-ai 路由=profile 存在）按官方排序
    /// （账户 → DeepSeek → 其余按目录声明序）⊕ 设置文档中存在但目录缺失的路由兜底行 ⊕
    /// 目录不可用时从设置文档兜底（DeepSeek 与 pi-ai 路由）。</summary>
    private void RebuildRows()
    {
        Providers.Clear();
        var map       = _lastNamespaces;
        var directory = _lastDirectory;

        // 目录不可用（查询失败/后端未提供）：DeepSeek 官方路由从设置文档兜底呈现（显示名固定）。
        if (directory is null && map.TryGetValue(DeepSeekNs, out var deepSeekView))
            AddProviderRow(CreateRow("deepseek-official", DeepSeekNs, [], "DeepSeek", deepSeekView));

        if (directory != null)
        {
            foreach (var entry in OrderForDisplay(directory))
            {
                if (entry.Provider == "deepseek-account" && !_accountAvailable) continue;
                if (!IsConfigured(entry, map)) continue;

                // 账户行显示名官方在渲染层强制覆盖（zh：「DeepSeek 账号」）。
                var displayName = entry.Provider == "deepseek-account" ? "DeepSeek 账号" : entry.DisplayName;
                var nsView      = map.GetValueOrDefault(entry.SettingsNs);
                AddProviderRow(CreateRow(entry.Provider, entry.SettingsNs, entry.SettingsPath, displayName,
                                         nsView));
            }
        }

        // 设置文档中存在但目录未收录的路由（后端版本差异兜底）：显示名回退 profile.displayName。
        if (map.TryGetValue(PiAiNs, out var piAi) &&
            SettingsValues.GetNode(piAi.Value, ["providers"]) is { ValueKind: JsonValueKind.Object } providers)
        {
            var listed = Providers.Select(row => row.ProviderId).ToHashSet(StringComparer.Ordinal);
            foreach (var route in providers.EnumerateObject().Where(route => !listed.Contains(route.Name)))
            {
                var displayName = SettingsValues.GetString(route.Value, ["displayName"]) is { Length: > 0 } name
                    ? name
                    : route.Name;
                AddProviderRow(CreateRow(route.Name, PiAiNs, ["providers", route.Name], displayName, piAi));
            }
        }

        CanAdd = map.ContainsKey(PiAiNs);
        OnPropertyChanged(nameof(HasProviders));
    }

    /// <summary>装配行编辑请求（事件在行重建时随实例重新挂接）。</summary>
    private void AddProviderRow(ProviderRowViewModel row)
    {
        row.EditRequested += OnRowEditRequested;
        Providers.Add(row);
    }

    private void OnRowEditRequested(object? sender, EventArgs e)
    {
        EditRow((ProviderRowViewModel)sender!);
    }

    private static IEnumerable<LlmConfigurableProvider> OrderForDisplay(IReadOnlyList<LlmConfigurableProvider> entries)
    {
        return entries.Select((entry, index) => (Entry : entry, Index : index))
                      .OrderBy(pair => pair.Entry.Provider switch
                       {
                           "deepseek-account"  => 0,
                           "deepseek-official" => 1,
                           _                   => 2
                       })
                      .ThenBy(pair => pair.Index)
                      .Select(pair => pair.Entry);
    }

    private static bool IsConfigured(LlmConfigurableProvider                            entry,
                                     IReadOnlyDictionary<string, SettingsNamespaceView> map)
    {
        if (!map.TryGetValue(entry.SettingsNs, out var view)) return false;
        return entry.SettingsPath.Count == 0 || SettingsValues.HasPath(view.Value, entry.SettingsPath);
    }

    private static ProviderRowViewModel CreateRow(string                 providerId,   string settingsNs,
                                                  IReadOnlyList<string>  settingsPath, string displayName,
                                                  SettingsNamespaceView? view)
    {
        var credentialSet = view?.Secrets?.FirstOrDefault(secret => secret.Path.SequenceEqual(
                                                           settingsPath.Append("apiKeyEnv")))?.Set;
        return new ProviderRowViewModel(providerId, settingsNs, settingsPath, displayName, credentialSet);
    }

    private void OpenAdd()
    {
        if (!_canEdit || !CanAdd) return;

        SwitchAddEditor(ProviderEditorMode.CreateCatalog);
    }

    private void EditRow(ProviderRowViewModel row)
    {
        if (!_canEdit || !row.CanEdit) return;

        if (row.SettingsNs == DeepSeekNs)
        {
            SetEditorOpen(true);
            return;
        }

        if (row.SettingsNs != PiAiNs ||
            !_lastNamespaces.TryGetValue(PiAiNs, out var piAi))
            return;

        var entry = _lastDirectory?.FirstOrDefault(candidate =>
                                                       candidate.SettingsNs == PiAiNs &&
                                                       candidate.Provider   == row.ProviderId);
        SwitchEditor(ProviderEditorViewModel.ForEdit(_runner, _catalogService, piAi, row.ProviderId, entry,
                                                     OnEditorSaved));
    }

    /// <summary>编辑器完成回调：携带写后视图刷新行缓存并回列表。</summary>
    private void OnEditorSaved(SettingsNamespaceView view)
    {
        _lastNamespaces = ReplaceNamespace(_lastNamespaces, view);
        RebuildRows();
        Editor = null;
        SetEditorOpen(false);
    }

    /// <summary>添加卡目录/自定义 tab 切换：丢弃当前表单重建目标模式的编辑器。</summary>
    private void SwitchAddEditor(ProviderEditorMode mode)
    {
        if (!_lastNamespaces.TryGetValue(PiAiNs, out var piAi)) return;

        SwitchEditor(ProviderEditorViewModel.ForCreate(_runner, _catalogService, piAi, mode,
                                                       CollectAddable(), OnEditorSaved));
    }

    private void SwitchEditor(ProviderEditorViewModel editor)
    {
        editor.TabSwitchRequested += (_, mode) => SwitchAddEditor(mode);
        editor.Cancelled += (_, _) =>
        {
            Editor = null;
            SetEditorOpen(false);
        };
        Editor = editor;
        SetEditorOpen(true);
    }

    /// <summary>添加下拉候选：目录中 llm-pi-ai 命名空间挂载、未配置且非 declared 的条目。</summary>
    private List<LlmConfigurableProvider> CollectAddable()
    {
        if (_lastDirectory is not { } entries ||
            !_lastNamespaces.TryGetValue(PiAiNs, out var piAi))
            return [];

        return entries.Where(entry => entry.SettingsNs == PiAiNs &&
                                      entry.Declared   != true   &&
                                      (entry.SettingsPath.Count == 0 ||
                                       !SettingsValues.HasPath(piAi.Value, entry.SettingsPath)))
                      .ToList();
    }

    private static Dictionary<string, SettingsNamespaceView> ReplaceNamespace(
        IReadOnlyDictionary<string, SettingsNamespaceView> namespaces, SettingsNamespaceView view)
    {
        var map = new Dictionary<string, SettingsNamespaceView>(namespaces) { [view.Ns] = view };
        return map;
    }

    private void SetEditorOpen(bool value)
    {
        if (!SetProperty(ref _editorOpen, value)) return;

        OnPropertyChanged(nameof(IsListView));
        OnPropertyChanged(nameof(IsEditorOpen));
        OnPropertyChanged(nameof(IsEditingDeepSeek));
        OnPropertyChanged(nameof(IsEditingProvider));
    }

    /// <summary>目录服务由面板壳注入（测试环境可为 null，此时「获取可用模型」不可用）。</summary>
    private ILlmCatalogService? _catalogService;

    internal void SetCatalogService(ILlmCatalogService? catalogService)
    {
        _catalogService = catalogService;
    }
}

/// <summary>模型分区的一行提供方：圆角行卡（名称 + 凭据圆点 + 编辑按钮）。</summary>
public sealed class ProviderRowViewModel : ObservableObject
{
    private bool _canEdit;

    public ProviderRowViewModel(string providerId,  string settingsNs, IReadOnlyList<string> settingsPath,
                                string displayName, bool?  credentialSet)
    {
        ProviderId    = providerId;
        SettingsNs    = settingsNs;
        SettingsPath  = settingsPath;
        DisplayName   = displayName;
        CredentialSet = credentialSet;
        EditCommand   = new RelayCommand(() => EditRequested?.Invoke(this, EventArgs.Empty));
        _canEdit      = true;
    }

    /// <summary>路由 wire 标识（deepseek-official / deepseek-account / pi-ai 路由 id）。</summary>
    public string ProviderId { get; }

    public string SettingsNs { get; }

    public IReadOnlyList<string> SettingsPath { get; }

    /// <summary>行显示名：目录 displayName（deepseek-account 官方覆盖为「DeepSeek 账号」）、
    /// profile displayName 或路由 id 回退。</summary>
    public string DisplayName { get; }

    /// <summary>凭据槽位状态（apiKeyEnv 槽位 Set 与否）；null = 无显式命名引用，不画圆点（官方规则）。</summary>
    public bool? CredentialSet { get; }

    public bool ShowCredentialDot => CredentialSet.HasValue;

    public bool IsCredentialSet => CredentialSet == true;

    public bool IsCredentialUnset => CredentialSet == false;

    public RelayCommand EditCommand { get; }

    /// <summary>行编辑请求（由分区订阅编排编辑器打开）。</summary>
    public event EventHandler? EditRequested;

    public bool CanEdit
    {
        get => _canEdit;
        internal set => SetProperty(ref _canEdit, value);
    }
}

/// <summary>DeepSeek 模型卡：API 密钥（单向）、接口地址与模型目录（数组整写/恢复默认）。</summary>
public sealed class DeepSeekModelCardViewModel : ObservableObject
{
    private const string DefaultCredentialReference = "DEEPSEEK_API_KEY";

    private readonly ISettingsMutationRunner _runner;
    private          SettingsNamespaceView   _snapshot;
    private          long                    _baselineRevision;
    private          string                  _baselineBaseUrl = string.Empty;
    private          JsonElement             _baselineModels;
    private          bool                    _restoreDefaultCatalogStaged;
    private          bool?                   _secretSet;
    private          bool                    _canEdit = true;
    private          bool                    _isSaving;
    private          string?                 _saveError;
    private          string?                 _saveSuccessText;
    private          string                  _apiKeyDraft  = string.Empty;
    private          string                  _baseUrlDraft = string.Empty;

    public DeepSeekModelCardViewModel(ISettingsMutationRunner runner, SettingsNamespaceView view)
    {
        _runner                      = runner;
        _snapshot                    = view;
        SaveCommand                  = new AsyncRelayCommand(SaveAsync);
        CancelCommand                = new RelayCommand(CancelEdits);
        AddModelCommand              = new RelayCommand(AddModel);
        RestoreDefaultCatalogCommand = new RelayCommand(ToggleRestoreDefaultCatalog);
        ApplySnapshot(view);
    }

    public string Title => "DeepSeek";

    public ObservableCollection<SettingsModelEntryViewModel> ModelEntries { get; } = [];

    public AsyncRelayCommand SaveCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand AddModelCommand { get; }

    /// <summary>暂存「恢复默认模型目录」：保存时以 unset models 提交。</summary>
    public RelayCommand RestoreDefaultCatalogCommand { get; }

    /// <summary>编辑收尾（取消或保存成功）通知；由分区订阅以返回行列表。</summary>
    internal event EventHandler? EditFinished;

    public string ApiKeyDraft
    {
        get => _apiKeyDraft;
        set
        {
            if (SetProperty(ref _apiKeyDraft, value)) NotifyDirtyChanged();
        }
    }

    public string BaseUrlDraft
    {
        get => _baseUrlDraft;
        set
        {
            if (SetProperty(ref _baseUrlDraft, value)) NotifyDirtyChanged();
        }
    }

    public bool CanEdit
    {
        get => _canEdit;
        internal set
        {
            if (SetProperty(ref _canEdit, value)) OnPropertyChanged(nameof(CanSave));
        }
    }

    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (!SetProperty(ref _isSaving, value)) return;
            OnPropertyChanged(nameof(CanSave));
            OnPropertyChanged(nameof(SaveButtonText));
        }
    }

    public string? SaveError
    {
        get => _saveError;
        private set
        {
            if (SetProperty(ref _saveError, value)) OnPropertyChanged(nameof(HasSaveError));
        }
    }

    public bool HasSaveError => !string.IsNullOrEmpty(SaveError);

    /// <summary>保存成功的行内提示；任何编辑动作清除。</summary>
    public string? SaveSuccessText
    {
        get => _saveSuccessText;
        private set
        {
            if (SetProperty(ref _saveSuccessText, value)) OnPropertyChanged(nameof(HasSaveSuccess));
        }
    }

    public bool HasSaveSuccess => !string.IsNullOrEmpty(SaveSuccessText);

    public string SaveButtonText => IsSaving ? "保存中…" : "保存";

    // 凭据状态圆点：取 Secrets 中 apiKeyEnv 槽位（Set=true 实心绿点 / false 空心灰点；无信息不显示）。
    public bool ShowCredentialDot => _secretSet.HasValue;

    public bool IsCredentialSet => _secretSet == true;

    public bool IsCredentialUnset => _secretSet == false;

    public string CredentialStatusText => IsCredentialSet ? "API 密钥已配置" : "API 密钥未配置";

    /// <summary>密钥框占位：已配置提示可替换，未配置提示输入；值永不回显。</summary>
    public string ApiKeyWatermark => IsCredentialSet ? "已配置——输入新值可替换" : "输入 API 密钥";

    /// <summary>凭据引用：值中的 apiKeyEnv 非空取其值，否则回退内置引用名。</summary>
    public string ApiKeyReference =>
        SettingsValues.GetString(_snapshot.Value, ["apiKeyEnv"]) is { Length: > 0 } reference
            ? reference
            : DefaultCredentialReference;

    /// <summary>模型目录状态：生效值与 base 层同序同值为默认目录，否则视为已自定义。</summary>
    public bool UsesDefaultCatalog =>
        SettingsValues.TryGetModels(_snapshot.Value) is { } current &&
        _snapshot.Base is { } baseSegment                           &&
        SettingsValues.TryGetModels(baseSegment) is { } seed        &&
        JsonElement.DeepEquals(current, seed);

    public string CatalogStatusText => UsesDefaultCatalog ? "正在使用默认模型目录" : "已自定义模型目录";

    public bool RestoreDefaultCatalogStaged
    {
        get => _restoreDefaultCatalogStaged;
        private set
        {
            if (SetProperty(ref _restoreDefaultCatalogStaged, value)) NotifyDirtyChanged();
        }
    }

    /// <summary>校验错误（首个）：ID 必填且去重、数字字段为正整数（可带 K/M 后缀）或空、输入类型至少一项。</summary>
    public string? FirstValidationError
    {
        get
        {
            var identifiers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in ModelEntries)
            {
                var identifier = entry.IdDraft.Trim();
                if (identifier.Length == 0) return "模型 ID 不能为空。";
                if (!identifiers.Add(identifier)) return $"模型 ID 重复：{identifier}";
                if (HasInvalidTokenCount(entry.ContextWindowDraft))
                    return "上下文窗口必须是正整数（可带 K/M 后缀）或留空。";
                if (HasInvalidTokenCount(entry.MaxTokensDraft))
                    return "最大输出 token 数必须是正整数（可带 K/M 后缀）或留空。";
                if (entry is { IsTextSelected: false, IsImageSelected: false }) return "输入类型至少勾选一项。";
            }

            return null;
        }
    }

    public bool HasValidationError => FirstValidationError is not null;

    public bool IsDirty
    {
        get
        {
            if (ApiKeyDraft.Trim().Length > 0 || RestoreDefaultCatalogStaged) return true;
            if (BaseUrlDraft.Trim() != _baselineBaseUrl) return true;
            return SettingsModelArrayBuilder.Build(ModelEntries, "inputModalities") is { } current &&
                   (_baselineModels.ValueKind     != JsonValueKind.Array
                       ? current.GetArrayLength() > 0
                       : !JsonElement.DeepEquals(current, _baselineModels));
        }
    }

    public bool CanSave => CanEdit && IsDirty && !HasValidationError && !IsSaving;

    /// <summary>从视图整建快照与草稿（打开面板、保存成功、取消编辑共用）。</summary>
    internal void ApplySnapshot(SettingsNamespaceView view)
    {
        _snapshot         = view;
        _baselineRevision = view.Revision;
        _baselineBaseUrl  = SettingsValues.GetString(view.Value, ["baseURL"]) ?? string.Empty;
        _baselineModels   = SettingsValues.TryGetModels(view.Value)           ?? default;
        _secretSet        = view.Secrets?.FirstOrDefault(secret => secret.Path is ["apiKeyEnv"])?.Set;

        BaseUrlDraft                = _baselineBaseUrl;
        ApiKeyDraft                 = string.Empty;
        RestoreDefaultCatalogStaged = false;
        SaveError                   = null;
        SaveSuccessText             = null;

        // 保存/取消重投影时按 ID 保持既有条目的展开状态，避免保存后全部折叠
        // （快照必须在 Clear 之前收集，Clear 后集合已空）。
        var expandedIds = ModelEntries.Where(entry => entry.IsExpanded)
                                      .Select(entry => entry.IdDraft.Trim())
                                      .ToHashSet(StringComparer.Ordinal);
        ModelEntries.Clear();
        if (SettingsValues.TryGetModels(view.Value) is { } models)
            foreach (var entry in models.EnumerateArray().Select(node => SettingsModelEntryViewModel.FromJson(node)))
            {
                entry.IsExpanded      =  expandedIds.Contains(entry.IdDraft.Trim());
                entry.DeleteCommand   =  new RelayCommand(() => RemoveEntry(entry));
                entry.PropertyChanged += OnEntryPropertyChanged;
                ModelEntries.Add(entry);
            }

        OnPropertyChanged(nameof(ShowCredentialDot));
        OnPropertyChanged(nameof(IsCredentialSet));
        OnPropertyChanged(nameof(IsCredentialUnset));
        OnPropertyChanged(nameof(CredentialStatusText));
        OnPropertyChanged(nameof(ApiKeyWatermark));
        OnPropertyChanged(nameof(UsesDefaultCatalog));
        OnPropertyChanged(nameof(CatalogStatusText));
        OnPropertyChanged(nameof(ApiKeyReference));
        NotifyDirtyChanged();
    }

    /// <summary>外部改动重投影：只更新基线 revision 与状态标记，草稿文本不动。</summary>
    internal void Rebase(SettingsNamespaceView view)
    {
        _snapshot         = view;
        _baselineRevision = view.Revision;
        _secretSet        = view.Secrets?.FirstOrDefault(secret => secret.Path is ["apiKeyEnv"])?.Set;
        OnPropertyChanged(nameof(ShowCredentialDot));
        OnPropertyChanged(nameof(IsCredentialSet));
        OnPropertyChanged(nameof(IsCredentialUnset));
        OnPropertyChanged(nameof(CredentialStatusText));
        OnPropertyChanged(nameof(ApiKeyWatermark));
        OnPropertyChanged(nameof(UsesDefaultCatalog));
        OnPropertyChanged(nameof(CatalogStatusText));
        NotifyDirtyChanged();
    }

    private async Task SaveAsync()
    {
        if (!CanSave) return;

        var apiKey = ApiKeyDraft.Trim();
        IsSaving        = true;
        SaveError       = null;
        SaveSuccessText = null;
        try
        {
            var ops     = new List<SettingsMutationOp>();
            var baseUrl = BaseUrlDraft.Trim();
            if (baseUrl != _baselineBaseUrl)
                ops.Add(SettingsMutationOp.Set(["baseURL"], JsonElementFactory.FromString(baseUrl)));

            if (RestoreDefaultCatalogStaged)
                ops.Add(SettingsMutationOp.Unset(["models"]));
            else if (SettingsModelArrayBuilder.Build(ModelEntries, "inputModalities") is { } current &&
                     (_baselineModels.ValueKind     != JsonValueKind.Array
                         ? current.GetArrayLength() > 0
                         : !JsonElement.DeepEquals(current, _baselineModels)))
                ops.Add(SettingsMutationOp.Set(["models"], current));

            if (ops.Count > 0)
            {
                var view = await _runner.MutateAsync(ModelsSettingsSectionViewModel.DeepSeekNs, ops, _baselineRevision);
                ApplySnapshot(view);
            }

            if (apiKey.Length > 0) await _runner.SetCredentialAsync(ApiKeyReference, apiKey);

            SaveSuccessText = "已保存 DeepSeek。";
            EditFinished?.Invoke(this, EventArgs.Empty);
        }
        catch (SettingsConflictException)
        {
            SaveError = "这张卡片打开期间，这些设置已被其他地方改动。请关闭后重新打开，在当前值上编辑。";
        }
        catch (CredentialRejectedException exception)
        {
            SaveError = exception.Message;
        }
        catch (Exception exception)
        {
            SaveError = $"保存失败：{exception.Message}";
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>放弃草稿回到当前生效值并返回行列表（卡保持投影，基线不变）。</summary>
    private void CancelEdits()
    {
        ApplySnapshot(_snapshot);
        EditFinished?.Invoke(this, EventArgs.Empty);
    }

    private void AddModel()
    {
        if (!CanEdit) return;

        var entry = new SettingsModelEntryViewModel { IsExpanded = true };
        entry.DeleteCommand   =  new RelayCommand(() => RemoveEntry(entry));
        entry.PropertyChanged += OnEntryPropertyChanged;
        ModelEntries.Add(entry);
        SaveError       = null;
        SaveSuccessText = null;
        NotifyDirtyChanged();
    }

    /// <summary>删除条目：仅移除集合成员，同时刷新校验/脏标记——否则「ID 不能为空」
    /// 之类的条目级错误会在条目删除后残留。</summary>
    private void RemoveEntry(SettingsModelEntryViewModel entry)
    {
        ModelEntries.Remove(entry);
        SaveError       = null;
        SaveSuccessText = null;
        NotifyDirtyChanged();
    }

    private void ToggleRestoreDefaultCatalog()
    {
        if (!CanEdit) return;

        RestoreDefaultCatalogStaged = !RestoreDefaultCatalogStaged;
        SaveError                   = null;
        SaveSuccessText             = null;
    }

    private void OnEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        SaveError       = null;
        SaveSuccessText = null;
        NotifyDirtyChanged();
    }

    private void NotifyDirtyChanged()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(FirstValidationError));
        OnPropertyChanged(nameof(HasValidationError));
    }

    private static bool HasInvalidTokenCount(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return false;
        return !SettingsModelEntryViewModel.TryParseTokenCount(trimmed, out _);
    }
}

/// <summary>从草稿条目重建模型数组的共享构建器：DeepSeek 目录与 pi-ai 路由 profile 共用，
/// 仅输入类型数组字段名不同（DeepSeek=inputModalities，pi-ai=input）。按来源节点的属性
/// 顺序重写已知字段并逐字保留未建模字段（description、systemPromptUpdate、toolUpdate、
/// reasoningEfforts、compat 等）；图片限额（imagePixelBudget/imageMaxBytes）仅勾选图片时
/// 保留（上游禁止纯文本模型携带）；来源缺输入类型数组且未启用图片时不补写缺省。</summary>
internal static class SettingsModelArrayBuilder
{
    public static JsonElement? Build(IReadOnlyList<SettingsModelEntryViewModel> entries, string modalityField)
    {
        if (entries.Count == 0) return null;

        return JsonElementFactory.FromArray(writer =>
        {
            foreach (var entry in entries)
            {
                writer.WriteStartObject();
                var name = entry.NameDraft.Trim();
                var hasContext = SettingsModelEntryViewModel.TryParsePositiveTokenCount(entry.ContextWindowDraft,
                    out var contextWindow);
                var hasMaxTokens = SettingsModelEntryViewModel.TryParsePositiveTokenCount(entry.MaxTokensDraft,
                    out var maxTokens);
                var wroteModalities = false;
                if (entry.SourceNode.ValueKind == JsonValueKind.Object)
                    foreach (var property in entry.SourceNode.EnumerateObject())
                    {
                        switch (property.Name)
                        {
                            case "id" :
                                writer.WriteString("id", entry.IdDraft.Trim());
                                break;
                            case "name" :
                                if (name.Length > 0) writer.WriteString("name", name);
                                break;
                            case "contextWindow" :
                                if (hasContext) writer.WriteNumber("contextWindow", contextWindow);
                                break;
                            case "maxTokens" :
                                if (hasMaxTokens) writer.WriteNumber("maxTokens", maxTokens);
                                break;
                            case "inputModalities" or "input" :
                                WriteModalities(writer, entry, modalityField);
                                wroteModalities = true;
                                break;
                            case "imagePixelBudget" or "imageMaxBytes" :
                                if (entry.IsImageSelected) property.WriteTo(writer);
                                break;
                            default :
                                property.WriteTo(writer);
                                break;
                        }
                    }

                if (!SourceHas(entry.SourceNode, "id"))
                    writer.WriteString("id", entry.IdDraft.Trim());
                if (name.Length > 0 && !SourceHas(entry.SourceNode, "name"))
                    writer.WriteString("name", name);
                if (hasContext && !SourceHas(entry.SourceNode, "contextWindow"))
                    writer.WriteNumber("contextWindow", contextWindow);
                if (hasMaxTokens && !SourceHas(entry.SourceNode, "maxTokens"))
                    writer.WriteNumber("maxTokens", maxTokens);
                if (!wroteModalities && entry.IsImageSelected)
                    WriteModalities(writer, entry, modalityField);
                writer.WriteEndObject();
            }
        });
    }

    private static void WriteModalities(Utf8JsonWriter writer, SettingsModelEntryViewModel entry, string modalityField)
    {
        writer.WriteStartArray(modalityField);
        if (entry.IsTextSelected) writer.WriteStringValue("text");
        if (entry.IsImageSelected) writer.WriteStringValue("image");
        writer.WriteEndArray();
    }

    private static bool SourceHas(JsonElement node, string property)
    {
        return node.ValueKind == JsonValueKind.Object && node.TryGetProperty(property, out _);
    }
}

/// <summary>模型目录的单条草稿：折叠仅显示 ID/名称行，展开补上下文窗口、
/// 最大输出 token 数（正整数，可带 K/M 后缀）与输入类型（text/image）。
/// 保存时未建模字段从来源节点逐字保留（对齐上游 DeepSeekCatalogModel 与 pi-ai modelProfile）。</summary>
public sealed class SettingsModelEntryViewModel : ObservableObject
{
    private JsonElement _sourceNode;
    private string      _contextWindowDraft = string.Empty;
    private string      _idDraft            = string.Empty;
    private string      _maxTokensDraft     = string.Empty;
    private string      _nameDraft          = string.Empty;
    private bool        _isExpanded;
    private bool        _isTextSelected = true;
    private bool        _isImageSelected;

    public SettingsModelEntryViewModel()
    {
        ToggleExpandCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public string IdDraft
    {
        get => _idDraft;
        set => SetProperty(ref _idDraft, value);
    }

    public string NameDraft
    {
        get => _nameDraft;
        set => SetProperty(ref _nameDraft, value);
    }

    public string ContextWindowDraft
    {
        get => _contextWindowDraft;
        set => SetProperty(ref _contextWindowDraft, value);
    }

    public string MaxTokensDraft
    {
        get => _maxTokensDraft;
        set => SetProperty(ref _maxTokensDraft, value);
    }

    /// <summary>折叠/展开：折叠只留 ID/名称行，对齐官方目录条目。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public RelayCommand ToggleExpandCommand { get; }

    /// <summary>输入类型 text；上游缺省即 ['text']，来源缺字段视为勾选。</summary>
    public bool IsTextSelected
    {
        get => _isTextSelected;
        set => SetProperty(ref _isTextSelected, value);
    }

    /// <summary>输入类型 image；上游禁止纯文本模型携带图片限额，保存时连带丢弃。</summary>
    public bool IsImageSelected
    {
        get => _isImageSelected;
        set => SetProperty(ref _isImageSelected, value);
    }

    /// <summary>来源节点（打开时的原始条目）；新建条目为 Undefined。</summary>
    internal JsonElement SourceNode => _sourceNode;

    public ICommand? DeleteCommand { get; internal set; }

    internal static SettingsModelEntryViewModel FromJson(JsonElement node, string modalityField = "inputModalities")
    {
        var modalities = node.TryGetProperty(modalityField, out var input) &&
                         input.ValueKind == JsonValueKind.Array
            ? input
            : default;
        return new SettingsModelEntryViewModel
        {
            _sourceNode = node.Clone(),
            IdDraft = node.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString() ?? string.Empty
                : string.Empty,
            NameDraft = node.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString() ?? string.Empty
                : string.Empty,
            ContextWindowDraft = ReadNumber(node, "contextWindow") is { } contextWindow
                ? FormatTokenCount(contextWindow)
                : string.Empty,
            MaxTokensDraft = ReadNumber(node, "maxTokens") is { } maxTokens
                ? FormatTokenCount(maxTokens)
                : string.Empty,
            _isTextSelected  = modalities.ValueKind != JsonValueKind.Array || ContainsString(modalities, "text"),
            _isImageSelected = modalities.ValueKind == JsonValueKind.Array && ContainsString(modalities, "image"),
        };
    }

    private static long? ReadNumber(JsonElement node, string property)
    {
        return node.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
                                                            && value.TryGetInt64(out var number)
            ? number
            : null;
    }

    /// <summary>大数缩写显示：整百万/整千缩为 M/K（1000000→1M、128000→128K），其余原样。</summary>
    internal static string FormatTokenCount(long value)
    {
        if (value == 0) return "0";
        if (value % 1_000_000 == 0)
            return (value / 1_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture) + "M";
        if (value % 1_000 == 0)
            return (value / 1_000).ToString(System.Globalization.CultureInfo.InvariantCulture) + "K";
        return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>解析数字草稿：允许 K/M 后缀（1M=1000000）；0 合法并视为不写入该字段。</summary>
    internal static bool TryParseTokenCount(string text, out long value)
    {
        value = 0;
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return false;

        long multiplier = 1;
        switch (trimmed[^1])
        {
            case 'K' or 'k' :
                multiplier = 1_000;
                trimmed    = trimmed[..^1];
                break;
            case 'M' or 'm' :
                multiplier = 1_000_000;
                trimmed    = trimmed[..^1];
                break;
        }

        if (!long.TryParse(trimmed, out var number) || number < 0) return false;
        if (multiplier                                        > 1 && number > long.MaxValue / multiplier) return false;
        value = number * multiplier;
        return true;
    }

    /// <summary>解析并要求正整数（0 视为不写入该字段，不算失败）。</summary>
    internal static bool TryParsePositiveTokenCount(string text, out long value)
    {
        value = 0;
        return TryParseTokenCount(text, out value) && value > 0;
    }

    private static bool ContainsString(JsonElement array, string value) => array.EnumerateArray()
       .Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == value);
}
