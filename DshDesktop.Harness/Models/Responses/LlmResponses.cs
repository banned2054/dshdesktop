namespace DshDesktop.Harness.Models.Responses;

/// <summary>llm/listConfigurableProviders 条目 wire 形状。</summary>
public sealed record LlmConfigurableProviderWire(
    string                Provider,
    string                DisplayName,
    string                SettingsNs,
    IReadOnlyList<string> SettingsPath,
    bool?                 Declared = null,
    string?               Error    = null);

/// <summary>llm/discoverModels 返回条目 wire 形状。</summary>
public sealed record LlmDiscoveredModelWire(
    string                 Id,
    string?                Name            = null,
    long?                  ContextWindow   = null,
    long?                  MaxTokens       = null,
    IReadOnlyList<string>? InputModalities = null);
