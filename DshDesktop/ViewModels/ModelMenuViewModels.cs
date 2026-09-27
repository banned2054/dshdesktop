using System.Windows.Input;

namespace DshDesktop.ViewModels;

/// <summary>模型二级菜单里一个提供方分组：分组标题 + 组内可选项。</summary>
public sealed class ModelGroupMenuViewModel(string name, IReadOnlyList<ModelOptionViewModel> options)
{
    public string Name { get; } = name;

    public IReadOnlyList<ModelOptionViewModel> Options { get; } = options;
}

/// <summary>推理等级二级菜单项：wire 取值 + 展示名 + 当前勾选。</summary>
public sealed class EffortOptionViewModel : ObservableObject
{
    private bool _isSelected;

    public EffortOptionViewModel(string value, string label, Action<EffortOptionViewModel> select)
    {
        Value         = value;
        Label         = label;
        SelectCommand = new RelayCommand(() => select(this));
    }

    /// <summary>session/selectModel 的 reasoningEffort wire 取值。</summary>
    public string Value { get; }

    public string Label { get; }

    public ICommand SelectCommand { get; }

    /// <summary>是否为当前生效档位（以后端回声为准）。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
