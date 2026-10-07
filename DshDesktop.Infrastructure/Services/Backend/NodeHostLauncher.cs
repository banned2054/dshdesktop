using DshDesktop.Infrastructure.Exceptions;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DshDesktop.Infrastructure.Services.Backend;

/// <summary>Host 进程退出结果；Clean 表示走完请求的优雅关闭握手。</summary>
public sealed record NodeHostExited(int? Code, bool Clean);

/// <summary>
///     驱动 launcher.mjs：stdout 逐行 JSON 控制协议（v1），stderr 为日志。
///     控制输出与日志分离，启动超时与异常退出都以可读原因上抛。
/// </summary>
public sealed class NodeHostLauncher : IAsyncDisposable
{
    private const    int                                  MaxStderrTailChars = 16_000;
    private readonly TaskCompletionSource<NodeHostExited> _exited            = CreateCompletion<NodeHostExited>();

    private readonly Process _process;

    private readonly TaskCompletionSource<Uri> _ready = CreateCompletion<Uri>();

    private readonly Lock          _stderrLock = new();
    private readonly StringBuilder _stderrTail = new();

    private readonly WindowsProcessTreeGuard? _treeGuard;
    private          int                      _state; // 0 运行中，1 已请求停止，2 已终结

    private NodeHostLauncher(Process process, WindowsProcessTreeGuard? treeGuard)
    {
        _process   = process;
        _treeGuard = treeGuard;
    }

    /// <summary>就绪后完成，携带领启动令牌的认证 URL；失败或超时则出错。</summary>
    public Task<Uri> Ready => _ready.Task;

