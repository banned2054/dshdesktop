using System.Text.Json;

namespace DshDesktop.Harness.Models.Responses;

/// <summary>session/list 返回值。</summary>
public sealed record SessionListValue(IReadOnlyList<SessionSummaryWire> Items);

/// <summary>session/create 返回值。</summary>
public sealed record SessionCreateValue(string SessionId, string? AgentPreset = null);

/// <summary>session/fork 返回值：服务端新铸的子会话 id。</summary>
public sealed record SessionForkValue(string SessionId);

/// <summary>session/rename 返回值：规范化后的标题与提交它的持久事件位置。</summary>
public sealed record SessionRenameValue(string Title, long Seq);

/// <summary>session/prompt 与 session/cancel 的接受回执。</summary>
public sealed record SessionAcceptedValue(bool Accepted);

/// <summary>会话概要（线上形态）。updatedAt 为 Unix 毫秒。</summary>
public sealed record SessionSummaryWire(
    string                      SessionId,
    long                        UpdatedAt,
    bool                        Running,
    bool                        Blank,
    string?                     ParentSessionId = null,
    string?                     Origin          = null,
    string?                     Cwd             = null,
    SessionProjectionHintsWire? Projections     = null);

/// <summary>
///     投影基线；values 按投影键散列，title 投影键为 "title"，sessionListMetadata 为
///     空白判定权威。Kind 是水印所属序列空间：sequenced 为活跃注册表，cached 为
///     持久缓存冷行；两类水印不可按同一 seq 空间比较。
/// </summary>
public sealed record SessionProjectionHintsWire(
    string                           Kind,
    long                             AsOfSeq,
    Dictionary<string, JsonElement>? Values);

/// <summary>
///     session/page 返回值。Records 是 {type:'event', event:{...}} 条目数组（整体以 JsonElement 承载），
///     event 形态与 follow 快照的 records 完全一致，经 FollowFrameJson 统一解析。
/// </summary>
public sealed record SessionPageValue(JsonElement Records, bool HasMore);

/// <summary>
///     session/projections 返回值：{ asOfSeq, values }。values 只按需解析本端消费的键，
///     不搬运全部后端投影模型。会话不存在时 result 本身为 null（不产生本类型实例）。
/// </summary>
public sealed record SessionProjectionsValue(long AsOfSeq, Dictionary<string, JsonElement>? Values);

/// <summary>sessionListMetadata 投影视图；blank 是空白判定的唯一权威布尔值。</summary>
public sealed record SessionListMetadataWire(bool Blank, long? LastPromptAt);

/// <summary>
///     投影 values 中 sessionListMetadata 的解析（session/projections 与列表行共用）。
///     只有解析出真实布尔 blank 的元数据才视为有效；键缺失或形状错误一律返回 null，
///     调用方必须保持未知状态，不得据此判定空白或有内容。
/// </summary>
public static class SessionProjectionsJson
{
    /// <summary>列表元数据在投影 values 中的键名。</summary>
    public const string ListMetadataKey = "sessionListMetadata";

    /// <summary>从投影 values 字典解析 sessionListMetadata；键缺失或形状错误返回 null。</summary>
    public static SessionListMetadataWire? TryParseListMetadata(Dictionary<string, JsonElement>? values)
    {
        return values is not null &&
               values.TryGetValue(ListMetadataKey, out var element)
            ? ParseListMetadata(element)
            : null;
    }

    /// <summary>解析一个 sessionListMetadata JSON 元素；blank 必须是真实布尔值才有效。</summary>
    public static SessionListMetadataWire? ParseListMetadata(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object              ||
            !element.TryGetProperty("blank", out var blankElement) ||
            blankElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return null;

        long? lastPromptAt = null;
        if (element.TryGetProperty("lastPromptAt", out var lastPromptElement) &&
            lastPromptElement.ValueKind == JsonValueKind.Number               &&
            lastPromptElement.TryGetInt64(out var parsed))
            lastPromptAt = parsed;

        return new SessionListMetadataWire(blankElement.ValueKind == JsonValueKind.True, lastPromptAt);
    }
}
