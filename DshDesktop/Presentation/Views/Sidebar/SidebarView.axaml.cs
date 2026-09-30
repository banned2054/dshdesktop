using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DshDesktop.Services.Platform;
using DshDesktop.Utils;
using DshDesktop.ViewModels;

namespace DshDesktop.Presentation.Views.Sidebar;

/// <summary>
///     侧栏视图：搜索行的展开聚焦与 Esc 收起、添加工作区的文件夹对话框选择属于界面
///     行为；过滤、弹层状态、登记编排与命令由 <see cref="SidebarViewModel" /> 与
///     <see cref="MainWindowViewModel" /> 承担。
/// </summary>
public partial class SidebarView : UserControl
{
    public SidebarView()
    {
        InitializeComponent();
        SearchButton.Click += OnSearchButtonClick;
        SessionSearchInput.AddHandler(KeyDownEvent, OnSearchInputKeyDown, RoutingStrategies.Tunnel);
        AddWorkspaceButton.Click += OnAddWorkspaceButtonClick;
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

    /// <summary>文件夹对话框取得路径后交给根 ViewModel 登记；取消即结束，异常由登记编排呈现。</summary>
    private void OnAddWorkspaceButtonClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;

        _ = FolderPicker.PickAndRegisterWorkspaceAsync(this, viewModel.RegisterWorkspaceAsync,
                                                       viewModel.ReportWorkspaceError);
    }
}
