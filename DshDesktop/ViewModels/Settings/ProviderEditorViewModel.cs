using DshDesktop.Core.Exceptions;
using DshDesktop.Core.Models;
using DshDesktop.Core.Services;
using DshDesktop.Utils;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Input;

namespace DshDesktop.ViewModels.Settings;

/// <summary>添加/编辑卡的表单模式：目录路由创建（第三方 tab）、自定义路由创建（自定义 tab）、
/// 两种编辑态（由目录条目的 declared 判定，declared 路由拥有显示名与协议字段）。</summary>
public enum ProviderEditorMode
{
    CreateCatalog,
    CreateDeclared,
    EditCatalog,
    EditDeclared
}

/// <summary>pi-ai 路由（llm-pi-ai ns 的 providers.&lt;route&gt;）的添加/编辑卡：官方目录厂商
/// 仅密钥 + 自定义设置（API 地址/模型目录）；自定义路由补 Provider ID/显示名称/API 协议。
/// 创建整段写入 providers.&lt;route&gt;（目录路由无字段时物化空对象收养默认），编辑按字段级
/// diff 提交（未建模字段不动）；密钥另走凭据域（引用名沿用已命名值，否则按路由派生）。</summary>
public sealed class ProviderEditorViewModel : ObservableObject
{
    private const string Ns = ModelsSettingsSectionViewModel.PiAiNs;

    /// <summary>自定义路由 id 校验：小写字母开头，仅小写字母/数字/连字符（对齐上游）。</summary>
    private static readonly Regex RouteIdPattern =
        new(@"^[a-z][a-z0-9]*(-[a-z0-9]+)*$", RegexOptions.CultureInvariant);

    /// <summary>API 协议三选一（wire 值对齐 pi-ai 支持协议）。</summary>
    public static readonly IReadOnlyList<(string Value, string Label)> ApiProtocolChoices =
    [
        ("openai-completions", "OpenAI Chat Completions"),
        ("openai-responses", "OpenAI Responses"),
        ("anthropic-messages", "Anthropic Messages")
    ];

    private readonly ISettingsMutationRunner          _runner;
    private readonly ILlmCatalogService?              _catalogService;
    private readonly Action<SettingsNamespaceView>    _onSaved;
    private readonly string                           _route;
    private readonly JsonElement                      _original;
    private          SettingsNamespaceView            _snapshot;

    private LlmConfigurableProvider? _selectedProvider;
    private bool _isProviderMenuOpen;
    private string _routeIdDraft     = string.Empty;
    private string _displayNameDraft = string.Empty;
    private string _baseUrlDraft     = string.Empty;
    private string _apiDraft         = "openai-completions";
    private string _apiKeyDraft      = string.Empty;
    private bool _isApiMenuOpen;
    private bool _isCustomExpanded;
    private bool _isSaving;
    private string? _saveError;

    private bool _isDiscovering;
    private bool _isDiscoverPopupOpen;
    private string? _discoverError;
    private string? _discoverNotice;
    private bool _discoverEmptyVisible;

