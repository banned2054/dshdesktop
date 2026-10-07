using DshDesktop.Services.Backend;
using NUnit.Framework;
using NUnit.Framework.Legacy;

namespace DshDesktop.Tests;

/// <summary>
///     后端版本来源：runtime 目录内 dsh 包的 package.json version（供关于面板等消费）；
///     文件缺失、损坏或字段非法时返回 null，由调用方回退处理。
/// </summary>
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
[NonParallelizable]
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
    public void ResolvesBundledBackendBesideApplicationExecutable()
    {
        var node = WriteBundledBackend(_root);
        using var environment = new EnvironmentScope(
            ("DSH_DESKTOP_BACKEND_MODE", null),
            ("DSH_DESKTOP_RUNTIME_DIR", null),
            ("DSH_DESKTOP_NODE", null),
            ("DSH_DESKTOP_LAUNCHER", null),
            ("DSH_DESKTOP_PRIMARY_RUNTIME", null),
            ("DSH_DESKTOP_PROFILE_DIR", null),
            ("DSH_DESKTOP_DSH_HOME", null),
            ("DSH_DESKTOP_RESOLUTION", null));

        var configuration = DesktopBackendConfiguration.FromEnvironment(_root);

        ClassicAssert.IsTrue(configuration.UseRealBackend);
        ClassicAssert.AreEqual(Path.Combine(_root, "backend", "runtime"), configuration.Options!.RuntimeDir);
        ClassicAssert.AreEqual(node, configuration.Options.NodeExecutablePath);
        ClassicAssert.AreEqual(Path.Combine(_root, "Assets", "Backend", "launcher.mjs"), configuration.Options.LauncherScriptPath);
        ClassicAssert.AreEqual(Path.Combine(_root, "backend", "primary-runtime"), configuration.Options.PrimaryRuntimeDir);
    }

    [Test]
    public void ResolvesBundledBackendInsideMacApplicationBundle()
    {
        var executableDirectory = Path.Combine(_root, "DshDesktop.app", "Contents", "MacOS");
        var resourcesDirectory = Path.Combine(_root, "DshDesktop.app", "Contents", "Resources");
        WriteBundledBackend(resourcesDirectory);
        using var environment = new EnvironmentScope(
            ("DSH_DESKTOP_BACKEND_MODE", null),
            ("DSH_DESKTOP_RUNTIME_DIR", null),
            ("DSH_DESKTOP_NODE", null),
            ("DSH_DESKTOP_LAUNCHER", null),
            ("DSH_DESKTOP_PRIMARY_RUNTIME", null),
            ("DSH_DESKTOP_PROFILE_DIR", null),
            ("DSH_DESKTOP_DSH_HOME", null),
            ("DSH_DESKTOP_RESOLUTION", null));

        var configuration = DesktopBackendConfiguration.FromEnvironment(executableDirectory);

        ClassicAssert.IsTrue(configuration.UseRealBackend);
        ClassicAssert.AreEqual(Path.Combine(resourcesDirectory, "backend", "runtime"), configuration.Options!.RuntimeDir);
        ClassicAssert.AreEqual(Path.Combine(resourcesDirectory, "backend", "primary-runtime"), configuration.Options.PrimaryRuntimeDir);
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

    private sealed class EnvironmentScope(params (string Name, string? Value)[] variables) : IDisposable
    {
        private readonly (string Name, string? Value)[] _previous = variables
            .Select(variable => (variable.Name, Environment.GetEnvironmentVariable(variable.Name)))
            .ToArray();

        public void Dispose()
        {
            foreach (var (name, value) in _previous)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    private string WriteBundledBackend(string root)
    {
        var runtime = Path.Combine(root, "backend", "runtime");
        var primaryRuntime = Path.Combine(root, "backend", "primary-runtime");
        var node = Path.Combine(primaryRuntime, "dependencies", "node", "bin",
                                 OperatingSystem.IsWindows() ? "node.exe" : "node");
        var launcher = Path.Combine(root, "Assets", "Backend", "launcher.mjs");
        Directory.CreateDirectory(runtime);
        Directory.CreateDirectory(Path.GetDirectoryName(node)!);
        Directory.CreateDirectory(Path.GetDirectoryName(launcher)!);
        File.WriteAllText(node, string.Empty);
        File.WriteAllText(launcher, string.Empty);
        return node;
    }

    private void WriteManifest(string content)
    {
        var path = Path.Combine(_root, "node_modules", "@deepseek-ai", "dsh", "package.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
