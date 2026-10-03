using DshDesktop.Services.Backend;
using NUnit.Framework;
using NUnit.Framework.Legacy;

namespace DshDesktop.Tests;

/// <summary>
///     后端版本来源：runtime 目录内 dsh 包的 package.json version（供关于面板等消费）；
///     文件缺失、损坏或字段非法时返回 null，由调用方回退处理。
/// </summary>
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public sealed class DesktopBackendConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dsh-backend-config-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结果。
        }
    }

    [Test]
    public void ResolvesDshPackageVersionFromRuntimeDir()
    {
        WriteManifest("""{ "name": "@deepseek-ai/dsh", "version": "0.2.0-rc.2" }""");

        ClassicAssert.AreEqual("0.2.0-rc.2", DesktopBackendConfiguration.TryResolveBackendVersion(_root));
    }

    [Test]
    public void MissingOrInvalidManifestYieldsNull()
    {
        // 目录存在但未安装包。
        ClassicAssert.IsNull(DesktopBackendConfiguration.TryResolveBackendVersion(_root));

        WriteManifest("{ not json");
        ClassicAssert.IsNull(DesktopBackendConfiguration.TryResolveBackendVersion(_root));

        WriteManifest("""{ "name": "@deepseek-ai/dsh" }""");
        ClassicAssert.IsNull(DesktopBackendConfiguration.TryResolveBackendVersion(_root));
    }

    private void WriteManifest(string content)
    {
        var path = Path.Combine(_root, "node_modules", "@deepseek-ai", "dsh", "package.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
