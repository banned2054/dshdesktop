using DshDesktop.Core.Models;

namespace DshDesktop.Core.Services;

/// <summary>
///     后端按轮次记录的会话文件改动读取（workspace/changes 域）。内容只存于后端进程内存，
///     会话销毁或 Host 重启后不可再取，此时两个方法都返回 null；这与官方客户端
///     「卡片随内容失效消失」的行为一致，不视为错误。
/// </summary>
public interface IWorkspaceChangesService
{
    /// <summary>读取 workspace/changes 事件（按会话 id 与事件 seq 定位）宣告的文件摘要。</summary>
    Task<WorkspaceChangesSummary?> GetSummaryAsync(
        string sessionId, long seq, CancellationToken cancellationToken = default);

    /// <summary>读取摘要中第 <paramref name="index" /> 个文件的轮首/轮末对比；索引越界同样返回 null。</summary>
    Task<WorkspaceFileDiff?> GetDiffAsync(
        string sessionId, long seq, int index, CancellationToken cancellationToken = default);
}
