using DshDesktop.Core.Models;
using DshDesktop.Harness.Services.Sessions;
using DshDesktop.Infrastructure.Services.Backend;
using System.Collections.Concurrent;
using Xunit;
using Xunit.Abstractions;

namespace DshDesktop.Tests;

/// <summary>
///     真实后端 E2E 测试的共享支撑：环境解析、Host 启动参数与线程安全的更新收集。
///     订阅任务自身的异常会被记录并在测试末尾断言，不再被静默丢弃。
/// </summary>
internal static class RealBackendTestSupport
{
    public const string RuntimeDirVariable = "DSH_E2E_RUNTIME_DIR";

    public const string RealHomeVariable = "DSH_E2E_REAL_HOME";

    public const string RealModelVariable = "DSH_E2E_REAL_MODEL";

    public const string ModelOverrideVariable = "DSH_E2E_MODEL";

    public const string CredentialRefVariable = "DSH_E2E_CREDENTIAL_REF";

    public static string? FindNodeExecutable()
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

    public static string? FindLauncherScript()
    {
        var launcher = Path.Combine(AppContext.BaseDirectory, "Assets", "Backend", "launcher.mjs");
        return File.Exists(launcher) ? launcher : null;
    }

    /// <summary>真实 home（DSH_HOME 或 ~/.dsh）；供配置/对话测试共享。</summary>
    public static string ResolveSharedDshHome()
    {
        var configured = Environment.GetEnvironmentVariable("DSH_HOME");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
    }

    public static (string Provider, string Model) ParseModel(string value)
    {
        var separator = value.IndexOf('/');
        return separator <= 0
            ? ("glm", "glm-5.3-flash")
            : (value[..separator], value[(separator + 1)..]);
    }

    public static NodeHostOptions BuildOptions(
        string node, string launcherScript, string runtimeDir, string dshHome, string root)
    {
        return new NodeHostOptions
        {
            NodeExecutablePath = node,
            LauncherScriptPath = launcherScript,
            RuntimeDir         = Path.GetFullPath(runtimeDir),
            ProfileDir         = Path.Combine(root, "profile"),
            PrimaryRuntimeDir  = Path.Combine(root, "primary-runtime"),
            DshHome            = dshHome,
            ReadyTimeout       = TimeSpan.FromSeconds(120),
            StopTimeout        = TimeSpan.FromSeconds(30)
        };
    }

    public static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout, string description)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition() && DateTimeOffset.UtcNow < deadline) await Task.Delay(200);

        if (!condition()) throw new TimeoutException($"{description}（超时 {timeout.TotalSeconds:0}s）。");

        return true;
    }

    public static async Task<T> WaitForAsync<T>(Func<T?> condition, TimeSpan timeout, string description)
        where T : class
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var match = condition();
            if (match is not null) return match;

            await Task.Delay(200);
        }

        throw new TimeoutException($"{description}（超时 {timeout.TotalSeconds:0}s）。");
    }

    public static async Task<bool> WaitForAsync(Func<Task<bool>> condition, TimeSpan timeout, string description)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!await condition() && DateTimeOffset.UtcNow < deadline) await Task.Delay(200);

        if (!await condition()) throw new TimeoutException($"{description}（超时 {timeout.TotalSeconds:0}s）。");

        return true;
    }

    /// <summary>
    ///     订阅线程与断言线程之间的更新收集：ConcurrentQueue 避免跨线程读写普通 List 的竞态。
    /// </summary>
    internal sealed class UpdateCollector(ITestOutputHelper output)
    {
        private readonly ConcurrentQueue<SessionUpdate> _updates = new();

        private volatile Exception? _fault;

        public Task StartAsync(HarnessSessionService   sessions, string sessionId,
                               CancellationTokenSource followCancellation)
        {
            return Task.Run(async () =>
            {
                try
                {
                    await foreach (var update in sessions.FollowSessionAsync(sessionId, followCancellation.Token))
                        _updates.Enqueue(update);
                }
                catch (OperationCanceledException) when (followCancellation.IsCancellationRequested)
                {
                    // 测试结束时主动取消订阅，属正常退出。
                }
                catch (Exception exception)
                {
                    _fault = exception;
                    output.WriteLine($"订阅任务异常：{exception}");
                }
            });
        }

        public IReadOnlyList<SessionUpdate> Snapshot()
        {
            return _updates.ToArray();
        }

        public T? FirstOrDefault<T>() where T : SessionUpdate
        {
            return _updates.OfType<T>().FirstOrDefault();
        }

        public void AssertNoSubscriptionFault()
        {
            Assert.Null(_fault);
        }
    }
}
