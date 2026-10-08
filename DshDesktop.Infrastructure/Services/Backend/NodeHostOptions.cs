namespace DshDesktop.Infrastructure.Services.Backend;

/// <summary>Node Host 进程的启动与监督配置。</summary>
public sealed record NodeHostOptions
{
    /// <summary>Node 可执行文件路径。</summary>
    public required string NodeExecutablePath { get; init; }

    /// <summary>launcher.mjs 的绝对路径。</summary>
    public required string LauncherScriptPath { get; init; }

    /// <summary>包含 node_modules 的运行时目录（install anchor 与 Host 入口所在）。</summary>
    public required string RuntimeDir { get; init; }

    /// <summary>桌面插件 profile 目录，由 launcher 初始化。</summary>
    public required string ProfileDir { get; init; }

    /// <summary>primary-runtime 载荷目录；缺失时 launcher 提供开发桩。</summary>
    public required string PrimaryRuntimeDir { get; init; }

    /// <summary>Host 的 Harness home；与会话数据隔离于应用数据目录。</summary>
    public required string DshHome { get; init; }

    /// <summary>保留的 launcher 参数（runtime 或 link）；当前锁定 Host 自行进行进程内解析，不再接收此位置参数。</summary>
    public string ResolutionMode { get; init; } = "runtime";

    /// <summary>等待 Host 就绪的上限。</summary>
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>正常停止的总预算，超时升级为强制终止。</summary>
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
