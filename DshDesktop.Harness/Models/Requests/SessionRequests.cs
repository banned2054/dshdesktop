using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshDesktop.Harness.Models.Requests;

/// <summary>session/list 请求。</summary>
public sealed record SessionListRequest(string? Cursor = null);

/// <summary>session/create 请求；不携带 SessionId 时每次调用都会创建新会话。</summary>
public sealed record SessionCreateRequest(
    string? WorkspaceId = null,
    string? Cwd         = null,
    string? SessionId   = null,
    string? AgentPreset = null);

/// <summary>session/prompt 请求；RequestId 为幂等键，Mode 为 queue 或 steer。</summary>
public sealed record SessionPromptRequest(
    string                        RequestId,
    string                        SessionId,
    string                        Mode,
    IReadOnlyList<PromptTextPart> Content,
    string?                       ClientTimeZone = null);

/// <summary>prompt 的文本分块；线形为 { "type": "text", "text": ... }。</summary>
[JsonConverter(typeof(PromptTextPartConverter))]
public sealed record PromptTextPart(string Text)
{
    public sealed class PromptTextPartConverter : JsonConverter<PromptTextPart>
    {
        public override PromptTextPart? Read(ref Utf8JsonReader    reader, Type typeToConvert,
                                             JsonSerializerOptions options)
        {
            string? text = null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;

                if (reader.TokenType == JsonTokenType.PropertyName &&
                    reader.ValueTextEquals("text"u8)               &&
                    reader.Read())
                    text = reader.GetString();
            }

            return text is null ? null : new PromptTextPart(text);
        }

        public override void Write(Utf8JsonWriter writer, PromptTextPart value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", value.Text);
            writer.WriteEndObject();
        }
    }
}

/// <summary>session/cancel 请求。</summary>
public sealed record SessionCancelRequest(string SessionId);

/// <summary>
///     session/projections 请求：只读查询一个会话的完整投影（observeSession 计算，
///     冷会话走 hydratePrepared，不激活 Agent、不发起模型请求、不持久化投影缓存）。
/// </summary>
public sealed record SessionProjectionsRequest(string SessionId);

/// <summary>会话地址；阶段 2 只使用顶层会话形态 { "kind": "session", "sessionId": ... }。</summary>
[JsonConverter(typeof(SessionAddressConverter))]
public sealed record SessionAddress(string SessionId)
{
    public sealed class SessionAddressConverter : JsonConverter<SessionAddress>
    {
        public override SessionAddress? Read(ref Utf8JsonReader    reader, Type typeToConvert,
                                             JsonSerializerOptions options)
        {
            string? sessionId = null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;

                if (reader.TokenType == JsonTokenType.PropertyName &&
                    reader.ValueTextEquals("sessionId"u8)          &&
                    reader.Read())
                    sessionId = reader.GetString();
            }

            return sessionId is null ? null : new SessionAddress(sessionId);
        }

        public override void Write(Utf8JsonWriter writer, SessionAddress value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "session");
            writer.WriteString("sessionId", value.SessionId);
            writer.WriteEndObject();
        }
    }
}

/// <summary>session/follow 请求；AssistantStream 为 true 时订阅助手流式分块。</summary>
public sealed record SessionFollowRequest(
    SessionAddress Address,
    int?           MaxMessages     = null,
    bool?          AssistantStream = null);

/// <summary>
///     session/page 请求：向后翻一页更早历史。ThroughSeq 是快照游标（包含性上界），
///     BeforeSeq 是当前窗口首条事件 seq（排除性上界），每页按消息对齐裁剪。
/// </summary>
public sealed record SessionPageRequest(
    SessionAddress Address,
    long           ThroughSeq,
    long?          BeforeSeq   = null,
    int?           MaxMessages = null);
