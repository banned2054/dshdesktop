using DshDesktop.Harness.Models.Responses;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace DshDesktop.Harness.Models.Events;

/// <summary>会话事件日志的一条事件（线上形态）。data 为开放结构，按 type 解释。</summary>
public sealed record SessionWireEvent(string Type, long Seq, long Time, JsonElement Data);

/// <summary>会话头部（线上形态）。</summary>
public sealed record SessionWireHeader(
    int     Version,
    string  Id,
    long    CreatedAt,
    string? Cwd,
    string? ParentSession,
    bool    IsSeeded,
    string? Origin,
    string? AgentPreset);

/// <summary>一条消息（线上形态）；content 为分块数组，文本在 type 为 text 的分块中。</summary>
public sealed record WireMessage(string Id, string Role, IReadOnlyList<WireContentBlock> Blocks, JsonElement Source);

public sealed record WireContentBlock(string Type, string? Text, string? Name, JsonElement Raw);

/// <summary>tool/call 事件数据；Arguments 是模型产出的原始参数 JSON 文本。</summary>
public sealed record ToolCallWire(string CallId, string Name, string? Arguments);

/// <summary>
///     tool/result 事件数据。ContentText 是结果内容块的文本拼接；
///     Error* 来自事件 error 字段（工具内部失败身份与用户可读原因，不进入模型内容）。
/// </summary>
public sealed record ToolResultWire(
    string? CallId,
    bool    IsError,
    string? ContentText,
    string? ErrorName,
    string? ErrorCode,
    string? ErrorReason);

/// <summary>session 历史记录的事件条目解析。</summary>
public static class WireEventJson
{
    public static SessionWireEvent? ParseEvent(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object    ||
            !TryGetString(element, "type", out var type) ||
            !TryGetNumber(element, "seq", out var seq)   ||
            !TryGetNumber(element, "time", out var time))
            return null;

