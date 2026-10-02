using DshDesktop.Core.Models;
using DshDesktop.Utils;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Input;

namespace DshDesktop.ViewModels.Settings;

/// <summary>设置分区：左导航的一项，拥有自己的行/卡投影。导航选中与内容激活标记由面板壳维护。</summary>
public abstract class SettingsSectionViewModel : ObservableObject
{
    private bool _isActive;
    private bool _isSelected;

    protected SettingsSectionViewModel(string id, string title, string? intro = null)
    {
        Id    = id;
        Title = title;
        Intro = intro;
    }

    public string Id { get; }

    public string Title { get; }

    /// <summary>分区副标题（内容区顶部说明行）；可为空。</summary>
    public string? Intro { get; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    /// <summary>当前分区是否为激活内容；同一时刻至多一个分区激活。</summary>
    public bool IsActive
    {
        get => _isActive;
        internal set => SetProperty(ref _isActive, value);
    }

    /// <summary>导航点击命令（由面板壳装配）。</summary>
    public ICommand? SelectCommand { get; internal set; }
}

/// <summary>通用分区的行基类：标题/描述、ns 存在性驱动的可见性、只读降级与行内错误。</summary>
public abstract class SettingsRowViewModel : ObservableObject
{
    private string? _errorText;
    private bool    _isLast;
    private bool    _isVisible = true;
    private bool    _canEdit   = true;

    protected SettingsRowViewModel(string ns, IReadOnlyList<string> path, string title, string? description = null)
    {
        Ns    = ns;
        Path  = path;
        Title = title;
        Description = description;
    }

    public string Ns { get; }

    public IReadOnlyList<string> Path { get; }

    public string Title { get; }

    public string? Description { get; }

    /// <summary>describe 中存在所属 ns 时该行可见；ns 缺失则整行隐藏。</summary>
    public bool IsVisible
    {
        get => _isVisible;
        internal set => SetProperty(ref _isVisible, value);
    }

    /// <summary>后端报告只读（或行不可编辑）时写控件禁用。</summary>
    public bool CanEdit
    {
        get => _canEdit;
        internal set => SetProperty(ref _canEdit, value);
    }

    /// <summary>末行标记：末行不绘制底部分隔线；由分区投影按可见行序计算。</summary>
    public bool IsLast
    {
        get => _isLast;
        internal set => SetProperty(ref _isLast, value);
    }

