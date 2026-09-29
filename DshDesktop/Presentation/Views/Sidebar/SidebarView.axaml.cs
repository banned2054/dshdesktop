using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DshDesktop.ViewModels;

namespace DshDesktop.Presentation.Views.Sidebar;

/// <summary>
///     侧栏视图：搜索行的展开聚焦与 Esc 收起属于界面行为；过滤、弹层状态与命令
///     由 <see cref="SidebarViewModel" /> 承担。
/// </summary>
public partial class SidebarView : UserControl
{
    public SidebarView()
    {
        InitializeComponent();
        SearchButton.Click += OnSearchButtonClick;
        SessionSearchInput.AddHandler(KeyDownEvent, OnSearchInputKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>同步左栏内容表面的宽度上限；列宽上限由 MainWindow 的 clamp 逻辑统一计算。</summary>
    public void SetSurfaceMaxWidth(double maxWidth) => Surface.MaxWidth = maxWidth;

    /// <summary>展开搜索行后把焦点交给输入框（DSH 展开即聚焦）；命令已先行切换状态。</summary>
    private void OnSearchButtonClick(object? sender, RoutedEventArgs e)
        => Dispatcher.UIThread.Post(() => SessionSearchInput.Focus());

    private void OnSearchInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not MainWindowViewModel viewModel) return;

        e.Handled = true;
        viewModel.Sidebar.CloseSearchCommand.Execute(null);
    }
}
