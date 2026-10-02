using DshDesktop.Core.Exceptions;
using DshDesktop.Core.Models;
using DshDesktop.Utils;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace DshDesktop.ViewModels.Settings;

/// <summary>内置插件分区：终端 / Agent 循环 / 子代理 / 网页搜索配置卡（暂存式保存）。</summary>
public sealed class PluginsSettingsSectionViewModel(ISettingsMutationRunner runner)
    : SettingsSectionViewModel("plugins", "内置插件", "配置内置插件的行为")
{
    private const string DefaultCredentialReference = "DEEPSEEK_API_KEY";

    public ObservableCollection<PluginCardViewModel> Cards { get; } = [];

    public bool HasCards => Cards.Count > 0;

    /// <summary>全量投影：按 describe 重建卡（丢弃草稿）；终端卡 Windows 优先取 pwsh-sandbox。</summary>
    internal void Project(IReadOnlyDictionary<string, SettingsNamespaceView> namespaces,
                          IReadOnlyDictionary<string, CredentialStatus>      credentialStatuses)
    {
        Cards.Clear();
        AddTerminalCard(namespaces);
        AddSimpleCard(namespaces, "agent-loop", "Agent 循环", "Agent 每轮执行时可并行运行的工具调用数量。",
        [
            new PluginFieldViewModel("maxParallelToolCalls", "并行工具调用数",
                                     PluginFieldKind.PositiveInteger)
        ]);
        AddSimpleCard(namespaces, "subagent", "子代理", "限制子代理任务的嵌套深度与并发数量。",
        [
            new PluginFieldViewModel("maxDepth", "最大深度", PluginFieldKind.NonNegativeInteger,
                                     "限制子代理可以嵌套的层数"),
            new PluginFieldViewModel("maxActiveSubagents", "最大并发子代理",
                                     PluginFieldKind.PositiveInteger)
        ]);
        AddWebSearchCard(namespaces, credentialStatuses);
        OnPropertyChanged(nameof(HasCards));
    }

    /// <summary>外部改动重投影：只更新各卡基线 revision 与覆盖标记，草稿文本不动。</summary>
    internal void Rebase(IReadOnlyDictionary<string, SettingsNamespaceView> namespaces)
    {
        foreach (var card in Cards)
            if (namespaces.TryGetValue(card.Ns, out var view))
                card.Rebase(view);
    }

    internal void SetCanEdit(bool value)
    {
        foreach (var card in Cards) card.CanEdit = value;
    }

    /// <summary>凭据状态批量回流（打开面板与凭据写入事件共用）：刷新密码字段的圆点。</summary>
    internal void UpdateCredentialStatuses(IReadOnlyDictionary<string, CredentialStatus> statuses)
    {
        foreach (var card in Cards)
        foreach (var field in card.Fields)
        {
            if (field.Kind != PluginFieldKind.Password) continue;
            if (statuses.TryGetValue(field.CredentialReference ?? DefaultCredentialReference, out var status))
                field.ApplyCredentialStatus(status.Configured);
        }
    }

    private void AddTerminalCard(IReadOnlyDictionary<string, SettingsNamespaceView> namespaces)
    {
        // Windows 优先 pwsh-sandbox；不存在时回退 bash-sandbox，均无则不渲染该卡。
        var view = namespaces.TryGetValue("pwsh-sandbox", out var pwsh)
            ? pwsh
            : namespaces.GetValueOrDefault("bash-sandbox");
        if (view is null) return;

        var card = new PluginCardViewModel(runner, view.Ns, "终端", "限制内置终端命令的执行时长与输出量。");
        card.AddField(new PluginFieldViewModel("timeoutMs", "命令超时（毫秒）", PluginFieldKind.PositiveInteger));
        card.AddField(new PluginFieldViewModel("maxOutputBytes", "单流输出上限（字节）",
                                               PluginFieldKind.PositiveInteger));
        card.ApplyView(view);
        Cards.Add(card);
    }

    private void AddSimpleCard(IReadOnlyDictionary<string, SettingsNamespaceView> namespaces, string ns, string title,
                               string description, IReadOnlyList<PluginFieldViewModel> fields)
    {
        if (!namespaces.TryGetValue(ns, out var view)) return;

        var card = new PluginCardViewModel(runner, ns, title, description);
        foreach (var field in fields) card.AddField(field);
        card.ApplyView(view);
        Cards.Add(card);
    }

    private void AddWebSearchCard(IReadOnlyDictionary<string, SettingsNamespaceView> namespaces,
                                  IReadOnlyDictionary<string, CredentialStatus>      credentialStatuses)
    {
        if (!namespaces.TryGetValue("web-search-deepseek", out var view)) return;

        var reference = SettingsValues.GetString(view.Value, ["apiKeyEnv"]) is { Length: > 0 } declared
            ? declared
            : DefaultCredentialReference;
        var card = new PluginCardViewModel(runner, view.Ns, "网页搜索", "为内置网页搜索工具配置访问参数。");
        card.AddField(new PluginFieldViewModel("apiKeyEnv", "API 密钥", PluginFieldKind.Password)
        {
            CredentialReference = reference
        });
        card.AddField(new PluginFieldViewModel("baseURL", "接口地址", PluginFieldKind.Text));
        card.AddField(new PluginFieldViewModel("maxUses", "单次请求最多搜索次数", PluginFieldKind.PositiveInteger));
        card.ApplyView(view);
        if (credentialStatuses.TryGetValue(reference, out var status))
            card.ApplyCredentialStatus(reference, status.Configured);

        Cards.Add(card);
    }
}

