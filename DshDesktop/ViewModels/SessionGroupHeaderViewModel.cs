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
    bool     isCurrent  = false,
    bool     isCategory = false) : ObservableObject
{
    private string _titleText    = title;
    private int    _sessionCount = sessionCount;
    private bool   _isExpanded   = isExpanded;
    private bool   _isCurrent    = isCurrent;
    private bool   _pinned;

    /// <summary>分组标识；工作区 id、置顶分类或未分组的固定哨兵值。</summary>
    public string Key { get; } = key;

    public string TitleText
    {
        get => _titleText;
        private set => SetProperty(ref _titleText, value);
    }

    public int SessionCount
    {
        get => _sessionCount;
        private set => SetProperty(ref _sessionCount, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        private set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>该组是否包含当前选中的会话；驱动工作区行的选中高亮。</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        private set => SetProperty(ref _isCurrent, value);
    }

    public bool IsWorkspace { get; } = isWorkspace;

    /// <summary>
    ///     该行是否为分类头（「置顶」「工作区」「未分组」）：纯文字标题、文字右侧
    ///     展开箭头，无图标、无圆角矩形与悬停/选中高亮。工作区行恒为 false。
    /// </summary>
    public bool IsCategory { get; } = isCategory;

    /// <summary>
    ///     工作区是否已置顶（本地置顶注册表投影，由侧栏在重建行投影时同步）：
    ///     驱动工作区菜单置顶项的文案与图标形态。
    /// </summary>
    public bool Pinned
    {
        get => _pinned;
        private set
        {
            if (SetProperty(ref _pinned, value)) OnPropertyChanged(nameof(PinActionText));
        }
    }

    /// <summary>置顶动作的菜单文案：未置顶「置顶工作区」，已置顶「取消置顶」。</summary>
    public string PinActionText => Pinned ? "取消置顶" : "置顶工作区";

    public ICommand ToggleCommand { get; } = toggleCommand;

    internal void Update(string titleText, int count, bool expanded, bool current, bool pinned = false)
    {
        TitleText    = titleText;
        SessionCount = count;
        IsExpanded   = expanded;
        IsCurrent    = current;
        Pinned       = pinned;
    }
}
