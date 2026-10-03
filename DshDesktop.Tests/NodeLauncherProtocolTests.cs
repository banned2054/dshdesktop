using DshDesktop.Core.Models;
using DshDesktop.Infrastructure.Exceptions;
using DshDesktop.Infrastructure.Services.Backend;
using NUnit.Framework;
using NUnit.Framework.Legacy;

namespace DshDesktop.Tests;

/// <summary>
///     launcher.mjs 控制协议（v1）生命周期行为：就绪、致命错误、优雅关闭与超时终止。
///     需要本机存在 Node 可执行文件与随包复制的 launcher 脚本，缺省时跳过。
/// </summary>
public sealed class NodeLauncherProtocolTests
{
    private const string StubHostScript = """
        const path = require('node:path')
        const mode = path.basename(process.argv[4] || '')
        const send = (message) => { if (process.connected) process.send(message) }
        if (mode.includes('fatal')) {
          send({ type: 'fatal', message: 'boom-from-stub' })
          setTimeout(() => process.exit(1), 50)
        } else if (mode.includes('silent')) {
          setInterval(() => {}, 60000)
          process.on('disconnect', () => process.exit(0))
        } else {
          send({ type: 'ready', url: 'http://127.0.0.1:19999/?token=stub-token' })
          process.on('message', (message) => {
            if (message && message.type === 'shutdown') {
              send({ type: 'shutdown-complete' })
              setTimeout(() => { try { process.disconnect() } catch {} process.exit(0) }, 20)
            }
          })
          process.on('disconnect', () => process.exit(0))
        }
        """;

    [Test]
    public async Task ReadyUrlIsRelayedAndShutdownIsClean()
    {
        var environment = StubEnvironment.TryCreate("ready");
        if (environment is null) return;

        using var _        = environment;
        var       launcher = NodeHostLauncher.Start(environment.Options);
        var       readyUrl = await launcher.Ready.WaitAsync(TimeSpan.FromSeconds(15));

        ClassicAssert.AreEqual("http://127.0.0.1:19999/?token=stub-token", readyUrl.ToString());

        await launcher.StopAsync(TimeSpan.FromSeconds(10));
        var exited = await launcher.Exited.WaitAsync(TimeSpan.FromSeconds(10));
        ClassicAssert.IsTrue(exited.Clean);
        await launcher.DisposeAsync();
    }

    [Test]
    public async Task HostFatalIsSurfacedWithMessage()
    {
        var environment = StubEnvironment.TryCreate("fatal");
        if (environment is null) return;

        using var _        = environment;
        var       launcher = NodeHostLauncher.Start(environment.Options);
        var exception =
            await Assert.ThrowsAsync<BackendProcessException>(() => launcher.Ready.WaitAsync(TimeSpan.FromSeconds(15)));

        Assert.That(exception.Message, Does.Contain("boom-from-stub"));
        await launcher.DisposeAsync();
    }

    [Test]
    public async Task DisposeWithoutStopTerminatesTheTree()
    {
        var environment = StubEnvironment.TryCreate("silent");
        if (environment is null) return;

        using var _         = environment;
        var       launcher  = NodeHostLauncher.Start(environment.Options);
        var       startTime = DateTimeOffset.UtcNow;

        // 不等待就绪，直接释放：必须终止进程树而不是挂起。
        await launcher.DisposeAsync();
        await launcher.Exited.WaitAsync(TimeSpan.FromSeconds(15));

        ClassicAssert.IsTrue(DateTimeOffset.UtcNow - startTime < TimeSpan.FromSeconds(15));
    }

    [Test]
    public async Task StopDuringStartupCancelsStartupAndReclaimsLauncher()
    {
        var environment = StubEnvironment.TryCreate("silent");
        if (environment is null) return;

        using var       _       = environment;
        var             options = environment.Options with { StopTimeout = TimeSpan.FromMilliseconds(100) };
        await using var host    = new NodeBackendHostService(options);

        var start = host.StartAsync();
        await host.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));

        Assert.That(() => start, Throws.InstanceOf<OperationCanceledException>());
        ClassicAssert.AreEqual(BackendStatus.Offline, host.Status);
    }

    private sealed class StubEnvironment : IDisposable
    {
        private readonly string _root;

        private StubEnvironment(string root, NodeHostOptions options)
        {
            _root   = root;
            Options = options;
        }

        public NodeHostOptions Options { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不影响测试结论。
            }
        }

        public static StubEnvironment? TryCreate(string mode)
        {
            var node           = FindNodeExecutable();
            var launcherScript = Path.Combine(AppContext.BaseDirectory, "Assets", "Backend", "launcher.mjs");
            if (node is null || !File.Exists(launcherScript)) return null;

            var root       = Path.Combine(Path.GetTempPath(), $"dsh-launcher-test-{Guid.NewGuid():N}");
            var runtimeDir = Path.Combine(root, "runtime");
            var entryDir   = Path.Combine(runtimeDir, "node_modules", "@deepseek-ai", "dsh-desktop-host", "lib");
            Directory.CreateDirectory(entryDir);
            File.WriteAllText(Path.Combine(entryDir, "index.js"), StubHostScript);

            var profileDir        = Path.Combine(root, "profile");
            var primaryRuntimeDir = Path.Combine(root, $"primary-runtime-{mode}");
            var dshHome           = Path.Combine(root, "dsh-home");
            Directory.CreateDirectory(profileDir);
            Directory.CreateDirectory(primaryRuntimeDir);
            Directory.CreateDirectory(dshHome);

            var options = new NodeHostOptions
            {
                NodeExecutablePath = node,
                LauncherScriptPath = launcherScript,
                RuntimeDir         = runtimeDir,
                ProfileDir         = profileDir,
                PrimaryRuntimeDir  = primaryRuntimeDir,
                DshHome            = dshHome,
                ReadyTimeout       = TimeSpan.FromSeconds(15),
                StopTimeout        = TimeSpan.FromSeconds(10)
            };
            return new StubEnvironment(root, options);
        }

        private static string? FindNodeExecutable()
        {
            var executableName = OperatingSystem.IsWindows() ? "node.exe" : "node";
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                    .Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;

                try
                {
                    var candidate = Path.Combine(directory.Trim(), executableName);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch (ArgumentException)
                {
                }
            }

            return null;
        }
    }
}
