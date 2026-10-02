using DshDesktop.Core.Models;
using DshDesktop.Core.Services;
using DshDesktop.Harness.Exceptions;
using DshDesktop.Harness.Json;
using DshDesktop.Harness.Models.Events;
using DshDesktop.Harness.Models.Requests;
using DshDesktop.Harness.Models.Responses;
using DshDesktop.Harness.Services.Connection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace DshDesktop.Harness.Services.Settings;

/// <summary>
///     通过后端 settings 域读写 dsh 偏好。写后以返回的最新脱敏视图为准落状态；
///     patch/section/ops 写入具备同值重放安全性，载波故障重试不会造成状态分叉。
/// </summary>
public sealed class HarnessSettingsService : ISettingsService
{
    private readonly HarnessConnection _connection;

    public HarnessSettingsService(HarnessConnection connection)
    {
        _connection = connection;
        _connection.SettingsDocumentUpdated += OnConnectionDocumentUpdated;
    }

    public event EventHandler<SettingsDocumentUpdate>? DocumentUpdated;

    public async Task<SettingsDescribeValue> DescribeAsync(CancellationToken cancellationToken = default)
    {
        var value = await _connection.InvokeEmptyAsync("settings/describe",
                                                       HarnessJsonContext.Default.SettingsDescribeValueWire,
                                                       cancellationToken)
                                      .ConfigureAwait(false);
        return ToDescribeValue(value);
    }

    public async Task<SettingsNamespaceView> UpdateAsync(string ns, JsonElement patch,
                                                         long? expectedRevision = null,
                                                         CancellationToken cancellationToken = default)
    {
        var value = await WriteAsync("settings/update",
                                     new SettingsUpdateRequest(ns, patch, expectedRevision),
                                     HarnessJsonContext.Default.SettingsUpdateRequest,
                                     cancellationToken)
                         .ConfigureAwait(false);
        return ToNamespaceView(value);
    }

    public async Task<SettingsNamespaceView> ReplaceAsync(string ns, JsonElement section,
                                                          long? expectedRevision = null,
                                                          CancellationToken cancellationToken = default)
    {
        var value = await WriteAsync("settings/replace",
                                     new SettingsReplaceRequest(ns, section, expectedRevision),
                                     HarnessJsonContext.Default.SettingsReplaceRequest,
                                     cancellationToken)
                         .ConfigureAwait(false);
        return ToNamespaceView(value);
    }

    public async Task<SettingsNamespaceView> MutateAsync(string ns, IReadOnlyList<SettingsMutationOp> ops,
                                                         long? expectedRevision = null,
                                                         CancellationToken cancellationToken = default)
    {
        var wireOps = ops.Select(op => new SettingsOpRequest(op.Op, op.Path, op.Value)).ToArray();
        var value = await WriteAsync("settings/mutate",
                                     new SettingsMutateRequest(ns, wireOps, expectedRevision),
                                     HarnessJsonContext.Default.SettingsMutateRequest,
                                     cancellationToken)
                         .ConfigureAwait(false);
        return ToNamespaceView(value);
    }

    public async Task OpenSettingsDocumentAsync(CancellationToken cancellationToken = default)
    {
        await _connection.InvokeEmptyAsync("settings/openSettingsDocument",
                                           HarnessJsonContext.Default.SettingsOpenDocumentValue,
                                           cancellationToken)
                         .ConfigureAwait(false);
    }

    /// <summary>写路径共用：扁平 args 信封 + 域错误码映射。</summary>
    private async Task<SettingsNamespaceViewWire> WriteAsync<TRequest>(
        string method, TRequest request, JsonTypeInfo<TRequest> requestType, CancellationToken cancellationToken)
        where TRequest : notnull
    {
        try
        {
            return await _connection.InvokeArgsAsync(method, request, requestType,
                                                     HarnessJsonContext.Default.SettingsNamespaceViewWire,
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

    private void OnConnectionDocumentUpdated(object? sender, SettingsDocumentNotice notice)
    {
        DocumentUpdated?.Invoke(this, new SettingsDocumentUpdate(notice.Ns, notice.Revision));
    }

    internal static SettingsDescribeValue ToDescribeValue(SettingsDescribeValueWire wire)
    {
        return new SettingsDescribeValue(wire.Writable, [.. wire.Namespaces.Select(ToNamespaceView)]);
    }

    internal static SettingsNamespaceView ToNamespaceView(SettingsNamespaceViewWire wire)
    {
        return new SettingsNamespaceView(wire.Ns,
                                         wire.AutoGenerate,
                                         wire.Schema,
                                         wire.Value,
                                         wire.Applies,
                                         wire.Revision,
                                         wire.Base,
                                         wire.User,
                                         [.. (wire.Secrets ?? [])
                                             .Select(secret => new SettingsSecretInfo(secret.Path, secret.Set))]);
    }
}