/// <summary>插件配置卡：编辑只改草稿；保存 = 一次 MutateAsync（每字段一个 set / 恢复默认为 unset）。</summary>
public sealed class PluginCardViewModel : ObservableObject
{
    private readonly ISettingsMutationRunner _runner;
    private          SettingsNamespaceView?  _snapshot;

    private long    _baselineRevision;
    private bool    _canEdit = true;
    private bool    _isSaving;
    private string? _saveError;
    private string? _saveSuccessText;

    public PluginCardViewModel(ISettingsMutationRunner runner, string ns, string title, string description)
    {
        _runner       = runner;
        Ns            = ns;
        Title         = title;
        Description   = description;
        SaveCommand   = new AsyncRelayCommand(SaveAsync);
        CancelCommand = new RelayCommand(CancelEdits);
    }

    public string Ns { get; }

    public string Title { get; }

    public string Description { get; }

    public ObservableCollection<PluginFieldViewModel> Fields { get; } = [];

    public AsyncRelayCommand SaveCommand { get; }

    public RelayCommand CancelCommand { get; }

    public bool CanEdit
    {
        get => _canEdit;
        internal set
        {
            if (!SetProperty(ref _canEdit, value)) return;
            OnPropertyChanged(nameof(CanSave));
            foreach (var item in Fields) item.SetCanEdit(value);
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

    /// <summary>保存成功的短暂提示；任何编辑动作清除。</summary>
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

    public bool IsDirty => Fields.Any(item => item.IsDirty);

    public bool HasValidationError => Fields.Any(item => item.HasValidationError);

    public bool CanSave => CanEdit && IsDirty && !HasValidationError && !IsSaving;

    /// <summary>从视图整建基线与草稿（打开面板、保存成功、取消编辑共用）。</summary>
    internal void ApplyView(SettingsNamespaceView view)
    {
        _snapshot         = view;
        _baselineRevision = view.Revision;
        foreach (var field in Fields) field.ApplyValue(view.Value, view.User);
        SaveError       = null;
        SaveSuccessText = null;
        NotifyDirtyChanged();
    }

    /// <summary>外部改动重投影：只更新基线 revision 与覆盖标记，草稿文本不动。</summary>
    internal void Rebase(SettingsNamespaceView view)
    {
        _snapshot         = view;
        _baselineRevision = view.Revision;
        foreach (var field in Fields) field.UpdateOverrideMark(view.User);
        NotifyDirtyChanged();
    }

    internal void ApplyCredentialStatus(string reference, bool configured)
    {
        foreach (var field in Fields)
            if (field.Kind == PluginFieldKind.Password && field.CredentialReference == reference)
                field.ApplyCredentialStatus(configured);
    }

    private async Task SaveAsync()
    {
        if (!CanSave) return;

        var credentialField = Fields.FirstOrDefault(field => field.Kind == PluginFieldKind.Password && field.IsDirty);
        var credentialValue = credentialField?.DraftText.Trim() ?? string.Empty;
        IsSaving        = true;
        SaveError       = null;
        SaveSuccessText = null;
        try
        {
            var ops = new List<SettingsMutationOp>();
            foreach (var field in Fields)
                if (field.BuildOp() is { } op)
                    ops.Add(op);

            if (ops.Count > 0)
            {
                var view = await _runner.MutateAsync(Ns, ops, _baselineRevision);
                ApplyView(view);
            }

            if (credentialField is not null && credentialValue.Length > 0)
            {
                await _runner.SetCredentialAsync(credentialField.CredentialReference ?? "DEEPSEEK_API_KEY",
                                                 credentialValue);
                credentialField.ClearDraft();
            }

            SaveSuccessText = "已保存";
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

    /// <summary>放弃草稿回到当前生效值（卡片保持打开，基线不变）。</summary>
    private void CancelEdits()
    {
        if (_snapshot is { } view) ApplyView(view);
    }

    internal void AddField(PluginFieldViewModel field)
    {
        field.SetCanEdit(CanEdit);
        field.PropertyChanged += OnFieldPropertyChanged;
        Fields.Add(field);
    }

    private void OnFieldPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PluginFieldViewModel.IsDirty) &&
            e.PropertyName != nameof(PluginFieldViewModel.HasValidationError))
            return;

        SaveError       = null;
        SaveSuccessText = null;
        NotifyDirtyChanged();
    }

    private void NotifyDirtyChanged()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(HasValidationError));
        OnPropertyChanged(nameof(CanSave));
    }
}

