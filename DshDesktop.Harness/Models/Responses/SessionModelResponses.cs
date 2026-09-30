namespace DshDesktop.Harness.Models.Responses;

/// <summary>session/selectModel 返回值。</summary>
public sealed record SessionSelectModelValue(SessionModelSelectionWire Selected);

/// <summary>一次模型选择。</summary>
public sealed record SessionModelSelectionWire(string Provider, string Model, string? ReasoningEffort = null);

/// <summary>session/modelCatalog 返回值：默认选型、可路由提供方、成组模型与装载失败项。</summary>
public sealed record SessionModelCatalogValue(
    SessionModelSelectionWire?              Default,
    IReadOnlyList<string>?                  RoutableProviders,
    IReadOnlyList<ModelProviderGroupWire>?  Groups   = null,
    IReadOnlyList<ModelCatalogFailureWire>? Failures = null);

/// <summary>目录中一个成功装载的提供方组。</summary>
public sealed record ModelProviderGroupWire(
    string                                Id,
    string                                Name,
    IReadOnlyList<ModelCatalogModelWire>? Models = null);

/// <summary>组内一个模型，含其推理档位元数据（后端按模型能力校验档位，显式携带不支持值会被拒）。</summary>
public sealed record ModelCatalogModelWire(
    string              Id,
    string              Name,
    string?             Description = null,
    ModelReasoningWire? Reasoning   = null);

/// <summary>一个模型的推理元数据：支持的档位列表（adapter 声明顺序）与默认档位。</summary>
public sealed record ModelReasoningWire(IReadOnlyList<ModelReasoningEffortWire> Efforts, string? DefaultEffort = null);

/// <summary>一个推理档位：wire id（off/low/high/max 等模型自有取值）与展示名。</summary>
public sealed record ModelReasoningEffortWire(string Id, string Name, string? Description = null);

/// <summary>目录装载失败的一个提供方。</summary>
public sealed record ModelCatalogFailureWire(
    string  Id,
    string? Name    = null,
    string? Message = null);
