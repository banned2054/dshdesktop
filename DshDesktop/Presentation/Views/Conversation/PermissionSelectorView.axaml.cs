using Avalonia.Controls;
using Avalonia.Input;
using DshDesktop.ViewModels;

namespace DshDesktop.Presentation.Views.Conversation;

/// <summary>
///     执行权限选择控件：按钮 + 预设弹出菜单 + Full access/Auto 风险确认弹层。
///     轻量弹层关不掉 Esc 时的兜底：确认弹层 Esc 即取消（不发请求），预设菜单 Esc 即收起。
/// </summary>
public partial class PermissionSelectorView : UserControl
{
    public PermissionSelectorView()
    {
        InitializeComponent();
        PermissionPickerPopup.AddHandler(KeyDownEvent, OnPopupKeyDown);
        PermissionConfirmPopup.AddHandler(KeyDownEvent, OnPopupKeyDown);
    }

    private void OnPopupKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not PermissionSelectorViewModel selector) return;

        e.Handled = true;
        if (selector.IsConfirmOpen) selector.CancelSwitchCommand.Execute(null);
        else selector.IsMenuOpen = false;
    }
}
