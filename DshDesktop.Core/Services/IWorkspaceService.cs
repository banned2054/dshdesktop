using DshDesktop.Core.Models;

namespace DshDesktop.Core.Services;

/// <summary>
///     工作区注册表：哪些目录被登记为工作区、每个工作区记账了哪些会话。
///     实现负责订阅后端状态流并在断线恢复后重读基线；客户端只消费投影，
///     不在工作区记账之外维护第二套归属状态。
/// </summary>
public interface IWorkspaceService
{
    /// <summary>
    ///     后端登记的已归档会话集合（workspace/archived 帧的全量投影）；基线未到达时为空。
    ///     复用候选与可见性判定据此排除归档会话，不在客户端猜测归档状态。
    /// </summary>
    IReadOnlySet<string> ArchivedSessionIds { get; }

    /// <summary>工作区集合变化（登记、移除、重排或会话记账变化）时触发。</summary>
    event EventHandler? WorkspacesChanged;

    /// <summary>
    ///     当前工作区列表（后端维护的顺序）。首次调用可能返回空集合
    ///     （基线尚未到达），消费方应订阅 <see cref="WorkspacesChanged" /> 增量更新。
    /// </summary>
    Task<IReadOnlyList<WorkspaceSummary>> GetWorkspacesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     归档会话（workspace/archiveSession，移出列表表面，记录保留）。成功后归档集合经
    ///     工作区状态流回流；会话有运行中活动等业务错误抛出。置顶不走后端（本端
    ///     <see cref="ISidebarPinService" /> 自有方案），归档集合仍由本服务持有。
    /// </summary>
    Task ArchiveSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     登记一个已存在的目录为工作区（workspace/create，幂等：路径已登记时返回既有行）。
    ///     成功后投影经工作区状态流回流，消费方无需手动刷新；路径非法等业务错误抛出。
    /// </summary>
    Task<WorkspaceSummary> RegisterWorkspaceAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    ///     重命名工作区显示名（workspace/rename）。成功后投影经工作区状态流回流，
    ///     消费方无需手动刷新；名称冲突或工作区不存在等业务错误抛出。
    /// </summary>
    Task<WorkspaceSummary> RenameWorkspaceAsync(
        string workspaceId, string title, CancellationToken cancellationToken = default);

    /// <summary>
    ///     从工作区列表移除工作区（workspace/delete，只删注册，不删目录与会话；
    ///     其下会话由后端记账语义回到「未分组」）。成功后投影经工作区状态流回流；
    ///     工作区不存在等业务错误抛出。
    /// </summary>
    Task DeleteWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default);
}
