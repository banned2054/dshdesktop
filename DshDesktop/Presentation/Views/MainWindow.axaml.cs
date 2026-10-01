using Avalonia.Controls;
using DshDesktop.Infrastructure.Services;
using DshDesktop.ViewModels;

namespace DshDesktop.Presentation.Views;

/// <summary>主窗口：组合侧栏与会话主区域，并承担跨两个视图的窗口级布局——
/// 侧栏列宽 clamp、拖拽上限与抽屉式折叠。</summary>
public partial class MainWindow : Window
{
    // 侧栏拖拽/折叠常量：宽度上限 = min(480, 可用宽 - 右栏最小 560)；
    // splitter 布局列宽 0，命中区悬跨分界线、不占布局宽度。
    private const double SidebarMinWidth     = 240;
    private const double SidebarMaxWidth     = 480;
    private const double SidebarDefaultWidth = 280;
    private const double ContentMinWidth     = 560;

    private bool   _isSidebarCollapsed;
    private double _lastSidebarWidth = SidebarDefaultWidth;

    // Avalonia 不为 ColumnDefinition 的 x:Name 生成字段，按位置取列。
    private ColumnDefinition SidebarColumn => ContentGrid.ColumnDefinitions[0];

    public MainWindow() : this(new MainWindowViewModel(new SimulatedSessionService(),
                                                       new SimulatedBackendStatusService(),
                                                       new SimulatedWorkspaceService(),
                                                       new SimulatedToolApprovalService(),
                                                       isSimulatedMode : true,
                                                       // 预览器路径：置顶落盘到临时目录，避免污染真实用户配置。
                                                       sidebarPinService :
                                                       new SidebarPinService(Path.Combine(Path.GetTempPath(),
                                                                                      "DshDesktop.Preview",
                                                                                      "sidebar-pins.json"))))
    {
    }

    public MainWindow(MainWindowViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        TitleBar.SidebarToggleRequested += OnSidebarToggleRequested;
        RootGrid.SizeChanged            += OnRootGridSizeChanged;
    }

    /// <summary>窗口尺寸变化后重新 clamp 左栏宽度；折叠状态下保持整列隐藏，不介入。</summary>
    private void OnRootGridSizeChanged(object? sender, SizeChangedEventArgs e) => ClampSidebarWidth();

    private void OnSidebarToggleRequested(object? sender, EventArgs e)
    {
        if (_isSidebarCollapsed)
        {
            ExpandSidebar();
        }
        else
        {
            CollapseSidebar();
        }
    }

    /// <summary>折叠：左栏与 splitter 隐藏、左列连同 MinWidth 清零，右侧填满；
    /// 列的 MinWidth 不清零时 Width=0 会被钉在 240，露出窗口原始背景。</summary>
    private void CollapseSidebar()
    {
        _isSidebarCollapsed = true;
        if (SidebarColumn.Width.IsAbsolute)
        {
            _lastSidebarWidth = SidebarColumn.Width.Value;
        }

        SidebarColumn.MinWidth    = 0;
        SidebarColumn.Width       = new GridLength(0);
        Sidebar.IsVisible         = false;
        SidebarSplitter.IsVisible = false;
        TitleBar.SetSidebarCollapsed(true);
    }

    /// <summary>展开：恢复 MinWidth 下限与上次宽度并按当前窗口 clamp；splitter 命中区随之恢复。</summary>
    private void ExpandSidebar()
    {
        _isSidebarCollapsed       = false;
        SidebarColumn.MinWidth    = SidebarMinWidth;
        SidebarColumn.Width       = new GridLength(_lastSidebarWidth, GridUnitType.Pixel);
        Sidebar.IsVisible         = true;
        SidebarSplitter.IsVisible = true;
        ClampSidebarWidth();
        TitleBar.SetSidebarCollapsed(false);
    }

    /// <summary>
    /// 左栏宽度 clamp 到 [240, min(480, 可用宽-560)]；上限同步写入列与左栏内容的
    /// MaxWidth，让 GridSplitter 拖拽在右栏最小宽 560 之前停下。
    /// </summary>
    private void ClampSidebarWidth()
    {
        if (_isSidebarCollapsed)
        {
            return;
        }

        var available = ContentGrid.Bounds.Width;
        var upper     = Math.Max(SidebarMinWidth, Math.Min(SidebarMaxWidth, available - ContentMinWidth));
        SidebarColumn.MaxWidth = upper;
        Sidebar.SetSurfaceMaxWidth(upper);
        var current = SidebarColumn.Width.IsAbsolute ? SidebarColumn.Width.Value : SidebarDefaultWidth;
        SidebarColumn.Width = new GridLength(Math.Clamp(current, SidebarMinWidth, upper), GridUnitType.Pixel);
        _lastSidebarWidth   = SidebarColumn.Width.Value;
    }
}
