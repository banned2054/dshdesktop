using DshDesktop.Core.Models;

namespace DshDesktop.Core.Services;

/// <summary>
///     工作区注册表：哪些目录被登记为工作区、每个工作区记账了哪些会话。
///     实现负责订阅后端状态流并在断线恢复后重读基线；客户端只消费投影，
///     不在工作区记账之外维护第二套归属状态。
/// </summary>
public interface IWorkspaceService
{
    /// <summary>工作区集合变化（登记、移除、重排或会话记账变化）时触发。</summary>
    event EventHandler? WorkspacesChanged;

    /// <summary>
    ///     当前工作区列表（后端维护的顺序）。首次调用可能返回空集合
    ///     （基线尚未到达），消费方应订阅 <see cref="WorkspacesChanged" /> 增量更新。
    /// </summary>
    Task<IReadOnlyList<WorkspaceSummary>> GetWorkspacesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     后端登记的已归档会话集合（workspace/archived 帧的全量投影）；基线未到达时为空。
    ///     复用候选与可见性判定据此排除归档会话，不在客户端猜测归档状态。
    /// </summary>
    IReadOnlySet<string> ArchivedSessionIds { get; }
}