    /// <summary>行内错误（写失败/冲突回滚后的提示）；下次成功写或重投影清除。</summary>
    public string? ErrorText
    {
        get => _errorText;
        internal set
        {
            if (SetProperty(ref _errorText, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    /// <summary>写入口（由面板壳装配）：值变化后按 ns 串行队列提交。</summary>
    internal Func<SettingsRowViewModel, JsonElement, Task>? WriteSink { get; set; }

    protected Task WriteValueAsync(JsonElement value)
    {
        return WriteSink?.Invoke(this, value) ?? Task.CompletedTask;
    }

    internal void ClearError()
    {
        ErrorText = null;
    }

    /// <summary>从命名空间视图重投影该行的当前值（写成功回流、外部刷新与打开面板共用）。</summary>
    internal abstract void ApplyView(SettingsNamespaceView view);
}

/// <summary>下拉选项条目：固定 value/label，勾选态由所属行随投影刷新。</summary>
public sealed class SettingsChoiceOptionViewModel : ObservableObject
{
    private bool _isSelected;

    public SettingsChoiceOptionViewModel(string value, string label)
    {
        Value = value;
        Label = label;
    }

    public string Value { get; }

    public string Label { get; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    public ICommand? SelectCommand { get; internal set; }
}

/// <summary>下拉选择行：按钮 + 弹层选项；选中即写（含 full 权限的风险确认门槛）。</summary>
public sealed class SettingsChoiceRowViewModel : SettingsRowViewModel
{
    private readonly Func<string, string>  _labelResolver;
    private readonly Func<string, bool>?   _requiresConfirmation;
    private          string?               _currentValue;
    private          string?               _pendingConfirmValue;
    private          bool                  _isAcknowledged;
    private          bool                  _isConfirmOpen;
    private          bool                  _isMenuOpen;

    public SettingsChoiceRowViewModel(
        string                            ns,
        string                            field,
        string                            title,
        string?                           description,
        IReadOnlyList<(string Value, string Label)> options,
        Func<string, string>?             labelResolver       = null,
        Func<string, bool>?               requiresConfirmation = null)
        : base(ns, [field], title, description)
    {
        _labelResolver       = labelResolver ?? (value => value);
        _requiresConfirmation = requiresConfirmation;
        Options              = new ObservableCollection<SettingsChoiceOptionViewModel>(
            options.Select(option => CreateOption(option.Value, option.Label)));
        ToggleMenuCommand      = new RelayCommand(() => IsMenuOpen = !IsMenuOpen);
        CancelConfirmCommand   = new RelayCommand(() => IsConfirmOpen = false);
        ConfirmSelectionCommand = new RelayCommand(ConfirmPendingSelection);
    }

    public ObservableCollection<SettingsChoiceOptionViewModel> Options { get; }

    public RelayCommand ToggleMenuCommand { get; }

    /// <summary>风险确认弹层的取消：放弃待确认值，不发送任何请求。</summary>
    public RelayCommand CancelConfirmCommand { get; }

    /// <summary>风险确认弹层的允许：必勾确认后提交待确认值。</summary>
    public RelayCommand ConfirmSelectionCommand { get; }

    public bool IsMenuOpen
    {
        get => _isMenuOpen;
        set => SetProperty(ref _isMenuOpen, value);
    }

    public bool IsConfirmOpen
    {
        get => _isConfirmOpen;
        set => SetProperty(ref _isConfirmOpen, value);
    }

    /// <summary>风险确认弹层的必勾项；每次打开弹层重置。</summary>
    public bool IsAcknowledged
    {
        get => _isAcknowledged;
        set => SetProperty(ref _isAcknowledged, value);
    }

    /// <summary>按钮文案：当前值的显示名（含遗留值映射），缺省时提示未设置。</summary>
    public string CurrentLabel => _currentValue is { } value ? _labelResolver(value) : "未设置";

    public string? CurrentValue => _currentValue;

    internal void SelectOption(SettingsChoiceOptionViewModel option)
    {
        if (!CanEdit) return;

        IsMenuOpen = false;
        if (option.Value == _currentValue) return;

        if (_requiresConfirmation?.Invoke(option.Value) == true)
        {
            _pendingConfirmValue = option.Value;
            IsAcknowledged       = false;
            IsConfirmOpen        = true;
            return;
        }

        _ = WriteValueAsync(JsonElementFactory.FromString(option.Value));
    }

    /// <summary>重建选项（权限行候选取自 schema.choices，投影时刷新）。</summary>
    internal void SetOptions(IReadOnlyList<(string Value, string Label)> options)
    {
        Options.Clear();
        foreach (var option in options) Options.Add(CreateOption(option.Value, option.Label));
        RefreshSelection();
    }

    internal override void ApplyView(SettingsNamespaceView view)
    {
        _currentValue = SettingsValues.GetString(view.Value, Path);
        RefreshSelection();
        OnPropertyChanged(nameof(CurrentLabel));
    }

    private void ConfirmPendingSelection()
    {
        IsConfirmOpen = false;
        if (_pendingConfirmValue is { } value) _ = WriteValueAsync(JsonElementFactory.FromString(value));

        _pendingConfirmValue = null;
    }

    private void RefreshSelection()
    {
        foreach (var option in Options) option.IsSelected = option.Value == _currentValue;
    }

    private SettingsChoiceOptionViewModel CreateOption(string value, string label)
    {
        var option = new SettingsChoiceOptionViewModel(value, label);
        option.SelectCommand = new RelayCommand(() => SelectOption(option));
        return option;
    }
}

/// <summary>选择块行（外观主题）：light/dark/system 三个图标+文字块，点选即写。</summary>
public sealed class SettingsSegmentRowViewModel : SettingsRowViewModel
{
    private string? _currentValue;

    public SettingsSegmentRowViewModel(string ns, string field, string title, string? description)
        : base(ns, [field], title, description)
    {
        SelectLightCommand  = new RelayCommand(() => Select("light"));
        SelectDarkCommand   = new RelayCommand(() => Select("dark"));
        SelectSystemCommand = new RelayCommand(() => Select("system"));
    }

    public RelayCommand SelectLightCommand { get; }

    public RelayCommand SelectDarkCommand { get; }

    public RelayCommand SelectSystemCommand { get; }

    public bool IsLightSelected => _currentValue == "light";

    public bool IsDarkSelected => _currentValue == "dark";

    public bool IsSystemSelected => _currentValue == "system";

    private void Select(string value)
    {
        if (!CanEdit || _currentValue == value) return;

        _ = WriteValueAsync(JsonElementFactory.FromString(value));
    }

    internal override void ApplyView(SettingsNamespaceView view)
    {
        _currentValue = SettingsValues.GetString(view.Value, Path);
        OnPropertyChanged(nameof(IsLightSelected));
        OnPropertyChanged(nameof(IsDarkSelected));
        OnPropertyChanged(nameof(IsSystemSelected));
    }
}

/// <summary>开关行：自绘 ToggleSwitch 即时写；值缺省视为默认值（不回写）。</summary>
public sealed class SettingsToggleRowViewModel : SettingsRowViewModel
{
    private readonly bool _defaultValue;
    private          bool _isChecked;
    private          bool _isProjecting;

    public SettingsToggleRowViewModel(string ns, string field, string title, string? description,
                                      bool defaultValue = false)
        : base(ns, [field], title, description)
    {
        _defaultValue = defaultValue;
        _isChecked    = defaultValue;
    }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (!SetProperty(ref _isChecked, value)) return;

            // 投影装载属于恢复既值，不构成新的用户意图。
            if (!_isProjecting && CanEdit) _ = WriteValueAsync(JsonElementFactory.FromBoolean(value));
        }
    }

    internal override void ApplyView(SettingsNamespaceView view)
    {
        _isProjecting = true;
        try
        {
            IsChecked = SettingsValues.GetBoolean(view.Value, Path) ?? _defaultValue;
        }
        finally
        {
            _isProjecting = false;
        }
    }
}

/// <summary>只读展示行（当前版本等）：不参与写入。</summary>
public sealed class SettingsReadOnlyRowViewModel : SettingsRowViewModel
{
    public SettingsReadOnlyRowViewModel(string title, string value) : base("", [], title)
    {
        Value = value;
    }

    public string Value { get; }

    internal override void ApplyView(SettingsNamespaceView view)
    {
    }
}

/// <summary>命名空间视图中的标量读取辅助：按路径逐层取值，类型不符返回 null。</summary>
public static class SettingsValues
{
    public static string? GetString(JsonElement value, IReadOnlyList<string> path)
    {
        return TryGet(value, path, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;
    }

    public static bool? GetBoolean(JsonElement value, IReadOnlyList<string> path)
    {
        return TryGet(value, path, out var node) && node.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? node.GetBoolean()
            : null;
    }

    public static long? GetInt64(JsonElement value, IReadOnlyList<string> path)
    {
        return TryGet(value, path, out var node) && node.ValueKind == JsonValueKind.Number
            ? node.GetInt64()
            : null;
    }

    public static bool HasPath(JsonElement value, IReadOnlyList<string> path)
    {
        return TryGet(value, path, out _);
    }

    /// <summary>按路径取节点（存在但类型不限）；缺失返回 null。供嵌套路由对象（providers.&lt;route&gt;）读取。</summary>
    public static JsonElement? GetNode(JsonElement value, IReadOnlyList<string> path)
    {
        return TryGet(value, path, out var node) ? node : null;
    }

    /// <summary>读取命名空间值中的 models 数组；缺失或非数组返回 null。</summary>
    public static JsonElement? TryGetModels(JsonElement value)
    {
        return value.ValueKind == JsonValueKind.Object &&
               value.TryGetProperty("models", out var models) &&
               models.ValueKind == JsonValueKind.Array
            ? models
            : null;
    }

    private static bool TryGet(JsonElement root, IReadOnlyList<string> path, out JsonElement node)
    {
        node = root;
        foreach (var segment in path)
        {
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(segment, out node)) return false;
        }

        return true;
    }
}
