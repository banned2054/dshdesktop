using DshDesktop.Core.Models;

namespace DshDesktop.ViewModels;

/// <summary>
///     执行权限下拉的一个预设选项。DisplayName 沿用 WebUI 的展示映射（ui-permission-presets
///     presentation.ts）：内置三键在宿主未定制名称（裸键名或英文默认标签）时用产品化中文
///     标签；定制名与未知值透传展示（kebab-case 转 Title Case，如 custom → Custom）。
/// </summary>
public sealed class PermissionOptionViewModel : ObservableObject
{
    private static readonly IReadOnlyDictionary<string, string> BuiltinLabels =
        new Dictionary<string, string>
        {
            [PermissionPresetValues.ReadOnly]       = "仅可查看",
            [PermissionPresetValues.WorkspaceWrite] = "工作区内修改",
            [PermissionPresetValues.FullAccess]     = "完全权限"
        };

    private static readonly IReadOnlyDictionary<string, string> EnglishDefaultLabels =
        new Dictionary<string, string>
        {
            [PermissionPresetValues.ReadOnly]       = "Read Only",
            [PermissionPresetValues.WorkspaceWrite] = "Workspace Write",
            [PermissionPresetValues.FullAccess]     = "Full access"
        };

    private readonly Action<PermissionOptionViewModel> _select;
    private          bool                              _isSelected;

    public PermissionOptionViewModel(
        string value, string name, string? description, Action<PermissionOptionViewModel> select)
    {
        Value       = value;
        Name        = name;
        Description = description;
        _select     = select;
        // 捕获 this 的无参命令：不依赖 XAML 的 CommandParameter 传参
        // （草稿页下拉曾因漏 CommandParameter 出现过「条目点击无效果」）。
        SelectCommand = new RelayCommand(() => _select(this));
    }

    /// <summary>预设 wire 值（permissions 投影与 /permission 命令的取值）。</summary>
    public string Value { get; }

    /// <summary>目录携带的原始展示名（宿主定制名透传）。</summary>
    public string Name { get; }

    /// <summary>目录携带的一句话说明（可空；作行悬停提示）。</summary>
    public string? Description { get; }

    public string DisplayName => DisplayPermissionPreset(Value, Name);

    /// <summary>实验性预设（auto）角标；对齐 WebUI 的 EXP badge。</summary>
    public bool IsExperimental => Value == PermissionPresetValues.AutoReview;

    /// <summary>是否为投影权威的当前值（下拉勾选态）。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>菜单行点击命令；经选择器统一入口（含确认门）。</summary>
    public RelayCommand SelectCommand { get; }

    /// <summary>内置键的产品化展示判定（宿主定制名与英文默认标签不劫持，其余透传）。</summary>
    internal static string DisplayPermissionPreset(string value, string name)
    {
        if (BuiltinLabels.TryGetValue(value, out var label) &&
            (string.Equals(name, value, StringComparison.Ordinal) ||
             string.Equals(name, EnglishDefaultLabels[value], StringComparison.Ordinal)))
            return label;

        return DisplayPresetName(name);
    }

    /// <summary>kebab-case 键转 Title Case（custom → Custom）；其余原样返回。</summary>
    internal static string DisplayPresetName(string name)
    {
        if (!IsConventionalKey(name)) return name;

        return string.Join(' ', name.Split('-')
                                    .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    }

    /// <summary>惯用键形态 ^[a-z0-9]+(-[a-z0-9]+)*$（对齐 displayPresetName 的正则判定）。</summary>
    private static bool IsConventionalKey(string name)
    {
        if (name.Length == 0) return false;

        return name.Split('-').All(group => group.Length > 0 &&
                                            group.All(IsLowercaseAlphanumeric));
    }

    private static bool IsLowercaseAlphanumeric(char character)
    {
        return character is (>= 'a' and <= 'z') or (>= '0' and <= '9');
    }
}
