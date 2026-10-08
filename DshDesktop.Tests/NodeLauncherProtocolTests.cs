using DshDesktop.Core.Models;
using DshDesktop.Infrastructure.Exceptions;
using DshDesktop.Infrastructure.Services.Backend;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using System.Diagnostics;

namespace DshDesktop.Tests;

/// <summary>
///     launcher.mjs 控制协议（v1）生命周期行为：就绪、致命错误、优雅关闭与超时终止。
///     需要本机存在 Node 可执行文件与随包复制的 launcher 脚本，缺省时跳过。
/// </summary>
public sealed class NodeLauncherProtocolTests
{
    private const string StubHostScript = """
        const path = require('node:path')
        require('node:fs').writeFileSync(path.join(process.argv[3], 'host.pid'), String(process.pid))
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

        using var       _        = environment;
        await using var launcher = NodeHostLauncher.Start(environment.Options);
        var             readyUrl = await launcher.Ready.WaitAsync(TimeSpan.FromSeconds(15));

        ClassicAssert.AreEqual("http://127.0.0.1:19999/?token=stub-token", readyUrl.ToString());

        await launcher.StopAsync(TimeSpan.FromSeconds(10));
        var exited = await launcher.Exited.WaitAsync(TimeSpan.FromSeconds(10));
        ClassicAssert.IsTrue(exited.Clean);
    }

    [Test]
    [Platform("Win")]
    public async Task ReadOnlySourceNodeDoesNotPreventRuntimeCleanup()
    {
        var source = StubEnvironment.TryCreate("source");
        if (source is null) return;

        using var _ = source;
        var node = source.RuntimeNodePath;
        var attributes = File.GetAttributes(node);
        File.SetAttributes(node, attributes | FileAttributes.ReadOnly);
        try
        {
            using (var environment = StubEnvironment.TryCreate("ready", node)!)
            {
                Assert.That(File.GetAttributes(environment.RuntimeNodePath) & FileAttributes.ReadOnly,
                            Is.EqualTo((FileAttributes)0));
                await using var launcher = NodeHostLauncher.Start(environment.Options);
                await launcher.Ready.WaitAsync(TimeSpan.FromSeconds(15));
                await launcher.StopAsync(TimeSpan.FromSeconds(10));
            }

            Assert.That(File.GetAttributes(node) & FileAttributes.ReadOnly, Is.EqualTo(FileAttributes.ReadOnly),
                        "运行时清理不能修改共享 Node 的只读属性。");
        }
        finally
        {
            File.SetAttributes(node, attributes);
        }
    }

    [Test]
    public async Task HostFatalIsSurfacedWithMessage()
    {
        var environment = StubEnvironment.TryCreate("fatal");
        if (environment is null) return;

        using var       _        = environment;
        await using var launcher = NodeHostLauncher.Start(environment.Options);
        var exception =
            await Assert.ThrowsAsync<BackendProcessException>(() => launcher.Ready.WaitAsync(TimeSpan.FromSeconds(15)));

        Assert.That(exception!.Message, Does.Contain("boom-from-stub"));
    }

    [Test]
    [Repeat(20)]
    public async Task DisposeWithoutStopTerminatesTheTree()
    {
        var environment = StubEnvironment.TryCreate("silent");
        if (environment is null) return;

        using var _         = environment;
        var       startTime = DateTimeOffset.UtcNow;

        Process host;
        // 不等待就绪；确认 Host 子进程已创建，避免只测到尚未 spawn 的 launcher。
        await using (var launcher = NodeHostLauncher.Start(environment.Options))
        {
            host = await environment.WaitForHostProcessAsync();
            Assert.That(host.HasExited, Is.False);
        }

        using (host)
        {
            Assert.That(host.HasExited, Is.True, "释放 launcher 后不能遗留 Host 子进程。");
        }
        ClassicAssert.IsTrue(DateTimeOffset.UtcNow - startTime < TimeSpan.FromSeconds(15));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ForcedStopReclaimsUnresponsiveHost(bool cancelStop)
    {
        var environment = StubEnvironment.TryCreate("silent");
        if (environment is null) return;

        using var _ = environment;
        using var cancellation = new CancellationTokenSource();
        Process host;
        await using (var launcher = NodeHostLauncher.Start(environment.Options))
        {
            host = await environment.WaitForHostProcessAsync();
            if (cancelStop)
            {
                cancellation.Cancel();
                await Assert.CatchAsync<OperationCanceledException>(() =>
                    launcher.StopAsync(TimeSpan.FromSeconds(10), cancellation.Token));
            }
            else
            {
                await launcher.StopAsync(TimeSpan.FromMilliseconds(100));
                Assert.That(host.HasExited, Is.True, "停止超时返回前必须回收 Host 子进程。");
                Assert.That((await launcher.Exited).Clean, Is.False);
            }
        }

        using (host)
        {
            Assert.That(host.HasExited, Is.True, "取消停止等待后，释放仍必须回收 Host 子进程。");
        }
    }

    [Test]
    public async Task DisposeReclaimsLauncherThatIgnoresControlMessages()
    {
        var environment = StubEnvironment.TryCreate("ready");
        if (environment is null) return;

        using var _ = environment;
        var script = Path.Combine(environment.Options.ProfileDir, "unresponsive.mjs");
        File.WriteAllText(script, """
            process.stdout.write(JSON.stringify({ type: 'ready', url: `http://127.0.0.1:19999/?pid=${process.pid}` }) + '\n')
            setInterval(() => {}, 60000)
            """);

        Process process;
        await using (var launcher = NodeHostLauncher.Start(environment.Options with { LauncherScriptPath = script }))
        {
            var ready = await launcher.Ready.WaitAsync(TimeSpan.FromSeconds(15));
            process = Process.GetProcessById(int.Parse(ready.Query["?pid=".Length..]));
            Assert.That(process.HasExited, Is.False);
        }

        using (process)
        {
            Assert.That(process.HasExited, Is.True, "launcher 不响应控制消息时仍必须限时强杀。");
        }
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

    [Test]
    [Repeat(20)]
    public async Task DisposeWaitsForLauncherExitAfterHostExitMessage()
    {
        var environment = StubEnvironment.TryCreate("ready");
        if (environment is null) return;

        using var _ = environment;
        var script = Path.Combine(environment.Options.ProfileDir, "early-exit.mjs");
        File.WriteAllText(script, """
            import { createInterface } from 'node:readline'
            process.stdout.write(JSON.stringify({ type: 'ready', url: `http://127.0.0.1:19999/?pid=${process.pid}` }) + '\n')
            createInterface({ input: process.stdin }).on('line', () => {
              process.stdout.write(JSON.stringify({ type: 'exited', code: 0, clean: true }) + '\n')
            })
            setInterval(() => {}, 60000)
            """);

        Process process;
        await using (var launcher = NodeHostLauncher.Start(environment.Options with { LauncherScriptPath = script }))
        {
            var ready = await launcher.Ready.WaitAsync(TimeSpan.FromSeconds(15));
            process = Process.GetProcessById(int.Parse(ready.Query["?pid=".Length..]));
            await launcher.StopAsync(TimeSpan.FromSeconds(10));
            Assert.That((await launcher.Exited).Clean, Is.True);
            Assert.That(process.HasExited, Is.False, "Host exited 控制消息不代表 launcher 已退出。");
        }

        using (process)
        {
            Assert.That(process.HasExited, Is.True, "Dispose 必须等待实际进程退出，不能只等待控制消息。");
        }
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

        public string RuntimeNodePath => Path.Combine(Options.PrimaryRuntimeDir, "dependencies", "node", "bin",
                                                     OperatingSystem.IsWindows() ? "node.exe" : "node");

        public async Task<Process> WaitForHostProcessAsync()
        {
            var pidFile = Path.Combine(Options.ProfileDir, "host.pid");
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (File.Exists(pidFile) && int.TryParse(await File.ReadAllTextAsync(pidFile), out var pid))
                    return Process.GetProcessById(pid);

                await Task.Delay(20);
            }

            throw new TimeoutException("Stub Host 未记录进程 ID。");
        }

        public void Dispose()
        {
            // 清理失败必须暴露；不能把仍在使用运行时文件的进程泄漏当成测试通过。
            Directory.Delete(_root, true);
        }

        public static StubEnvironment? TryCreate(string mode, string? runtimeNodeSourcePath = null)
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
            var runtimeNode = Path.Combine(primaryRuntimeDir, "dependencies", "node", "bin",
                                           OperatingSystem.IsWindows() ? "node.exe" : "node");
            Directory.CreateDirectory(Path.GetDirectoryName(runtimeNode)!);
            // 预置副本，避免 launcher 将共享 Node 的只读属性带入临时运行时。
            var runtimeNodeSource = runtimeNodeSourcePath ?? node;
            File.Copy(runtimeNodeSource, runtimeNode);
            File.SetAttributes(runtimeNode, File.GetAttributes(runtimeNode) & ~FileAttributes.ReadOnly);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(runtimeNode, File.GetUnixFileMode(runtimeNodeSource));

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