/// <summary>插件字段种类：文本 / 正整数 / 非负整数 / 凭据（单向写入，永不回显）。</summary>
public enum PluginFieldKind
{
    Text,
    PositiveInteger,
    NonNegativeInteger,
    Password
}

/// <summary>插件卡的单字段草稿：标签上方、输入框下方；覆盖标记与恢复默认暂存。</summary>
public sealed class PluginFieldViewModel : ObservableObject
{
    private readonly long _minValue;

    private bool   _canEdit = true;
    private bool   _credentialConfigured;
    private bool   _credentialDotVisible;
    private string _draftText = string.Empty;
    private bool   _isOverridden;
    private bool   _isRestoreStaged;
    private string _baselineText = string.Empty;

    public PluginFieldViewModel(string key, string label, PluginFieldKind kind, string? fieldDescription = null)
    {
        Key                   = key;
        Label                 = label;
        Kind                  = kind;
        FieldDescription      = fieldDescription;
        _minValue             = kind == PluginFieldKind.NonNegativeInteger ? 0 : 1;
        RestoreDefaultCommand = new RelayCommand(ToggleRestoreStaged);
    }

    public string Key { get; }

    public string Label { get; }

    public PluginFieldKind Kind { get; }

    /// <summary>密码字段标记（视图据此切换 PasswordBox 与普通 TextBox）。</summary>
    public bool IsPassword => Kind == PluginFieldKind.Password;

    public string? FieldDescription { get; }

    /// <summary>恢复默认按钮文案：暂存后变为撤销。</summary>
    public string RestoreActionText => IsRestoreStaged ? "撤销恢复默认" : "恢复默认";

    /// <summary>恢复默认：把该字段暂存为 unset 操作；再次点击撤销暂存回到基线值。</summary>
    public RelayCommand RestoreDefaultCommand { get; }

    /// <summary>凭据引用（密码字段）：由分区投影从 ns 值解析。</summary>
    public string? CredentialReference { get; internal set; }

    public string DraftText
    {
        get => _draftText;
        set
        {
            if (SetProperty(ref _draftText, value)) NotifyDraftChanged();
        }
    }

    public bool CanEdit
    {
        get => _canEdit;
        private set => SetProperty(ref _canEdit, value);
    }

    /// <summary>User 段存在该键 = 用户覆盖；密码字段不参与覆盖/恢复语义。</summary>
    public bool IsOverridden
    {
        get => _isOverridden;
        private set => SetProperty(ref _isOverridden, value);
    }

