using DshDesktop.Core.Models;

namespace DshDesktop.Core.Services;

public interface ISessionService
{
    /// <summary>会话列表发生变化（新增、移除、运行状态或活动时间更新）时触发。</summary>
    event EventHandler? SessionsChanged;

    Task<IReadOnlyList<SessionSummary>> GetSessionsAsync(CancellationToken cancellationToken = default);

    /// <summary>创建新会话，可指定所属工作区。非幂等操作，失败后不得携带新的意图自动重试。</summary>
    Task<SessionSummary> CreateSessionAsync(
        string? workspaceId = null, CancellationToken cancellationToken = default);

    /// <summary>查询模型目录（默认选型与各提供方可选模型）。只读，可安全重试。</summary>
    Task<ModelCatalog> GetModelCatalogAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     为会话显式选型（session/selectModel）。选型经会话日志的 model/selection 事件
    ///     持久化并对后续 prompt 生效；返回后端接受后的选型。reasoningEffort 是可选推理
    ///     档位（off/low/high/max），null 表示不指定档位，由后端按其默认行为处理。
    /// </summary>
    Task<ModelSelection> SelectModelAsync(
        string sessionId,
        string provider,
        string model,
        string? reasoningEffort = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(
        string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     向前翻一页更早历史（session/page）。throughSeq 是快照携带的游标，
    ///     beforeSeq 是当前窗口首条事件 seq（下一页从它之前开始）。只读，可安全重试。
    /// </summary>
    Task<SessionHistoryPage> LoadOlderAsync(
        string            sessionId,
        long              throughSeq,
        long              beforeSeq,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     发送用户消息。requestId 是幂等键：同一 requestId 重复发送会被后端去重；
    ///     结果不确定的失败只能交给用户决定是否重发，不得换新 id 自动重发。
    /// </summary>
    Task SendPromptAsync(string sessionId,
                         string requestId,
                         string content, CancellationToken cancellationToken = default);

    /// <summary>取消会话当前生成。幂等，可安全重试。</summary>
    Task CancelAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     订阅会话增量更新。断线恢复由实现负责：重连后重新下发 Snapshot。
    /// </summary>
    IAsyncEnumerable<SessionUpdate> FollowSessionAsync(string sessionId, CancellationToken cancellationToken = default);
}
