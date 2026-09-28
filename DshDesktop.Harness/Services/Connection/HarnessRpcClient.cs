using DshDesktop.Harness.Exceptions;
using DshDesktop.Harness.Utils;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace DshDesktop.Harness.Services.Connection;

/// <summary>
///     一元 RPC 客户端：POST /api/&lt;method&gt;，请求与响应均为 JSON 信封，
///     认证依赖 HttpClient 共享的 CookieContainer。
/// </summary>
public sealed class HarnessRpcClient(HttpClient httpClient)
{
    public async Task<TValue> InvokeAsync<TValue, TRequest>(
        string                 method,
        TRequest               request,
        JsonTypeInfo<TRequest> requestType,
        JsonTypeInfo<TValue>   valueType,
        CancellationToken      cancellationToken,
        string                 argName = "request")
    {
        var rpcId = WireIds.NewRpcId();
        var body  = RpcEnvelope.BuildRequest(rpcId, method, request, requestType, argName);
        var uri   = new Uri($"/api/{method}", UriKind.Relative);

        using var content  = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await httpClient.PostAsync(uri, content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HarnessConnectionException($"RPC 传输失败（{method}）：HTTP {(int)response.StatusCode}。");

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        RpcEnvelope.RpcResponse result;
        try
        {
            result = RpcEnvelope.ParseResponse(json);
        }
        catch (JsonException exception)
        {
            throw new HarnessConnectionException($"RPC 响应无法解析（{method}）。", exception);
        }

        if (!string.Equals(result.RpcId, rpcId, StringComparison.Ordinal))
            throw new HarnessConnectionException($"RPC 回显标识不匹配（{method}）。");

        if (!result.Ok)
            throw new HarnessRpcException(result.ErrorCode ?? "gateway/unknown",
                                          result.ErrorMessage is null
                                              ? $"调用 {method} 失败。"
                                              : $"{method} 失败：{result.ErrorMessage}",
                                          result.ErrorDetails);

        if (result.Value is null) return default!;

        return result.Value.Value.Deserialize(valueType) ?? default!;
    }

    /// <summary>调用无参方法（payload 为 { "args": {} }），如 session/modelCatalog。</summary>
    public async Task<TValue> InvokeEmptyAsync<TValue>(
        string               method,
        JsonTypeInfo<TValue> valueType,
        CancellationToken    cancellationToken)
    {
        var rpcId = WireIds.NewRpcId();
        var body  = RpcEnvelope.BuildEmptyRequest(rpcId, method);
        var uri   = new Uri($"/api/{method}", UriKind.Relative);

        using var content  = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await httpClient.PostAsync(uri, content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HarnessConnectionException($"RPC 传输失败（{method}）：HTTP {(int)response.StatusCode}。");

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        RpcEnvelope.RpcResponse result;
        try
        {
            result = RpcEnvelope.ParseResponse(json);
        }
        catch (JsonException exception)
        {
            throw new HarnessConnectionException($"RPC 响应无法解析（{method}）。", exception);
        }

        if (!string.Equals(result.RpcId, rpcId, StringComparison.Ordinal))
            throw new HarnessConnectionException($"RPC 回显标识不匹配（{method}）。");

        if (!result.Ok)
            throw new HarnessRpcException(result.ErrorCode ?? "gateway/unknown",
                                          result.ErrorMessage is null
                                              ? $"调用 {method} 失败。"
                                              : $"{method} 失败：{result.ErrorMessage}",
                                          result.ErrorDetails);

        if (result.Value is null) return default!;

        return result.Value.Value.Deserialize(valueType) ?? default!;
    }
}