    public bool IsRestoreStaged
    {
        get => _isRestoreStaged;
        private set
        {
            if (!SetProperty(ref _isRestoreStaged, value)) return;

            // 暂存态参与 IsDirty/校验：通知卡片重新计算可保存性。
            OnPropertyChanged(nameof(RestoreActionText));
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(ValidationError));
            OnPropertyChanged(nameof(HasValidationError));
        }
    }

    public bool CanRestoreDefault => CanEdit && IsOverridden && Kind != PluginFieldKind.Password;

    public string? ValidationError
    {
        get
        {
            if (Kind is PluginFieldKind.Text or PluginFieldKind.Password || IsRestoreStaged) return null;
            var text = DraftText.Trim();
            if (text.Length == 0) return null;
            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                return _minValue == 0 ? "必须为大于等于 0 的整数。" : "必须为正整数。";
            return number < _minValue ? _minValue == 0 ? "必须为大于等于 0 的整数。" : "必须为正整数。" : null;
        }
    }

    public bool HasValidationError => ValidationError is not null;

    public bool IsDirty
    {
        get
        {
            if (IsRestoreStaged) return true;
            return Kind                   == PluginFieldKind.Password
                ? DraftText.Trim().Length > 0
                : DraftText.Trim()        != _baselineText;
        }
    }

    // 凭据状态圆点（网页搜索卡）：查询 credentials.DescribeAsync 的配置态。
    public bool ShowCredentialDot => Kind == PluginFieldKind.Password && _credentialDotVisible;

    public bool IsCredentialSet => _credentialConfigured;

    public bool IsCredentialUnset => !_credentialConfigured;

    public string CredentialStatusText => _credentialConfigured ? "API 密钥已配置" : "API 密钥未配置";

    public string PasswordWatermark => _credentialConfigured ? "已配置——输入新值可替换" : "输入 API 密钥";

    /// <summary>草稿文本变化后刷新派生状态（脏、校验、占位）。</summary>
    private void NotifyDraftChanged()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(ValidationError));
        OnPropertyChanged(nameof(HasValidationError));
    }

    internal void SetCanEdit(bool value)
    {
        CanEdit = value;
        OnPropertyChanged(nameof(CanRestoreDefault));
    }

    /// <summary>从命名空间值装载基线与草稿（密码字段恒为空：留空 = 保留已存）。</summary>
    internal void ApplyValue(JsonElement value, JsonElement? userSegment)
    {
        _baselineText = Kind switch
        {
            PluginFieldKind.Text => SettingsValues.GetString(value, [Key]) ?? string.Empty,
            PluginFieldKind.PositiveInteger or PluginFieldKind.NonNegativeInteger
                => SettingsValues.GetInt64(value, [Key])?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            _ => string.Empty
        };
        _isRestoreStaged = false;
        DraftText        = Kind == PluginFieldKind.Password ? string.Empty : _baselineText;
        IsOverridden = Kind != PluginFieldKind.Password &&
                       userSegment is { } user          &&
                       SettingsValues.HasPath(user, [Key]);
        OnPropertyChanged(nameof(IsOverridden));
        OnPropertyChanged(nameof(IsRestoreStaged));
        OnPropertyChanged(nameof(CanRestoreDefault));
    }

    /// <summary>外部改动只刷新覆盖标记，草稿不动。</summary>
    internal void UpdateOverrideMark(JsonElement? userSegment)
    {
        IsOverridden = Kind != PluginFieldKind.Password &&
                       userSegment is { } user          &&
                       SettingsValues.HasPath(user, [Key]);
        OnPropertyChanged(nameof(CanRestoreDefault));
    }

    internal void ApplyCredentialStatus(bool configured)
    {
        _credentialConfigured = configured;
        _credentialDotVisible = true;
        OnPropertyChanged(nameof(ShowCredentialDot));
        OnPropertyChanged(nameof(IsCredentialSet));
        OnPropertyChanged(nameof(IsCredentialUnset));
        OnPropertyChanged(nameof(CredentialStatusText));
        OnPropertyChanged(nameof(PasswordWatermark));
    }

    internal void ClearDraft()
    {
        DraftText = string.Empty;
    }

    /// <summary>构建该字段的写操作：未变化或密码字段返回 null；恢复默认返回 unset。</summary>
    internal SettingsMutationOp? BuildOp()
    {
        if (Kind == PluginFieldKind.Password) return null;
        if (IsRestoreStaged) return SettingsMutationOp.Unset([Key]);
        if (!IsDirty) return null;

        var normalized = DraftText.Trim();
        return Kind switch
        {
            PluginFieldKind.Text => SettingsMutationOp.Set([Key], JsonElementFactory.FromString(normalized)),
            PluginFieldKind.PositiveInteger or PluginFieldKind.NonNegativeInteger
                when long.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                => SettingsMutationOp.Set([Key], JsonElementFactory.FromInt64(number)),
            _ => null
        };
    }

    private void ToggleRestoreStaged()
    {
        if (!CanRestoreDefault) return;

        if (IsRestoreStaged)
        {
            IsRestoreStaged = false;
            DraftText       = _baselineText;
        }
        else
        {
            IsRestoreStaged = true;
            DraftText       = string.Empty;
        }
    }
}
