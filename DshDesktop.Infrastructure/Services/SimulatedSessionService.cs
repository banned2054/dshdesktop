using DshDesktop.Core.Models;
using DshDesktop.Core.Services;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace DshDesktop.Infrastructure.Services;

/// <summary>模拟会话服务：内存数据 + 通道推送，行为对齐真实服务的更新语义。</summary>
public sealed class SimulatedSessionService : ISessionService
{
    /// <summary>模拟窗口大小：首屏只给最近这么多条消息（附随条目随组携带），更早的按页提供。</summary>
    private const int WindowSize = 3;

    /// <summary>模拟单页更早历史的条目数。</summary>
    private const int OlderPageSize = 4;

    /// <summary>模拟模型目录：默认选型与两个提供方；选型状态按会话记账并经 follow 回声。</summary>
    private static readonly ModelCatalog Catalog = new(new ModelSelection("sim", "sim-chat"),
    [
        new ModelProviderGroup("sim", "Simulated",
        [
            new ModelCatalogEntry("sim-chat", "Sim Chat"), new ModelCatalogEntry("sim-reasoner", "Sim Reasoner")
        ]),
        new ModelProviderGroup("sim-alt", "Simulated Alt", [new ModelCatalogEntry("alt-chat", "Alt Chat")])
    ], []);

    private readonly Dictionary<string, SimulatedSession> _sessions = new()
    {
        ["session-welcome"] =
            new SimulatedSession(new SessionSummary("session-welcome", "欢迎使用", DateTimeOffset.Now.AddMinutes(-4),
                                                    false, SessionBlankState.Engaged),
            [
                CreateMessage(1, "welcome-user", MessageRole.User, "这个客户端现在能做什么？", -4, 1),
                CreateMessage(2, "welcome-assistant", MessageRole.Assistant,
                              "当前阶段支持会话切换、消息发送和取消。真实 Harness 后端接入后，这里会显示流式输出。", -4, 1),
                CreateBoundary(3, 1, -4)
            ], new ModelSelection("sim", "sim-chat")),
        ["session-native"] =
            new SimulatedSession(new SessionSummary("session-native", "Native AOT 验证", DateTimeOffset.Now.AddHours(-1),
                                                    false, SessionBlankState.Engaged),
            [
                CreateMessage(1, "native-user", MessageRole.User, "为什么先做原生界面？", -60, 1),
                CreateMessage(2, "native-assistant", MessageRole.Assistant,
                              "这样可以先稳定窗口、输入和状态模型，再把协议适配层接入，不让 UI 直接依赖后端细节。", -59, 1),
                CreateBoundary(3, 1, -59)
            ], new ModelSelection("sim", "sim-reasoner")),
        ["session-design"] =
            new SimulatedSession(new SessionSummary("session-design", "界面草稿", DateTimeOffset.Now.AddDays(-1),
                                                    false, SessionBlankState.Engaged),
            [
                CreateMessage(1, "design-user", MessageRole.User, "布局需要哪些区域？", -1440, 1),
                CreateMessage(2, "design-assistant", MessageRole.Assistant, "工作区/会话侧栏、消息区、输入区和后端状态提示。", -1439,
                              1),
                CreateBoundary(3, 1, -1439)
            ]),
        // 长历史会话：初始窗口只含尾部，翻页逐步露出更早轮次；全部读完后
        // 各轮过程条目折叠为过程组（对齐参考 Web 客户端的 historyIncomplete 行为）。
        // 置为最新，模拟模式首屏即展示工具卡片与历史翻页 UI。
        ["session-history"] =
            new SimulatedSession(new SessionSummary("session-history", "长会话翻页", DateTimeOffset.Now.AddMinutes(-1),
                                                    false, SessionBlankState.Engaged), BuildLongHistoryEntries(),
                                 new ModelSelection("sim", "sim-chat"))
    };

    /// <summary>真实新建（非收养复用）的累计次数；测试用于断言"只创建一次"。</summary>
    public int CreatedSessionCount { get; private set; }

    private readonly Lock _syncRoot = new();

    private readonly Action<string, string>? _onSessionCreatedInWorkspace;

    private int _nextSessionNumber = 5;

    public SimulatedSessionService(Action<string, string>? onSessionCreatedInWorkspace = null)
    {
        _onSessionCreatedInWorkspace = onSessionCreatedInWorkspace;
    }

