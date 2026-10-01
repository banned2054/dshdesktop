using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DshDesktop.Services.Platform;
using DshDesktop.ViewModels;

namespace DshDesktop.Presentation.Views.Sidebar;

/// <summary>
///     侧栏视图：搜索行的展开聚焦与 Esc 收起、添加工作区的文件夹对话框选择、工作区/
///     会话菜单与重命名弹窗的开关聚焦属于界面行为；过滤、弹层状态、登记编排与命令由
///     <see cref="SidebarViewModel" /> 与 <see cref="MainWindowViewModel" /> 承担。
/// </summary>
public partial class SidebarView : UserControl
{
    public SidebarView()
    {
        InitializeComponent();
        SearchButton.Click += OnSearchButtonClick;
        SessionSearchInput.AddHandler(KeyDownEvent, OnSearchInputKeyDown, RoutingStrategies.Tunnel);
        AddWorkspaceButton.Click += OnAddWorkspaceButtonClick;
        WorkspaceRenameInput.AddHandler(KeyDownEvent, OnRenameInputKeyDown, RoutingStrategies.Tunnel);
        WorkspaceRenamePopup.Opened += OnWorkspaceRenamePopupOpened;
        WorkspaceRenamePopup.AddHandler(KeyDownEvent, OnRenamePopupKeyDown, RoutingStrategies.Tunnel);
        SessionRenameInput.AddHandler(KeyDownEvent, OnSessionRenameInputKeyDown, RoutingStrategies.Tunnel);
        SessionRenamePopup.Opened += OnSessionRenamePopupOpened;
        SessionRenamePopup.AddHandler(KeyDownEvent, OnSessionRenamePopupKeyDown, RoutingStrategies.Tunnel);
        WorkspaceDeletePopup.AddHandler(KeyDownEvent, OnDeletePopupKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>同步左栏内容表面的宽度上限；列宽上限由 MainWindow 的 clamp 逻辑统一计算。</summary>
    public void SetSurfaceMaxWidth(double maxWidth) => Surface.MaxWidth = maxWidth;

    /// <summary>展开搜索行后把焦点交给输入框（DSH 展开即聚焦）；命令已先行切换状态。</summary>
    private void OnSearchButtonClick(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(() => SessionSearchInput.Focus());

    private void OnSearchInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not MainWindowViewModel viewModel) return;

        e.Handled = true;
        viewModel.Sidebar.CloseSearchCommand.Execute(null);
    }

    /// <summary>文件夹对话框取得路径后交给根 ViewModel 登记；取消即结束，异常由登记编排呈现。</summary>
    private void OnAddWorkspaceButtonClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;

        _ = FolderPicker.PickAndRegisterWorkspaceAsync(this, viewModel.RegisterWorkspaceAsync,
                                                       viewModel.ReportWorkspaceError);
    }

