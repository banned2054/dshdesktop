using DshDesktop.Core.Models;
using System.Text.Json;

namespace DshDesktop.Harness.Models.Events;

/// <summary>session/follow 的下行帧；每代第一帧必为 Snapshot，之后为增量。</summary>
public abstract record FollowFrame
{
    /// <summary>整窗快照；重连恢复也以新 Snapshot 开始，消费者必须整体替换。</summary>
    public sealed record Snapshot(
        SessionWireHeader               Header,
        long                            Cursor,
        IReadOnlyList<SessionWireEvent> Records,
        bool                            HasMore,
        string?                         Title,
        ModelSelection?                 CurrentModel      = null,
        SessionUsage?                   Usage             = null,
        SessionStats?                   Stats             = null,
        long                            ProjectionAsOfSeq = 0) : FollowFrame;

    public sealed record EventFrame(SessionWireEvent Event) : FollowFrame;

    public sealed record AssistantStream(AssistantStreamFrame Frame) : FollowFrame;
}

/// <summary>follow 帧解析。</summary>
public static class FollowFrameJson
{
    /// <summary>
    ///     解析历史记录条目数组（快照 records 与 session/page 返回的 records 同形：
    ///     每项为 {type:'event', event:{...}}，event 为线形会话事件）。
    /// </summary>
    public static List<SessionWireEvent> ParseHistoryRecords(JsonElement recordsElement)
    {
        var records = new List<SessionWireEvent>();
        if (recordsElement.ValueKind != JsonValueKind.Array) return records;

        foreach (var record in recordsElement.EnumerateArray())
            if (record.ValueKind == JsonValueKind.Object           &&
                record.TryGetProperty("event", out var entryEvent) &&
                WireEventJson.ParseEvent(entryEvent) is { } parsed)
                records.Add(parsed);

        return records;
    }

    public static FollowFrame? Parse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object            ||
            !element.TryGetProperty("type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String)
            return null;

        switch (typeElement.GetString())
        {
            case "snapshot" :
            {
                if (!element.TryGetProperty("cursor", out var cursorElement) ||
                    cursorElement.ValueKind != JsonValueKind.Number          ||
                    !cursorElement.TryGetInt64(out var cursor))
                    return null;

                var header = element.TryGetProperty("header", out var headerElement) &&
                             headerElement.ValueKind == JsonValueKind.Object
                    ? ParseHeader(headerElement)
                    : null;
                var records = element.TryGetProperty("records", out var recordsElement)
                    ? ParseHistoryRecords(recordsElement)
                    : [];

                var hasMore = element.TryGetProperty("hasMore", out var hasMoreElement) &&
                              hasMoreElement.ValueKind == JsonValueKind.True;
                var           title  = ReadTitleProjection(element);
                var           values = ReadProjectionValues(element, out var projectionAsOfSeq);
                SessionUsage? usage  = null;
                SessionStats? stats  = null;
                if (values.ValueKind == JsonValueKind.Object)
                {
                    if (values.TryGetProperty(SessionControlFrameJson.UsageKey, out var usageElement))
                        usage = ProjectionValuesJson.ParseUsage(usageElement);

                    if (values.TryGetProperty(SessionControlFrameJson.StatsKey, out var statsElement))
                        stats = ProjectionValuesJson.ParseStats(statsElement);
                }

                return new FollowFrame.Snapshot(header ?? new SessionWireHeader(0, string.Empty, 0, null, null, false,
                                                                                    null, null), cursor, records,
                                                hasMore, title, ReadModelSelectionProjection(element), usage, stats,
                                                projectionAsOfSeq);
            }

            case "event" :
                return element.TryGetProperty("event", out var eventElement)
                    ? WireEventJson.ParseEvent(eventElement) is { } wireEvent
                        ? new FollowFrame.EventFrame(wireEvent)
                        : null
                    : null;

            case "assistant-stream" :
                return element.TryGetProperty("frame", out var frameElement) &&
                       AssistantStreamFrameJson.ParseFrame(frameElement) is { } frame
                    ? new FollowFrame.AssistantStream(frame)
                    : null;

            default :
                return null;
        }
    }

