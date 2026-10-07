using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using DshDesktop.ViewModels;

namespace DshDesktop.Presentation.Views;

public partial class AboutPanelView : UserControl
{
    public AboutPanelView()
    {
        InitializeComponent();
    }

    /// <summary>覆盖层由宿主以 IsVisible 开合：变为可见后聚焦面板根，让 Esc 立即可用。</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && change.NewValue is true)
            Dispatcher.UIThread.Post(() => PanelRoot.Focus(), DispatcherPriority.Input);
    }

    /// <summary>Esc 等价关闭（焦点在面板内任意控件时经冒泡到达此处）。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is MainWindowViewModel viewModel)
        {
            e.Handled = true;
            viewModel.CloseAboutCommand.Execute(null);
            return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>遮罩左键点击关闭面板；面板本体是遮罩的兄弟节点，点击不会冒泡到这里。</summary>
    private void OnMaskPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;

        e.Handled = true;
        if (DataContext is MainWindowViewModel viewModel) viewModel.CloseAboutCommand.Execute(null);
    }
}
