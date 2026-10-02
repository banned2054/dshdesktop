using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DshDesktop.ViewModels.Settings;

namespace DshDesktop.Presentation.Views.Settings;

/// <summary>
///     设置面板视图的界面行为：遮罩点击关闭、Esc 关闭、打开时聚焦面板根、
///     以及随宿主尺寸把面板 MaxWidth/MaxHeight 钳制为宿主 − 48（小窗不溢出）。
///     业务状态与命令在 <see cref="SettingsPanelViewModel" />；关闭请求经 CloseRequested
///     交宿主（MainWindowViewModel）把 IsSettingsOpen 置回 false。
/// </summary>
public partial class SettingsPanelView : UserControl
{
    private const double PanelMargin = 48;
    private const double MinClampedSize = 240;

    public SettingsPanelView()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
    }

    /// <summary>覆盖层由宿主以 IsVisible 开合：变为可见后聚焦面板根，让 Esc 与键盘导航立即可用。</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && change.NewValue is true)
            Dispatcher.UIThread.Post(() => PanelRoot.Focus(), DispatcherPriority.Input);
    }

    /// <summary>覆盖层自身随宿主布局变化：钳制面板最大尺寸，小窗口下居中面板不溢出。</summary>
    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        PanelRoot.MaxWidth  = Math.Max(MinClampedSize, e.NewSize.Width  - PanelMargin);
        PanelRoot.MaxHeight = Math.Max(MinClampedSize, e.NewSize.Height - PanelMargin);
    }

    /// <summary>Esc 等价关闭（焦点在面板内任意输入控件时经冒泡到达此处）。</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is SettingsPanelViewModel viewModel)
        {
            e.Handled = true;
            viewModel.CloseCommand.Execute(null);
            return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>遮罩左键点击关闭面板；面板本体是遮罩的兄弟节点，点击不会冒泡到这里。</summary>
    private void OnMaskPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed) return;

        e.Handled = true;
        if (DataContext is SettingsPanelViewModel viewModel) viewModel.CloseCommand.Execute(null);
    }
}