    public ProviderEditorViewModel(ISettingsMutationRunner runner, ILlmCatalogService? catalogService,
                                   SettingsNamespaceView snapshot, ProviderEditorMode mode, string route,
                                   JsonElement original,
                                   IReadOnlyList<LlmConfigurableProvider> addable,
                                   Action<SettingsNamespaceView> onSaved)
    {
        _runner         = runner;
        _catalogService = catalogService;
        _snapshot       = snapshot;
        Mode            = mode;
        _route          = route;
        _original       = original;
        _onSaved        = onSaved;

        SaveCommand           = new AsyncRelayCommand(SaveAsync);
        CancelCommand         = new RelayCommand(() => Cancelled?.Invoke(this, EventArgs.Empty));
        ShowCatalogTabCommand = new RelayCommand(() => RequestTab(ProviderEditorMode.CreateCatalog));
        ShowDeclaredTabCommand = new RelayCommand(() => RequestTab(ProviderEditorMode.CreateDeclared));
        ToggleCustomCommand       = new RelayCommand(() => IsCustomExpanded = !IsCustomExpanded);
        ToggleProviderMenuCommand = new RelayCommand(() => IsProviderMenuOpen = !IsProviderMenuOpen);
        ToggleApiMenuCommand      = new RelayCommand(() => IsApiMenuOpen = !IsApiMenuOpen);
        AddModelCommand       = new RelayCommand(AddModel);
        DiscoverCommand       = new AsyncRelayCommand(DiscoverAsync, () => CanDiscover);
        AdoptDiscoveredCommand = new RelayCommand(AdoptDiscovered);
        CancelDiscoverCommand  = new RelayCommand(() => IsDiscoverPopupOpen = false);

        foreach (var entry in addable) ProviderOptions.Add(new ProviderOptionViewModel(entry, SelectProvider));
        foreach (var (value, label) in ApiProtocolChoices)
        {
            var option = new SettingsChoiceOptionViewModel(value, label);
            option.SelectCommand = new RelayCommand(() => SelectApiProtocol(option));
            ApiOptions.Add(option);
        }

        if (original.ValueKind == JsonValueKind.Object) LoadDrafts(original);
        if (IsEditing && route.Length > 0)
        {
            // 编辑态 Provider ID 字段只读展示路由 id（不参与保存 ops 的 diff）。
            _routeIdDraft = route;
            OnPropertyChanged(nameof(RouteIdDraft));
        }
    }

    /// <summary>创建入口：目录路由（第三方 tab）或自定义路由（自定义 tab）；已占用路由集合由快照导出。</summary>
    public static ProviderEditorViewModel ForCreate(ISettingsMutationRunner runner, ILlmCatalogService? catalogService,
                                                    SettingsNamespaceView snapshot, ProviderEditorMode mode,
                                                    IReadOnlyList<LlmConfigurableProvider> addable,
                                                    Action<SettingsNamespaceView> onSaved)
    {
        var editor = new ProviderEditorViewModel(runner, catalogService, snapshot, mode, string.Empty, default,
                                                 addable, onSaved);
        if (SettingsValues.GetNode(snapshot.Value, ["providers"]) is { ValueKind: JsonValueKind.Object } providers)
            foreach (var route in providers.EnumerateObject()) editor.TakenRoutes.Add(route.Name);

        return editor;
    }

    /// <summary>编辑入口：declared 路由（目录缺失兜底按 declared 处理）显示完整字段。</summary>
    public static ProviderEditorViewModel ForEdit(ISettingsMutationRunner runner, ILlmCatalogService? catalogService,
                                                  SettingsNamespaceView snapshot, string route,
                                                  LlmConfigurableProvider? entry,
                                                  Action<SettingsNamespaceView> onSaved)
    {
        var original = SettingsValues.GetNode(snapshot.Value, ["providers", route]) ?? default;
        var declared = entry?.Declared ?? true;
        var mode     = declared ? ProviderEditorMode.EditDeclared : ProviderEditorMode.EditCatalog;
        return new ProviderEditorViewModel(runner, catalogService, snapshot, mode, route, original, [], onSaved);
    }

    public ProviderEditorMode Mode { get; }

    public bool IsAddMode => Mode is ProviderEditorMode.CreateCatalog or ProviderEditorMode.CreateDeclared;

    public bool IsCatalogMode => Mode is ProviderEditorMode.CreateCatalog or ProviderEditorMode.EditCatalog;

    public bool IsDeclaredMode => Mode is ProviderEditorMode.CreateDeclared or ProviderEditorMode.EditDeclared;

    public bool IsCatalogTabSelected => Mode == ProviderEditorMode.CreateCatalog;

    /// <summary>提供商下拉可编辑性：仅目录创建；编辑态为只读展示。</summary>
    public bool IsProviderEditable => Mode == ProviderEditorMode.CreateCatalog;

    /// <summary>Provider ID 可编辑性：仅自定义创建；编辑态只读展示路由 id。</summary>
    public bool IsRouteIdEditable => Mode == ProviderEditorMode.CreateDeclared;