        var data = element.TryGetProperty("data", out var dataElement) &&
                   dataElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            ? dataElement.Clone()
            : default;
        return new SessionWireEvent(type, seq, time, data);
    }

    /// <summary>从事件 data 提取消息；仅处理 user/message 与 assistant/message。</summary>
    public static WireMessage? TryGetMessage(SessionWireEvent wireEvent)
    {
        return wireEvent.Type switch
        {
            "user/message" => ParseMessage(wireEvent.Data),
            "assistant/message" => wireEvent.Data.ValueKind == JsonValueKind.Object &&
                                   wireEvent.Data.TryGetProperty("message", out var message)
                ? ParseMessage(message)
                : null,
            _ => null
        };
    }

    /// <summary>assistant/message 的中断标记。</summary>
    public static bool IsInterrupted(SessionWireEvent wireEvent)
    {
        return wireEvent is { Type: "assistant/message", Data.ValueKind: JsonValueKind.Object } &&
               wireEvent.Data.TryGetProperty("interrupted", out var interrupted)                &&
               interrupted.ValueKind == JsonValueKind.True;
    }

    /// <summary>
    ///     事件的轮次与步序号（assistant/message、tool/call、tool/result 的 data.turn/step）。
    ///     参考客户端按 turn 折叠过程条目；缺失时返回假。
    /// </summary>
    public static bool TryGetTurnStep(SessionWireEvent wireEvent, out long turn, out long step)
    {
        turn = 0;
        step = 0;
        return wireEvent.Data.ValueKind == JsonValueKind.Object &&
               TryGetNumber(wireEvent.Data, "turn", out turn)   &&
               TryGetNumber(wireEvent.Data, "step", out step);
    }

    /// <summary>turn/end 边界事件的轮次序号。</summary>
    public static bool TryGetTurnEnd(SessionWireEvent wireEvent, out long turn)
    {
        turn = 0;
        return wireEvent is { Type: "turn/end", Data.ValueKind: JsonValueKind.Object } &&
               TryGetNumber(wireEvent.Data, "turn", out turn);
    }

    /// <summary>
    ///     turn/end 的结束原因（data.reason.kind，如 completed/aborted/interrupted）；
    ///     缺失或非字符串返回 null。快照尾部的 interrupted 边界是 Host 为开放轮合成的，
    ///     不是持久事件，消费方据此区分。
    /// </summary>
    public static string? TurnEndReason(SessionWireEvent wireEvent)
    {
        return wireEvent is { Type: "turn/end", Data.ValueKind: JsonValueKind.Object } &&
               wireEvent.Data.TryGetProperty("reason", out var reason)                 &&
               reason.ValueKind == JsonValueKind.Object                                &&
               reason.TryGetProperty("kind", out var kind)                             &&
               kind.ValueKind == JsonValueKind.String
            ? kind.GetString()
            : null;
    }

    /// <summary>消息内容块中是否含 tool-call 块（这类消息是轮次提交，不是可见回复）。</summary>
    public static bool HasToolCallBlocks(WireMessage message)
    {
        return message.Blocks.Any(block => block.Type == "tool-call");
    }

    /// <summary>
    ///     消息的 source.kind（谁产出的）。真实用户输入为 'user'；后端注入的上下文
    ///     （runtime-context 快照、技能目录/内容等）是 user 角色但携带其他 kind。
    /// </summary>
    public static string? GetUserSourceKind(WireMessage message)
    {
        return message.Source.ValueKind == JsonValueKind.Object    &&
               message.Source.TryGetProperty("kind", out var kind) &&
               kind.ValueKind == JsonValueKind.String
            ? kind.GetString()
            : null;
    }

    /// <summary>session/title 事件的新标题。</summary>
    public static bool TryGetTitle(SessionWireEvent wireEvent, out string? title)
    {
        title = null;
        if (wireEvent.Type           != "session/title"      ||
            wireEvent.Data.ValueKind != JsonValueKind.Object ||
            !wireEvent.Data.TryGetProperty("title", out var titleElement))
            return false;

        title = titleElement.ValueKind == JsonValueKind.String ? titleElement.GetString() : null;
        return true;
    }

    /// <summary>model/selection 事件：会话的持久化模型选型，对后续 prompt 生效。</summary>
    public static SessionModelSelectionWire? TryGetModelSelection(SessionWireEvent wireEvent)
    {
        if (wireEvent.Type != "model/selection" || wireEvent.Data.ValueKind != JsonValueKind.Object) return null;

        if (!TryGetString(wireEvent.Data, "provider", out var provider) ||
            !TryGetString(wireEvent.Data, "model", out var model))
            return null;

        var reasoningEffort = wireEvent.Data.TryGetProperty("reasoningEffort", out var effort) &&
                              effort.ValueKind == JsonValueKind.String
            ? effort.GetString()
            : null;
        return new SessionModelSelectionWire(provider, model, reasoningEffort);
    }

    /// <summary>tool/call 事件：模型请求的一次工具调用。</summary>
    public static ToolCallWire? TryGetToolCall(SessionWireEvent wireEvent)
    {
        if (wireEvent.Type != "tool/call" || wireEvent.Data.ValueKind != JsonValueKind.Object) return null;

        if (!TryGetString(wireEvent.Data, "callId", out var callId) ||
            !TryGetString(wireEvent.Data, "name", out var name))
            return null;

        var arguments = wireEvent.Data.TryGetProperty("arguments", out var argumentsElement) &&
                        argumentsElement.ValueKind == JsonValueKind.String
            ? argumentsElement.GetString()
            : null;
        return new ToolCallWire(callId, name, arguments);
    }

    /// <summary>
    ///     tool/result 事件：工具调用的结果。v4（含迁移后的 v3 历史）是 first-class tool
    ///     消息：toolCallId/isError 在消息顶层，文本块直接位于 content；v3 的 tool-result
    ///     wrapper 块形态保留兜底。callId 优先取 message.source，回退顶层 toolCallId 或内容块。
    /// </summary>
    public static ToolResultWire? TryGetToolResult(SessionWireEvent wireEvent)
    {
        if (wireEvent.Type           != "tool/result"                  ||
            wireEvent.Data.ValueKind != JsonValueKind.Object           ||
            !wireEvent.Data.TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Object)
            return null;

        var callId = message.TryGetProperty("source", out var source) &&
                     source.ValueKind == JsonValueKind.Object         &&
                     TryGetString(source, "callId", out var sourceCallId)
            ? sourceCallId
            : null;
        if (callId is null && TryGetString(message, "toolCallId", out var messageCallId)) callId = messageCallId;

        string? contentText = null;
        var     isError     = false;
        var     textParts   = new List<string>();
        if (message.TryGetProperty("content", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
            foreach (var block in blocks.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object ||
                    !TryGetString(block, "type", out var blockType))
                    continue;

                if (blockType == "tool-result")
                {
                    // v3 wrapper 块：结果与文本嵌在内层 content。
                    if (callId is null                                            &&
                        block.TryGetProperty("toolCallId", out var wrapperCallId) &&
                        wrapperCallId.ValueKind == JsonValueKind.String)
                        callId = wrapperCallId.GetString();

                    if (block.TryGetProperty("isError", out var isErrorElement) &&
                        isErrorElement.ValueKind == JsonValueKind.True)
                        isError = true;

                    if (block.TryGetProperty("content", out var inner) && inner.ValueKind == JsonValueKind.Array)
                    {
                        var parts = inner.EnumerateArray()
                                         .Where(item => item.ValueKind == JsonValueKind.Object           &&
                                                        item.TryGetProperty("type", out var typeElement) &&
                                                        typeElement.ValueKind   == JsonValueKind.String  &&
                                                        typeElement.GetString() == "text"                &&
                                                        item.TryGetProperty("text", out var textElement) &&
                                                        textElement.ValueKind == JsonValueKind.String)
                                         .Select(item => item.GetProperty("text").GetString());
                        contentText = string.Join("\n", parts);
                    }

                    break; // 线形约定 content 为单个 tool-result 块。
                }

                if (blockType == "text"                        &&
                    block.TryGetProperty("text", out var text) &&
                    text.ValueKind == JsonValueKind.String)
                    textParts.Add(text.GetString()!);
            }

        // v4 直接文本块：无 wrapper 时拼接全部 text 块。
        if (contentText is null && textParts.Count > 0) contentText = string.Join("\n", textParts);

        // v4 的失败标记在消息顶层。
        if (!isError                                                  &&
            message.TryGetProperty("isError", out var messageIsError) &&
            messageIsError.ValueKind == JsonValueKind.True)
            isError = true;

        if (callId is null) return null;

        string? errorName   = null;
        string? errorCode   = null;
        string? errorReason = null;
        if (!wireEvent.Data.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
            return new ToolResultWire(callId, isError, contentText, errorName, errorCode, errorReason);
        TryGetString(error, "name", out errorName);
        TryGetString(error, "code", out errorCode);
        TryGetString(error, "reason", out errorReason);

        return new ToolResultWire(callId, isError, contentText, errorName, errorCode, errorReason);
    }

    /// <summary>拼接消息的全部文本分块；非文本分块阶段 2 不展示。</summary>
    public static string ExtractText(WireMessage message)
    {
        return string.Join("\n", message.Blocks
                                        .Where(block => block is { Type: "text", Text: not null })
                                        .Select(block => block.Text));
    }

    /// <summary>拼接消息的全部思考（reasoning）分块；思考不算回复正文，单独展示。</summary>
    public static string ExtractReasoning(WireMessage message)
    {
        return string.Join("\n", message.Blocks
                                        .Where(block => block is { Type: "reasoning", Text: not null })
                                        .Select(block => block.Text));
    }

    private static WireMessage? ParseMessage(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !TryGetString(element, "id", out var id)  ||
            !TryGetString(element, "role", out var role))
            return null;

        var blocks = new List<WireContentBlock>();
        if (element.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            foreach (var block in content.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object ||
                    !TryGetString(block, "type", out var blockType))
                    continue;

                var text = block.TryGetProperty("text", out var textElement) &&
                           textElement.ValueKind == JsonValueKind.String
                    ? textElement.GetString()
                    : null;
                var name = block.TryGetProperty("name", out var nameElement) &&
                           nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString()
                    : null;
                blocks.Add(new WireContentBlock(blockType, text, name, block.Clone()));
            }

        var source = element.TryGetProperty("source", out var sourceElement) &&
                     sourceElement.ValueKind == JsonValueKind.Object
            ? sourceElement.Clone()
            : default;
        return new WireMessage(id, role, blocks, source);
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
