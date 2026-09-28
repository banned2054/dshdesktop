using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DshDesktop.ViewModels;

namespace DshDesktop.Presentation.Views.Conversation;

/// <summary>
///     模型/推理等级选择控件：按钮 + 两级弹出菜单，底部输入区与新对话草稿页共用。
///     Esc 关闭弹出菜单是界面行为，与视图模式无关。
/// </summary>
public partial class ModelPickerView : UserControl
{
    public ModelPickerView()
    {
        InitializeComponent();
        // 轻量弹层关不掉 Esc 时的兜底：焦点在菜单行内时按 Esc 关闭模型菜单。
        ModelPickerPopup.AddHandler(KeyDownEvent, OnModelPickerPopupKeyDown);
    }

    private void OnModelPickerPopupKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not ComposerViewModel composer) return;

        e.Handled = true;
        composer.IsModelMenuOpen = false;
    }
}