    /// <summary>Host exited 控制消息或 launcher 退出时完成；不保证 launcher 已实际终止。</summary>
    public Task<NodeHostExited> Exited => _exited.Task;

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _state, 2);
        // Dispose is the final ownership boundary. It must also terminate a
        // launcher whose graceful StopAsync wait was cancelled.
        KillTree();

        try
        {
            // The "exited" control message describes the Host, not the launcher.
            // Kill is asynchronous; wait for the OS process before releasing its
            // handles or allowing the owner to remove runtime files on Windows.
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        finally
        {
            // Even a failed exit wait must close the Job Object, but its failure
            // must propagate rather than reporting successful disposal.
            _process.OutputDataReceived -= OnControlLine;
            _process.ErrorDataReceived  -= OnStderrLine;
            _process.Exited             -= OnProcessExited;
            _treeGuard?.Dispose();
            _process.Dispose();
        }
    }

    /// <summary>就绪之后发生的意外失败（Host 崩溃或 IPC 报 fatal），参数为可读原因。</summary>
    public event Action<string>? UnexpectedFailure;

    /// <summary>启动 launcher 进程并开始读取控制输出；不等待就绪。</summary>
    public static NodeHostLauncher Start(NodeHostOptions options)
    {
        if (!File.Exists(options.NodeExecutablePath))
            throw new BackendProcessException($"找不到 Node 可执行文件：{options.NodeExecutablePath}");

        if (!File.Exists(options.LauncherScriptPath))
            throw new BackendProcessException($"找不到 launcher 脚本：{options.LauncherScriptPath}");

        if (!Directory.Exists(options.RuntimeDir))
            throw new BackendProcessException($"后端运行时目录不存在：{options.RuntimeDir}");

        var info = new ProcessStartInfo
        {
            FileName               = options.NodeExecutablePath,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8
        };
        info.ArgumentList.Add(options.LauncherScriptPath);
        info.ArgumentList.Add("--runtime-dir");
        info.ArgumentList.Add(options.RuntimeDir);
        info.ArgumentList.Add("--profile-dir");
        info.ArgumentList.Add(options.ProfileDir);
        info.ArgumentList.Add("--primary-runtime");
        info.ArgumentList.Add(options.PrimaryRuntimeDir);
        info.ArgumentList.Add("--resolution");
        info.ArgumentList.Add(options.ResolutionMode);
        info.ArgumentList.Add("--dsh-home");
        info.ArgumentList.Add(options.DshHome);

        var process = Process.Start(info)
                   ?? throw new BackendProcessException("无法启动 Node launcher 进程。");

        var treeGuard = WindowsProcessTreeGuard.TryCreate();
        treeGuard?.Assign(process);
        var launcher = new NodeHostLauncher(process, treeGuard);
        launcher.BeginReading();
        return launcher;
    }

    /// <summary>请求优雅停止；超时升级为强杀整个进程树，保证 Host 不遗留。</summary>
    public async Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
        {
            await _exited.Task.ConfigureAwait(false);
            return;
        }

        TryWriteControlLine("""{"type":"shutdown"}""");
        try
        {
            await _exited.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            KillTree();
            try
            {
                await _exited.Task.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // 进程树已强杀仍未见退出通知；Job Object 兜底回收。
            }
        }
    }

    private void BeginReading()
    {
        _process.OutputDataReceived  += OnControlLine;
        _process.ErrorDataReceived   += OnStderrLine;
        _process.Exited              += OnProcessExited;
        _process.EnableRaisingEvents =  true;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    private void OnControlLine(object sender, DataReceivedEventArgs e)
    {
        var line = e.Data;
        if (string.IsNullOrWhiteSpace(line)) return;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            AppendStderrLine($"忽略无法解析的控制消息：{line}");
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object            ||
                !root.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
                return;

            switch (typeElement.GetString())
            {
                case "ready" when root.TryGetProperty("url", out var urlElement) &&
                                  Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var url) :
                    _ready.TrySetResult(url);
                    break;

                case "fatal" or "error" :
                {
                    var message = root.TryGetProperty("message", out var messageElement) &&
                                  messageElement.ValueKind == JsonValueKind.String
                        ? messageElement.GetString()
                        : "后端报告了未知的致命错误。";
                    var stderrTail = GetString(root, "stderrTail");
                    Fail(message ?? "后端报告了未知的致命错误。", stderrTail);
                    break;
                }

                case "exited" :
                {
                    int? code = root.TryGetProperty("code", out var codeElement) &&
                                codeElement.ValueKind == JsonValueKind.Number    &&
                                codeElement.TryGetInt32(out var exitCode)
                        ? exitCode
                        : null;
                    var clean = root.TryGetProperty("clean", out var cleanElement) &&
                                cleanElement.ValueKind == JsonValueKind.True;
                    CompleteExit(code, clean);
                    break;
                }

                // stopping / shutdown-complete 为流程性消息，不单独处理。
            }
        }
    }

    private void OnStderrLine(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null) return;

        lock (_stderrLock)
        {
            if (_stderrTail.Length > MaxStderrTailChars) _stderrTail.Remove(0, _stderrTail.Length - MaxStderrTailChars);

            _stderrTail.AppendLine(e.Data);
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        // 控制行是主要退出信号；这里兜底 launcher 自身崩溃而没有发出 exited 的情况。
        CompleteExit(_process.HasExited ? _process.ExitCode : null, false);
    }

    private void Fail(string message, string? stderrTail)
    {
        var described = stderrTail is { Length: > 0 } ? $"{message}\n{Limit(stderrTail)}" : message;
        var exception = new BackendProcessException(described);
        _ready.TrySetException(exception);
        if (Interlocked.Exchange(ref _state, 2) == 0) RaiseUnexpectedFailure(described);
    }

    private void CompleteExit(int? code, bool clean)
    {
        var wasRunning = Interlocked.Exchange(ref _state, 2) == 0;
        _exited.TrySetResult(new NodeHostExited(code, clean));
        _ready.TrySetException(new
                                   BackendProcessException($"后端进程在就绪前退出（退出码 {code?.ToString() ?? "未知"}）。{CurrentStderrTail()}"));
        if (wasRunning) RaiseUnexpectedFailure($"后端进程意外退出（退出码 {code?.ToString() ?? "未知"}）。");
    }

    private void RaiseUnexpectedFailure(string reason)
    {
        try
        {
            UnexpectedFailure?.Invoke(reason);
        }
        catch (Exception)
        {
            // 事件处理器异常不影响进程管理。
        }
    }

    private string CurrentStderrTail()
    {
        lock (_stderrLock)
        {
            return Limit(_stderrTail.ToString());
        }
    }

    private void TryWriteControlLine(string line)
    {
        try
        {
            _process.StandardInput.WriteLine(line);
            _process.StandardInput.Flush();
        }
        catch (IOException)
        {
            // 控制管道已关闭：launcher 侧 stdin 关闭兜底会触发停止。
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void KillTree()
    {
        try
        {
            _process.Kill(true);
        }
        catch (InvalidOperationException)
        {
            // 进程已退出。
        }
        catch (Win32Exception)
        {
            // 终止失败交给 Job Object 与退出等待兜底。
        }
    }

    private void AppendStderrLine(string text)
    {
        lock (_stderrLock)
        {
            _stderrTail.AppendLine(text);
        }
    }

    private static string? GetString(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
    }

    private static string Limit(string text)
    {
        const int limit = 4_000;
        return text.Length <= limit ? text : $"…{text[^limit..]}";
    }

    private static TaskCompletionSource<T> CreateCompletion<T>()
    {
        return new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
