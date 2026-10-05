using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace DshDesktop.Harness.Models.Events;

/// <summary>$events 生成流的下行帧。</summary>
public abstract record RemoteEventFrame
{
    /// <summary>代就绪：clientId 为本客户端在本代上的标识。</summary>
    public sealed record Ready(string ClientId) : RemoteEventFrame;

    /// <summary>事件广播；args 为位置参数数组。</summary>
    public sealed record Emit(string Event, IReadOnlyList<JsonElement> Args) : RemoteEventFrame;

    /// <summary>
    ///     需要客户端裁决的请求（审批、用户问题等）；须通过 $events/result 回复。
    ///     AgentId 即会话 id；Request 是投影后的 JSON 载荷（agent/signal 字段已剥离）。
    /// </summary>
    public sealed record Waterfall(string Event, string EventId, string AgentId, JsonElement Request)
        : RemoteEventFrame;

    public sealed record Cancelled(string EventId) : RemoteEventFrame;
}

/// <summary>approval/request 瀑布载荷；toolName 之外字段可缺省。</summary>
public sealed record ApprovalRequestWire(string ToolName, string? CallId = null, string? Reason = null);

/// <summary>api-session/* 事件的归属信息：事件名（如 api-session/activity）与会话 id。</summary>
public sealed record SessionActivityNotice(string Event, string SessionId);

/// <summary>settings/document-updated 事件的通知：命名空间与新 revision。</summary>
public sealed record SettingsDocumentNotice(string Ns, long Revision);

/// <summary>credentials/reference-updated 事件的通知：发生变化的凭据引用名。</summary>
public sealed record CredentialReferenceNotice(string Reference);

public static class RemoteEventJson
{
    /// <summary>审批瀑布的事件名（interaction/user-approval 的 answerer waterfall）。</summary>
    public const string ApprovalRequestEvent = "approval/request";

    /// <summary>权限预设目录变化广播（interaction/permission-presets 的 emit；payload-free）。</summary>
    public const string PermissionCatalogChangedEvent = "permission-presets/catalog-changed";

    /// <summary>设置文档更新广播（settings remote 的 emit；位置参数 ns, revision）。</summary>
    public const string SettingsDocumentUpdatedEvent = "settings/document-updated";

    /// <summary>凭据引用更新广播（credentials remote 的 emit；位置参数 ref）。</summary>
    public const string CredentialReferenceUpdatedEvent = "credentials/reference-updated";

    public static RemoteEventFrame? Parse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object            ||
            !element.TryGetProperty("type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String)
            return null;

        switch (typeElement.GetString())
        {
            case "ready" when element.TryGetProperty("clientId", out var clientIdElement) &&
                              clientIdElement.ValueKind == JsonValueKind.String :
                return new RemoteEventFrame.Ready(clientIdElement.GetString() ?? string.Empty);

            case "emit" when element.TryGetProperty("event", out var eventElement) &&
                             eventElement.ValueKind == JsonValueKind.String :
            {
                var args = new List<JsonElement>();
                if (element.TryGetProperty("args", out var argsElement) && argsElement.ValueKind == JsonValueKind.Array)
                    args.AddRange(argsElement.EnumerateArray().Select(arg => arg.Clone()));

                return new RemoteEventFrame.Emit(eventElement.GetString() ?? string.Empty, args);
            }

            case "waterfall" when element.TryGetProperty("event", out var waterfallEvent)   &&
                                  waterfallEvent.ValueKind == JsonValueKind.String          &&
                                  element.TryGetProperty("eventId", out var eventIdElement) &&
                                  eventIdElement.ValueKind == JsonValueKind.String          &&
                                  element.TryGetProperty("agentId", out var agentIdElement) &&
                                  agentIdElement.ValueKind == JsonValueKind.String          &&
                                  element.TryGetProperty("request", out var requestElement) :
                return new RemoteEventFrame.Waterfall(waterfallEvent.GetString() ?? string.Empty,
                                                      eventIdElement.GetString() ?? string.Empty,
                                                      agentIdElement.GetString() ?? string.Empty,
                                                      requestElement.Clone());

            case "cancel" when element.TryGetProperty("eventId", out var cancelEventId) &&
                               cancelEventId.ValueKind == JsonValueKind.String :
                return new RemoteEventFrame.Cancelled(cancelEventId.GetString() ?? string.Empty);

            default :
                return null;
        }
    }

    /// <summary>解析 approval/request 载荷；toolName 缺失时返回 null（视为不可呈现）。</summary>
    public static ApprovalRequestWire? TryGetApprovalRequest(RemoteEventFrame.Waterfall waterfall)
    {
        if (waterfall.Event             != ApprovalRequestEvent ||
            waterfall.Request.ValueKind != JsonValueKind.Object ||
            !TryGetString(waterfall.Request, "toolName", out var toolName))
            return null;

        var callId = waterfall.Request.TryGetProperty("callId", out var callIdElement) &&
                     callIdElement.ValueKind == JsonValueKind.String
            ? callIdElement.GetString()
            : null;
        var reason = waterfall.Request.TryGetProperty("reason", out var reasonElement) &&
                     reasonElement.ValueKind == JsonValueKind.String
            ? reasonElement.GetString()
            : null;
        return new ApprovalRequestWire(toolName, callId, reason);
    }

    /// <summary>
    ///     从 api-session/* 事件的第一个位置参数提取会话 id：removed/status/activity/error
    ///     的 args[0] 是 sessionId 字符串，added 的 args[0] 是含 sessionId 字段的 summary 对象。
    /// </summary>
    public static bool TryGetSessionId(RemoteEventFrame.Emit emit, [NotNullWhen(true)] out string? sessionId)
    {
        sessionId = null;
        if (emit.Args.Count == 0) return false;

        var first = emit.Args[0];
        if (first.ValueKind == JsonValueKind.String)
        {
            sessionId = first.GetString();
            return sessionId is not null;
        }

        if (first.ValueKind != JsonValueKind.Object) return false;
        var summaryId = first.TryGetProperty("sessionId", out var idElement) &&
                        idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString()
            : null;
        if (summaryId is null) return false;
        sessionId = summaryId;
        return true;
    }

    /// <summary>解析 settings/document-updated 的 (ns, revision) 位置参数。</summary>
    public static bool TryGetSettingsDocumentUpdate(
        RemoteEventFrame.Emit emit, [NotNullWhen(true)] out SettingsDocumentNotice? notice)
    {
        notice = null;
        if (emit.Args.Count        < 2                     ||
            emit.Args[0].ValueKind != JsonValueKind.String ||
            emit.Args[1].ValueKind != JsonValueKind.Number ||
            !emit.Args[1].TryGetInt64(out var revision))
            return false;

        notice = new SettingsDocumentNotice(emit.Args[0].GetString() ?? string.Empty, revision);
        return true;
    }

    /// <summary>解析 credentials/reference-updated 的 (ref) 位置参数。</summary>
    public static bool TryGetCredentialReference(
        RemoteEventFrame.Emit emit, [NotNullWhen(true)] out string? reference)
    {
        reference = emit.Args.Count > 0 && emit.Args[0].ValueKind == JsonValueKind.String
            ? emit.Args[0].GetString()
            : null;
        return reference is not null;
    }

    private static bool TryGetString(JsonElement element, string name, [NotNullWhen(true)] out string? value)
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
