using System.Text.Json.Serialization;

namespace DshDesktop.Harness.Models.Requests;

/// <summary>llm/discoverModels 请求：多参方法按属性名扁平展开为 args（settingsNs + request）。</summary>
public sealed record LlmDiscoverModelsRequest(string SettingsNs, LlmDiscoveryProbeRequest Request);

/// <summary>模型探测参数；wire 字段 baseURL 为小写驼峰特例，需显式命名。</summary>
public sealed record LlmDiscoveryProbeRequest(
    [property: JsonPropertyName("provider")] string? Provider,
    [property: JsonPropertyName("baseURL")] string?  BaseUrl,
    [property: JsonPropertyName("api")] string?      Api,
    [property: JsonPropertyName("apiKey")] string?   ApiKey);
