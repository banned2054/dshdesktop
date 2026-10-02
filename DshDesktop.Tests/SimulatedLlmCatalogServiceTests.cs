using DshDesktop.Core.Models;
using DshDesktop.Infrastructure.Services;
using Xunit;

namespace DshDesktop.Tests;

/// <summary>模拟 llm 目录域：目录种子与发现语义（INVALID_DISCOVERY / DISCOVERY_FAILED / 目录厂商直回）。</summary>
public sealed class SimulatedLlmCatalogServiceTests
{
    [Fact]
    public async Task DirectoryContainsFirstPartyRoutesBuiltinsAndDeclaredRoutes()
    {
        var service = new SimulatedLlmCatalogService();
        var entries = await service.GetConfigurableProvidersAsync();

        // DeepSeek 两条第一方路由（整段路由 settingsPath=[]）。
        Assert.Contains(entries, entry => entry.Provider == "deepseek-official" &&
                                          entry.SettingsNs == "llm-deepseek" &&
                                          entry.SettingsPath.Count == 0);
        Assert.Contains(entries, entry => entry.Provider == "deepseek-account" &&
                                          entry.SettingsNs == "llm-deepseek-account");
        // pi-ai 内置目录厂商：settingsPath 恒 ['providers', id] 且非 declared。
        var mistral = entries.Single(entry => entry.Provider == "mistral");
        Assert.Equal("llm-pi-ai", mistral.SettingsNs);
        Assert.Equal(["providers", "mistral"], mistral.SettingsPath);
        Assert.NotEqual(true, mistral.Declared);
        // declared 自定义路由。
        var glm = entries.Single(entry => entry.Provider == "glm");
        Assert.True(glm.Declared);
        Assert.Equal(["providers", "glm"], glm.SettingsPath);
    }

    [Fact]
    public async Task DiscoverReturnsCatalogForKnownProviderAndRejectsInvalidProbes()
    {
        var service = new SimulatedLlmCatalogService();

        var mistral = await service.DiscoverModelsAsync("llm-pi-ai", new LlmDiscoveryRequest("mistral"));
        Assert.Single(mistral);
        Assert.Equal("mistral-large-latest", mistral[0].Id);

        // 未知厂商且无端点 → DISCOVERY_FAILED 语义。
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DiscoverModelsAsync("llm-pi-ai", new LlmDiscoveryRequest("unknown-provider")));

        // provider 与 baseURL 均空 → INVALID_DISCOVERY 语义。
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DiscoverModelsAsync("llm-pi-ai", new LlmDiscoveryRequest()));

        // 未知厂商带端点 → 占位模型。
        var relay = await service.DiscoverModelsAsync("llm-pi-ai",
                                                      new LlmDiscoveryRequest(BaseUrl: "https://relay.example/v1"));
        Assert.Single(relay);
    }
}
