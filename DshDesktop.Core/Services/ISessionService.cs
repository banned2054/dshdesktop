using DshDesktop.Core.Models;

namespace DshDesktop.Core.Services;

public interface ISessionService
{
    /// <summary>会话列表发生变化（新增、移除、运行状态或活动时间更新）时触发。</summary>
    event EventHandler? SessionsChanged;

    Task<IReadOnlyList<SessionSummary>> GetSessionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     创建会话；提供 <paramref name="workspaceId" /> 时归属该工作区。
    ///     有 <paramref name="sessionId" /> 时按身份收养已有会话，后端校验 cwd/preset，且不应用默认模型；
    ///     否则新建会话。<paramref name="agentPreset" /> 在创建时绑定并由后端锁定，null 使用后端默认值；
    ///     收养时须与已有会话一致。操作非幂等，结果不确定时不得自动重试新意图。
    /// </summary>
    Task<SessionSummary> CreateSessionAsync(
        string?           workspaceId       = null, string? sessionId = null, string? agentPreset = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     以源会话最近一个已完成 turn 为种子创建分支并返回新会话 id；不指定分支点。
    ///     源会话无已完成 turn 时后端拒绝。分支继承 cwd 与预设，模型使用后端当前默认值；
    ///     非幂等，结果不确定时不得自动重试。
    /// </summary>
    Task<string> ForkSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>重命名会话，返回后端规范化并持久化的标题。</summary>
    Task<string> RenameSessionAsync(string sessionId, string title, CancellationToken cancellationToken = default);

    /// <summary>
    ///     标记本进程已观察到会话参与活动，避免迟到的空白摘要清除该状态；标记不持久化，
    ///     重连或重启后以重新验证的后端状态为准。
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
