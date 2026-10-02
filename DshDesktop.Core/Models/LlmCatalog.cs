namespace DshDesktop.Core.Models;

/// <summary>llm/listConfigurableProviders 条目：可配置提供方目录（含休眠项）。
/// settingsPath 为空表示整段路由（如 DeepSeek 官方编辑器），否则指向
/// llm-pi-ai 值内 providers 下的单个路由对象。</summary>
public sealed record LlmConfigurableProvider(
    string                Provider,
    string                DisplayName,
    string                SettingsNs,
    IReadOnlyList<string> SettingsPath,
    bool?                 Declared = null,
    string?               Error    = null);

/// <summary>llm/discoverModels 探测参数：字段缺省即省略；apiKey 仅本次询问使用，host 不存储。</summary>
public sealed record LlmDiscoveryRequest(
    string? Provider = null,
    string? BaseUrl  = null,
    string? Api      = null,
    string? ApiKey   = null);

/// <summary>llm/discoverModels 返回条目（host 已按 id 去重，保留端点顺序）。</summary>
public sealed record LlmDiscoveredModel(
    string                 Id,
    string?                Name            = null,
    long?                  ContextWindow   = null,
    long?                  MaxTokens       = null,
    IReadOnlyList<string>? InputModalities = null);
