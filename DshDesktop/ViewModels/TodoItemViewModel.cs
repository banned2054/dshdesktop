using DshDesktop.Core.Models;

namespace DshDesktop.ViewModels;

/// <summary>
///     任务面板的单条条目（composer 上方 TodoPanel）。整体随清单替换重建，
///     不需要可变属性通知；状态三分支供圆点图形切换。
/// </summary>
public sealed class TodoItemViewModel(string content, SessionTodoStatus status)
{
    public string Content { get; } = content;

    public SessionTodoStatus Status { get; } = status;

    public bool IsCompleted => Status == SessionTodoStatus.Completed;

    public bool IsInProgress => Status == SessionTodoStatus.InProgress;

    public bool IsPending => Status == SessionTodoStatus.Pending;

    /// <summary>状态词（对齐官方 todo.status.*，作圆点的可读标注）。</summary>
    public string StatusText => Status switch
    {
        SessionTodoStatus.Completed  => "已完成",
        SessionTodoStatus.InProgress => "进行中",
        _                            => "待处理"
    };
}
