using DshDesktop.Core.Models;

namespace DshDesktop.ViewModels;

/// <summary>
///     新对话草稿页模式下拉的一个选项（dsh 内置 Agent 预设）。点选只改本地草稿预选
///     （经 root 注入的命令），不调用 session/create；显示名与描述对齐 dsh Web 端
///     locales（ui-agent-preset），用户自定义预设待接入 agentPresets/list 后动态加入。
/// </summary>
public sealed class AgentPresetOptionViewModel(
    string                                   id,
    string                                   name,
    string                                   description,
    RelayCommand<AgentPresetOptionViewModel> selectCommand) : ObservableObject
{
    private bool _isSelected;

    /// <summary>预设 wire id（session/create 的 agentPreset 字段取值）。</summary>
    public string Id { get; } = id;

    /// <summary>模式显示名，如「标准模式」。</summary>
    public string Name { get; } = name;

    /// <summary>模式一句话说明（下拉第二行弱化文字）。</summary>
    public string Description { get; } = description;

    /// <summary>是否为草稿当前预选（下拉勾选态）；由 root 在预选变化时对齐。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public RelayCommand<AgentPresetOptionViewModel> SelectCommand { get; } = selectCommand;

    /// <summary>dsh 内置四模式；文案取自 Web 端 zh locales（preset*Name/Description）。</summary>
    public static IReadOnlyList<AgentPresetOptionViewModel> CreateBuiltIns(
        RelayCommand<AgentPresetOptionViewModel> selectCommand)
    {
        return
        [
            new AgentPresetOptionViewModel(AgentPresetModes.Standard, "标准模式",
                                           "处理代码、文件和资料，适合大多数任务。Agent 会按需使用检索、编辑和终端等工具。",
                                           selectCommand),
            new AgentPresetOptionViewModel(AgentPresetModes.Ptc, "PTC 模式",
                                           "包含标准模式的所有能力，更适合批量调用工具，并对结果进行筛选、整理、去重、统计或汇总的任务。",
                                           selectCommand),
            new AgentPresetOptionViewModel(AgentPresetModes.Minimal, "极简模式",
                                           "Agent 仅使用终端工具完成任务，适合测试和对比其基础表现。",
                                           selectCommand),
            new AgentPresetOptionViewModel(AgentPresetModes.Cordis, "创造模式",
                                           "用对话定制 DSH：让 Agent 编写插件，添加新功能或界面；也能组合工具和提示词，创建自己的模式。",
                                           selectCommand)
        ];
    }
}
