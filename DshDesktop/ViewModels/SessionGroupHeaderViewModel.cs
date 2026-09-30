using System.Windows.Input;

namespace DshDesktop.ViewModels;

/// <summary>会话列表的分组标题行；成员行是否显示由列表投影按展开态决定。</summary>
public sealed class SessionGroupHeaderViewModel(
    string   key,
    string   title,
    int      sessionCount,
    bool     isExpanded,
    ICommand toggleCommand,
    bool     isWorkspace,
    bool     isCurrent = false)
{
    /// <summary>分组标识；工作区 id 或未分组的固定哨兵值。</summary>
    public string Key { get; } = key;

    public string TitleText { get; } = title;

    public int SessionCount { get; } = sessionCount;

    public bool IsExpanded { get; } = isExpanded;

    /// <summary>该组是否包含当前选中的会话；驱动工作区行的选中高亮。</summary>
    public bool IsCurrent { get; } = isCurrent;

    public bool IsWorkspace { get; } = isWorkspace;

    public ICommand ToggleCommand { get; } = toggleCommand;
}
