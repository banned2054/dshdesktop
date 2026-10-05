using DshDesktop.Core.Models;
using DshDesktop.Core.Services;
using DshDesktop.Infrastructure.Exceptions;

namespace DshDesktop.Infrastructure.Services.Backend;

/// <summary>
///     基于 Node launcher 的后端 Host 服务：负责启动、就绪、意外失败上报与正常停止，
///     不涉及业务协议（业务连接由 Harness 项目建立）。
/// </summary>
public sealed class NodeBackendHostService : IBackendHostService
{
    private readonly Func<NodeHostOptions, NodeHostLauncher> _launcherFactory;

    private readonly Lock            _lock = new();
    private readonly NodeHostOptions _options;
    private          string?         _lastError;

    private NodeHostLauncher?            _launcher;
    private CancellationTokenSource?     _startCancellation;
    private Task<BackendConnectionInfo>? _startTask;
    private BackendStatus                _status = BackendStatus.Offline;

    public NodeBackendHostService(NodeHostOptions options)
        : this(options, static candidate => NodeHostLauncher.Start(candidate))
    {
    }

    /// <summary>测试可注入 launcher 工厂。</summary>
    internal NodeBackendHostService(
        NodeHostOptions options, Func<NodeHostOptions, NodeHostLauncher> launcherFactory)
    {
        _options         = options;
        _launcherFactory = launcherFactory;
    }

    public BackendStatus Status
    {
        get
        {
            lock (_lock)
            {
                return _status;
            }
        }
    }

    public string? LastError
    {
        get
        {
            lock (_lock)
            {
                return _lastError;
            }
        }
    }

    public event EventHandler? StatusChanged;

    public Task<BackendConnectionInfo> StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_startTask is not null) return _startTask;

            _startCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _startTask         = StartCoreAsync(_startCancellation.Token);
            return _startTask;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task<BackendConnectionInfo>? startTask;
        CancellationTokenSource?     startCancellation;
        lock (_lock)
        {
            startTask         = _startTask;
            startCancellation = _startCancellation;
        }

        // Stop must synchronize with startup. Otherwise a stop issued before the
        // launcher factory returns can report Offline while startup continues and
        // installs a live launcher afterwards.
        try
        {
            startCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Startup failure may have already released the linked source.
        }

        if (startTask is not null)
            try
            {
                // Startup owns the launcher cleanup on cancellation/failure; wait
                // for it before taking the launcher for normal shutdown.
                await startTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The startup exception is already reported to its caller.
            }

        NodeHostLauncher? launcher;
        lock (_lock)
        {
            launcher           = _launcher;
            _launcher          = null;
            _startTask         = null;
            _startCancellation = null;
        }

        startCancellation?.Dispose();

        if (launcher is null)
        {
            SetStatus(BackendStatus.Offline, null);
            return;
        }

        try
        {
            await launcher.StopAsync(_options.StopTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await launcher.DisposeAsync().ConfigureAwait(false);
            SetStatus(BackendStatus.Offline, null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private async Task<BackendConnectionInfo> StartCoreAsync(CancellationToken cancellationToken)
    {
        SetStatus(BackendStatus.Starting, null);
        NodeHostLauncher? launcher = null;
        try
        {
            launcher = _launcherFactory(_options);
            lock (_lock)
            {
                _launcher = launcher;
            }

            launcher.UnexpectedFailure += OnUnexpectedFailure;

            var readyUrl = await launcher.Ready.WaitAsync(_options.ReadyTimeout, cancellationToken)
                                         .ConfigureAwait(false);
            SetStatus(BackendStatus.Connected, null);
            return new BackendConnectionInfo(readyUrl);
        }
        catch (TimeoutException)
        {
            var message = $"后端启动超时（{_options.ReadyTimeout.TotalSeconds:0} 秒）未见就绪信号。";
            await AbortAsync(launcher).ConfigureAwait(false);
            throw new BackendProcessException(message);
        }
        catch (OperationCanceledException)
        {
            await AbortAsync(launcher).ConfigureAwait(false);
            throw;
        }
        catch (BackendProcessException)
        {
            await AbortAsync(launcher).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await AbortAsync(launcher).ConfigureAwait(false);
            throw new BackendProcessException($"后端启动失败：{exception.Message}", null, exception);
        }
    }

    private async Task AbortAsync(NodeHostLauncher? launcher)
    {
        CancellationTokenSource? startCancellation = null;

        lock (_lock)
        {
            if (launcher is null || ReferenceEquals(_launcher, launcher))
            {
                if (ReferenceEquals(_launcher, launcher)) _launcher = null;

                _startTask         = null;
                startCancellation  = _startCancellation;
                _startCancellation = null;
            }
        }

        startCancellation?.Dispose();
        SetStatus(BackendStatus.Error, GetLastError());
        if (launcher is null) return;

        try
        {
            await launcher.StopAsync(_options.StopTimeout).ConfigureAwait(false);
        }
        finally
        {
            await launcher.DisposeAsync().ConfigureAwait(false);
        }
    }

    private string? GetLastError()
    {
        lock (_lock)
        {
            return _lastError;
        }
    }

    private void OnUnexpectedFailure(string reason)
    {
        SetStatus(BackendStatus.Error, reason);
    }

    private void SetStatus(BackendStatus status, string? error)
    {
        EventHandler? handlers;
        lock (_lock)
        {
            if (_status == status && _lastError == error) return;

            _status    = status;
            _lastError = error;
            handlers   = StatusChanged;
        }

        handlers?.Invoke(this, EventArgs.Empty);
    }
}