    public event EventHandler? SessionsChanged;

    public Task<ModelCatalog> GetModelCatalogAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Catalog);
    }

    public Task<ModelSelection> SelectModelAsync(
        string sessionId, string provider, string model,
        string? reasoningEffort = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
                return Task.FromException<ModelSelection>(
                                                          new KeyNotFoundException($"未找到会话：{sessionId}"));

            // 与真实后端一致：选型（含推理档位）落在会话上，经 model/selection 更新回声生效。
            ModelSelection selection = new(provider, model, reasoningEffort);
            session.CurrentModel = selection;
            session.PushUpdate(new SessionUpdate.ModelSelected(selection));
            return Task.FromResult(selection);
        }
    }

    public Task<IReadOnlyList<SessionSummary>> GetSessionsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            IReadOnlyList<SessionSummary> result = _sessions.Values
                                                            .Select(session => session.Summary)
                                                            .OrderByDescending(summary => summary.UpdatedAt)
                                                            .ToArray();
            return Task.FromResult(result);
        }
    }

    public Task<SessionSummary> CreateSessionAsync(
        string? workspaceId = null, string? sessionId = null, string? agentPreset = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SessionSummary summary;
        lock (_syncRoot)
        {
            // 收养语义：按身份复用已有会话，返回其真实当前状态，不伪造空白/标题。
            if (sessionId is not null && _sessions.TryGetValue(sessionId, out var adopted))
            {
                if (!string.IsNullOrWhiteSpace(workspaceId))
                    _onSessionCreatedInWorkspace?.Invoke(workspaceId, sessionId);

                RaiseSessionsChanged();
                return Task.FromResult(adopted.Summary);
            }

            var now       = DateTimeOffset.Now;
            var newId     = sessionId ?? $"session-{_nextSessionNumber++}";
            summary       = new SessionSummary(newId, "新对话", now, false, SessionBlankState.ConfirmedBlank);
            _sessions.Add(newId, new SimulatedSession(summary, []));
            CreatedSessionCount++;
        }

        if (!string.IsNullOrWhiteSpace(workspaceId))
            _onSessionCreatedInWorkspace?.Invoke(workspaceId, summary.Id);

        RaiseSessionsChanged();
        return Task.FromResult(summary);
    }

    public void MarkSessionEngaged(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;

        lock (_syncRoot)
        {
            if (!_sessions.TryGetValue(sessionId, out var session)) return;

            session.Summary = session.Summary with { BlankState = SessionBlankState.Engaged };
        }

        RaiseSessionsChanged();
    }

    /// <summary>
    ///     测试种子：预置一个确认空白的会话（携带 cwd 供复用候选匹配），不经过创建入口。
    /// </summary>
    internal void SeedBlankSession(string sessionId, string? cwd, string? title = null)
    {
        lock (_syncRoot)
        {
            var summary = new SessionSummary(sessionId, title, DateTimeOffset.Now, false,
                                             SessionBlankState.ConfirmedBlank, cwd);
            _sessions.Add(sessionId, new SimulatedSession(summary, []));
        }

        RaiseSessionsChanged();
    }

    public Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(
        string sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
                throw new KeyNotFoundException($"未找到会话：{sessionId}");

            IReadOnlyList<ConversationMessage> result = session.Entries.OfType<ConversationMessage>().ToArray();
            return Task.FromResult(result);
        }
    }

    public Task<SessionHistoryPage> LoadOlderAsync(
        string sessionId, long throughSeq, long beforeSeq, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
                throw new KeyNotFoundException($"未找到会话：{sessionId}");

            var older = session.Entries.TakeWhile(entry => entry.Seq < beforeSeq).TakeLast(OlderPageSize).ToArray();
            var pageStart = older.Length > 0 ? older[0].Seq : beforeSeq;
            SessionHistoryPage page = new(older, pageStart, pageStart > 1);
            return Task.FromResult(page);
        }
    }

    public async Task SendPromptAsync(
        string sessionId, string requestId, string content, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content)) throw new ArgumentException("消息不能为空。", nameof(content));

        cancellationToken.ThrowIfCancellationRequested();
        await Task.Delay(260, cancellationToken).ConfigureAwait(false);

        lock (_syncRoot)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
                throw new KeyNotFoundException($"未找到会话：{sessionId}");

            var now = DateTimeOffset.Now;
            var userMessage = new ConversationMessage(session.NextSeq(), $"{requestId}-user", MessageRole.User,
                                                      content.Trim(), now);
            session.Entries.Add(userMessage);
            session.PushUpdate(new SessionUpdate.MessageAppended(userMessage));
            session.Summary = session.Summary with { UpdatedAt = now, BlankState = SessionBlankState.Engaged };
        }

        RaiseSessionsChanged();
    }

    public Task CancelAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<SessionUpdate> FollowSessionAsync(
        string sessionId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Channel<SessionUpdate> channel;
        SessionUpdate.Snapshot snapshot;
        SessionUsage           usage;
        SessionStats           stats;
        long                   statsSeq;
        lock (_syncRoot)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
                throw new KeyNotFoundException($"未找到会话：{sessionId}");

            channel  = session.Subscribe();
            snapshot = BuildSnapshot(session, session.WindowEntries(WindowSize));
            usage    = session.Usage;
            stats    = session.Stats;
            statsSeq = session.LastSeq;
        }

        yield return snapshot;
        // 统计整值随快照下发（对齐真实后端快照投影基线），seq 供界面做乱序 gating。
        yield return new SessionUpdate.UsageUpdated(usage, statsSeq);
        yield return new SessionUpdate.StatsUpdated(stats, statsSeq);

        try
        {
            await foreach (var update in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return update;
        }
        finally
        {
            lock (_syncRoot)
            {
                if (_sessions.TryGetValue(sessionId, out var session)) session.Unsubscribe(channel);
            }
        }
    }

    /// <summary>快照携带会话当前选型；调用方持有 _syncRoot。</summary>
    private static SessionUpdate.Snapshot BuildSnapshot(
        SimulatedSession session, IReadOnlyList<ConversationEntry> entries)
    {
        // 窗口是 Entries 的后缀：只要窗口起点之前还有条目（seq 从 1 起）就有更早历史。
        var windowStart = entries.Count > 0 ? entries[0].Seq : 0;
        return new SessionUpdate.Snapshot(entries, windowStart, windowStart, windowStart > 1,
                                          session.Summary.Title, session.CurrentModel);
    }

    private void RaiseSessionsChanged()
    {
        try
        {
            SessionsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            // 模拟服务的事件异常无需处理。
        }
    }

    private static ConversationMessage CreateMessage(
        long seq,                  string  id, MessageRole role, string content, int minutesAgo, long? turn = null,
        bool hasToolCalls = false, string? reasoning = null, bool isInterrupted = false)
    {
        return new ConversationMessage(seq, id, role, content, DateTimeOffset.Now.AddMinutes(minutesAgo),
                                       turn, hasToolCalls, Reasoning : reasoning, IsInterrupted : isInterrupted);
    }

    private static TurnBoundary CreateBoundary(long seq, long turn, int minutesAgo)
    {
        return new TurnBoundary(seq, turn, DateTimeOffset.Now.AddMinutes(minutesAgo));
    }

    private static List<ConversationEntry> BuildLongHistoryEntries()
    {
        // 每轮模拟真实 Harness 的工具执行形态：用户消息 → 思考（reasoning-only）→ 中间说明
        // → 工具调用 → 再思考 → 再调用工具 → 带思考的总结回答 → turn/end。历史未读全时不折叠；
        // 翻到顶部后各轮折叠为「过程组、总结回答」，思考条目随组折叠（对齐参考 Web 客户端）。
        List<ConversationEntry> entries = [];
        for (var index = 0; index < 4; index++)
        {
            var turn       = index     + 1;
            var baseSeq    = index * 8 + 1;
            var minutesAgo = -180      + index * 30;
            entries.Add(CreateMessage(baseSeq, $"history-user-{index}", MessageRole.User,
                                      $"历史问题 {index}：第 {index + 1} 轮", minutesAgo, turn));
            entries.Add(CreateMessage(baseSeq + 1, $"history-think-a-{index}", MessageRole.Assistant,
                                      string.Empty, minutesAgo, turn,
                                      reasoning : $"第 {index + 1} 轮：先定位相关文件，确认现有结构后再动手。"));
            entries.Add(CreateMessage(baseSeq + 2, $"history-assistant-interim-{index}", MessageRole.Assistant,
                                      $"先查看第 {index + 1} 轮的相关记录，再做定点更新。", minutesAgo, turn));
            entries.Add(new ToolActivity(baseSeq + 3, $"call-read-{index}", "fs.read",
                                         $"{{\"path\":\"notes-{index}.md\"}}",
                                         index % 2 == 0 ? ToolActivityStatus.Succeeded : ToolActivityStatus.Failed,
                                         index % 2 == 0 ? $"notes-{index}.md 的内容摘要……" : null,
                                         index % 2 == 0 ? null : "文件不存在（not-found）",
                                         DateTimeOffset.Now.AddMinutes(minutesAgo).AddSeconds(5),
                                         DateTimeOffset.Now.AddMinutes(minutesAgo).AddSeconds(6), turn));
            entries.Add(CreateMessage(baseSeq + 4, $"history-think-b-{index}", MessageRole.Assistant,
                                      string.Empty, minutesAgo, turn,
                                      reasoning : $"读取结果已知：对 notes-{index}.md 做定点替换即可，不需要重写全文。"));
            entries.Add(new ToolActivity(baseSeq + 5, $"call-edit-{index}", "fs.edit",
                                         $"{{\"path\":\"notes-{index}.md\",\"mode\":\"replace\"}}",
                                         ToolActivityStatus.Succeeded,
                                         $"notes-{index}.md 已更新。",
                                         null,
                                         DateTimeOffset.Now.AddMinutes(minutesAgo).AddSeconds(7),
                                         DateTimeOffset.Now.AddMinutes(minutesAgo).AddSeconds(8), turn));
            entries.Add(CreateMessage(baseSeq + 6, $"history-assistant-{index}", MessageRole.Assistant,
                                      $"历史回答 {index}：基于第 {index + 1} 轮工具结果的整理。", minutesAgo + 1, turn,
                                      reasoning : $"整理第 {index  + 1} 轮的两步执行结果并给出结论。"));
            entries.Add(CreateBoundary(baseSeq + 7, turn, minutesAgo + 1));
        }

        return entries;
    }

    /// <summary>模拟发送后推送助手回复，验证流式 UI 路径；结算按一步计费。</summary>
    internal void PushAssistantReply(string sessionId, string text, long? turn = null)
    {
        lock (_syncRoot)
        {
            if (!_sessions.TryGetValue(sessionId, out var session)) return;

            var attemptId = $"attempt-{Guid.NewGuid():N}";
            session.PushUpdate(new SessionUpdate.StreamStarted(attemptId));
            session.PushUpdate(new SessionUpdate.StreamTextDelta(attemptId, text));
            session.PushUpdate(new SessionUpdate.StreamEnded(attemptId, StreamOutcomeKind.Committed));
            var message = new ConversationMessage(session.NextSeq(), $"assistant-{Guid.NewGuid():N}",
                                                  MessageRole.Assistant, text, DateTimeOffset.Now, turn);
            session.Entries.Add(message);
            session.PushUpdate(new SessionUpdate.MessageAppended(message));
            session.Summary = session.Summary with { UpdatedAt = DateTimeOffset.Now };
            session.AccountBilledStep(turn, Math.Max(4, text.Length / 3));
        }

        RaiseSessionsChanged();
    }

    /// <summary>推送 turn/end 边界，驱动界面折叠该轮过程条目。</summary>
    internal void PushTurnEnded(string sessionId, long turn)
    {
        lock (_syncRoot)
        {
            if (!_sessions.TryGetValue(sessionId, out var session)) return;

            var boundary = new TurnBoundary(session.NextSeq(), turn, DateTimeOffset.Now);
            session.Entries.Add(boundary);
            session.PushUpdate(new SessionUpdate.TurnEnded(turn, boundary.Seq));
            session.Summary = session.Summary with { UpdatedAt = DateTimeOffset.Now };
        }

        RaiseSessionsChanged();
    }

    /// <summary>推送一次工具调用与结果，验证工具卡片路径。</summary>
    internal void PushToolActivity(
        string sessionId, string toolName, string? resultText, bool isError, long? turn = null)
    {
        lock (_syncRoot)
        {
            if (!_sessions.TryGetValue(sessionId, out var session)) return;

            var now    = DateTimeOffset.Now;
            var callId = $"call-{Guid.NewGuid():N}";
            var call = new ToolActivity(session.NextSeq(), callId, toolName, "{\"query\":\"模拟参数\"}",
                                        ToolActivityStatus.Running, null, null, now, Turn : turn);
            session.Entries.Add(call);
            session.PushUpdate(new SessionUpdate.ToolCallStarted(call));
            var settled = call.Settle(isError ? ToolActivityStatus.Failed : ToolActivityStatus.Succeeded,
                                      resultText, isError ? "模拟工具失败（simulated）" : null, now.AddSeconds(1));
            session.PushUpdate(new SessionUpdate.ToolCallSettled(settled));
        }
    }

    /// <summary>推送一个保持运行中的工具调用（无结果），验证运行中的工具卡片状态。</summary>
    internal ToolActivity? BeginToolActivity(string sessionId, string toolName, long? turn = null)
    {
        lock (_syncRoot)
        {
            if (!_sessions.TryGetValue(sessionId, out var session)) return null;

            var call = new ToolActivity(session.NextSeq(), $"call-{Guid.NewGuid():N}", toolName,
                                        "{\"query\":\"模拟参数\"}", ToolActivityStatus.Running, null, null,
                                        DateTimeOffset.Now, Turn : turn);
            session.Entries.Add(call);
            session.PushUpdate(new SessionUpdate.ToolCallStarted(call));
            return call;
        }
    }

    /// <summary>落定一个运行中的工具调用（按 CallId 匹配 BeginToolActivity 的返回值）。</summary>
    internal void SettleToolActivity(string sessionId, string callId, string? resultText, bool isError)
    {
        lock (_syncRoot)
        {
            if (!_sessions.TryGetValue(sessionId, out var session)) return;

            var index = session.Entries.FindIndex(entry => entry is ToolActivity tool && tool.CallId == callId);
            if (index < 0 || session.Entries[index] is not ToolActivity call) return;

            var settled = call.Settle(isError ? ToolActivityStatus.Failed : ToolActivityStatus.Succeeded,
                                      resultText, isError ? "模拟工具失败（simulated）" : null,
                                      DateTimeOffset.Now);
            session.Entries[index] = settled;
            session.PushUpdate(new SessionUpdate.ToolCallSettled(settled));
        }
    }

    /// <summary>
    ///     推送一条已落盘的助手消息（不经过流式路径）；空内容用于模拟纯思考或纯工具调用轮的
    ///     提交（hasToolCalls 表示内容块只含工具调用，reasoning 表示只含思考）。
    ///     助手消息结算按一步计费：累计 usage 与时间统计并推送统计更新。
    /// </summary>
    internal void PushCommittedAssistantMessage(
        string  sessionId,        string content, long? turn = null, bool hasToolCalls = false,
        string? reasoning = null, bool   isInterrupted = false)
    {
        lock (_syncRoot)
        {
            if (!_sessions.TryGetValue(sessionId, out var session)) return;

            var message = new ConversationMessage(session.NextSeq(), $"assistant-{Guid.NewGuid():N}",
                                                  MessageRole.Assistant, content, DateTimeOffset.Now, turn,
                                                  hasToolCalls, Reasoning : reasoning, IsInterrupted : isInterrupted);
            session.Entries.Add(message);
            session.PushUpdate(new SessionUpdate.MessageAppended(message));
            session.Summary = session.Summary with { UpdatedAt = DateTimeOffset.Now };
            session.AccountBilledStep(turn, Math.Max(4, content.Length / 3));
        }

        RaiseSessionsChanged();
    }

    /// <summary>推送一条不结束的流式增量，验证生成中的界面状态（如列表刷新不打断显示）。</summary>
    internal void BeginAssistantStream(string sessionId, string text)
    {
        lock (_syncRoot)
        {
            if (!_sessions.TryGetValue(sessionId, out var session)) return;

            var attemptId = $"attempt-{Guid.NewGuid():N}";
            session.PushUpdate(new SessionUpdate.StreamStarted(attemptId));
            session.PushUpdate(new SessionUpdate.StreamTextDelta(attemptId, text));
        }
    }

    /// <summary>推送被放弃的流式尝试（取消/失败），不会有正式消息事件。</summary>
    internal void PushAbandonedStream(string sessionId, string text)
    {
        lock (_syncRoot)
        {
            if (!_sessions.TryGetValue(sessionId, out var session)) return;

            var attemptId = $"attempt-{Guid.NewGuid():N}";
            session.PushUpdate(new SessionUpdate.StreamStarted(attemptId));
            session.PushUpdate(new SessionUpdate.StreamTextDelta(attemptId, text));
            session.PushUpdate(new SessionUpdate.StreamEnded(attemptId, StreamOutcomeKind.Abandoned));
        }
    }

    private sealed class SimulatedSession
    {
        private const    long          InputTokensPerStep = 96;
        private const    long          CacheReadPerStep   = 512;
        private const    long          CacheWritePerStep  = 128;
        private const    double        LlmMsPerStep       = 1500;
        private const    double        DecodeMsPerToken   = 45;
        private readonly HashSet<long> _billedTurns       = [];

        private readonly List<Channel<SessionUpdate>> _subscribers = [];

        public SimulatedSession(SessionSummary  summary, List<ConversationEntry> entries,
                                ModelSelection? currentModel = null)
        {
            Summary      = summary;
            Entries      = entries;
            CurrentModel = currentModel;
            LastSeq      = entries.Count > 0 ? entries[^1].Seq : 0;

            // 预置会话从既有条目推演初始计量：每条助手消息结算按一步计费。
            foreach (var entry in entries)
                if (entry is ConversationMessage { Role: MessageRole.Assistant } message)
                    AccountBilledStep(message.Turn, Math.Max(4, message.Content.Length / 3));
        }

        public SessionSummary Summary { get; set; }

        public List<ConversationEntry> Entries { get; }

        public ModelSelection? CurrentModel { get; set; }

        public SessionUsage Usage { get; set; } = new(0, 0, 0, 0);

        public SessionStats Stats { get; set; } = new(0, 0, 0, 0, 0, 0, 0, 0);

        public long LastSeq { get; private set; }

        public long NextSeq()
        {
            return ++LastSeq;
        }

        /// <summary>
        ///     一步计费的模拟记账：累加 usage 四桶与时间统计，并按当前 seq 推送统计更新
        ///     （与真实后端 control 流的整值投影语义一致）。
        /// </summary>
        public void AccountBilledStep(long? turn, int outputTokens)
        {
            if (turn is { } billedTurn) _billedTurns.Add(billedTurn);

            Usage = Usage with
            {
                UncachedInputTokens = Usage.UncachedInputTokens + InputTokensPerStep,
                OutputTokens = Usage.OutputTokens               + outputTokens,
                CacheReadTokens = Usage.CacheReadTokens         + CacheReadPerStep,
                CacheWriteTokens = Usage.CacheWriteTokens       + CacheWritePerStep
            };
            Stats = Stats with
            {
                Turns = _billedTurns.Count,
                Steps = Stats.Steps               + 1,
                LlmMs = Stats.LlmMs               + LlmMsPerStep,
                DecodeMs = Stats.DecodeMs         + outputTokens * DecodeMsPerToken,
                DecodeTokens = Stats.DecodeTokens + outputTokens
            };
            PushUpdate(new SessionUpdate.UsageUpdated(Usage, LastSeq));
            PushUpdate(new SessionUpdate.StatsUpdated(Stats, LastSeq));
        }

        /// <summary>最近 windowSize 条消息及其附随条目构成的初始窗口；消息不足窗口大小时包含全部。</summary>
        public IReadOnlyList<ConversationEntry> WindowEntries(int windowSize)
        {
            var messageCount = 0;
            var cut          = 0;
            for (var index = Entries.Count - 1; index >= 0; index--)
                if (Entries[index] is ConversationMessage)
                {
                    messageCount++;
                    if (messageCount >= windowSize)
                    {
                        cut = index;
                        break;
                    }
                }

            return Entries.Skip(cut).ToArray();
        }

        public Channel<SessionUpdate> Subscribe()
        {
            var channel = Channel.CreateUnbounded<SessionUpdate>();
            _subscribers.Add(channel);
            return channel;
        }

        public void Unsubscribe(Channel<SessionUpdate> channel)
        {
            _subscribers.Remove(channel);
            channel.Writer.TryComplete();
        }

        public void PushUpdate(SessionUpdate update)
        {
            foreach (var channel in _subscribers) channel.Writer.TryWrite(update);
        }
    }
}
