using DshDesktop.Core.Models;
using DshDesktop.Core.Services;
using DshDesktop.Harness.Json;
using DshDesktop.Harness.Models.Events;
using DshDesktop.Harness.Models.Requests;
using DshDesktop.Harness.Models.Responses;
using DshDesktop.Harness.Services.Connection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace DshDesktop.Harness.Services.Sessions;

/// <summary>基于真实 Harness 协议的会话服务：协议 DTO 到应用模型的转换。</summary>
public sealed class HarnessSessionService : ISessionService
{
    /// <summary>与参考实现一致的单页消息预算（服务端默认也是 50）。</summary>
    private const int HistoryPageMessages = 50;

    /// <summary>后端会要求 IANA 时区；Windows 本地 id 需要转换，无法确定时省略。</summary>
    private static readonly string? ClientTimeZone = ResolveClientTimeZone();

    private readonly HarnessConnection _connection;

    /// <summary>创建会话后自动选用的模型（provider/model 形态）；未配置时不干预。</summary>
    private readonly (string Provider, string Model)? _preferredModel;

    public HarnessSessionService(HarnessConnection connection, (string Provider, string Model)? preferredModel = null)
    {
        _connection                 =  connection;
        _preferredModel             =  preferredModel;
        _connection.SessionActivity += OnConnectionNotified;
        _connection.ConnectionReset += OnConnectionNotified;
    }

    public event EventHandler? SessionsChanged;

    public async Task<IReadOnlyList<SessionSummary>> GetSessionsAsync(CancellationToken cancellationToken = default)
    {
        var value = await _connection.InvokeAsync("session/list",
                                                  new SessionListRequest(),
                                                  HarnessJsonContext.Default.SessionListRequest,
                                                  HarnessJsonContext.Default.SessionListValue,
                                                  cancellationToken,
                                                  "_request")
                                     .ConfigureAwait(false);
        return value.Items.Select(ToSummary)
                    .OrderByDescending(summary => summary.UpdatedAt)
                    .ToArray();
    }

    public async Task<SessionSummary> CreateSessionAsync(
        string? workspaceId = null, CancellationToken cancellationToken = default)
    {
        var value = await _connection.InvokeAsync("session/create",
                                                  new SessionCreateRequest(workspaceId),
                                                  HarnessJsonContext.Default.SessionCreateRequest,
                                                  HarnessJsonContext.Default.SessionCreateValue,
                                                  cancellationToken)
                                     .ConfigureAwait(false);
        if (_preferredModel is { } preferred)
            // 默认模型可能指向未配置凭据的提供方；显式选型保证新会话立即可用。
            await _connection.InvokeAsync("session/selectModel",
                                          new SessionSelectModelRequest(value.SessionId, preferred.Provider,
                                                                        preferred.Model),
                                          HarnessJsonContext.Default.SessionSelectModelRequest,
                                          HarnessJsonContext.Default.SessionSelectModelValue,
                                          cancellationToken)
                             .ConfigureAwait(false);

        var summary = new SessionSummary(value.SessionId, null, DateTimeOffset.Now, false, true);
        RaiseSessionsChanged();
        return summary;
    }

    /// <summary>查询模型目录（默认选型与各提供方可选模型）。</summary>
    public async Task<ModelCatalog> GetModelCatalogAsync(CancellationToken cancellationToken = default)
    {
        var value = await _connection.InvokeEmptyAsync("session/modelCatalog",
                                                       HarnessJsonContext.Default.SessionModelCatalogValue,
                                                       cancellationToken)
                                     .ConfigureAwait(false);
        return ToCatalog(value);
    }

    public async Task<ModelSelection> SelectModelAsync(
        string sessionId, string provider, string model,
        string? reasoningEffort = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("会话 id 不能为空。", nameof(sessionId));

        if (string.IsNullOrWhiteSpace(provider)) throw new ArgumentException("提供方不能为空。", nameof(provider));

        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("模型不能为空。", nameof(model));

        var value = await _connection.InvokeAsync("session/selectModel",
                                                  new SessionSelectModelRequest(sessionId, provider, model,
                                                                                reasoningEffort),
                                                  HarnessJsonContext.Default.SessionSelectModelRequest,
                                                  HarnessJsonContext.Default.SessionSelectModelValue,
                                                  cancellationToken)
                                     .ConfigureAwait(false);
        return new ModelSelection(value.Selected.Provider, value.Selected.Model, value.Selected.ReasoningEffort);
    }

    public async Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(
        string sessionId, CancellationToken cancellationToken = default)
    {
        var snapshot = await _connection.TakeSnapshotAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null) throw new KeyNotFoundException($"未找到会话：{sessionId}");