    private static SessionWireHeader ParseHeader(JsonElement element)
    {
        return new SessionWireHeader(element.TryGetProperty("version", out var version) &&
                                     version.TryGetInt32(out var v)
                                         ? v
                                         : 0,
                                     element.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                                         ? id.GetString() ?? string.Empty
                                         : string.Empty,
                                     element.TryGetProperty("createdAt", out var createdAt) &&
                                     createdAt.TryGetInt64(out var created)
                                         ? created
                                         : 0,
                                     element.TryGetProperty("cwd", out var cwd) && cwd.ValueKind == JsonValueKind.String
                                         ? cwd.GetString()
                                         : null,
                                     element.TryGetProperty("parentSession", out var parent) &&
                                     parent.ValueKind == JsonValueKind.String
                                         ? parent.GetString()
                                         : null,
                                     element.TryGetProperty("isSeeded", out var seeded) &&
                                     seeded.ValueKind == JsonValueKind.True,
                                     element.TryGetProperty("origin", out var origin) &&
                                     origin.ValueKind == JsonValueKind.String
                                         ? origin.GetString()
                                         : null,
                                     element.TryGetProperty("agentPreset", out var preset) &&
                                     preset.ValueKind == JsonValueKind.String
                                         ? preset.GetString()
                                         : null);
    }

    private static string? ReadTitleProjection(JsonElement snapshot)
    {
        if (!snapshot.TryGetProperty("projections", out var projections) ||
            projections.ValueKind != JsonValueKind.Object                ||
            !projections.TryGetProperty("values", out var values)        ||
            values.ValueKind != JsonValueKind.Object                     ||
            !values.TryGetProperty("title", out var title))
            return null;

        return title.ValueKind == JsonValueKind.String ? title.GetString() : null;
    }

    /// <summary>
    ///     快照 projections.values.modelSelection 投影（{lastUsed, next}）。
    ///     next 是下一次请求将使用的选型（pending ?? lastUsed）；next 为空时回退 lastUsed，
    ///     两者皆空（未选过型）返回 null。
    /// </summary>
    private static ModelSelection? ReadModelSelectionProjection(JsonElement snapshot)
    {
        var values = ReadProjectionValues(snapshot, out _);
        if (values.ValueKind != JsonValueKind.Object                         ||
            !values.TryGetProperty("modelSelection", out var modelSelection) ||
            modelSelection.ValueKind != JsonValueKind.Object)
            return null;

        return ParseSelection(modelSelection, "next") ?? ParseSelection(modelSelection, "lastUsed");
    }

    /// <summary>快照 projections.values 字典；缺失时返回 Undefined 且 asOfSeq 为 0。</summary>
    private static JsonElement ReadProjectionValues(JsonElement snapshot, out long asOfSeq)
    {
        asOfSeq = 0;
        if (!snapshot.TryGetProperty("projections", out var projections) ||
            projections.ValueKind != JsonValueKind.Object                ||
            !projections.TryGetProperty("values", out var values)        ||
            values.ValueKind != JsonValueKind.Object)
            return default;

        if (projections.TryGetProperty("asOfSeq", out var seqElement) &&
            seqElement.ValueKind == JsonValueKind.Number              &&
            seqElement.TryGetInt64(out var seq))
            asOfSeq = seq;

        return values;
    }

    private static ModelSelection? ParseSelection(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var element)         ||
            element.ValueKind != JsonValueKind.Object             ||
            !element.TryGetProperty("provider", out var provider) ||
            provider.ValueKind != JsonValueKind.String            ||
            !element.TryGetProperty("model", out var model)       ||
            model.ValueKind != JsonValueKind.String)
            return null;

        var reasoningEffort =
            element.TryGetProperty("reasoningEffort", out var effort) && effort.ValueKind == JsonValueKind.String
                ? effort.GetString()
                : null;
        return new ModelSelection(provider.GetString()!, model.GetString()!, reasoningEffort);
    }
}