    public string IntroText => Mode switch
    {
        ProviderEditorMode.CreateCatalog =>
            "从内置目录选择 OpenAI、Anthropic、Kimi 等提供商，填入其 API 密钥即可使用。",
        ProviderEditorMode.CreateDeclared =>
            "连接中转站、自部署服务或其他兼容 OpenAI / Anthropic 协议的接口，需填写 API 地址、协议和模型。",
        _ => string.Empty
    };

    public bool HasIntroText => IntroText.Length > 0;

    public ObservableCollection<ProviderOptionViewModel> ProviderOptions { get; } = [];

    public ObservableCollection<SettingsChoiceOptionViewModel> ApiOptions { get; } = [];

    /// <summary>「获取可用模型」发现的候选（仅未添加的模型），弹层勾选后采纳。</summary>
    public ObservableCollection<DiscoveredModelOptionViewModel> DiscoverOptions { get; } = [];

    public ObservableCollection<SettingsModelEntryViewModel> ModelEntries { get; } = [];

    public bool HasModelEntries => ModelEntries.Count > 0;

    public LlmConfigurableProvider? SelectedProvider
    {
        get => _selectedProvider;
        private set
        {
            if (SetProperty(ref _selectedProvider, value))
            {
                OnPropertyChanged(nameof(SelectedProviderLabel));
                NotifyDirtyChanged();
            }
        }
    }

    public string SelectedProviderLabel => SelectedProvider?.Provider ?? "选择提供商";

    public bool IsProviderMenuOpen
    {
        get => _isProviderMenuOpen;
        set => SetProperty(ref _isProviderMenuOpen, value);
    }

    public string RouteIdDraft
    {
        get => _routeIdDraft;
        set
        {
            if (SetProperty(ref _routeIdDraft, value)) NotifyDirtyChanged();
        }
    }

    public string DisplayNameDraft
    {
        get => _displayNameDraft;
        set
        {
            if (SetProperty(ref _displayNameDraft, value)) NotifyDirtyChanged();
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

    /// <summary>API 地址占位：目录路由回退适配器默认（清空即不写/取消覆盖）。</summary>
    public string BaseUrlPlaceholder => IsCatalogMode ? "提供商默认" : "https://gateway.example/v1";

    public string ApiDraft
    {
        get => _apiDraft;
        set
        {
            if (SetProperty(ref _apiDraft, value))
            {
                OnPropertyChanged(nameof(ApiLabel));
                NotifyDirtyChanged();
            }
        }
    }

    public string ApiLabel => ApiProtocolChoices.FirstOrDefault(choice => choice.Value == _apiDraft).Label
                              ?? _apiDraft;

    public bool IsApiMenuOpen
    {
        get => _isApiMenuOpen;
        set => SetProperty(ref _isApiMenuOpen, value);
    }

    public string ApiKeyDraft
    {
        get => _apiKeyDraft;
        set
        {
            if (SetProperty(ref _apiKeyDraft, value)) NotifyDirtyChanged();
        }
    }

    /// <summary>密钥框占位：目录路由留空走环境认证。</summary>
    public string ApiKeyPlaceholder => IsCatalogMode ? "输入 API 密钥，或留空使用环境认证" : "输入 API 密钥";

    /// <summary>自定义设置折叠区展开态（仅目录模式渲染头部）。</summary>
    public bool IsCustomExpanded
    {
        get => _isCustomExpanded;
        set => SetProperty(ref _isCustomExpanded, value);
    }

    /// <summary>目录模式显示折叠头部；declared 模式直接展开目录区块。</summary>
    public bool ShowsCustomHeader => IsCatalogMode;

    public bool ShowsCatalogBlock => IsDeclaredMode || (IsCatalogMode && _isCustomExpanded);

    /// <summary>目录状态：草稿存在模型条目（或原始 profile 已带 models）为已自定义，否则继承适配器默认。</summary>
    public string CatalogStatusText
    {
        get
        {
            var hasOriginalModels = _original.ValueKind == JsonValueKind.Object &&
                                    SettingsValues.HasPath(_original, ["models"]);
            return HasModelEntries || hasOriginalModels ? "已自定义模型目录" : "正在使用适配器默认模型";
        }
    }

    public RelayCommand ShowCatalogTabCommand { get; }

    public RelayCommand ShowDeclaredTabCommand { get; }

    public RelayCommand ToggleCustomCommand { get; }

    public RelayCommand ToggleProviderMenuCommand { get; }

    public RelayCommand ToggleApiMenuCommand { get; }

    public RelayCommand AddModelCommand { get; }

    public AsyncRelayCommand DiscoverCommand { get; }

    public RelayCommand AdoptDiscoveredCommand { get; }

    public RelayCommand CancelDiscoverCommand { get; }

    public AsyncRelayCommand SaveCommand { get; }

    public RelayCommand CancelCommand { get; }

    /// <summary>添加卡 tab 切换请求（由分区重建对应模式的编辑器）。</summary>
    public event EventHandler<ProviderEditorMode>? TabSwitchRequested;

    /// <summary>取消编辑（丢弃草稿回列表）。</summary>
    public event EventHandler? Cancelled;

    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (SetProperty(ref _isSaving, value))
            {
                OnPropertyChanged(nameof(CanSave));
                OnPropertyChanged(nameof(SaveButtonText));
            }
        }
    }

