using DshDesktop.Core.Models;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace DshDesktop.Harness.Models.Events;

/// <summary>
///     session/control 状态流帧（Host 级，每代以 Baseline 开场）。
///     jobs 帧暂无消费方，解析为 null 跳过（与 workspace archived 帧同一先例）。
/// </summary>
public abstract record SessionControlFrame
{
    /// <summary>整代基线：各会话的投影值（本客户端只消费 tokenUsage/sessionStats）。</summary>
    public sealed record Baseline(IReadOnlyDictionary<string, SessionProjectionSnapshotWire> Projections)
        : SessionControlFrame;

    /// <summary>单个投影值更新：整值替换，seq 是促成该值的事件 seq。</summary>
    public sealed record ProjectionUpdate(
        string        SessionId,
        string        Key,
        SessionUsage? Usage,
        SessionStats? Stats,
        long          Seq,
        string?       PermissionValue = null) : SessionControlFrame;
}

/// <summary>一个会话的全部投影快照（线上形态；只解释消费的键）。</summary>
public sealed record SessionProjectionSnapshotWire(long AsOfSeq, JsonElement Values);

/// <summary>session/control 帧解析。</summary>
public static class SessionControlFrameJson
{
    /// <summary>projection 帧仅在这些键上有消费方；其余键解析为无载荷更新（忽略）。</summary>
    public const string UsageKey = "tokenUsage";

    public const string StatsKey = "sessionStats";

    public const string PermissionsKey = "permissions";

    public static SessionControlFrame? Parse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object            ||
            !element.TryGetProperty("type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String)
            return null;

        switch (typeElement.GetString())
        {
            case "baseline" :
            {
                if (!element.TryGetProperty("value", out var value)           ||
                    value.ValueKind != JsonValueKind.Object                   ||
                    !value.TryGetProperty("projections", out var projections) ||
                    projections.ValueKind != JsonValueKind.Object)
                    return null;

                var snapshot = new Dictionary<string, SessionProjectionSnapshotWire>();
                foreach (var entry in projections.EnumerateObject())
                    if (entry.Value.ValueKind == JsonValueKind.Object          &&
                        entry.Value.TryGetProperty("asOfSeq", out var asOfSeq) &&
                        asOfSeq.TryGetInt64(out var seq)                       &&
                        entry.Value.TryGetProperty("values", out var values)   &&
                        values.ValueKind == JsonValueKind.Object)
                        snapshot[entry.Name] = new SessionProjectionSnapshotWire(seq, values.Clone());

                return new SessionControlFrame.Baseline(snapshot);
            }

            case "projection" :
            {
                if (!TryGetString(element, "sessionId", out var sessionId) ||
                    !TryGetString(element, "key", out var key)             ||
                    !element.TryGetProperty("seq", out var seqElement)     ||
                    !seqElement.TryGetInt64(out var seq))
                    return null;

                SessionUsage? usage      = null;
                SessionStats? stats      = null;
                string?       permission = null;
                if (!element.TryGetProperty("value", out var value))
                    return new SessionControlFrame.ProjectionUpdate(sessionId, key, usage, stats, seq);
                switch (key)
                {
                    case UsageKey :
                        usage = ProjectionValuesJson.ParseUsage(value);
                        break;
                    case StatsKey :
                        stats = ProjectionValuesJson.ParseStats(value);
                        break;
                    case PermissionsKey :
                        permission = ProjectionValuesJson.ParsePermissions(value);
                        break;
                }

                return new SessionControlFrame.ProjectionUpdate(sessionId, key, usage, stats, seq, permission);
            }

            default :
                return null;
        }
    }

    private static bool TryGetString(
        JsonElement element, string name, [NotNullWhen(true)] out string? value)
    {
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return value is not null;
        }

        value = null;
        return false;
    }
}

/// <summary>会话投影 values 字典中受支持投影的解析（快照与 control 帧共用）。</summary>
public static class ProjectionValuesJson
{
    /// <summary>tokenUsage 投影视图：四桶互斥累计（uncachedInput/output/cacheRead/cacheWrite）。</summary>
    public static SessionUsage? ParseUsage(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Object                         &&
               TryGetLong(element, "uncachedInputTokens", out var uncachedInput) &&
               TryGetLong(element, "outputTokens", out var output)               &&
               TryGetLong(element, "cacheReadTokens", out var cacheRead)         &&
               TryGetLong(element, "cacheWriteTokens", out var cacheWrite)
            ? new SessionUsage(uncachedInput, output, cacheRead, cacheWrite)
            : null;
    }

    /// <summary>sessionStats 投影视图：轮次/步数与累计耗时（llm/tool/ttft/decode）。</summary>
    public static SessionStats? ParseStats(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Object           &&
               TryGetLong(element, "turns", out var turns)         &&
               TryGetLong(element, "steps", out var steps)         &&
               TryGetDouble(element, "llmMs", out var llmMs)       &&
               TryGetDouble(element, "toolMs", out var toolMs)     &&
               TryGetDouble(element, "ttftMs", out var ttftMs)     &&
               TryGetLong(element, "ttftSteps", out var ttftSteps) &&
               TryGetDouble(element, "decodeMs", out var decodeMs) &&
               TryGetLong(element, "decodeTokens", out var decodeTokens)
            ? new SessionStats(turns, steps, llmMs, toolMs, ttftMs, ttftSteps, decodeMs, decodeTokens)
            : null;
    }

    /// <summary>
    ///     permissions 投影视图：{ currentValue }。currentValue 是会话当前生效的权限预设
    ///     （可能是 'custom' 等不在 catalog 中的派生态），形状不符返回 null（保持无基线）。
    /// </summary>
    public static string? ParsePermissions(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Object               &&
               element.TryGetProperty("currentValue", out var current) &&
               current.ValueKind == JsonValueKind.String               &&
               current.GetString() is { Length: > 0 } value
            ? value
            : null;
    }

    private static bool TryGetLong(JsonElement element, string name, out long value)
    {
        if (element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Number     &&
            property.TryGetInt64(out value))
            return true;

        value = 0;
        return false;
    }

    private static bool TryGetDouble(JsonElement element, string name, out double value)
    {
        if (element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Number     &&
            property.TryGetDouble(out value))
            return true;

        value = 0;
        return false;
    }
}
