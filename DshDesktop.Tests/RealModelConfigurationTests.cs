using DshDesktop.Harness.Services.Connection;
using DshDesktop.Harness.Services.Sessions;
using DshDesktop.Harness.Services.Settings;
using DshDesktop.Infrastructure.Services.Backend;
using NUnit.Framework;
using NUnit.Framework.Legacy;

namespace DshDesktop.Tests;

/// <summary>
///     对正式 dsh home（~/.dsh，含用户 llm 配置）的接入配置验证：模型目录、显式选型、
///     以及通过后端 credentials/describe 查询目标凭据引用的解析状态。
///     本测试不调用付费模型；凭据是否可用只如实报告，不作为通过条件。
///     运行条件：DSH_E2E_RUNTIME_DIR 指向开发 runtime 且 DSH_E2E_REAL_HOME=1，缺失时明确跳过。
///     注意：本测试会在真实 home 中创建一个新会话。
/// </summary>
public sealed class RealModelConfigurationTests
{
    [Test]
    public async Task CatalogListsProviderSelectionEchoesAndCredentialStateIsQueryable()
    {
        var runtimeDir     = Environment.GetEnvironmentVariable(RealBackendTestSupport.RuntimeDirVariable);
        var realHome       = Environment.GetEnvironmentVariable(RealBackendTestSupport.RealHomeVariable);
        var node           = RealBackendTestSupport.FindNodeExecutable();
        var launcherScript = RealBackendTestSupport.FindLauncherScript();
        if (string.IsNullOrWhiteSpace(runtimeDir))
            Assert.Ignore($"未设置 {RealBackendTestSupport.RuntimeDirVariable}，跳过真实 home 配置验证。");
        if (!string.Equals(realHome, "1", StringComparison.Ordinal))
            Assert.Ignore($"未设置 {RealBackendTestSupport.RealHomeVariable}=1，跳过真实 home 配置验证。");
        if (node is null) Assert.Ignore("PATH 中找不到 Node 可执行文件，跳过。");
        if (launcherScript is null) Assert.Ignore("找不到 launcher 脚本，跳过。");

        var dshHome = RealBackendTestSupport.ResolveSharedDshHome();
        if (!File.Exists(Path.Combine(dshHome, "settings.yaml")))
            Assert.Ignore($"未找到 {Path.Combine(dshHome, "settings.yaml")}，跳过。");

        var (provider, model) =
            RealBackendTestSupport.ParseModel(Environment.GetEnvironmentVariable(RealBackendTestSupport
                                                 .ModelOverrideVariable) ?? "glm/glm-5.3-flash");
        var credentialRef = Environment.GetEnvironmentVariable(RealBackendTestSupport.CredentialRefVariable)
                         ?? "GLM_API_KEY";
        var root    = Path.Combine(Path.GetTempPath(), $"dsh-config-e2e-{Guid.NewGuid():N}");
        var options = RealBackendTestSupport.BuildOptions(node!, launcherScript!, runtimeDir!, dshHome, root);

        var hostService = new NodeBackendHostService(options);
        var connection  = new HarnessConnection(hostService.StartAsync);
        var sessions    = new HarnessSessionService(connection);
        var credentials = new HarnessCredentialService(connection);
        try
        {
            await hostService.StartAsync().WaitAsync(TimeSpan.FromSeconds(150));

            var catalog = await sessions.GetModelCatalogAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await TestContext.Out.WriteLineAsync($"默认模型：{catalog.Default?.Provider}/{catalog.Default?.Model}");
            await TestContext.Out
                             .WriteLineAsync($"目录提供方：{string.Join(", ", catalog.Groups.Select(group => group.Id))}");
            Assert.That(catalog.Groups.Any(group => group.Id == provider), Is.True);

            var created = await sessions.CreateSessionAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await TestContext.Out.WriteLineAsync($"新会话：{created.Id}");

            var selection = await sessions.SelectModelAsync(created.Id, provider, model)
                                          .WaitAsync(TimeSpan.FromSeconds(30));
            ClassicAssert.AreEqual(provider, selection.Provider);
            ClassicAssert.AreEqual(model, selection.Model);
            await TestContext.Out.WriteLineAsync($"已选型：{selection.Provider}/{selection.Model}");

            // 凭据判定交给当前 Host（credentials/describe），客户端不解析凭据文件。
            var entries = await credentials.DescribeAsync([credentialRef]).WaitAsync(TimeSpan.FromSeconds(30));
            entries.TryGetValue(credentialRef, out var status);
            ClassicAssert.IsNotNull(status);
            await TestContext.Out.WriteLineAsync(status!.Configured
                                                     ? $"凭据 {credentialRef}：已配置（来源 {status.Source ?? "未报告"}，可写 {status.Writable}）。"
                                                     : $"凭据 {credentialRef}：未配置（可写 {status.Writable}）。完整模型往返见 RealModelConversationTests。");

            ClassicAssert.IsFalse(hostService.LastError is { Length: > 0 }, $"后端意外出错：{hostService.LastError}");
        }
        finally
        {
            await connection.DisposeAsync();
            await hostService.DisposeAsync();
        }
    }
}
