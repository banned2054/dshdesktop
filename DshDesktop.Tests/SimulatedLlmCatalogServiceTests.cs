using DshDesktop.Core.Models;
using DshDesktop.Infrastructure.Services;
using NUnit.Framework;
using NUnit.Framework.Legacy;

namespace DshDesktop.Tests;

/// <summary>模拟 llm 目录域：目录种子与发现语义（INVALID_DISCOVERY / DISCOVERY_FAILED / 目录厂商直回）。</summary>
public sealed class SimulatedLlmCatalogServiceTests
{
    [Test]
    public async Task DirectoryContainsFirstPartyRoutesBuiltinsAndDeclaredRoutes()
    {
        var service = new SimulatedLlmCatalogService();
        var entries = await service.GetConfigurableProvidersAsync();

        // DeepSeek 两条第一方路由（整段路由 settingsPath=[]）。
        Assert.That(entries.Any(entry => entry.Provider           == "deepseek-official" &&
                                         entry.SettingsNs         == "llm-deepseek"      &&
                                         entry.SettingsPath.Count == 0), Is.True);
        Assert.That(entries.Any(entry => entry.Provider   == "deepseek-account" &&
                                         entry.SettingsNs == "llm-deepseek-account"), Is.True);
        // pi-ai 内置目录厂商：settingsPath 恒 ['providers', id] 且非 declared。
        var mistral = entries.Single(entry => entry.Provider == "mistral");
        ClassicAssert.AreEqual("llm-pi-ai", mistral.SettingsNs);
        ClassicAssert.AreEqual(new[] { "providers", "mistral" }, mistral.SettingsPath);
        ClassicAssert.AreNotEqual(true, mistral.Declared);
        // declared 自定义路由。
        var glm = entries.Single(entry => entry.Provider == "glm");
        ClassicAssert.IsTrue(glm.Declared);
        ClassicAssert.AreEqual(new[] { "providers", "glm" }, glm.SettingsPath);
    }

    [Test]
    public async Task DiscoverReturnsCatalogForKnownProviderAndRejectsInvalidProbes()
    {
        var service = new SimulatedLlmCatalogService();

        var mistral = await service.DiscoverModelsAsync("llm-pi-ai", new LlmDiscoveryRequest("mistral"));
        Assert.That(mistral, Has.Count.EqualTo(1));
        ClassicAssert.AreEqual("mistral-large-latest", mistral[0].Id);

        // 未知厂商且无端点 → DISCOVERY_FAILED 语义。
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
                                                                service.DiscoverModelsAsync("llm-pi-ai",
                                                                    new LlmDiscoveryRequest("unknown-provider")));

        // provider 与 baseURL 均空 → INVALID_DISCOVERY 语义。
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
                                                                service.DiscoverModelsAsync("llm-pi-ai",
                                                                    new LlmDiscoveryRequest()));

        // 未知厂商带端点 → 占位模型。
        var relay = await service.DiscoverModelsAsync("llm-pi-ai",
                                                      new LlmDiscoveryRequest(BaseUrl : "https://relay.example/v1"));
        Assert.That(relay, Has.Count.EqualTo(1));
    }
}
