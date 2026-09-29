using DshDesktop.Core.Models;
using System.Windows.Input;

namespace DshDesktop.ViewModels;

/// <summary>
///     悬浮面板审批横幅的一个待决条目。
///     裁决命令（允许一次/拒绝）由 MainWindowViewModel 创建条目时注入，
///     使独立的 ApprovalPromptView 无需回查窗口级 DataContext。
/// </summary>
public sealed class PendingApprovalViewModel(
    PendingApproval approval,
    ICommand        approveCommand,
    ICommand        rejectCommand)
{
    public PendingApproval Approval { get; } = approval;

    /// <summary>允许一次；命令参数为本条目。</summary>
    public ICommand ApproveCommand { get; } = approveCommand;

    /// <summary>拒绝；命令参数为本条目。</summary>
    public ICommand RejectCommand { get; } = rejectCommand;

    public string EventId => Approval.EventId;

    public string SessionId => Approval.SessionId;

    public string ToolName => Approval.ToolName;

    /// <summary>横幅标题：有理由用理由，否则回退到「工具 X 请求授权」。</summary>
    public string HeadlineText => Approval.Reason is { Length: > 0 } reason
        ? reason
        : $"工具 {Approval.ToolName} 请求执行授权";

    public string ToolNameText => Approval.CallId is { Length: > 0 }
        ? $"{Approval.ToolName} · {Approval.CallId}"
        : Approval.ToolName;
}
