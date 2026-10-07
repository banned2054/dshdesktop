namespace DshDesktop.Core.Models;

/// <summary>助手一次流式尝试的收尾结局。</summary>
public enum StreamOutcomeKind
{
    /// <summary>输出已由后端落盘为正式消息（assistant/message 或 assistant/attempt）。</summary>
    Committed,

    /// <summary>尝试被放弃（取消、失败或被新尝试取代），不会有对应的正式消息事件。</summary>
    Abandoned,

    /// <summary>后端未给出可识别的结局。</summary>
    Unknown
}

/// <summary>
///     会话订阅的增量更新。重连恢复后后端会再次下发 <see cref="Snapshot" />，
///     消费者必须用快照整体替换本地状态，而不是追加。
/// </summary>
public abstract record SessionUpdate
{
    /// <summary>
    ///     整窗替换：包含窗口内全部可显示条目。窗口与消息和轮次对齐（至少 50 条消息、
    ///     2 个轮次起点，最多 500 条消息；工具调用与其来源消息同组），<see cref="WindowStartSeq" /> 是窗口首条事件的 seq，
    ///     <see cref="HasMore" /> 表示仍有更早历史可经 LoadOlderAsync 翻页。
    ///     <see cref="CurrentModel" /> 是快照投影里会话当前生效的模型选型，未选过型时为 null。
    /// </summary>
    public sealed record Snapshot(
        IReadOnlyList<ConversationEntry> Entries,
        long                             Cursor,
        long                             WindowStartSeq,
        bool                             HasMore,
        string?                          Title,
        ModelSelection?                  CurrentModel = null,
        string?                          Cwd = null) : SessionUpdate;

    public sealed record MessageAppended(ConversationMessage Message) : SessionUpdate;

    public sealed record TitleChanged(string? Title) : SessionUpdate;

    /// <summary>会话选型变化（model/selection 事件）：下一次请求将使用的模型。</summary>
    public sealed record ModelSelected(ModelSelection Selection) : SessionUpdate;

    /// <summary>
    ///     会话累计 token 计量更新（快照投影基线或 session/control 实时投影帧，均为整值替换）。
    ///     <see cref="Seq" /> 是投影水位（快照 asOfSeq 或投影事件 seq），乱序到达时以新值为准。
    /// </summary>
    public sealed record UsageUpdated(SessionUsage Usage, long Seq) : SessionUpdate;

    /// <summary>会话累计时间/步数统计更新；整值替换，<see cref="Seq" /> 语义同 <see cref="UsageUpdated" />。</summary>
    public sealed record StatsUpdated(SessionStats Stats, long Seq) : SessionUpdate;

    /// <summary>
    ///     会话权限预设更新（permissions 投影：快照基线或 session/control 实时帧，均为
    ///     整值替换）。<see cref="Seq" /> 语义同 <see cref="UsageUpdated" />；CurrentValue
    ///     是会话权限的唯一权威来源（Composer 权限下拉的展示与勾选以此为据）。
    /// </summary>
    public sealed record PermissionsUpdated(string CurrentValue, long Seq) : SessionUpdate;

    /// <summary>工具调用发起（tool/call）；结果到达前状态为 Running。</summary>
    public sealed record ToolCallStarted(ToolActivity Activity) : SessionUpdate;

    /// <summary>工具调用落定（tool/result）；按 CallId 匹配先前发起的条目。</summary>
    public sealed record ToolCallSettled(ToolActivity Activity) : SessionUpdate;

    /// <summary>一轮对话收束（turn/end）：界面据此折叠该轮的过程条目；Reason 为结束原因。</summary>
    public sealed record TurnEnded(long Turn, long Seq, string? Reason = null) : SessionUpdate;

    /// <summary>一轮对话开始（turn/start）：界面据此清空上一轮的任务面板显示。</summary>
    public sealed record TurnStarted(long Seq, long? Turn = null, bool IsReplay = false) : SessionUpdate;

    /// <summary>
    ///     任务清单被整体替换（todo/write 事件，快照重放同样到达）。Todos 是最新全量
    ///     清单（可能为空）；空清单与无清单在面板显示上同义，但差异基线语义不同。
    /// </summary>
    public sealed record TodoListUpdated(IReadOnlyList<SessionTodoItem> Todos, long Seq) : SessionUpdate;

    /// <summary>
    ///     一轮文件改动摘要已宣告（workspace/changes 持久事件，快照重放同样到达）。
    ///     <see cref="Seq" /> 是取回摘要与逐文件对比的定位键；同一轮后到的事件取代先前事件。
    ///     内容本身不随事件携带，经 IWorkspaceChangesService 按 Seq 拉取。
    /// </summary>
    public sealed record WorkspaceChanged(long Turn, long Seq) : SessionUpdate;

    /// <summary>
    ///     一轮交付文件声明（deliverables/presented 持久事件，快照重放与实时增量同形）。
    ///     同一轮的多次事件是追加而非整体替换；快照条目与重放更新可能重复送达同一条
    ///     声明，按 (Seq, Index) 去重。与 <see cref="WorkspaceChanged" /> 相互独立。
    /// </summary>
    public sealed record DeliverablesPresented(DeliverablesPresentedAnnouncement Announcement) : SessionUpdate;

    public sealed record StreamStarted(string AttemptId) : SessionUpdate;

    public sealed record StreamTextDelta(string AttemptId, string Text) : SessionUpdate;

    /// <summary>
    ///     流式尝试结束。<see cref="Outcome" /> 为 Committed 时后端已落盘结算：
    ///     <see cref="SettlementEventType" /> 为 assistant/message（取消时消息带 interrupted 标记）
    ///     或 assistant/attempt（无可见内容时的空结算）；Abandoned 仅出现在无法落盘的错误路径。
    /// </summary>
    public sealed record StreamEnded(string AttemptId, StreamOutcomeKind Outcome, string? SettlementEventType = null)
        : SessionUpdate;
}

/// <summary>LoadOlderAsync 返回的一页更早历史。</summary>
/// <param name="Entries">
///     本页映射后的可显示条目（可能为空：整页都是被过滤的注入上下文时）；
///     空页时调用方应继续翻页直到出现可见条目或 HasMore 为假。
/// </param>
/// <param name="WindowStartSeq">本页首条事件的 seq，是下一次翻页的 beforeSeq。</param>
public sealed record SessionHistoryPage(IReadOnlyList<ConversationEntry> Entries, long WindowStartSeq, bool HasMore);
