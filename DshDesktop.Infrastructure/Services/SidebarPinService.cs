using DshDesktop.Core.Services;
using DshDesktop.Infrastructure.Json;
using DshDesktop.Infrastructure.Models;
using System.Text.Json;

namespace DshDesktop.Infrastructure.Services;

/// <summary>
///     本地置顶注册表：集合持久化到本项目配置文件（SpecialFolder.ApplicationData 下的
///     DshDesktop/sidebar-pins.json，平台差异由 .NET 特殊目录归一）。每次变更在实例内
///     信号量与跨进程 .lock 文件锁双重互斥下执行读-改-写：先从磁盘读取最新持久状态，
///     在其上应用本次变更后原子落盘（临时文件整体替换），成功才提交内存并通知；读或写
///     失败时集合不变并抛出，由消费方呈现。多个客户端实例同时运行时以磁盘最新状态为
///     合并基准，不整体覆盖对方记录。文件缺失或损坏按空集合起底，不阻塞启动。
/// </summary>
public sealed class SidebarPinService : ISidebarPinService
{
    private static readonly TimeSpan ProcessLockRetryInterval = TimeSpan.FromMilliseconds(50);

    private static readonly TimeSpan DefaultProcessLockTimeout = TimeSpan.FromSeconds(5);

    private readonly string _filePath;

    // 同实例并发变更先在进程内排队（命令可能从 UI 线程与后台线程同时触发），
    // 跨进程互斥由 .lock 文件承担；两者缺一不可。
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly string _lockFilePath;

    private readonly TimeSpan _processLockTimeout;
    private readonly Lock     _sync = new();

    private List<string> _pinnedSessions   = [];
    private List<string> _pinnedWorkspaces = [];

