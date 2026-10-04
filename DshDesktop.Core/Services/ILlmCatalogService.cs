using DshDesktop.Core.Models;

namespace DshDesktop.Core.Services;

/// <summary>
///     llm 提供方目录域：可配置提供方目录与模型发现（对齐上游 llm/listConfigurableProviders、
///     llm/discoverModels）。llm/listProviders 未消费——活跃路由由设置文档 providers 投影得出。
/// </summary>
public interface ILlmCatalogService
{
    /// <summary>可配置提供方目录（含休眠项，声明顺序）。</summary>
    Task<IReadOnlyList<LlmConfigurableProvider>> GetConfigurableProvidersAsync(
        CancellationToken cancellationToken = default);

    /// <summary>按探测参数询问可用模型；provider 与 baseURL 均缺省或探测不可用时抛出异常。</summary>
    Task<IReadOnlyList<LlmDiscoveredModel>> DiscoverModelsAsync(
        string settingsNs, LlmDiscoveryRequest request, CancellationToken cancellationToken = default);
}
