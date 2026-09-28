using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DshDesktop.ViewModels;

namespace DshDesktop.Presentation.Views.Conversation;

/// <summary>
///     新对话草稿页：Enter 发送（走草稿首发送编排）、输入框焦点反馈与 Esc 收起
///     工作区下拉属于本视图的界面行为；状态与命令由 MainWindowViewModel 承担。
/// </summary>
public partial class NewConversationView : UserControl
{
    public NewConversationView()
    {
        InitializeComponent();
        MessageInput.AddHandler(KeyDownEvent, OnMessageInputKeyDown, RoutingStrategies.Tunnel);
        MessageInput.GotFocus  += OnMessageInputGotFocus;
        MessageInput.LostFocus += OnMessageInputLostFocus;
        WorkspacePickerPopup.AddHandler(KeyDownEvent, OnWorkspacePickerPopupKeyDown);
    }

    private void OnMessageInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None) return;

        if (DataContext is MainWindowViewModel viewModel && viewModel.SendDraftCommand.CanExecute(null))
        {
            e.Handled = true;
            viewModel.SendDraftCommand.Execute(null);
        }
    }

    private void OnWorkspacePickerPopupKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not MainWindowViewModel viewModel) return;

        e.Handled = true;
        viewModel.IsWorkspaceMenuOpen = false;
    }

    /// <summary>
    ///     输入框焦点变化时切换面板 focused 类（与底部输入区同一反馈语义）。
    /// </summary>
    private void OnMessageInputGotFocus(object? sender, FocusChangedEventArgs e) => SetComposerFocused(true);

    private void OnMessageInputLostFocus(object? sender, RoutedEventArgs e) => SetComposerFocused(false);

    private void SetComposerFocused(bool focused) => ComposerSurface.Classes.Set("focused", focused);
}
