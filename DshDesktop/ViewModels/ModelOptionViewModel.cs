using DshDesktop.Core.Models;
using System.Windows.Input;

namespace DshDesktop.ViewModels;

/// <summary>模型菜单的一个可选项：某提供方内一个模型的投影。</summary>
public sealed class ModelOptionViewModel : ObservableObject
{
    private bool _isSelected;

    public ModelOptionViewModel(
        string                        provider,      string              providerName, string model, string modelName,
        Action<ModelOptionViewModel>? select = null, ModelReasoningInfo? reasoning = null)
    {
        Provider      = provider;
        ProviderName  = providerName;
        Model         = model;
        ModelName     = modelName;
        Reasoning     = reasoning;
        SelectCommand = select is null ? null : new RelayCommand(() => select(this));
    }

    public string Provider { get; }

    public string ProviderName { get; }

    public string Model { get; }

    public string ModelName { get; }

    /// <summary>模型的推理档位元数据（目录下发）；目录外选型为 null（任何显式档位都会被后端拒绝）。</summary>
    public ModelReasoningInfo? Reasoning { get; }

    /// <summary>解析跨模型切换携带的档位：不支持回退该模型默认档位；无元数据省略（null）。</summary>
    public string? ResolveEffort(string? requested)
    {
        return Reasoning?.Resolve(requested);
    }

    /// <summary>菜单行点击命令；由 ComposerViewModel 注入选型回调。</summary>
    public ICommand? SelectCommand { get; }

    /// <summary>是否为当前生效选型（provider/model 相同即勾选，档位不影响模型勾选）。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>选型是否与本项一致（provider/model 相同即视为同一项）。</summary>
    public bool Matches(ModelSelection selection)
    {
        return selection.Provider == Provider && selection.Model == Model;
    }
}
