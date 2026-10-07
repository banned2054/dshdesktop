using DshDesktop.Core.Models;
using DshDesktop.Utils;
using DshDesktop.ViewModels;
using System.Collections.ObjectModel;

namespace DshDesktop.Services.Conversations;

/// <summary>
///     把会话条目组装为时间线项目。轮内过程即时进入 <see cref="TurnProcessGroupViewModel" />，
///     组内保留完整已加载投影输入，界面只显示其尾部窗口；最终回复候选保持组外可见。
///     turn/end 按「最后一轮步的有正文且不含工具调用的助手消息为最终回复」结算。
///     快照、增量与翻页共用同一套规则。起点不在已加载窗口内的轮次标记为部分加载，
///     但仍然限制显示长度，且不虚构完整 turn 或完整总数。
/// </summary>
internal sealed class TimelineAssembly(
    ObservableCollection<ConversationItemViewModel>      target,
    Func<long?, long, bool, TurnProcessExpansionState?>? processExpansionStateResolver = null,
    Func<WorkspaceChangesAnnouncement, WorkspaceChangesCardViewModel?>? changesCardFactory = null,
    Func<DeliverablesPresentedAnnouncement, long?, DeliverablesCardViewModel?>? deliverablesCardFactory = null)
{
    private readonly ObservableCollection<ConversationItemViewModel> _target = target;

    private readonly Dictionary<string, (ToolActivityItemViewModel Card, TurnProcessGroupViewModel? Group)>
        _toolsByCallId = new();

    /// <summary>按轮次记录的改动卡片；同一轮后到的宣告取代先前卡片。</summary>
    private readonly Dictionary<long, WorkspaceChangesCardViewModel> _changesCardsByTurn = new();

    /// <summary>原始交付事件按轮保留，轮末按官方 closing-message seq 规则投影。</summary>
    private readonly Dictionary<long, DeliverablesCardViewModel> _deliverablesCardsByTurn = new();

    private readonly Dictionary<long, List<DeliverablesPresentedAnnouncement>> _deliverablesByTurn = new();
    private readonly Dictionary<long, HashSet<long>> _deliverablesEventSeqsByTurn = new();

    /// <summary>已收束轮次的最终助手 seq 与尾部锚点；迟到声明仍按官方规则投影。</summary>
    private readonly Dictionary<long, long> _closingMessageSeqByTurn = new();
    private readonly Dictionary<long, ConversationItemViewModel?> _closedTurnAnchors = new();

    /// <summary>当前轮的条目顺序；用于结算最终回复（最后一个无工具调用的有正文消息）。</summary>
    private readonly List<ConversationEntry> _turnEntries = [];

    private readonly List<ConversationItemViewModel> _turnItems = [];

    /// <summary>运行中的过程组：完整条目保存在组内，界面只读它的可见窗口。</summary>
    private TurnProcessGroupViewModel? _activeGroup;

    /// <summary>运行中的最终回复候选；新过程条目到达时并入过程组，轮末确认后留在组外。</summary>
    private MessageItemViewModel? _provisionalAnswer;

    /// <summary>
    ///     最近一次 todo/write 写入的清单，是 todo_write 折叠行 diff 摘要的基线；
    ///     null 表示尚无写入（对齐官方 todo-history 的 previous 口径，轮次边界不清除）。
    /// </summary>
    private IReadOnlyList<SessionTodoItem>? _todoBaseline;

    private long? _turn;

    /// <summary>上一条目是否是轮次起点（用户消息或 turn/end 边界）；窗口首条目按截断处理（假）。</summary>
    private bool _turnOpeningSeen;

    /// <summary>本轮起点是否在已加载窗口内；在本轮首个条目到达时快照 <see cref="_turnOpeningSeen" />。</summary>
    private bool _turnStartObserved;

    /// <summary>todo/write 事件到达：更新后续 todo_write 折叠行的 diff 基线。</summary>
    public void SetTodoBaseline(IReadOnlyList<SessionTodoItem>? todos)
    {
        _todoBaseline = todos;
    }

    public void Add(ConversationEntry entry)
    {
        switch (entry)
        {
            case TurnBoundary boundary :
                if (_turnItems.Count > 0)
                {
                    // Turn 身份缺失时以当前唯一开放轮回退；已知身份不匹配时视为迟到边界，
                    // 不得提前收束另一条已知轮次。
                    if (_turn is not null && _turn != boundary.Turn) return;

                    var endedNormally = boundary.Reason is null or "completed";
                    var boundaryClose = CloseTurn(_turnStartObserved && endedNormally);
                    if (boundaryClose.ClosingSeq is { } boundaryClosingSeq)
                        CompleteDeliverablesTurn(boundaryClose.Turn ?? boundary.Turn, boundaryClosingSeq);
                }

                // 边界收束上一轮，其后是新一轮的起点。
                _turnOpeningSeen = true;
                return;

            case ConversationMessage { Role: MessageRole.User } message :
                // 用户消息开新一轮：上一轮未见 turn/end 时保守收尾，不折叠。
                // 用户气泡不属于任何轮的过程条目，不进入轮内跟踪。
                var userMessageClose = CloseTurn(false);
                if (userMessageClose.Turn is { } previousTurn &&
                    userMessageClose.ClosingSeq is { } previousClosingSeq)
                    CompleteDeliverablesTurn(previousTurn, previousClosingSeq);
                _turnOpeningSeen = true;
                _target.Add(new MessageItemViewModel(message));
                return;

            case ConversationMessage message when string.IsNullOrWhiteSpace(message.Content) &&
                                                  string.IsNullOrWhiteSpace(message.Reasoning) :
                // 无正文也无思考的助手提交（内容只有工具调用块）：无可见内容，不产生条目；
                // 其工具调用由 tool/call 事件单独承载。
                return;

            case ConversationMessage message :
                // 正文与仅有思考的消息共用组装流程；思考条目在折叠组内显示。
                OpenTurn(message.Turn);
                var messageItem = new MessageItemViewModel(message);
                _turnEntries.Add(message);
                _turnItems.Add(messageItem);
                if (IsPotentialAnswer(message))
                    SetProvisionalAnswer(messageItem);
                else
                    AppendProcess(messageItem);

                _turnOpeningSeen = false;
                return;

            case ToolActivity tool :
                OpenTurn(tool.Turn);
                // todo_write 的 diff 摘要在发起时定格：基线是上一次 todo/write 的清单
                //（事件先于下一次 tool/call 到达），基线本身不随本次调用更新。
                var todoDiff = tool.Name == "todo_write"
                    ? ToolCallText.TodoDiffSummary(_todoBaseline,
                                                   ToolCallText.ParseTodos(tool.ArgumentsJson) ?? [])
                    : null;
                var card = new ToolActivityItemViewModel(tool, todoDiff);
                _toolsByCallId[tool.CallId] = (card, null);
                _turnEntries.Add(tool);
                _turnItems.Add(card);
                AppendProcess(card);
                _turnOpeningSeen = false;
                return;

            case WorkspaceChangesAnnouncement announcement :
                if (changesCardFactory is null) return;
                // 本地捕获不依赖 producer；同轮 Host 宣告不得覆盖它。
                if (announcement.Seq >= 0 && _changesCardsByTurn.TryGetValue(announcement.Turn, out var localCard) &&
                    localCard.Seq < 0) return;
                var changesCard = changesCardFactory(announcement);
                if (changesCard is null) return;
                if (_changesCardsByTurn.Remove(announcement.Turn, out var previousCard))
                    _target.Remove(previousCard);

                _changesCardsByTurn[announcement.Turn] = changesCard;
                _target.Add(changesCard);
                // 同轮交付卡已存在时补接计数来源：摘要宣告可以晚于轮收束到达。
                if (_deliverablesCardsByTurn.TryGetValue(announcement.Turn, out var deliveredCard))
                    deliveredCard.SetChangesSource(changesCard.Seq);
                MoveDeliverablesAfterChanges(announcement.Turn, changesCard);
                return;

            case DeliverablesPresentedAnnouncement announcement :
                if (!_deliverablesByTurn.TryGetValue(announcement.Turn, out var turnAnnouncements))
                {
                    turnAnnouncements = [];
                    _deliverablesByTurn.Add(announcement.Turn, turnAnnouncements);
                    _deliverablesEventSeqsByTurn.Add(announcement.Turn, []);
                }

                if (_deliverablesEventSeqsByTurn[announcement.Turn].Add(announcement.Seq))
                    turnAnnouncements.Add(announcement);

                if (_closingMessageSeqByTurn.TryGetValue(announcement.Turn, out var closingSeq))
                    RefreshDeliverablesCard(announcement.Turn, closingSeq);
                return;

            default :
                throw new NotSupportedException($"未支持的会话条目类型：{entry.GetType().Name}");
        }
    }

    private void PlaceDeliverablesCard(long turn)
    {
        if (!_deliverablesCardsByTurn.TryGetValue(turn, out var card) || _target.Contains(card)) return;

        var changes = _target.OfType<WorkspaceChangesCardViewModel>()
            .LastOrDefault(candidate => candidate.Turn == turn);
        var anchor = changes ?? _closedTurnAnchors.GetValueOrDefault(turn);
        var index = anchor is null ? -1 : _target.IndexOf(anchor);
        _target.Insert(index < 0 ? _target.Count : index + 1, card);
    }

    private void MoveDeliverablesAfterChanges(long turn, WorkspaceChangesCardViewModel changes)
    {
        if (!_deliverablesCardsByTurn.TryGetValue(turn, out var delivered)) return;

        var changesIndex = _target.IndexOf(changes);
        var deliveredIndex = _target.IndexOf(delivered);
        if (changesIndex < 0 || deliveredIndex < 0 || deliveredIndex == changesIndex + 1) return;

        _target.RemoveAt(deliveredIndex);
        changesIndex = _target.IndexOf(changes);
        _target.Insert(changesIndex + 1, delivered);
    }

    private void CompleteDeliverablesTurn(long turn, long closingSeq)
    {
        _closingMessageSeqByTurn.TryAdd(turn, closingSeq);
        _closedTurnAnchors.TryAdd(turn, _target.LastOrDefault());
        RefreshDeliverablesCard(turn, _closingMessageSeqByTurn[turn]);
    }

    private void RefreshDeliverablesCard(long turn, long closingSeq)
    {
        if (!_deliverablesByTurn.TryGetValue(turn, out var announcements)) return;

        var declarations = announcements.SelectMany(announcement => announcement.Files).ToArray();
        var presented = TurnDeliverables.ForClosing(declarations, closingSeq);
        if (presented.Count == 0) return;

        var first = announcements.MinBy(announcement => announcement.Seq)!;
        var projection = new DeliverablesPresentedAnnouncement(first.Seq, turn, first.CreatedAt, presented);
        if (_deliverablesCardsByTurn.TryGetValue(turn, out var existing))
        {
            existing.ReplaceFiles(presented);
            return;
        }

        // 第二参数是该轮改动宣告的 seq（无则 null），交付卡用它异步取增删计数。
        if (deliverablesCardFactory?.Invoke(
                projection,
                _changesCardsByTurn.TryGetValue(turn, out var changesCard) ? changesCard.Seq : null)
            is not { } card) return;
        _deliverablesCardsByTurn.Add(turn, card);
        PlaceDeliverablesCard(turn);
    }

    /// <summary>完成快照/历史重建后再固定每个过程组的恢复锚点。</summary>
    public void CompleteRestoredProjection()
    {
        foreach (var group in _target.OfType<TurnProcessGroupViewModel>())
            group.CompleteRestoredProjection();
    }

    /// <summary>
    ///     落定一个工具调用（按 CallId 匹配，无论其已折叠进组还是仍逐项显示）。
    /// </summary>
    public bool SettleTool(ToolActivity settled)
    {
        if (!_toolsByCallId.TryGetValue(settled.CallId, out var found)) return false;

        found.Card.Settle(settled);
        found.Group?.RefreshSummary();
        return true;
    }

    private void OpenTurn(long? turn)
    {
        if (_turnItems.Count != 0)
        {
            // Turn 可缺省：后续工具/消息提供身份时补全，但保留最初用户起点状态。
            if (_turn is null)
            {
                if (turn is not null) _turn = turn;
                return;
            }

            // 未知身份仍沿用当前轮；不同的已知身份不能混进同一过程组。
            if (turn is null || turn == _turn) return;

            var closed = CloseTurn(false);
            if (closed.Turn is { } closedTurn && closed.ClosingSeq is { } closingSeq)
                CompleteDeliverablesTurn(closedTurn, closingSeq);
        }

        _turn              = turn;
        _turnStartObserved = _turnOpeningSeen;
    }

    /// <summary>
    ///     结算当前轮：最终回复是轮内最后一条助手消息且它有正文（思考不算正文）、
    ///     不含工具调用块。最终回复与流式气泡始终在过程组外完整显示；若最终回复带思考，
    ///     生成思考-only 投影入组，本体经 HasVisibleReasoning 停止重复展示。
    ///     起点被会话历史页截断的轮次标记部分加载，过程组展示该轮当前已加载的全部条目。
    /// </summary>
    private (long? Turn, long? ClosingSeq) CloseTurn(bool foldAllowed)
    {
        var turn = _turn;
        try
        {
            if (_turnItems.Count == 0) return (turn, null);

            var answer = FindAnswer();
            if (answer is null)
            {
                // 没有独立最终回复时，已加载过程继续交给过程组完整展示。
                PromoteProvisionalAnswer();
                if (_activeGroup is not null && !foldAllowed) _activeGroup.MarkPartialTurn();
                return (turn, null);
            }

            var settledAnswer = answer.Value;
            turn ??= settledAnswer.Message.Turn;
            _activeGroup?.Remove(settledAnswer.Item);
            if (_provisionalAnswer is not null &&
                !ReferenceEquals(_provisionalAnswer, settledAnswer.Item))
                PromoteProvisionalAnswer();

            _provisionalAnswer = null;

            // 最终消息可同时携带正文与 reasoning。投影只进入本帧过程组，不改
            // _turnItems/_turnEntries 的 1:1 索引；空 Content 保持 MessageCount 的正文口径。
            if (!string.IsNullOrWhiteSpace(settledAnswer.Message.Reasoning))
            {
                // 中断标记属于最终气泡的状态，不随思考-only 投影复制；否则收束后
                // 组内思考行和组外正文气泡会重复显示「已中断」。
                var members = new List<ConversationItemViewModel>
                {
                    new MessageItemViewModel(settledAnswer.Message with
                    {
                        Content = string.Empty,
                        IsInterrupted = false
                    })
                };
                settledAnswer.Item.ProjectReasoning();

                var group = _activeGroup;
                if (group is null)
                {
                    group = CreateProcessGroup(members[0].Seq, false);
                    var answerIndex = _target.IndexOf(settledAnswer.Item);
                    _target.Remove(group);
                    _target.Insert(answerIndex < 0 ? _target.Count - 1 : answerIndex, group);
                }

                foreach (var item in members)
                    group.Add(item);
            }

            // 运行中的组已经位于最终回复之前；只移除空组，保留有效过程投影。
            if (_activeGroup is not null && _activeGroup.Process.Count == 0)
                _target.Remove(_activeGroup);

            if (_activeGroup is null) return (turn, settledAnswer.Message.Seq);
            if (foldAllowed && !_activeGroup.HasUserSetExpansion) _activeGroup.IsExpanded = false;
            else _activeGroup.MarkPartialTurn();
            return (turn, settledAnswer.Message.Seq);
        }
        finally
        {
            _turnItems.Clear();
            _turnEntries.Clear();
            _turn              = null;
            _activeGroup       = null;
            _provisionalAnswer = null;
        }
    }

    /// <summary>最终回复候选的运行中判定与轮末判定一致：有正文且不含工具调用块。</summary>
    private static bool IsPotentialAnswer(ConversationMessage message)
    {
        return !string.IsNullOrWhiteSpace(message.Content) && !message.HasToolCalls;
    }

    /// <summary>新的过程条目到达时，先让旧的最终候选回到过程投影，再追加该条目。</summary>
    private void AppendProcess(ConversationItemViewModel item)
    {
        PromoteProvisionalAnswer();
        var group                                                               = CreateProcessGroup(item.Seq, true);
        if (item is ToolActivityItemViewModel card) _toolsByCallId[card.CallId] = (card, group);

        group.Add(item);
    }

    private void SetProvisionalAnswer(MessageItemViewModel item)
    {
        PromoteProvisionalAnswer();
        _provisionalAnswer = item;
        _target.Add(item);
    }

    private void PromoteProvisionalAnswer()
    {
        if (_provisionalAnswer is null) return;

        var candidate = _provisionalAnswer;
        _provisionalAnswer = null;
        _target.Remove(candidate);
        var group = CreateProcessGroup(candidate.Seq, true);
        group.Add(candidate);
    }

    private TurnProcessGroupViewModel CreateProcessGroup(long seq, bool startExpanded)
    {
        if (_activeGroup is not null) return _activeGroup;

        var state = processExpansionStateResolver?.Invoke(_turn, seq, startExpanded);
        var group = new TurnProcessGroupViewModel(seq, _turn, state, startExpanded);
        if (!_turnStartObserved) group.MarkPartialTurn();

        _activeGroup = group;
        _target.Add(group);
        return group;
    }

    /// <summary>最终回复候选：轮内最后一条助手消息，须有正文且不含工具调用块。</summary>
    private (ConversationMessage Message, MessageItemViewModel Item)? FindAnswer()
    {
        for (var index = _turnEntries.Count - 1; index >= 0; index--)
        {
            if (_turnEntries[index] is not ConversationMessage message) continue;

            // 只看最后一条助手消息：它无正文（含只有思考）或含工具调用块时，
            // 该轮没有最终回复（对齐参考实现 latestAnswer 只取最后一个 step）。
            return !string.IsNullOrWhiteSpace(message.Content) &&
                   !message.HasToolCalls                       &&
                   _turnItems[index] is MessageItemViewModel item
                ? (message, item)
                : null;
        }

        return null;
    }
}