        return MapMessages(snapshot.Records);
    }

    public async Task<SessionHistoryPage> LoadOlderAsync(
        string sessionId, long throughSeq, long beforeSeq, CancellationToken cancellationToken = default)
    {
        var value = await _connection.InvokeAsync("session/page",
                                                  new SessionPageRequest(new SessionAddress(sessionId), throughSeq,
                                                                         beforeSeq, HistoryPageMessages),
                                                  HarnessJsonContext.Default.SessionPageRequest,
                                                  HarnessJsonContext.Default.SessionPageValue, cancellationToken)
                                     .ConfigureAwait(false);
        var records     = FollowFrameJson.ParseHistoryRecords(value.Records);
        var entries     = MapEntries(records);
        var windowStart = records.Count > 0 ? records[0].Seq : beforeSeq;
        return new SessionHistoryPage(entries, windowStart, value.HasMore);
    }

    public Task SendPromptAsync(
        string sessionId, string requestId, string content, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(content)) throw new ArgumentException("消息不能为空。", nameof(content));

        return _connection.InvokeAsync("session/prompt",
                                       new SessionPromptRequest(requestId, sessionId, "queue",
                                                                [new PromptTextPart(content.Trim())], ClientTimeZone),
                                       HarnessJsonContext.Default.SessionPromptRequest,
                                       HarnessJsonContext.Default.SessionAcceptedValue, cancellationToken);
    }

    public Task CancelAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        return _connection.InvokeAsync("session/cancel", new SessionCancelRequest(sessionId),
                                       HarnessJsonContext.Default.SessionCancelRequest,
                                       HarnessJsonContext.Default.SessionAcceptedValue, cancellationToken);
    }

    /// <summary>
    ///     订阅会话更新：follow 流（快照 + 事件 + 流式帧）与 session/control 投影流合并输出。
    ///     统计整值随流下发（快照投影基线 + control 实时帧），按投影 seq 以新值为准；
    ///     control 流失败只影响统计的实时性，不终止会话订阅。
    /// </summary>
    public async IAsyncEnumerable<SessionUpdate> FollowSessionAsync(
        string sessionId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var output = Channel.CreateUnbounded<SessionUpdate>();
        _ = PumpFollowFramesAsync(sessionId, output.Writer, cancellationToken);
        _ = PumpControlFramesAsync(sessionId, output.Writer, cancellationToken);
        while (await output.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        while (output.Reader.TryRead(out var update))
            yield return update;
    }

    /// <summary>目录线上形态到应用模型：空模型组剔除；失败项保留 name/message 供界面提示。</summary>
    internal static ModelCatalog ToCatalog(SessionModelCatalogValue value)
    {
        var defaultSelection = value.Default is { } preferred
            ? new ModelSelection(preferred.Provider, preferred.Model, preferred.ReasoningEffort)
            : null;
        var groups = (value.Groups ?? [])
                    .Select(group => new ModelProviderGroup(group.Id, group.Name,
                                                            (group.Models ?? [])
                                                           .Where(model => !string.IsNullOrWhiteSpace(model.Id) &&
                                                                           !string.IsNullOrWhiteSpace(model.Name))
                                                           .Select(model => new ModelCatalogEntry(model.Id, model.Name))
                                                           .ToArray()))
                    .Where(group => group.Models.Count > 0)
                    .ToArray();
        var failures = (value.Failures ?? [])
                      .Select(failure => new ModelCatalogFailure(failure.Id,
                                                                 failure.Name    ?? failure.Id,
                                                                 failure.Message ?? string.Empty))
                      .ToArray();
        return new ModelCatalog(defaultSelection, groups, failures);
    }

    /// <summary>会话流泵：帧映射为更新写入合并通道；流终止（含业务错误）即完成通道。</summary>
    private async Task PumpFollowFramesAsync(
        string sessionId, ChannelWriter<SessionUpdate> writer, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in _connection.FollowAsync(sessionId, cancellationToken).ConfigureAwait(false))
            foreach (var update in MapFollowFrame(frame))
                await writer.WriteAsync(update, cancellationToken).ConfigureAwait(false);

            writer.TryComplete();
        }
        catch (OperationCanceledException exception)
        {
            writer.TryComplete(exception);
        }
        catch (Exception exception)
        {
            writer.TryComplete(exception);
        }
    }

    /// <summary>控制流泵：只取本会话的投影整值更新。失败静默退出，不拖垮会话流。</summary>
    private async Task PumpControlFramesAsync(
        string sessionId, ChannelWriter<SessionUpdate> writer, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in _connection.FollowSessionControlAsync(cancellationToken)
                                                   .ConfigureAwait(false))
                switch (frame)
                {
                    case SessionControlFrame.Baseline baseline
                        when baseline.Projections.TryGetValue(sessionId, out var block) :
                        await WriteProjectionValue(block.Values, SessionControlFrameJson.UsageKey,
                                                   block.AsOfSeq, writer, cancellationToken);
                        await WriteProjectionValue(block.Values, SessionControlFrameJson.StatsKey,
                                                   block.AsOfSeq, writer, cancellationToken);
                        break;

                    case SessionControlFrame.ProjectionUpdate update when update.SessionId == sessionId :
                        if (update.Usage is { } usage)
                            await writer.WriteAsync(new SessionUpdate.UsageUpdated(usage, update.Seq),
                                                    cancellationToken).ConfigureAwait(false);

                        if (update.Stats is { } stats)
                            await writer.WriteAsync(new SessionUpdate.StatsUpdated(stats, update.Seq),
                                                    cancellationToken).ConfigureAwait(false);

                        break;
                }
        }
        catch (Exception)
        {
            // 统计通道是辅助数据：错误时放弃实时性，等待重连后的快照投影补回基线。
        }
    }

    /// <summary>从基线 values 字典读取一个投影键并按整值下发（缺失或形状不符则跳过）。</summary>
    private static async ValueTask WriteProjectionValue(
        JsonElement       values, string key, long seq, ChannelWriter<SessionUpdate> writer,
        CancellationToken cancellationToken)
    {
        if (!values.TryGetProperty(key, out var element)) return;

        switch (key)
        {
            case SessionControlFrameJson.UsageKey when ProjectionValuesJson.ParseUsage(element) is { } usage :
                await writer.WriteAsync(new SessionUpdate.UsageUpdated(usage, seq), cancellationToken)
                            .ConfigureAwait(false);
                break;

            case SessionControlFrameJson.StatsKey when ProjectionValuesJson.ParseStats(element) is { } stats :
                await writer.WriteAsync(new SessionUpdate.StatsUpdated(stats, seq), cancellationToken)
                            .ConfigureAwait(false);
                break;
        }
    }

    /// <summary>follow 帧到会话更新的映射；快照统计随快照作为独立更新紧随其后。</summary>
    private IEnumerable<SessionUpdate> MapFollowFrame(FollowFrame frame)
    {
        switch (frame)
        {
            case FollowFrame.Snapshot snapshot :
                yield return new SessionUpdate.Snapshot(MapEntries(snapshot.Records), snapshot.Cursor,
                                                        WindowStartSeqOf(snapshot.Records), snapshot.HasMore,
                                                        snapshot.Title, snapshot.CurrentModel);
                if (snapshot.Usage is { } usage)
                    yield return new SessionUpdate.UsageUpdated(usage, snapshot.ProjectionAsOfSeq);

                if (snapshot.Stats is { } stats)
                    yield return new SessionUpdate.StatsUpdated(stats, snapshot.ProjectionAsOfSeq);

                break;

            case FollowFrame.EventFrame { Event: var wireEvent } :
            {
                if (WireEventJson.TryGetMessage(wireEvent) is { } message
                 && IsDisplayable(message))
                {
                    // 增量与快照走同一内容映射：取消产生的 interrupted 消息即时带中断标注。
                    yield return new SessionUpdate.MessageAppended(ToConversationMessage(wireEvent, message));
                }
                else if (WireEventJson.TryGetTurnEnd(wireEvent, out var endedTurn))
                {
                    yield return new SessionUpdate.TurnEnded(endedTurn, wireEvent.Seq,
                                                             WireEventJson.TurnEndReason(wireEvent));
                }
                else if (wireEvent.Type == "tool/call"
                      && WireEventJson.TryGetToolCall(wireEvent) is { } call)
                {
                    long? turn = WireEventJson.TryGetTurnStep(wireEvent, out var callTurn, out _)
                        ? callTurn
                        : null;
                    yield return new SessionUpdate.ToolCallStarted(new ToolActivity(wireEvent.Seq, call.CallId,
                                                                       call.Name, call.Arguments,
                                                                       ToolActivityStatus.Running, null, null,
                                                                       DateTimeOffset
                                                                          .FromUnixTimeMilliseconds(wireEvent
                                                                              .Time), Turn : turn));
                }
                else if (WireEventJson.TryGetToolResult(wireEvent) is { } result)
                {
                    yield return new SessionUpdate.ToolCallSettled(ToSettledToolActivity(wireEvent, result));
                }
                else if (WireEventJson.TryGetTitle(wireEvent, out var title))
                {
                    yield return new SessionUpdate.TitleChanged(title);
                }
                else if (WireEventJson.TryGetModelSelection(wireEvent) is { } selection)
                {
                    yield return new SessionUpdate.ModelSelected(new ModelSelection(selection.Provider,
                                                                     selection.Model,
                                                                     selection.ReasoningEffort));
                }

                break;
            }

            case FollowFrame.AssistantStream { Frame: var streamFrame } :
            {
                switch (streamFrame)
                {
                    case AssistantStreamFrame.Start start :
                        yield return new SessionUpdate.StreamStarted(start.AttemptId);
                        break;

                    case AssistantStreamFrame.StreamChunkFrame { Chunk: StreamChunk.TextDelta textDelta } chunk :
                        yield return new SessionUpdate.StreamTextDelta(chunk.AttemptId, textDelta.Text);
                        break;

                    case AssistantStreamFrame.End end :
                        yield return new SessionUpdate.StreamEnded(end.AttemptId, ToOutcomeKind(end.Outcome),
                                                                   end.Outcome.EventType);
                        break;
                }

                break;
            }
        }
    }

    internal static SessionSummary ToSummary(SessionSummaryWire wire)
    {
        var title = wire.Projections?.Values is { } values
                 && values.TryGetValue("title", out var titleElement)
                 && titleElement.ValueKind == JsonValueKind.String
            ? titleElement.GetString()
            : null;
        // blank 只信任响应携带的空白状态，不得以投影缺失代替空白证据：会话格式迁移后
        // v3 老会话的 sessionListMetadata 会被投影缓存拒认，冷行统一回退 blank:false 且
        // 不带该投影——这类行与空白草稿在响应中不可区分，标记为空白会把可能有效的
        // 会话（有轮次但标题未生成的旧会话）误藏出列表。v3 时代未使用的草稿会以
        // "新对话"可见，打开一次后后端以当前格式重写投影，随后的刷新恢复既有过滤。
        return new SessionSummary(wire.SessionId, title, DateTimeOffset.FromUnixTimeMilliseconds(wire.UpdatedAt),
                                  wire.Running, wire.Blank);
    }

    /// <summary>后端 outcome：committed（落盘为正式消息）或 abandoned（取消/失败，无正式消息）。</summary>
    private static StreamOutcomeKind ToOutcomeKind(StreamOutcome outcome)
    {
        return outcome.Kind switch
        {
            "committed" => StreamOutcomeKind.Committed,
            "abandoned" => StreamOutcomeKind.Abandoned,
            _           => StreamOutcomeKind.Unknown
        };
    }

    internal static IReadOnlyList<ConversationMessage> MapMessages(IEnumerable<SessionWireEvent> records)
    {
        var messages = new List<ConversationMessage>();
        foreach (var wireEvent in records)
        {
            if (WireEventJson.TryGetMessage(wireEvent) is not { } message
             || !IsDisplayable(message))
                continue;

            messages.Add(ToConversationMessage(wireEvent, message));
        }

        return messages;
    }

    /// <summary>窗口首条事件的 seq；空窗口没有更早历史，返回 0 即可。</summary>
    private static long WindowStartSeqOf(IReadOnlyList<SessionWireEvent> records)
    {
        return records.Count > 0 ? records[0].Seq : 0;
    }

    /// <summary>
    ///     将窗口内的事件折叠为时间线条目：消息保持原样；tool/call 与其后同 callId 的
    ///     tool/result 折叠为同一 ToolActivity（保留发起位置与参数，补上结果状态）；
    ///     turn/end 产出 <see cref="TurnBoundary" /> 供界面折叠该轮过程条目。
    ///     窗口起点落在调用中间时可能出现无 call 的 result，作为独立条目展示结果。
    /// </summary>
    internal static IReadOnlyList<ConversationEntry> MapEntries(IReadOnlyList<SessionWireEvent> records)
    {
        var entries           = new List<ConversationEntry>();
        var toolIndexByCallId = new Dictionary<string, int>();
        for (var index = 0; index < records.Count; index++)
        {
            var wireEvent = records[index];
            switch (wireEvent.Type)
            {
                case "user/message" or "assistant/message" :
                    if (WireEventJson.TryGetMessage(wireEvent) is { } message && IsDisplayable(message))
                        entries.Add(ToConversationMessage(wireEvent, message));

                    break;

                case "turn/end" :
                    if (WireEventJson.TryGetTurnEnd(wireEvent, out var endedTurn))
                        entries.Add(new TurnBoundary(wireEvent.Seq, endedTurn,
                                                     DateTimeOffset.FromUnixTimeMilliseconds(wireEvent.Time),
                                                     WireEventJson.TurnEndReason(wireEvent)));

                    break;

                case "tool/call" :
                    if (WireEventJson.TryGetToolCall(wireEvent) is { } call)
                    {
                        long? callTurn = WireEventJson.TryGetTurnStep(wireEvent, out var parsedTurn, out _)
                            ? parsedTurn
                            : null;
                        entries.Add(new ToolActivity(wireEvent.Seq, call.CallId, call.Name, call.Arguments,
                                                     ToolActivityStatus.Running, null, null,
                                                     DateTimeOffset.FromUnixTimeMilliseconds(wireEvent.Time),
                                                     Turn : callTurn));
                        toolIndexByCallId[call.CallId] = entries.Count - 1;
                    }

                    break;

                case "tool/result" :
                    if (WireEventJson.TryGetToolResult(wireEvent) is { } result)
                    {
                        var settled = ToSettledToolActivity(wireEvent, result);
                        if (toolIndexByCallId.TryGetValue(result.CallId!, out var entryIndex))
                        {
                            var running = (ToolActivity)entries[entryIndex];
                            // 保留发起时刻的时间线位置与参数，仅补上结果。
                            entries[entryIndex] = running with
                            {
                                Status = settled.Status,
                                ResultText = settled.ResultText,
                                ErrorReason = settled.ErrorReason,
                                CompletedAt = settled.CompletedAt
                            };
                        }
                        else
                        {
                            entries.Add(settled);
                        }
                    }

                    break;
            }
        }

        return entries;
    }

    /// <summary>增量 tool/result 到落定 ToolActivity 的映射；调用参数在增量路径不可得，由界面按 callId 合并。</summary>
    private static ToolActivity ToSettledToolActivity(SessionWireEvent wireEvent, ToolResultWire result)
    {
        var status = result.IsError || result.ErrorName is not null
            ? ToolActivityStatus.Failed
            : ToolActivityStatus.Succeeded;
        var errorReason = result.ErrorReason ?? (result.ErrorName is null
            ? null
            : $"{result.ErrorName}（{result.ErrorCode ?? "error"}）");
        var   completedAt = DateTimeOffset.FromUnixTimeMilliseconds(wireEvent.Time);
        long? turn        = WireEventJson.TryGetTurnStep(wireEvent, out var parsedTurn, out _) ? parsedTurn : null;
        return new ToolActivity(wireEvent.Seq, result.CallId ?? string.Empty, string.Empty, null, status,
                                result.ContentText, errorReason,
                                DateTimeOffset.FromUnixTimeMilliseconds(wireEvent.Time), completedAt, turn);
    }

    /// <summary>
    ///     消息是否进入聊天视图。后端会以 user 角色但非 'user' 的 source.kind 注入上下文
    ///     （runtime-context 快照、技能目录/内容、工具结果等），它们不是用户输入，不作为用户气泡显示。
    /// </summary>
    private static bool IsDisplayable(WireMessage message)
    {
        if (message.Role != "user") return true;

        var kind = WireEventJson.GetUserSourceKind(message);
        return kind is null or "user";
    }

    /// <summary>消息文本与思考文本；中断标注走 IsInterrupted 独立展示，不混入正文。</summary>
    private static ConversationMessage ToConversationMessage(SessionWireEvent wireEvent, WireMessage message)
    {
        var role = message.Role switch
        {
            "user"      => MessageRole.User,
            "assistant" => MessageRole.Assistant,
            _           => MessageRole.System
        };
        var turn = WireEventJson.TryGetTurnStep(wireEvent, out var parsedTurn, out var step)
            ? parsedTurn
            : (long?)null;
        return new ConversationMessage(wireEvent.Seq, message.Id, role, WireEventJson.ExtractText(message),
                                       DateTimeOffset.FromUnixTimeMilliseconds(wireEvent.Time), turn,
                                       WireEventJson.HasToolCallBlocks(message), turn is null ? null : step,
                                       WireEventJson.ExtractReasoning(message), WireEventJson.IsInterrupted(wireEvent));
    }

    private static string? ResolveClientTimeZone()
    {
        try
        {
            var id = TimeZoneInfo.Local.Id;
            if (id.Contains('/')) return id;

            return TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana) ? iana : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void OnConnectionNotified(object? sender, EventArgs e)
    {
        RaiseSessionsChanged();
    }

    private void RaiseSessionsChanged()
    {
        try
        {
            SessionsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            // 事件处理器异常不影响连接层。
        }
    }
}
