using DshDesktop.Core.Models;

namespace DshDesktop.Core.Services;

public interface ISessionService
{
    /// <summary>会话列表发生变化（新增、移除、运行状态或活动时间更新）时触发。</summary>
    event EventHandler? SessionsChanged;

    Task<IReadOnlyList<SessionSummary>> GetSessionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     创建新会话，可指定所属工作区。携带 <paramref name="sessionId" /> 时走
    ///     session/create 的收养语义（按身份复用已有会话，后端按 cwd/preset 校验冲突），
    ///     不应用默认模型策略；不携带时创建全新会话。<paramref name="agentPreset" />
    ///     是创建时绑定的模式 id（内置取值见 <see cref="AgentPresetModes" />，null 交由
    ///     后端默认），随会话开始由后端锁定；收养路径必须携带与会话一致的取值。
    ///     非幂等操作，结果不确定的失败不得携带新的意图自动重试。
    /// </summary>
    Task<SessionSummary> CreateSessionAsync(
        string?           workspaceId       = null, string? sessionId = null, string? agentPreset = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     分支会话（session/fork）：以源会话最近一个已完成 turn 的事件前缀为种子创建
    ///     独立新会话，返回服务端新铸的子会话 id。不指定分支点，对齐参考客户端入口行为；
    ///     源会话没有已完成 turn 时后端拒绝（session/fork-unavailable）。子会话继承源
    ///     cwd 与预设，模型选型取后端当前默认；经 api-session/added 事件进入列表。
    ///     非幂等操作，结果不确定的失败不得自动重试。
    /// </summary>
    Task<string> ForkSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     重命名会话（session/rename）：标题交后端规范化并作为持久事件落盘，
    ///     返回接受后的标题。分支子会话的「尾部序号递增」改名复用本方法。
    /// </summary>
    Task<string> RenameSessionAsync(string sessionId, string title, CancellationToken cancellationToken = default);

    /// <summary>
    ///     记录"本端已参与对话"的过渡信号（发送被接受、观察到运行或已加载内容）。
    ///     台账在进程内生效：已确认开始的会话不被迟到的空白摘要退回空白；
    ///     不持久化，重连与重启后以后端重新验证的结果为准。
    /// </summary>
    void MarkSessionEngaged(string sessionId);

    /// <summary>查询模型目录（默认选型与各提供方可选模型）。只读，可安全重试。</summary>
    Task<ModelCatalog> GetModelCatalogAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     为会话显式选型（session/selectModel）。选型经会话日志的 model/selection 事件
    ///     持久化并对后续 prompt 生效；返回后端接受后的选型。reasoningEffort 是可选推理
    ///     档位（off/low/high/max），null 表示不指定档位，由后端按其默认行为处理。
    /// </summary>
    Task<ModelSelection> SelectModelAsync(
        string  sessionId,
        string  provider,
        string  model,
        string? reasoningEffort = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(
        string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     向前翻一页更早历史（session/page）。throughSeq 是快照携带的游标，
    ///     beforeSeq 是当前窗口首条事件 seq（下一页从它之前开始）。只读，可安全重试。
    /// </summary>
    Task<SessionHistoryPage> LoadOlderAsync(string sessionId,
                                            long   throughSeq,
                                            long   beforeSeq, CancellationToken cancellationToken = default);

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
