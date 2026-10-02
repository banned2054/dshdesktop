using DshDesktop.Infrastructure.Services.Backend;

namespace DshDesktop.Services.Backend;

/// <summary>
/// 从环境变量解析后端运行配置。真实模式需要 Node 与已构建的 Harness 运行时；
/// 配置缺失时回退模拟模式并给出可读原因，不让应用无法启动。
/// </summary>
public sealed record DesktopBackendConfiguration
{
    public bool UseRealBackend { get; init; }

    public NodeHostOptions? Options { get; init; }

    /// <summary>真实后端的 DSH 包版本（node_modules/@deepseek-ai/dsh 的 package.json）；读取失败为 null。</summary>
    public string? BackendVersion { get; init; }

    /// <summary>创建会话后自动选用的模型（provider/model）；来自 DSH_DESKTOP_MODEL。</summary>
    public (string Provider, string Model)? PreferredModel { get; init; }

    /// <summary>请求真实后端但环境不满足时的说明；此时回退模拟模式。</summary>
    public string? ConfigurationError { get; init; }

    public static DesktopBackendConfiguration FromEnvironment()
    {
        var mode       = Environment.GetEnvironmentVariable("DSH_DESKTOP_BACKEND_MODE");
        var runtimeDir = Environment.GetEnvironmentVariable("DSH_DESKTOP_RUNTIME_DIR");
        var useReal = string.Equals(mode, "real", StringComparison.OrdinalIgnoreCase) ||
                    (string.IsNullOrEmpty(mode) && !string.IsNullOrWhiteSpace(runtimeDir));
        if (!useReal)
        {
            return new DesktopBackendConfiguration { UseRealBackend = false };
        }

        if (string.IsNullOrWhiteSpace(runtimeDir))
        {
            return Misconfigured("已请求真实后端，但未设置 DSH_DESKTOP_RUNTIME_DIR，已回退到模拟模式。");
        }

        runtimeDir = Path.GetFullPath(runtimeDir);
        if (!Directory.Exists(runtimeDir))
        {
            return Misconfigured($"后端运行时目录不存在：{runtimeDir}，已回退到模拟模式。");
        }

        var node = ResolveNodeExecutable();
        if (node is null)
        {
            return Misconfigured("找不到 Node 可执行文件（可用 DSH_DESKTOP_NODE 指定），已回退到模拟模式。");
        }

        var launcher = Environment.GetEnvironmentVariable("DSH_DESKTOP_LAUNCHER")
                    ?? Path.Combine(AppContext.BaseDirectory, "Assets", "Backend", "launcher.mjs");
        if (!File.Exists(launcher))
        {
            return Misconfigured($"找不到后端 launcher 脚本：{launcher}，已回退到模拟模式。");
        }

        var baseDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                       "DshDesktop");
        var options = new NodeHostOptions
        {
            NodeExecutablePath = node,
            LauncherScriptPath = launcher,
            RuntimeDir         = runtimeDir,
            // 插件 profile 保持应用私有，避免与 Electron 桌面版或其他 profile 使用者争用。
            ProfileDir = FromEnvironmentOr("DSH_DESKTOP_PROFILE_DIR",
                                           Path.Combine(baseDataDir, "backend", "profile")),
            PrimaryRuntimeDir = FromEnvironmentOr("DSH_DESKTOP_PRIMARY_RUNTIME",
                                                  Path.Combine(baseDataDir, "backend", "primary-runtime")),
            // 与正式安装的 dsh 共享 Harness home：会话、凭据与 llm 配置（DSH_HOME 或 ~/.dsh）。
            DshHome        = FromEnvironmentOr("DSH_DESKTOP_DSH_HOME", ResolveSharedDshHome()),
            ResolutionMode = FromEnvironmentOrValue("DSH_DESKTOP_RESOLUTION", "runtime"),
        };
        return new DesktopBackendConfiguration
        {
            UseRealBackend = true,
            Options        = options,
            BackendVersion = TryResolveBackendVersion(runtimeDir),
            PreferredModel = ParsePreferredModel(Environment.GetEnvironmentVariable("DSH_DESKTOP_MODEL")),
        };
    }

    /// <summary>读 runtime 目录内 dsh 包版本；文件缺失或解析失败返回 null（调用方回退处理）。</summary>
    internal static string? TryResolveBackendVersion(string runtimeDir)
    {
        try
        {
            var manifestPath = Path.Combine(runtimeDir, "node_modules", "@deepseek-ai", "dsh", "package.json");
            if (!File.Exists(manifestPath)) return null;

            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath));
            var version = document.RootElement.TryGetProperty("version", out var value) &&
                          value.ValueKind == System.Text.Json.JsonValueKind.String
                ? value.GetString()
                : null;
            return string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                                       System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>正式 dsh 的共享 home：优先 DSH_HOME，其次 ~/.dsh；不存在目录时返回占位路径。</summary>
    internal static string ResolveSharedDshHome()
    {
        var configured = Environment.GetEnvironmentVariable("DSH_HOME");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".dsh");
    }

    /// <summary>解析 DSH_DESKTOP_MODEL（形如 glm/glm-5.3-flash）。</summary>
    private static (string Provider, string Model)? ParsePreferredModel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var separator = value.IndexOf('/');
        if (separator <= 0 || separator == value.Length - 1)
        {
            return null;
        }

        return (value[..separator], value[(separator + 1)..]);
    }

    private static DesktopBackendConfiguration Misconfigured(string reason)
        => new() { UseRealBackend = false, ConfigurationError = reason };

    private static string FromEnvironmentOr(string variable, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value) ? fallback : Path.GetFullPath(value);
    }

    private static string FromEnvironmentOrValue(string variable, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string? ResolveNodeExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("DSH_DESKTOP_NODE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? Path.GetFullPath(configured) : null;
        }

        var executableName = OperatingSystem.IsWindows() ? "node.exe" : "node";
        var pathVariable   = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathVariable.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            try
            {
                var candidate = Path.Combine(directory.Trim(), executableName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch (ArgumentException)
            {
                // PATH 中的非法片段跳过。
            }
        }

        return null;
    }
}
