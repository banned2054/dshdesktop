using DshDesktop.Core.Models;
using DshDesktop.Core.Services;

namespace DshDesktop.Infrastructure.Services;

/// <summary>模拟 llm 目录域：目录种子对齐上游 pi-ai 内置厂商全集（显示名用 id 占位），
/// 另含 DeepSeek 两条第一方路由与一条 declared 自定义路由（glm）。模型发现按
/// 已知目录厂商回 canned 目录、带 baseURL 的未知厂商回占位模型的语义模拟。</summary>
public sealed class SimulatedLlmCatalogService : ILlmCatalogService
{
    /// <summary>pi-ai 内置厂商 id 全集（getBuiltinProviders 对齐，声明顺序近似字母序）。</summary>
    private static readonly string[] BuiltinProviderIds =
    [
        "amazon-bedrock", "ant-ling", "anthropic", "azure-openai-responses", "baseten", "cerebras",
        "cloudflare-ai-gateway", "cloudflare-workers-ai", "deepseek", "faux", "fireworks",
        "github-copilot", "google", "google-vertex", "groq", "huggingface", "kimi-coding", "meta",
        "minimax", "minimax-cn", "mistral", "moonshotai", "moonshotai-cn", "nvidia", "openai",
        "openai-codex", "opencode", "opencode-go", "openrouter", "qwen-token-plan",
        "qwen-token-plan-cn", "qwen-token-plan-individual", "radius", "together",
        "vercel-ai-gateway", "xai", "xiaomi", "xiaomi-token-plan-ams", "xiaomi-token-plan-cn",
        "xiaomi-token-plan-sgp", "zai", "zai-coding-cn",
    ];

    private static readonly Dictionary<string, LlmDiscoveredModel[]> CatalogSeeds = new()
    {
        ["anthropic"] =
        [
            new("claude-sonnet-4-5", "Claude Sonnet 4.5", 200000, 64000, ["text", "image"]),
            new("claude-opus-4-1", "Claude Opus 4.1", 200000, 32000, ["text", "image"]),
        ],
        ["openai"] =
        [
            new("gpt-5.1", "GPT-5.1", 400000, 128000, ["text", "image"]),
            new("gpt-5.1-codex", "GPT-5.1 Codex", 400000, 128000, ["text", "image"]),
        ],
        ["google"] =
        [
            new("gemini-3-pro-preview", "Gemini 3 Pro", 1048576, 65536, ["text", "image"]),
        ],
        ["deepseek"] =
        [
            new("deepseek-chat", "DeepSeek Chat", 128000, 8192, ["text"]),
        ],
        ["mistral"] =
        [
            new("mistral-large-latest", "Mistral Large", 128000, 8192, ["text"]),
        ],
        ["glm"] =
        [
            new("glm-4.7", "GLM-4.7", 200000, 32768, ["text", "image"]),
            new("glm-4.7-air", "GLM-4.7-Air", 128000, 32768, ["text"]),
            new("glm-4.7-flash", "GLM-4.7-Flash", 128000, 32768, ["text"]),
        ],
    };

    public Task<IReadOnlyList<LlmConfigurableProvider>> GetConfigurableProvidersAsync(
        CancellationToken cancellationToken = default)
    {
        List<LlmConfigurableProvider> entries =
        [
            new("deepseek-account", "DeepSeek Account", "llm-deepseek-account", []),
            new("deepseek-official", "DeepSeek", "llm-deepseek", []),
        ];
        foreach (var id in BuiltinProviderIds)
            entries.Add(new LlmConfigurableProvider(id, id, "llm-pi-ai", ["providers", id], false));
        // declared 自定义路由：目录不认识、由 profile 自声明（与真实 profile patch 同构）。
        entries.Add(new LlmConfigurableProvider("glm", "GLM", "llm-pi-ai", ["providers", "glm"], true));

        return Task.FromResult<IReadOnlyList<LlmConfigurableProvider>>(entries);
    }

    public Task<IReadOnlyList<LlmDiscoveredModel>> DiscoverModelsAsync(
        string settingsNs, LlmDiscoveryRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settingsNs))
            throw new ArgumentException("设置命名空间不能为空。", nameof(settingsNs));
        cancellationToken.ThrowIfCancellationRequested();

        // 对齐上游语义：provider 与 baseURL 均空 → INVALID_DISCOVERY；目录厂商回 canned 目录；
        // 未知厂商带 baseURL → 占位模型；未知厂商无 baseURL → DISCOVERY_FAILED。
        if (string.IsNullOrWhiteSpace(request.Provider) && string.IsNullOrWhiteSpace(request.BaseUrl))
            throw new InvalidOperationException("模型发现不可用：请先填写提供商或 API 地址。");

        if (request.Provider is { Length: > 0 } provider)
        {
            if (CatalogSeeds.TryGetValue(provider, out var models))
                return Task.FromResult<IReadOnlyList<LlmDiscoveredModel>>(models);

            if (string.IsNullOrWhiteSpace(request.BaseUrl))
                throw new InvalidOperationException("该提供商未提供模型发现，请填写 API 地址后重试。");
        }

        return Task.FromResult<IReadOnlyList<LlmDiscoveredModel>>(
            [new LlmDiscoveredModel("model-1", "Model 1", 128000, 8192, ["text"])]);
    }
}
