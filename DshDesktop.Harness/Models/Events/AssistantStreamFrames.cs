using System.Text.Json;

namespace DshDesktop.Harness.Models.Events;

/// <summary>助手流式帧；revision 逐帧 +1，text-delta 携带增量文本。</summary>
public abstract record AssistantStreamFrame
{
    public sealed record Start(string AttemptId, long Revision, long StartedAfterSeq, int Turn, int Step)
        : AssistantStreamFrame;

    public sealed record StreamChunkFrame(string AttemptId, long Revision, long Index, long Time, StreamChunk Chunk)
        : AssistantStreamFrame;

    public sealed record End(string AttemptId, long Revision, long Index, StreamOutcome Outcome) : AssistantStreamFrame;
}

public abstract record StreamChunk
{
    public sealed record TextDelta(long Index, string Text) : StreamChunk;

    public sealed record ReasoningDelta(long Index, string Text) : StreamChunk;

    /// <summary>阶段 2 不解释的分块（block-start、tool-call-delta、usage、finish 等）。</summary>
    public sealed record Other(string Type) : StreamChunk;
}

public sealed record StreamOutcome(string Kind, string? EventType, long? Seq);

public static class AssistantStreamFrameJson
{
    public static AssistantStreamFrame? ParseFrame(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object              ||
            !TryGetString(element, "type", out var type)           ||
            !TryGetString(element, "attemptId", out var attemptId) ||
            !TryGetNumber(element, "revision", out var revision))
            return null;

        switch (type)
        {
            case "start" when TryGetNumber(element, "startedAfterSeq", out var startedAfterSeq) :
                return new AssistantStreamFrame.Start(attemptId, revision, startedAfterSeq,
                                                      element.TryGetProperty("turn", out var turn) &&
                                                      turn.TryGetInt32(out var turnValue)
                                                          ? turnValue
                                                          : 0,
                                                      element.TryGetProperty("step", out var step) &&
                                                      step.TryGetInt32(out var stepValue)
                                                          ? stepValue
                                                          : 0);

            case "chunk" when TryGetNumber(element, "index", out var index) &&
                              element.TryGetProperty("chunk", out var chunkElement) :
                return new AssistantStreamFrame.StreamChunkFrame(attemptId, revision, index,
                                                                 element.TryGetProperty("time", out var time) &&
                                                                 time.TryGetInt64(out var timeValue)
                                                                     ? timeValue
                                                                     : 0,
                                                                 ParseChunk(chunkElement) ??
                                                                 new StreamChunk.Other("unknown"));

            case "end" when TryGetNumber(element, "index", out var endIndex)          &&
                            element.TryGetProperty("outcome", out var outcomeElement) &&
                            outcomeElement.ValueKind == JsonValueKind.Object          &&
                            TryGetString(outcomeElement, "kind", out var outcomeKind) :
            {
                var eventType = outcomeElement.TryGetProperty("eventType", out var eventTypeElement) &&
                                eventTypeElement.ValueKind == JsonValueKind.String
                    ? eventTypeElement.GetString()
                    : null;
                var seq = outcomeElement.TryGetProperty("seq", out var seqElement) &&
                          seqElement.ValueKind == JsonValueKind.Number             &&
                          seqElement.TryGetInt64(out var seqValue)
                    ? seqValue
                    : (long?)null;
                return new AssistantStreamFrame.End(attemptId, revision, endIndex,
                                                    new StreamOutcome(outcomeKind, eventType, seq));
            }

            default :
                return null;
        }
    }

    private static StreamChunk? ParseChunk(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !TryGetString(element, "type", out var type))
            return null;

        switch (type)
        {
            case "text-delta" when TryGetNumber(element, "index", out var index) &&
                                   TryGetString(element, "text", out var text) :
                return new StreamChunk.TextDelta(index, text);

            case "reasoning-delta" when TryGetNumber(element, "index", out var reasoningIndex) &&
                                        TryGetString(element, "text", out var reasoningText) :
                return new StreamChunk.ReasoningDelta(reasoningIndex, reasoningText);

            default :
                return new StreamChunk.Other(type);
        }
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetNumber(JsonElement element, string name, out long value)
    {
        if (element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Number     &&
            property.TryGetInt64(out value))
            return true;

        value = 0;
        return false;
    }
}
