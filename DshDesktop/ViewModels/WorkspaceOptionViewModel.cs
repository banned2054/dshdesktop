using DshDesktop.Core.Models;

namespace DshDesktop.ViewModels;

/// <summary>
///     新对话草稿页工作区下拉的一个选项：工作区条目，或显式「不使用工作区」兼容项。
///     点选只改本地草稿预选（经 root 注入的命令），不调用 session/create。
/// </summary>
public sealed class WorkspaceOptionViewModel(
    string?                                id,
    string                                 title,
    string                                 path,
    bool                                   isWithoutWorkspace,
    RelayCommand<WorkspaceOptionViewModel> selectCommand) : ObservableObject
{
    private bool _isSelected;

    /// <summary>工作区 id；「不使用工作区」兼容项为 null（发送时不归属任何工作区）。</summary>
    public string? Id { get; } = id;

    /// <summary>工作区标题；后端未命名时回退目录名。兼容项为固定文案。</summary>
    public string TitleText { get; } = string.IsNullOrWhiteSpace(title) ? path : title;

    /// <summary>登记目录；悬停提示展示完整路径。兼容项为空。</summary>
    public string Path { get; } = path;

    /// <summary>是否为显式「不使用工作区」兼容项：发送前须由用户主动点选，不作默认。</summary>
    public bool IsWithoutWorkspace { get; } = isWithoutWorkspace;

    /// <summary>是否为草稿当前预选（下拉勾选态）；由 root 在预选变化时对齐。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public RelayCommand<WorkspaceOptionViewModel> SelectCommand { get; } = selectCommand;

    public static WorkspaceOptionViewModel CreateWorkspace(
        WorkspaceSummary workspace, RelayCommand<WorkspaceOptionViewModel> selectCommand)
    {
        return new WorkspaceOptionViewModel(workspace.Id, workspace.Title, workspace.Path, false,
                                            selectCommand);
    }

    public static WorkspaceOptionViewModel CreateWithoutWorkspace(
        RelayCommand<WorkspaceOptionViewModel> selectCommand)
    {
        return new WorkspaceOptionViewModel(null, "不使用工作区", string.Empty, true, selectCommand);
    }
}
