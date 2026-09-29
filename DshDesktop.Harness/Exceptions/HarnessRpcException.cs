using System.Text.Json;

namespace DshDesktop.Harness.Exceptions;

/// <summary>
///     后端 RPC 返回的业务错误；Code 形如 session/not-found。业务错误是终态，不应重试。
///     Details 是错误携带的原始结构（参考实现 error.details，如 session/writer-held 的
///     { sessionId }），按错误码选择性消费，不做通用解释。
/// </summary>
public sealed class HarnessRpcException(string code, string message, JsonElement? details = null)
    : Exception(message)
{
    public string Code { get; } = code;

    public JsonElement? Details { get; } = details;

    /// <summary>从 details 中读取字符串字段；缺失或形状不符返回 null。</summary>
    public string? FindDetailString(string name)
    {
        if (Details is not { ValueKind: JsonValueKind.Object } detailsElement ||
            !detailsElement.TryGetProperty(name, out var element)             ||
            element.ValueKind != JsonValueKind.String)
            return null;

        return element.GetString();
    }
}