    public string SaveButtonText => IsSaving ? "保存中…" : "保存";

    public string? SaveError
    {
        get => _saveError;
        private set
        {
            if (SetProperty(ref _saveError, value)) OnPropertyChanged(nameof(HasSaveError));
        }
    }

    public bool HasSaveError => !string.IsNullOrEmpty(SaveError);

    public bool IsDiscovering
    {
        get => _isDiscovering;
        private set
        {
            if (SetProperty(ref _isDiscovering, value)) OnPropertyChanged(nameof(CanDiscover));
        }
    }

    public bool IsDiscoverPopupOpen
    {
        get => _isDiscoverPopupOpen;
        set => SetProperty(ref _isDiscoverPopupOpen, value);
    }

    public string? DiscoverError
    {
        get => _discoverError;
        private set
        {
            if (SetProperty(ref _discoverError, value)) OnPropertyChanged(nameof(HasDiscoverError));
        }
    }

    public bool HasDiscoverError => !string.IsNullOrEmpty(DiscoverError);

    /// <summary>发现成功但无新模型的行内提示。</summary>
    public string? DiscoverNotice
    {
        get => _discoverNotice;
        private set
        {
            if (SetProperty(ref _discoverNotice, value)) OnPropertyChanged(nameof(HasDiscoverNotice));
        }
    }

    public bool HasDiscoverNotice => !string.IsNullOrEmpty(DiscoverNotice);

    /// <summary>后端未列出任何模型（官方 fetchEmpty 语义）。</summary>
    public bool DiscoverEmptyVisible
    {
        get => _discoverEmptyVisible;
        private set => SetProperty(ref _discoverEmptyVisible, value);
    }

