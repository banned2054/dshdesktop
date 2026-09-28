using DshDesktop.Harness.Json;
using DshDesktop.Harness.Models.Rpc;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace DshDesktop.Harness.Services.Connection;

/// <summary>RPC 信封的构建与解析。</summary>
public static class RpcEnvelope
{
    /// <summary>
    ///     构建 client-request 请求体；payload 形如 { "args": { "request": ... } }。
    ///     args 的键是宿主方法的形参名：绝大多数 session 方法为 request，session/list 为 _request。
    /// </summary>
    public static string BuildRequest<TRequest>(
        string rpcId, string method, TRequest request, JsonTypeInfo<TRequest> requestType, string argName = "request")
    {
        var requestElement = JsonSerializer.SerializeToElement(request, requestType);
        var payload = JsonSerializer.SerializeToElement(
                                                        new StreamPayloadWire(new Dictionary<string, JsonElement>
                                                        {
                                                            [argName] = requestElement
                                                        }),
                                                        HarnessJsonContext.Default.StreamPayloadWire);
        var envelope = new RpcRequestEnvelope("client-request", rpcId, method, payload);
        return JsonSerializer.Serialize(envelope, HarnessJsonContext.Default.RpcRequestEnvelope);
    }

    /// <summary>构建无参方法的请求体（payload 为 { "args": {} }）。</summary>
    public static string BuildEmptyRequest(string rpcId, string method)
    {
        var payload =
            JsonSerializer.SerializeToElement(new StreamPayloadWire([]), HarnessJsonContext.Default.StreamPayloadWire);
        var envelope = new RpcRequestEnvelope("client-request", rpcId, method, payload);
        return JsonSerializer.Serialize(envelope, HarnessJsonContext.Default.RpcRequestEnvelope);
    }

    /// <summary>解析 server-response；ok 为 false 时携带 code/message。</summary>
    public static RpcResponse ParseResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var       root     = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("响应不是 JSON 对象。");

        var rpcId = root.TryGetProperty("rpcId", out var rpcIdElement)
                 && rpcIdElement.ValueKind == JsonValueKind.String
            ? rpcIdElement.GetString() ?? string.Empty
            : string.Empty;
        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            throw new JsonException("响应缺少 result 对象。");

        var ok = result.TryGetProperty("ok", out var okElement) && okElement.ValueKind == JsonValueKind.True;
        if (ok)
        {
            var value = result.TryGetProperty("value", out var valueElement)
                     && valueElement.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
                ? (JsonElement?)valueElement.Clone()
                : null;
            return new RpcResponse(rpcId, true, value, null, null);
        }

        string? code    = null;
        string? message = null;
        JsonElement? details = null;
        if (result.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            code = error.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.String
                ? codeElement.GetString()
                : null;
            message = error.TryGetProperty("message", out var messageElement)
                   && messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString()
                : null;
            // 参考实现错误携带 details（如 session/writer-held 的 sessionId、
            // workspace-attach-failed 的归属信息）；保留原始结构供按错误码消费。
            if (error.TryGetProperty("details", out var detailsElement)
             && detailsElement.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                details = detailsElement.Clone();
        }

        return new RpcResponse(rpcId, false, null, code, message, details);
    }

    public readonly record struct RpcResponse(
        string       RpcId,
        bool         Ok,
        JsonElement? Value,
        string?      ErrorCode,
        string?      ErrorMessage,
        JsonElement? ErrorDetails = null);
}