    /// <summary>
    ///     左键三点按钮打开所在工作区行的菜单弹层：锚定按钮下缘左对齐，并加
    ///     menu-open 标记让悬浮按钮组保持显现——鼠标移入弹层后行 hover 即丢失，
    ///     锚点按钮随之隐藏会让弹层看起来悬空。
    /// </summary>
    private void OnWorkspaceMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Parent: Panel panel } ||
            panel.Children.OfType<Popup>().FirstOrDefault() is not { } popup)
            return;

        popup.Placement = PlacementMode.BottomEdgeAlignedLeft;
        OpenMenuPopup(popup, FindWorkspaceRow(panel), "menu-open");
    }

    /// <summary>右键工作区行同样打开菜单：位置左对齐点击处（Pointer 定位），非工作区分组不响应。</summary>
    private void OnWorkspaceRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsRightButtonPressed                                     ||
            sender is not Border { DataContext : SessionGroupHeaderViewModel { IsWorkspace: true } } row ||
            row.GetVisualDescendants().OfType<Popup>().FirstOrDefault() is not { } popup)
            return;

        e.Handled       = true;
        popup.Placement = PlacementMode.Pointer;
        OpenMenuPopup(popup, row, "menu-ctx-open");
    }

    /// <summary>
    ///     会话行三点按钮打开菜单弹层：锚定与 menu-open 防悬空处理同工作区菜单，
    ///     行标记落在 Button.session-row 上（操作条显隐样式按该类选择）。
    /// </summary>
    private void OnSessionMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Parent: Panel panel } ||
            panel.Children.OfType<Popup>().FirstOrDefault() is not { } popup)
            return;

        popup.Placement = PlacementMode.BottomEdgeAlignedLeft;
        OpenMenuPopup(popup, FindSessionRow(panel), "menu-open");
    }

    /// <summary>右键会话行同样打开菜单：位置贴点击处（Pointer 定位），空白会话行不响应。</summary>
    private void OnSessionRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsRightButtonPressed                           ||
            sender is not Button { DataContext : SessionItemViewModel { IsBlank: false } } row ||
            row.GetVisualDescendants().OfType<Popup>().FirstOrDefault() is not { } popup)
            return;

        e.Handled       = true;
        popup.Placement = PlacementMode.Pointer;
        OpenMenuPopup(popup, row, "menu-ctx-open");
    }

    /// <summary>
    ///     打开弹层并按 openClass 标记所在行、关闭时移除：menu-open 让悬浮按钮组
    ///     保持显现，menu-ctx-open 在弹层打开期间隐藏按钮组。已开实例先收起，
    ///     保证切换开合方式时行标记与定位一致。
    /// </summary>
    private static void OpenMenuPopup(Popup popup, Control? row, string openClass)
    {
        if (row is not null)
        {
            row.Classes.Add(openClass);

            void OnPopupClosed(object? _, EventArgs __)
            {
                popup.Closed -= OnPopupClosed;
                row.Classes.Remove(openClass);
            }

            popup.Closed += OnPopupClosed;
        }

        if (popup.IsOpen)
            popup.IsOpen = false;
        popup.IsOpen = true;
    }

    private static Border? FindWorkspaceRow(Control start) =>
        start.GetVisualAncestors().OfType<Border>().FirstOrDefault(ancestor => ancestor.Classes.Contains("group-row"));

    private static Button? FindSessionRow(Control start) =>
        start.GetVisualAncestors().OfType<Button>()
             .FirstOrDefault(ancestor => ancestor.Classes.Contains("session-row"));

    /// <summary>菜单项点击后收起所属菜单；业务动作仍由命令绑定执行。</summary>
    private void OnWorkspaceMenuItemClick(object? sender, RoutedEventArgs e) => CloseOwningMenuPopup(sender);

    /// <summary>会话菜单项点击后收起所属菜单；业务动作仍由命令绑定执行。</summary>
    private void OnSessionMenuItemClick(object? sender, RoutedEventArgs e) => CloseOwningMenuPopup(sender);

    private static void CloseOwningMenuPopup(object? sender)
    {
        if (sender is Control item &&
            item.GetLogicalAncestors().OfType<Popup>().FirstOrDefault() is { } popup)
            popup.IsOpen = false;
    }

    /// <summary>重命名弹窗展开即聚焦输入框并全选当前名（对齐 DSH 重命名 Modal）。</summary>
    private void OnWorkspaceRenamePopupOpened(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        WorkspaceRenameInput.Focus();
        WorkspaceRenameInput.SelectAll();
    });

    /// <summary>会话重命名弹窗展开即聚焦输入框并全选当前标题（同工作区重命名弹窗）。</summary>
    private void OnSessionRenamePopupOpened(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        SessionRenameInput.Focus();
        SessionRenameInput.SelectAll();
    });

    /// <summary>重命名输入框内 Enter 直接确认（等价点击「重命名」，守卫在命令方法内）。</summary>
    private void OnRenameInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not MainWindowViewModel viewModel) return;

        e.Handled = true;
        viewModel.Sidebar.ConfirmWorkspaceRenameCommand.Execute(null);
    }

    /// <summary>会话重命名输入框内 Enter 直接确认（等价点击「重命名」，守卫在命令方法内）。</summary>
    private void OnSessionRenameInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not MainWindowViewModel viewModel) return;

        e.Handled = true;
        viewModel.Sidebar.ConfirmSessionRenameCommand.Execute(null);
    }

    /// <summary>轻失焦关不掉的 Esc 由 code-behind 兜底：重命名弹窗 Esc 等价取消（ModelPicker 同款）。</summary>
    private void OnRenamePopupKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not MainWindowViewModel viewModel) return;

        e.Handled = true;
        viewModel.Sidebar.CancelWorkspaceRenameCommand.Execute(null);
    }

    /// <summary>会话重命名弹窗的 Esc 兜底：等价取消（工作区重命名弹窗同款）。</summary>
    private void OnSessionRenamePopupKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not MainWindowViewModel viewModel) return;

        e.Handled = true;
        viewModel.Sidebar.CancelSessionRenameCommand.Execute(null);
    }

    /// <summary>删除确认弹窗的 Esc 兜底：等价取消，不发送任何请求。</summary>
    private void OnDeletePopupKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not MainWindowViewModel viewModel) return;

        e.Handled = true;
        viewModel.Sidebar.CancelWorkspaceDeleteCommand.Execute(null);
    }
}
