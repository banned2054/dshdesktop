using Avalonia.Controls;

namespace DshDesktop.Presentation.Views.Conversation;

/// <summary>
///     会话区头部：纯当前会话信息展示（经 MainWindowViewModel 绑定），
///     无 code-behind 行为；窗口级布局见 MainWindow。
/// </summary>
public partial class SessionHeaderView : UserControl
{
    public SessionHeaderView()
    {
        InitializeComponent();
    }
}
