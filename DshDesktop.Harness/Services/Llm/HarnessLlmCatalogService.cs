using DshDesktop.Core.Models;
using DshDesktop.Core.Services;
using DshDesktop.Harness.Json;
using DshDesktop.Harness.Models.Requests;
using DshDesktop.Harness.Models.Responses;
using DshDesktop.Harness.Services.Connection;

namespace DshDesktop.Harness.Services.Llm;

/// <summary>
///     llm 目录域服务：listConfigurableProviders（无参）与 discoverModels（settingsNs + 探测参数）。
///     发现失败（无发现注册、参数缺失、端点拒绝）由后端以 RPC 错误返回，原样抛给消费方。
/// </summary>
public sealed class HarnessLlmCatalogService(HarnessConnection connection) : ILlmCatalogService
{
    private readonly HarnessConnection _connection = connection;

    public async Task<IReadOnlyList<LlmConfigurableProvider>> GetConfigurableProvidersAsync(
        CancellationToken cancellationToken = default)
    {
        var value = await _connection.InvokeEmptyAsync("llm/listConfigurableProviders",
                                                       HarnessJsonContext.Default.LlmConfigurableProviderWireArray,
                                                       cancellationToken)
                                     .ConfigureAwait(false);
        return [.. value.Select(MapProvider)];
    }

    public async Task<IReadOnlyList<LlmDiscoveredModel>> DiscoverModelsAsync(
        string settingsNs, LlmDiscoveryRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settingsNs))
            throw new ArgumentException("设置命名空间不能为空。", nameof(settingsNs));

        var value = await _connection.InvokeArgsAsync("llm/discoverModels",
                                                      new LlmDiscoverModelsRequest(settingsNs,
                                                               new LlmDiscoveryProbeRequest(request
                                                                           .Provider, request.BaseUrl,
                                                                        request.Api, request.ApiKey)),
                                                      HarnessJsonContext.Default.LlmDiscoverModelsRequest,
                                                      HarnessJsonContext.Default.LlmDiscoveredModelWireArray,
                                                      cancellationToken).ConfigureAwait(false);
        return
        [
            .. value.Select(model => new LlmDiscoveredModel(model.Id, model.Name, model.ContextWindow,
                                                            model.MaxTokens, model.InputModalities))
        ];
    }

    private static LlmConfigurableProvider MapProvider(LlmConfigurableProviderWire wire)
    {
        return new LlmConfigurableProvider(wire.Provider, wire.DisplayName, wire.SettingsNs,
                                           wire.SettingsPath, wire.Declared, wire.Error);
    }
}
