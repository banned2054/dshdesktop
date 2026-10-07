namespace DshDesktop.Core.Models;

/// <summary>会话模型选型；推理档位可省略。</summary>
public sealed record ModelSelection(string Provider, string Model, string? ReasoningEffort = null);

/// <summary>模型目录：默认选型、各提供方可选模型与装载失败项。</summary>
public sealed record ModelCatalog(
    ModelSelection?                    Default,
    IReadOnlyList<ModelProviderGroup>  Groups,
    IReadOnlyList<ModelCatalogFailure> Failures);

/// <summary>模型目录中一个提供方及其模型。</summary>
public sealed record ModelProviderGroup(string Id, string Name, IReadOnlyList<ModelCatalogEntry> Models);

/// <summary>模型目录条目及可选推理元数据。</summary>
public sealed record ModelCatalogEntry(string Id, string Name, ModelReasoningInfo? Reasoning = null)
{
    /// <summary>解析跨模型切换携带的推理档位；模型无 reasoning 元数据时一律省略（null）。</summary>
    public string? ResolveEffort(string? requested)
    {
        return Reasoning?.Resolve(requested);
    }
}

/// <summary>后端下发的推理档位，按 adapter 声明顺序展示。</summary>
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

/// <summary>模型推理档位的 wire id、展示名及可选说明。</summary>
public sealed record ReasoningEffortInfo(string Id, string Name, string? Description = null);

/// <summary>提供方目录装载失败信息，用于展示诊断。</summary>
public sealed record ModelCatalogFailure(string Id, string Name, string Message);
