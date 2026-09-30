using DshDesktop.Core.Models;
using DshDesktop.ViewModels;
using System.Collections.ObjectModel;

namespace DshDesktop.Services.Conversations;

/// <summary>
///     把会话条目组装为时间线项目，折叠规则对齐参考 Web 客户端的 turn-process 投影：
///     轮内条目（中间助手消息、工具调用）先逐项显示；turn/end 到达后按「最后一轮步的
///     有正文且不含工具调用的助手消息为最终回复」结算，最终回复存在时其之前的过程条目
///     折叠为一个 <see cref="TurnProcessGroupViewModel" />。快照、增量与翻页共用同一套规则。
///     逐轮判定折叠资格：只有本轮起点（用户消息或上一轮边界）落在已加载窗口内时才折叠，
///     被窗口截断的首轮保持逐项展示——否则中途 attach 长会话时，全程要手动翻到顶才能
///     看到折叠形态（WebUI 实时会话的已加载窗口天然是全量，不存在此落差）。
/// </summary>
internal sealed class TimelineAssembly(ObservableCollection<ConversationItemViewModel> target)
{
    private readonly ObservableCollection<ConversationItemViewModel> _target = target;

    private readonly Dictionary<string, (ToolActivityItemViewModel Card, TurnProcessGroupViewModel? Group)>
        _toolsByCallId = new();

    /// <summary>当前轮的条目顺序；用于结算最终回复（最后一个无工具调用的有正文消息）。</summary>
    private readonly List<ConversationEntry> _turnEntries = [];

    private readonly List<ConversationItemViewModel> _turnItems = [];

    private long? _turn;

    /// <summary>上一条目是否是轮次起点（用户消息或 turn/end 边界）；窗口首条目按截断处理（假）。</summary>
    private bool _turnOpeningSeen;

    /// <summary>本轮起点是否在已加载窗口内；在本轮首个条目到达时快照 <see cref="_turnOpeningSeen" />。</summary>
    private bool _turnStartObserved;

    public void Add(ConversationEntry entry)
    {
        switch (entry)
        {
            case TurnBoundary boundary :
                if (_turn is { } openTurn && openTurn == boundary.Turn)
                    CloseTurn(_turnStartObserved);
                else if (_turn is not null)
                    // 轮次号不衔接（窗口裁剪等）：当前轮保守收尾，不折叠。
                    CloseTurn(false);

                // 边界收束上一轮，其后是新一轮的起点。
                _turnOpeningSeen = true;
                return;

            case ConversationMessage { Role: MessageRole.User } message :
                // 用户消息开新一轮：上一轮未见 turn/end 时保守收尾，不折叠。
                // 用户气泡不属于任何轮的过程条目，不进入轮内跟踪。
                CloseTurn(false);
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
                Append(messageItem);
                _turnOpeningSeen = false;
                return;

            case ToolActivity tool :
                OpenTurn(tool.Turn);
                var card = new ToolActivityItemViewModel(tool);
                _toolsByCallId[tool.CallId] = (card, null);
                _turnEntries.Add(tool);
                Append(card);
                _turnOpeningSeen = false;
                return;

            default :
                throw new NotSupportedException($"未支持的会话条目类型：{entry.GetType().Name}");
        }
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
        if (_turnItems.Count != 0) return;
        _turn              = turn;
        _turnStartObserved = _turnOpeningSeen;
    }

    /// <summary>
    ///     结算当前轮：最终回复是轮内最后一条助手消息且它有正文（思考不算正文）、
    ///     不含工具调用块；存在且轮起点在窗口内时，其余过程条目收入过程组，最终回复保持
    ///     独立气泡。被窗口截断的轮次（起点未观察到）与被打断无正文的轮次保持逐项展示。
    /// </summary>
    private void CloseTurn(bool foldAllowed)
    {
        try
        {
            if (!foldAllowed || _turnItems.Count == 0) return;

            var answer = FindAnswer();
            if (answer is null)
                // 不折叠：条目保持逐项显示。
                return;

            var members = _turnItems.Where(item => !ReferenceEquals(item, answer)).ToList();
            if (members.Count == 0) return;

            var group = new TurnProcessGroupViewModel(members[0].Seq);
            foreach (var item in members)
            {
                if (item is ToolActivityItemViewModel card) _toolsByCallId[card.CallId] = (card, group);

                group.Add(item);
            }

            // 先移除整轮条目再按「过程组、最终回复」的顺序放回。
            foreach (var item in _turnItems) _target.Remove(item);

            _target.Add(group);
            _target.Add(answer);
        }
        finally
        {
            _turnItems.Clear();
            _turnEntries.Clear();
            _turn = null;
        }
    }

    /// <summary>最终回复候选：轮内最后一条助手消息，须有正文且不含工具调用块。</summary>
    private MessageItemViewModel? FindAnswer()
    {
        for (var index = _turnEntries.Count - 1; index >= 0; index--)
        {
            if (_turnEntries[index] is not ConversationMessage message) continue;

            // 只看最后一条助手消息：它无正文（含只有思考）或含工具调用块时，
            // 该轮没有最终回复（对齐参考实现 latestAnswer 只取最后一个 step）。
            return !string.IsNullOrWhiteSpace(message.Content) &&
                   !message.HasToolCalls                       &&
                   _turnItems[index] is MessageItemViewModel item
                ? item
                : null;
        }

        return null;
    }

    private void Append(ConversationItemViewModel item)
    {
        _target.Add(item);
        _turnItems.Add(item);
    }
}