    /// <summary>校验错误（首个）：目录创建需选提供商；自定义创建校验 id 正则/占用/地址/≥1 模型；
    /// 全模式校验模型条目（ID 必填去重、token 数、输入类型）。</summary>
    public string? FirstValidationError
    {
        get
        {
            if (Mode == ProviderEditorMode.CreateCatalog)
            {
                if (SelectedProvider is null) return "请选择提供商。";
            }
            else if (Mode == ProviderEditorMode.CreateDeclared)
            {
                var routeId = _routeIdDraft.Trim();
                if (!RouteIdPattern.IsMatch(routeId))
                    return "Provider ID 必须以小写字母开头，只能包含小写字母、数字与连字符。";
                if (TakenRoutes.Contains(routeId)) return "该 Provider ID 已被其他提供商使用。";
                if (!IsValidBaseUrl(_baseUrlDraft)) return "API 地址必填，且需以 http:// 或 https:// 开头。";
            }
            else if (IsDeclaredMode && !IsValidBaseUrl(_baseUrlDraft))
            {
                return "API 地址必填，且需以 http:// 或 https:// 开头。";
            }

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
                if (!entry.IsTextSelected && !entry.IsImageSelected) return "输入类型至少勾选一项。";
            }

            if (Mode == ProviderEditorMode.CreateDeclared && ModelEntries.Count == 0)
                return "至少添加一个模型。";

            return null;
        }
    }

    public bool HasValidationError => FirstValidationError is not null;

    /// <summary>已存在路由集合（创建自定义时查重）。</summary>
    public HashSet<string> TakenRoutes { get; } = new(StringComparer.Ordinal);

    public bool IsDirty
    {
        get
        {
            if (!IsEditing) return true;
            if (ApiKeyDraft.Trim().Length > 0) return true;
            if (IsDeclaredMode)
            {
                if (DisplayNameDraft.Trim() != (SettingsValues.GetString(_original, ["displayName"]) ?? string.Empty))
                    return true;
                if (ApiDraft != (SettingsValues.GetString(_original, ["api"]) ?? string.Empty)) return true;
            }

            if (BaseUrlDraft.Trim() != (SettingsValues.GetString(_original, ["baseURL"]) ?? string.Empty))
                return true;

            var current        = SettingsModelArrayBuilder.Build(ModelEntries, "input");
            var originalModels = TryGetOriginalModels();
            return current is { } array
                ? originalModels is { } baseline ? !JsonElement.DeepEquals(array, baseline) : true
                : originalModels is not null;
        }
    }

    public bool CanSave => !IsSaving && !HasValidationError && (IsDirty || !IsEditing);

    private bool IsEditing => Mode is ProviderEditorMode.EditCatalog or ProviderEditorMode.EditDeclared;

    /// <summary>「获取可用模型」可用性：存在可用的探测参数（目录路由按厂商、自定义路由按端点）。</summary>
    public bool CanDiscover => !IsDiscovering && _catalogService is not null && BuildProbe() is not null;

    /// <summary>外部改动重投影：仅更新基线 revision（草稿不动；保存冲突时按打开时 revision 落锁）。</summary>
    internal void Rebase(SettingsNamespaceView view)
    {
        _snapshot = view;
    }

    private void LoadDrafts(JsonElement profile)
    {
        _displayNameDraft = SettingsValues.GetString(profile, ["displayName"]) ?? string.Empty;
        _baseUrlDraft     = SettingsValues.GetString(profile, ["baseURL"]) ?? string.Empty;
        _apiDraft         = SettingsValues.GetString(profile, ["api"]) is { Length: > 0 } api
            ? api
            : "openai-completions";
        if (SettingsValues.GetNode(profile, ["models"]) is { ValueKind: JsonValueKind.Array } models)
            foreach (var node in models.EnumerateArray())
            {
                var entry = SettingsModelEntryViewModel.FromJson(node, "input");
                entry.DeleteCommand = new RelayCommand(() => RemoveEntry(entry));
                entry.PropertyChanged += OnEntryPropertyChanged;
                ModelEntries.Add(entry);
            }

        OnPropertyChanged(nameof(DisplayNameDraft));
        OnPropertyChanged(nameof(BaseUrlDraft));
        OnPropertyChanged(nameof(ApiDraft));
        OnPropertyChanged(nameof(ApiLabel));
        OnPropertyChanged(nameof(HasModelEntries));
        OnPropertyChanged(nameof(CatalogStatusText));
    }

    private void SelectProvider(ProviderOptionViewModel option)
    {
        if (option.Entry.Declared == true) return;

        IsProviderMenuOpen = false;
        SelectedProvider   = option.Entry;
        SaveError          = null;
    }

    private void SelectApiProtocol(SettingsChoiceOptionViewModel option)
    {
        IsApiMenuOpen = false;
        ApiDraft      = option.Value;
        SaveError     = null;
    }

    private void RequestTab(ProviderEditorMode mode)
    {
        if (Mode == mode) return;

        TabSwitchRequested?.Invoke(this, mode);
    }

    private void AddModel()
    {
        var entry = new SettingsModelEntryViewModel { IsExpanded = true };
        entry.DeleteCommand = new RelayCommand(() => RemoveEntry(entry));
        entry.PropertyChanged += OnEntryPropertyChanged;
        ModelEntries.Add(entry);
        SaveError      = null;
        DiscoverError  = null;
        DiscoverNotice = null;
        DiscoverEmptyVisible = false;
        NotifyDirtyChanged();
    }

    private void RemoveEntry(SettingsModelEntryViewModel entry)
    {
        ModelEntries.Remove(entry);
        SaveError      = null;
        DiscoverError  = null;
        DiscoverNotice = null;
        NotifyDirtyChanged();
    }

    private static bool HasInvalidTokenCount(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return false;
        return !SettingsModelEntryViewModel.TryParseTokenCount(trimmed, out _);
    }

    private static bool IsValidBaseUrl(string text)
    {
        var trimmed = text.Trim();
        return trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>探测参数：目录路由按厂商（可带自定义端点/密钥），declared 路由必须携带端点。</summary>
    private LlmDiscoveryRequest? BuildProbe()
    {
        string? provider = Mode switch
        {
            ProviderEditorMode.CreateCatalog => SelectedProvider?.Provider,
            ProviderEditorMode.EditCatalog   => _route,
            _                                => null
        };
        var url    = BaseUrlDraft.Trim();
        string? baseUrl = url.Length > 0 ? url : null;
        string? api     = IsDeclaredMode ? ApiDraft : null;
        var key         = ApiKeyDraft.Trim();
        string? apiKey  = key.Length > 0 ? key : null;

        if (Mode == ProviderEditorMode.CreateCatalog && provider is null) return null;
        if (IsDeclaredMode && baseUrl is null) return null;
        if (provider is null && baseUrl is null) return null;

        return new LlmDiscoveryRequest(provider, baseUrl, api, apiKey);
    }

    private async Task DiscoverAsync()
    {
        if (BuildProbe() is not { } probe || _catalogService is null) return;

        IsDiscovering        = true;
        DiscoverError        = null;
        DiscoverNotice       = null;
        DiscoverEmptyVisible = false;
        try
        {
            var found = await _catalogService.DiscoverModelsAsync(Ns, probe);
            var known = ModelEntries.Select(entry => entry.IdDraft.Trim()).ToHashSet(StringComparer.Ordinal);
            var fresh = found.Where(model => !known.Contains(model.Id)).ToList();
            if (found.Count == 0)
            {
                DiscoverEmptyVisible = true;
                return;
            }

            if (fresh.Count == 0)
            {
                DiscoverNotice = "目录模型均已添加，未发现新模型。";
                return;
            }

            DiscoverOptions.Clear();
            foreach (var model in fresh) DiscoverOptions.Add(new DiscoveredModelOptionViewModel(model));
            IsDiscoverPopupOpen = true;
        }
        catch (Exception exception)
        {
            DiscoverError = exception.Message;
        }
        finally
        {
            IsDiscovering = false;
        }
    }

    /// <summary>采纳勾选的发现结果：按 id 追加为草稿条目（折叠态），不直接写配置（官方 adopt 语义）。</summary>
    private void AdoptDiscovered()
    {
        IsDiscoverPopupOpen = false;
        foreach (var option in DiscoverOptions.Where(candidate => candidate.IsPicked))
        {
            var entry = new SettingsModelEntryViewModel
            {
                IdDraft            = option.Model.Id,
                NameDraft          = option.Model.Name ?? string.Empty,
                ContextWindowDraft = option.Model.ContextWindow is { } contextWindow
                    ? SettingsModelEntryViewModel.FormatTokenCount(contextWindow)
                    : string.Empty,
                MaxTokensDraft  = option.Model.MaxTokens is { } maxTokens
                    ? SettingsModelEntryViewModel.FormatTokenCount(maxTokens)
                    : string.Empty,
                IsTextSelected  = option.Model.InputModalities is null ||
                                  option.Model.InputModalities.Contains("text"),
                IsImageSelected = option.Model.InputModalities?.Contains("image") == true
            };
            entry.DeleteCommand = new RelayCommand(() => RemoveEntry(entry));
            entry.PropertyChanged += OnEntryPropertyChanged;
            ModelEntries.Add(entry);
        }

        SaveError = null;
        NotifyDirtyChanged();
    }

    private async Task SaveAsync()
    {
        if (!CanSave) return;

        var key = ApiKeyDraft.Trim();
        IsSaving  = true;
        SaveError = null;
        try
        {
            var ops = BuildSaveOps(out var route, out var keyReference);
            var view = ops.Count > 0
                ? await _runner.MutateAsync(Ns, ops, _snapshot.Revision)
                : _snapshot;
            if (key.Length > 0) await _runner.SetCredentialAsync(keyReference, key);
            _onSaved(view);
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

    /// <summary>构建保存 ops：创建 = 整段写 providers.&lt;route&gt;（目录路由无字段时物化空对象），
    /// 编辑 = 字段级 diff（displayName/api/baseURL/models；未建模字段不动）。
    /// keyReference 输出本次应写入凭据的引用名（沿用已命名值，否则按路由派生）。</summary>
    private List<SettingsMutationOp> BuildSaveOps(out string route, out string keyReference)
    {
        var ops    = new List<SettingsMutationOp>();
        var key    = ApiKeyDraft.Trim();
        var url    = BaseUrlDraft.Trim();
        route      = EffectiveRoute;
        keyReference = DeriveKeyRef(route);

        if (Mode == ProviderEditorMode.CreateCatalog)
        {
            keyReference = DeriveKeyRef(route);
            var hasModels = ModelEntries.Count > 0;
            if (key.Length == 0 && url.Length == 0 && !hasModels)
                ops.Add(SettingsMutationOp.Set(["providers", route], JsonElementFactory.FromObject(_ => { })));
            else
                ops.Add(SettingsMutationOp.Set(["providers", route], WriteCatalogProfile(route, key, url)));
            return ops;
        }

        if (Mode == ProviderEditorMode.CreateDeclared)
        {
            ops.Add(SettingsMutationOp.Set(["providers", route], WriteDeclaredProfile(route, key)));
            return ops;
        }

        // 编辑：字段级 diff（对齐官方 pathOps；apiKeyEnv 只增改不撤销）。
        var namedReference = SettingsValues.GetString(_original, ["apiKeyEnv"]);
        keyReference = namedReference is { Length: > 0 } named ? named : DeriveKeyRef(route);
        if (IsDeclaredMode)
        {
            var displayName = DisplayNameDraft.Trim();
            var originalName = SettingsValues.GetString(_original, ["displayName"]) ?? string.Empty;
            if (displayName != originalName)
                ops.Add(displayName.Length > 0
                            ? SettingsMutationOp.Set(["providers", route, "displayName"],
                                                     JsonElementFactory.FromString(displayName))
                            : SettingsMutationOp.Unset(["providers", route, "displayName"]));

            if (ApiDraft != (SettingsValues.GetString(_original, ["api"]) ?? string.Empty))
                ops.Add(SettingsMutationOp.Set(["providers", route, "api"], JsonElementFactory.FromString(ApiDraft)));
        }

        if (url != (SettingsValues.GetString(_original, ["baseURL"]) ?? string.Empty))
            ops.Add(url.Length > 0
                        ? SettingsMutationOp.Set(["providers", route, "baseURL"], JsonElementFactory.FromString(url))
                        : SettingsMutationOp.Unset(["providers", route, "baseURL"]));

        var current = SettingsModelArrayBuilder.Build(ModelEntries, "input");
        switch (current)
        {
            case { } array when TryGetOriginalModels() is { } baseline:
                if (!JsonElement.DeepEquals(array, baseline))
                    ops.Add(SettingsMutationOp.Set(["providers", route, "models"], array));
                break;
            case { } array:
                ops.Add(SettingsMutationOp.Set(["providers", route, "models"], array));
                break;
            default:
                if (TryGetOriginalModels() is not null)
                    ops.Add(SettingsMutationOp.Unset(["providers", route, "models"]));
                break;
        }

        if (key.Length > 0 && namedReference is not { Length: > 0 })
        {
            keyReference = DeriveKeyRef(route);
            ops.Add(SettingsMutationOp.Set(["providers", route, "apiKeyEnv"],
                                           JsonElementFactory.FromString(keyReference)));
        }

        return ops;
    }

    private string EffectiveRoute => Mode switch
    {
        ProviderEditorMode.CreateCatalog  => SelectedProvider?.Provider ?? string.Empty,
        ProviderEditorMode.CreateDeclared => RouteIdDraft.Trim(),
        _                                 => _route
    };

    private JsonElement? TryGetOriginalModels()
    {
        return SettingsValues.GetNode(_original, ["models"]) is { ValueKind: JsonValueKind.Array } models
            ? models
            : null;
    }

    /// <summary>目录路由 profile：仅写入用户提供的字段（apiKeyEnv/baseURL/models），其余继承适配器默认。</summary>
    private JsonElement WriteCatalogProfile(string route, string key, string url)
    {
        return JsonElementFactory.FromObject(writer =>
        {
            if (key.Length > 0) writer.WriteString("apiKeyEnv", DeriveKeyRef(route));
            if (url.Length > 0) writer.WriteString("baseURL", url);
            WriteModels(writer);
        });
    }

    /// <summary>自定义路由 profile：api/baseURL 必填（校验保证），models ≥1，密钥引用按路由派生。</summary>
    private JsonElement WriteDeclaredProfile(string route, string key)
    {
        return JsonElementFactory.FromObject(writer =>
        {
            var displayName = DisplayNameDraft.Trim();
            if (displayName.Length > 0) writer.WriteString("displayName", displayName);
            if (key.Length > 0) writer.WriteString("apiKeyEnv", DeriveKeyRef(route));
            writer.WriteString("api", ApiDraft);
            writer.WriteString("baseURL", BaseUrlDraft.Trim());
            WriteModels(writer);
        });
    }

    private void WriteModels(Utf8JsonWriter writer)
    {
        if (SettingsModelArrayBuilder.Build(ModelEntries, "input") is { } models)
        {
            writer.WritePropertyName("models");
            models.WriteTo(writer);
        }
    }

    /// <summary>凭据引用名派生（对齐官方）：路由 id 非字母数字字符逐字替换为下划线后大写 + _API_KEY。</summary>
    internal static string DeriveKeyRef(string route)
    {
        var builder = new System.Text.StringBuilder(route.Length + 8);
        foreach (var character in route)
            builder.Append(char.IsAsciiLetterOrDigit(character) ? char.ToUpperInvariant(character) : '_');
        builder.Append("_API_KEY");
        return builder.ToString();
    }

    private void OnEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        SaveError = null;
        NotifyDirtyChanged();
    }

    private void NotifyDirtyChanged()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(FirstValidationError));
        OnPropertyChanged(nameof(HasValidationError));
        OnPropertyChanged(nameof(CatalogStatusText));
        OnPropertyChanged(nameof(CanDiscover));
    }
}

/// <summary>添加下拉的目录厂商选项：展示路由 id（官方下拉同构），declared 条目不可选。</summary>
public sealed class ProviderOptionViewModel : ObservableObject
{
    private bool _isSelected;

    public ProviderOptionViewModel(LlmConfigurableProvider entry, Action<ProviderOptionViewModel> select)
    {
        Entry         = entry;
        SelectCommand = new RelayCommand(() => select(this));
    }

    public LlmConfigurableProvider Entry { get; }

    /// <summary>下拉展示文本：目录厂商显示路由 id（对齐官方）。</summary>
    public string Label => Entry.Provider;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public RelayCommand SelectCommand { get; }
}

/// <summary>「获取可用模型」弹层的候选项：默认全选（已存在模型在打开前已被过滤）。</summary>
public sealed class DiscoveredModelOptionViewModel : ObservableObject
{
    private bool _isPicked = true;

    public DiscoveredModelOptionViewModel(LlmDiscoveredModel model)
    {
        Model = model;
    }

    public LlmDiscoveredModel Model { get; }

    public string Label => Model.Name is { Length: > 0 } name ? name : Model.Id;

    public bool IsPicked
    {
        get => _isPicked;
        set => SetProperty(ref _isPicked, value);
    }
}
