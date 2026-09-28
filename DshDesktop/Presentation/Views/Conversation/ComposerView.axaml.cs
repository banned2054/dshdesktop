using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DshDesktop.ViewModels;

namespace DshDesktop.Presentation.Views.Conversation;

/// <summary>底部输入区：Enter 发送与输入框焦点反馈属于本视图的界面行为。</summary>
public partial class ComposerView : UserControl
{
    public ComposerView()
    {
        InitializeComponent();
        MessageInput.AddHandler(KeyDownEvent, OnMessageInputKeyDown, RoutingStrategies.Tunnel);
        MessageInput.GotFocus  += OnMessageInputGotFocus;
        MessageInput.LostFocus += OnMessageInputLostFocus;
    }

    private void OnMessageInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        if (DataContext is MainWindowViewModel viewModel && viewModel.Composer.SendMessageCommand.CanExecute(null))
        {
            e.Handled = true;
            viewModel.Composer.SendMessageCommand.Execute(null);
        }
    }

    /// <summary>
    /// 输入框焦点变化时切换面板 focused 类：聚焦反馈由整块面板的克制描边承担，
    /// 输入框自身保持透明无边框（对应 WebUI composer 的卡片级聚焦高亮）。
    /// </summary>
    private void OnMessageInputGotFocus(object? sender, FocusChangedEventArgs e) => SetComposerFocused(true);

    private void OnMessageInputLostFocus(object? sender, RoutedEventArgs e) => SetComposerFocused(false);

    private void SetComposerFocused(bool focused) => ComposerSurface.Classes.Set("focused", focused);
}
