using System.Text.Json.Serialization.Metadata;
using DshDesktop.Core.Models;
using DshDesktop.Core.Services;
using DshDesktop.Harness.Exceptions;
using DshDesktop.Harness.Json;
using DshDesktop.Harness.Models.Requests;
using DshDesktop.Harness.Models.Responses;
using DshDesktop.Harness.Services.Connection;

namespace DshDesktop.Harness.Services.Settings;

/// <summary>
///     通过后端 credentials 域查询与写入凭据引用。客户端不解析凭据文件，也不接触密钥值；
///     判定始终以当前 Host 的回答为准，credential/rejected 的 message 原样上抛。
/// </summary>
public sealed class HarnessCredentialService : ICredentialsService
{
    private readonly HarnessConnection _connection;

    public HarnessCredentialService(HarnessConnection connection)
    {
        _connection = connection;
        _connection.CredentialReferenceUpdated += OnCredentialReferenceUpdated;
    }

    public event EventHandler? ReferenceUpdated;

    public async Task<IReadOnlyDictionary<string, CredentialStatus>> DescribeAsync(
        IReadOnlyList<string> references, CancellationToken cancellationToken = default)
    {
        var entries = await DescribeCoreAsync([.. references], cancellationToken).ConfigureAwait(false);
        return entries.ToDictionary(pair => pair.Key,
                                    pair => new CredentialStatus(pair.Value.Configured,
                                                                 pair.Value.Source,
                                                                 pair.Value.Writable));
    }

    public async Task SetAsync(string reference, string value, CancellationToken cancellationToken = default)
    {
        await InvokeWriteAsync("credentials/set",
                               new CredentialsSetRequest(reference, value),
                               HarnessJsonContext.Default.CredentialsSetRequest,
                               cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task UnsetAsync(string reference, CancellationToken cancellationToken = default)
    {
        await InvokeWriteAsync("credentials/unset",
                               new CredentialsUnsetRequest(reference),
                               HarnessJsonContext.Default.CredentialsUnsetRequest,
                               cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<Dictionary<string, CredentialInfoWire>> DescribeCoreAsync(
        string[] references, CancellationToken cancellationToken)
    {
        // describe 的形参 refs 本身就是数组（不是请求对象），args 形如 { "refs": ["GLM_API_KEY"] }。
        return await _connection.InvokeAsync("credentials/describe", references,
                                             HarnessJsonContext.Default.StringArray,
                                             HarnessJsonContext.Default.DictionaryStringCredentialInfoWire,
                                             cancellationToken, "refs")
                                .ConfigureAwait(false);
    }

    private async Task InvokeWriteAsync<TRequest>(string                    method,
                                                  TRequest                  request,
                                                  JsonTypeInfo<TRequest>    requestType,
                                                  CancellationToken         cancellationToken)
        where TRequest : notnull
    {
        try
        {
            // 写操作返回 void（响应无 value），以 JsonElement 作为占位值类型。
            await _connection.InvokeArgsAsync(method, request, requestType,
                                              HarnessJsonContext.Default.JsonElement,
                                              cancellationToken)
                             .ConfigureAwait(false);
        }
        catch (HarnessRpcException exception)
        {
            if (SettingsRpcErrors.TryMap(exception, out var mapped))
                throw mapped;
            throw;
        }
    }

    private void OnCredentialReferenceUpdated(object? sender, string reference)
    {
        ReferenceUpdated?.Invoke(this, EventArgs.Empty);
    }
}