    public SidebarPinService(string? filePath = null, TimeSpan? processLockTimeout = null)
    {
        _processLockTimeout = processLockTimeout ?? DefaultProcessLockTimeout;
        _filePath = filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                             "DshDesktop", "sidebar-pins.json");
        _lockFilePath = _filePath + ".lock";
        Load();
    }

    public event EventHandler? PinsChanged;

    public IReadOnlyList<string> PinnedSessionIds
    {
        get
        {
            lock (_sync)
            {
                return _pinnedSessions.ToArray();
            }
        }
    }

    public IReadOnlyList<string> PinnedWorkspaceIds
    {
        get
        {
            lock (_sync)
            {
                return _pinnedWorkspaces.ToArray();
            }
        }
    }

    public Task PinSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        return SetPinnedAsync(sessionId, true, true, cancellationToken);
    }

    public Task UnpinSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        return SetPinnedAsync(sessionId, false, true, cancellationToken);
    }

    public Task PinWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        return SetPinnedAsync(workspaceId, true, false, cancellationToken);
    }

    public Task UnpinWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        return SetPinnedAsync(workspaceId, false, false, cancellationToken);
    }

    /// <summary>
    ///     幂等变更：持锁读取最新持久状态，在其上应用本次变更；目标状态已在最新持久层
    ///     成立时不重复落盘，仅把内存对齐到最新状态（吸收其他实例的变更）。否则先落盘
    ///     再提交内存（失败即整体不变）。
    /// </summary>
    private async Task SetPinnedAsync(
        string id, bool pinned, bool isSession, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (await AcquireProcessLockAsync(cancellationToken).ConfigureAwait(false))
            {
                var latest = await ReadLatestStateAsync(cancellationToken).ConfigureAwait(false);
                var source = isSession ? latest.Sessions : latest.Workspaces;
                var updated = pinned
                    ? [id, ..source.Where(existing => existing != id)]
                    : source.Where(existing => existing != id).ToList();
                if (updated.SequenceEqual(source))
                {
                    CommitState(latest.Sessions, latest.Workspaces);
                    return;
                }

                var nextSessions   = isSession ? updated : latest.Sessions;
                var nextWorkspaces = isSession ? latest.Workspaces : updated;
                await PersistAsync(nextSessions, nextWorkspaces, cancellationToken).ConfigureAwait(false);
                CommitState(nextSessions, nextWorkspaces);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>提交内存并按需通知：与内存一致时不触发，吸收其他实例变更时也会触发。</summary>
    private void CommitState(IReadOnlyList<string> sessions, IReadOnlyList<string> workspaces)
    {
        lock (_sync)
        {
            if (sessions.SequenceEqual(_pinnedSessions) && workspaces.SequenceEqual(_pinnedWorkspaces)) return;

            _pinnedSessions   = [..sessions];
            _pinnedWorkspaces = [..workspaces];
        }

        PinsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    ///     跨进程互斥：以配置文件旁的固定 .lock 文件为进程间锁——FileShare.None 独占打开
    ///     成功即持锁，被其他实例占用时等待重试，超过总超时抛出 IOException。持锁方崩溃
    ///     由操作系统释放句柄，不遗留死锁；.lock 文件常驻不删除，避免「先释放再删除」与
    ///     另一方重开文件之间的竞态。等待经 Task.Delay 异步进行，不阻塞 UI 线程。
    /// </summary>
    private async Task<LockFileScope> AcquireProcessLockAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var deadline = Environment.TickCount64 + (long)_processLockTimeout.TotalMilliseconds;
        while (true)
        {
            FileStream? stream = null;
            try
            {
                stream = new FileStream(_lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return new LockFileScope(stream);
            }
            catch (IOException)
            {
                stream?.Dispose();
            }

            // 超时判定在 try/catch 之外：放进 try 会被上面的 catch (IOException) 吞掉变成死循环。
            var remaining = deadline - Environment.TickCount64;
            if (remaining <= 0)
                throw new IOException($"等待置顶配置文件锁超时（{_processLockTimeout.TotalSeconds:0} 秒），可能有其他实例正在变更置顶。");

            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(remaining, ProcessLockRetryInterval.TotalMilliseconds)),
                             cancellationToken)
                      .ConfigureAwait(false);
        }
    }

    /// <summary>落盘集合的读取清洗：去空白项与重复项，保持首次出现顺序；null 按空集合处理。</summary>
    private static List<string> Sanitize(IReadOnlyList<string>? ids)
    {
        return ids?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList() ?? [];
    }

    /// <summary>从磁盘重读最新持久状态；缺失或损坏按空起底（与 Load 同一语义），IO 失败上抛。</summary>
    private async Task<SidebarPinState> ReadLatestStateAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath)) return new SidebarPinState([], []);

        // FileShare.Delete 允许另一实例的原子替换并发进行，读取方继续读到旧内容。
        await using var stream =
            new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        try
        {
            var state = await JsonSerializer.DeserializeAsync(stream, SidebarPinJsonContext.Default.SidebarPinState,
                                                              cancellationToken)
                                            .ConfigureAwait(false);
            return state is null
                ? new SidebarPinState([], [])
                : new SidebarPinState(Sanitize(state.Sessions), Sanitize(state.Workspaces));
        }
        catch (JsonException)
        {
            return new SidebarPinState([], []);
        }
    }

    /// <summary>整体落盘：写临时文件成功后替换目标，避免半截文件损坏既有配置。</summary>
    private async Task PersistAsync(
        IReadOnlyList<string> sessions, IReadOnlyList<string> workspaces, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temporary = _filePath + ".tmp";
        try
        {
            await using (var stream =
                         new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, new SidebarPinState(sessions, workspaces),
                                                    SidebarPinJsonContext.Default.SidebarPinState,
                                                    cancellationToken)
                                    .ConfigureAwait(false);
            }

            File.Move(temporary, _filePath, true);
        }
        catch
        {
            try
            {
                File.Delete(temporary);
            }
            catch
            {
                // 临时文件清理失败不影响错误上抛。
            }

            throw;
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;

            // FileShare.Delete 允许另一实例的原子替换并发进行，读取方继续读到旧内容。
            using var stream =
                new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var state = JsonSerializer.Deserialize(stream, SidebarPinJsonContext.Default.SidebarPinState);
            if (state is null) return;

            _pinnedSessions   = Sanitize(state.Sessions);
            _pinnedWorkspaces = Sanitize(state.Workspaces);
        }
        catch
        {
            // 配置文件不可读：按空集合起底，不阻塞启动；下次置顶变更整体重写。
            _pinnedSessions   = [];
            _pinnedWorkspaces = [];
        }
    }

    private sealed class LockFileScope(FileStream stream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            return stream.DisposeAsync();
        }
    }
}
