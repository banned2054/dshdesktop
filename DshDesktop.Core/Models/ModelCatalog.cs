namespace DshDesktop.Core.Models;

/// <summary>一次完整的模型选型（provider/model，可选推理档位）。</summary>
public sealed record ModelSelection(string Provider, string Model, string? ReasoningEffort = null);

/// <summary>模型目录：默认选型、各提供方可选模型与装载失败项。</summary>
public sealed record ModelCatalog(
    ModelSelection?                    Default,
    IReadOnlyList<ModelProviderGroup>  Groups,
    IReadOnlyList<ModelCatalogFailure> Failures);

/// <summary>一个提供方及其成功装载的模型清单。</summary>
public sealed record ModelProviderGroup(string Id, string Name, IReadOnlyList<ModelCatalogEntry> Models);

/// <summary>目录中一个可被选中的模型。</summary>
public sealed record ModelCatalogEntry(string Id, string Name, ModelReasoningInfo? Reasoning = null)
{
    /// <summary>解析跨模型切换携带的推理档位；模型无 reasoning 元数据时一律省略（null）。</summary>
    public string? ResolveEffort(string? requested)
    {
        return Reasoning?.Resolve(requested);
    }
}

/// <summary>模型支持的推理档位元数据（session/modelCatalog 下发，adapter 声明的展示顺序）。</summary>
public sealed record ModelReasoningInfo(IReadOnlyList<ReasoningEffortInfo> Efforts, string? DefaultEffort = null)
{
    /// <summary>
    ///     请求档位是否被该模型支持；null（不指定）总是安全——后端会自行取默认档位，
    ///     而对无 reasoning 元数据的模型，任何显式档位都会被拒绝。
    /// </summary>
    public bool Supports(string? requested)
    {
        return requested is null || Efforts.Any(effort => effort.Id == requested);
    }

    /// <summary>
    ///     解析跨模型切换携带的档位：支持则原样保留，不支持回退该模型默认档位
    ///     （可能为 null=不指定）。对齐后端语义：不支持档位没有 clamp/别名，显式携带必被拒。
    /// </summary>
    public string? Resolve(string? requested)
    {
        return Supports(requested) ? requested : DefaultEffort;
    }
}

/// <summary>一个推理档位：wire id（off/low/high/max 等模型自有取值）与展示名。</summary>
public sealed record ReasoningEffortInfo(string Id, string Name, string? Description = null);

/// <summary>一个目录装载失败的提供方（如凭据不可用）；保留展示诊断用。</summary>
public sealed record ModelCatalogFailure(string Id, string Name, string Message);
