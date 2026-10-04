using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DshDesktop.Presentation.Views;

/// <summary>
///     窗口标题栏（MainWindow 级视图）。侧栏折叠开关只上报事件，
///     折叠/展开的窗口级协调见 MainWindow。
/// </summary>
public partial class WindowTitleBarView : UserControl
{
    public WindowTitleBarView()
    {
        InitializeComponent();
    }

    /// <summary>用户点击侧栏折叠/展开开关。</summary>
    public event EventHandler? SidebarToggleRequested;

    private void OnSidebarToggleClick(object? sender, RoutedEventArgs e)
    {
        SidebarToggleRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>开关按钮状态：图标、Tooltip 与可访问名称随折叠态互斥切换，不重复显示。</summary>
    public void SetSidebarCollapsed(bool collapsed)
    {
        var tip = collapsed ? "展开侧栏" : "收起侧栏";
        ToolTip.SetTip(SidebarToggle, tip);
        AutomationProperties.SetName(SidebarToggle, tip);
        CollapseSidebarIcon.IsVisible = !collapsed;
        ExpandSidebarIcon.IsVisible   = collapsed;
    }
}
