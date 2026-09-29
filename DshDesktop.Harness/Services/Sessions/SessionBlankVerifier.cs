using DshDesktop.Core.Models;
using DshDesktop.Harness.Exceptions;
using DshDesktop.Harness.Models.Events;
using DshDesktop.Harness.Models.Responses;

namespace DshDesktop.Harness.Services.Sessions;

/// <summary>列表中的一行：应用模型概要 + 行内携带的有效 sessionListMetadata（无则 null）。</summary>
internal sealed record ListedSessionRow(SessionSummary Summary, SessionListMetadataWire? ListMetadata);

/// <summary>
///     历史未知（Unknown）会话的后台核实协调器：用只读 session/projections 逐个确认
///     空白状态，是空白判定在服务层的单一协调位置（完整目录、侧栏可见行与复用候选
///     都经由 <see cref="Resolve" /> 消费同一份结论）。
///     - 首屏不等待：列表先正常返回并显示，Unknown 行随后在后台核实，结果经变更通知合并；
///     - 小并发（2）+ 同会话在途合并 + 每会话每连接代有限重试（指数退避）；
///     - 有效结论：blank=true → ConfirmedBlank；blank=false → Engaged；
///       null（会话不存在）、元数据缺失、格式错误、超时或读取失败一律不判定（保持 Unknown，不误隐藏）；
///     - 竞争：本端已参与（台账）的会话不判空白；核实期间观察到会话活动则迟到结果作废并重验，
///       已接受的非空证据不会被迟到的空白响应覆盖；不比较 cached 与 sequenced 两类水印的 seq；
///     - 连接重置：取消旧代在途请求、递增代际并清空退避；旧代响应不写入新代，
///       旧代结论保持生效（避免隐藏行闪现）但会在新代重新核实；
///     - 结论只在内存中，随进程结束消失；不持久化，不写任何会话数据。
/// </summary>
internal sealed class SessionBlankVerifier(
    SessionBlankVerifier.ProjectionsReader reader,
    Func<string, bool>                     isEngaged,
    Action                                 changed,
    Func<DateTimeOffset>?                  now = null)
    : IDisposable
{
    /// <summary>session/projections 只读读取器；返回 null 表示后端确认会话不存在。</summary>
    internal delegate Task<SessionProjectionsValue?> ProjectionsReader(
        string sessionId, CancellationToken cancellationToken);

    /// <summary>核实请求并发上限。</summary>
    internal const int MaxConcurrency = 2;

    /// <summary>同一连接代内每会话的最大核实尝试数；超过后等待重连或活动触发。</summary>
    internal const int MaxAttemptsPerGeneration = 5;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryMaxDelay  = TimeSpan.FromSeconds(30);

    private readonly Func<DateTimeOffset> _now  = now ?? (() => DateTimeOffset.UtcNow);
    private readonly Lock                 _sync = new();

    /// <summary>已核实结论；State=null 表示"不判定"结论（如会话不存在），仅抑制本轮重复扫描。</summary>
    private readonly Dictionary<string, VerifiedEntry> _results = [];

    /// <summary>每会话的失败退避：尝试次数与下次允许核实的时刻。</summary>
    private readonly Dictionary<string, (int Attempts, DateTimeOffset NotBefore)> _failures = [];

    /// <summary>会话活动版本：核实期间版本变化说明出现了更新证据，迟到结果作废。</summary>
    private readonly Dictionary<string, long> _activityVersions = [];

    /// <summary>已排队待发的会话（认领制：RunAsync 原子移除后进入在途）。</summary>
    private readonly HashSet<string> _scheduled = [];

    /// <summary>在途核实的会话。</summary>
    private readonly HashSet<string> _inFlight = [];

    private readonly SemaphoreSlim _gate = new(MaxConcurrency, MaxConcurrency);

    private CancellationTokenSource _lifetime = new();
    private TaskCompletionSource    _idle     = NewIdleSource();

    private long _generation;
    private bool _unsupported;
    private bool _disposed;
    private int  _pending;

    private readonly record struct VerifiedEntry(SessionBlankState? State, long Generation);

    /// <summary>后端拒绝了 projections 接口（不支持或请求形状不符）；为 true 时不再发起核实。</summary>
    internal bool IsUnsupported
    {
        get
        {
            lock (_sync)
            {
                return _unsupported;
            }
        }
    }

    /// <summary>等待全部排队与在途核实结束（测试辅助）。</summary>
    internal Task WaitForIdleAsync()
    {
        lock (_sync)
        {
            return _pending == 0 ? Task.CompletedTask : _idle.Task;
        }
    }

    /// <summary>
    ///     合并一轮会话列表：计算每行的最终空白状态（台账已参与 &gt; 当前代有效核实结论 &gt;
    ///     行内权威 sessionListMetadata &gt; 原始映射），写回行内权威证据，清理已消失的会话，
    ///     并为 Unknown 行与旧代结论安排后台核实。首屏不等待核实完成。
    /// </summary>
    public IReadOnlyList<SessionSummary> Resolve(IReadOnlyList<ListedSessionRow> rows)
    {
        var resolved = new SessionSummary[rows.Count];
        lock (_sync)
        {
            var listedIds = new HashSet<string>();
            foreach (var row in rows) listedIds.Add(row.Summary.Id);

            PruneAbsent(listedIds);

            for (var index = 0; index < rows.Count; index++)
            {
                var row   = rows[index];
                var state = ResolveRowState(row);
                resolved[index] = state == row.Summary.BlankState
                    ? row.Summary
                    : row.Summary with { BlankState = state };
                if (state == SessionBlankState.Unknown) ScheduleCore(row.Summary.Id);
            }

            // 连接重置后旧代结论保持生效（避免隐藏行闪现），但安排在新代重新核实。
            foreach (var (sessionId, entry) in _results)
                if (entry.Generation != _generation)
                    ScheduleCore(sessionId);
        }

        DispatchScheduled();
        return resolved;
    }

    /// <summary>本端已参与会话（发送被接受等）：清除可能存在的空白结论并跳过后续核实。</summary>
    public void OnSessionEngaged(string sessionId)
    {
        lock (_sync)
        {
            if (_results.TryGetValue(sessionId, out var entry) &&
              entry.State == SessionBlankState.ConfirmedBlank)
                _results[sessionId] = new VerifiedEntry(SessionBlankState.Engaged, entry.Generation);

            _failures.Remove(sessionId);
        }
    }

    /// <summary>
    ///     观察到归属到具体会话的活动： bumped 活动版本（使在途核实结果过期）；
    ///     已删除的会话清理判定；曾确认空白的会话结论失效（重新可见）并重新核实。
    /// </summary>
    public void OnSessionActivity(SessionActivityNotice notice)
    {
        var shouldRaise  = false;
        var hasScheduled = false;
        lock (_sync)
        {
            if (notice.Event == "api-session/removed")
            {
                _results.Remove(notice.SessionId);
                _failures.Remove(notice.SessionId);
                _activityVersions.Remove(notice.SessionId);
                return;
            }

            _activityVersions[notice.SessionId] = _activityVersions.GetValueOrDefault(notice.SessionId) + 1;

            if (_results.TryGetValue(notice.SessionId, out var entry))
            {
                switch (entry.State)
                {
                    case SessionBlankState.ConfirmedBlank :
                        // 曾确认空白的会话出现活动（可能已被其他客户端使用）：结论失效，重新可见并重验。
                        _results.Remove(notice.SessionId);
                        hasScheduled = ScheduleCore(notice.SessionId);
                        shouldRaise  = true;
                        break;
                    case null :
                        // "不判定"结论（如会话不存在）随活动失效，允许重验。
                        _results.Remove(notice.SessionId);
                        hasScheduled = ScheduleCore(notice.SessionId);
                        break;
                }
            }
        }

        if (hasScheduled) DispatchScheduled();
        if (shouldRaise) changed();
    }

    /// <summary>连接重置：取消旧代在途请求、递增代际并重建退避状态。</summary>
    public void OnConnectionReset()
    {
        CancellationTokenSource previous;
        lock (_sync)
        {
            _generation++;
            _failures.Clear();
            previous  = _lifetime;
            _lifetime = new CancellationTokenSource();
        }

        previous.Cancel();
        previous.Dispose();
    }

    public void Dispose()
    {
        CancellationTokenSource lifetime;
        lock (_sync)
        {
            if (_disposed) return;

            _disposed = true;
            lifetime  = _lifetime;
        }

        lifetime.Cancel();
    }

    /// <summary>计算一行的最终空白状态。行内有效元数据视为当前代权威证据并写回核实结果。</summary>
    private SessionBlankState ResolveRowState(ListedSessionRow row)
    {
        if (isEngaged(row.Summary.Id)) return SessionBlankState.Engaged;

        switch (row.Summary.BlankState)
        {
            // 行内元数据 blank=false：会话已开始（turn/start 已发生），权威且不可逆。
            case SessionBlankState.Engaged :
                WriteBackLocked(row.Summary.Id, SessionBlankState.Engaged);
                return SessionBlankState.Engaged;

            // 行内元数据 blank=true：元数据确认空白（区别于 seq=0 的回退信号，不写回）。
            case SessionBlankState.ConfirmedBlank when row.ListMetadata is { Blank: true } :
                WriteBackLocked(row.Summary.Id, SessionBlankState.ConfirmedBlank);
                return SessionBlankState.ConfirmedBlank;
        }

        if (_results.TryGetValue(row.Summary.Id, out var entry) &&
          entry.Generation == _generation &&
          entry.State is { } verified)
            // 普通列表返回的 Unknown 行不得撤销当前连接上下文中的有效核实结论。
            return verified;

        return row.Summary.BlankState;
    }

    private void WriteBackLocked(string sessionId, SessionBlankState state)
    {
        _results[sessionId] = new VerifiedEntry(state, _generation);
        _failures.Remove(sessionId);
    }

    /// <summary>清理已从列表消失的会话（被删除或归档）的全部判定状态。</summary>
    private void PruneAbsent(HashSet<string> listedIds)
    {
        RemoveAbsentKeys(_results, listedIds);
        RemoveAbsentKeys(_failures, listedIds);
        RemoveAbsentKeys(_activityVersions, listedIds);
    }

    private static void RemoveAbsentKeys<TValue>(Dictionary<string, TValue> source, HashSet<string> listedIds)
    {
        List<string>? absent = null;
        foreach (var sessionId in source.Keys.Where(sessionId => !listedIds.Contains(sessionId)))
            (absent ??= []).Add(sessionId);

        if (absent is null) return;

        foreach (var sessionId in absent) source.Remove(sessionId);
    }

    /// <summary>排队一次核实（调用方持有锁）；返回是否真正入队。单飞 + 退避 + 当前代结论去重。</summary>
    private bool ScheduleCore(string sessionId)
    {
        if (_disposed                      || _unsupported || isEngaged(sessionId)) return false;
        if (_scheduled.Contains(sessionId) || _inFlight.Contains(sessionId)) return false;

        // 当前代已有结论（含"不判定"）：不重复扫描；重连或活动才触发重验。
        if (_results.TryGetValue(sessionId, out var entry) && entry.Generation == _generation) return false;

        if (_failures.TryGetValue(sessionId, out var failure) &&
          (failure.Attempts >= MaxAttemptsPerGeneration || failure.NotBefore > _now()))
            return false;

        _scheduled.Add(sessionId);
        if (_pending++ == 0) _idle = NewIdleSource();

        return true;
    }

    private void DispatchScheduled()
    {
        string[] snapshot;
        lock (_sync)
        {
            if (_scheduled.Count == 0) return;

            snapshot = [.. _scheduled];
        }

        foreach (var sessionId in snapshot) _ = RunAsync(sessionId);
    }

    private async Task RunAsync(string sessionId)
    {
        long generationAtIssue = 0;
        long activityAtIssue   = 0;
        var  lifetimeToken     = CancellationToken.None;
        var  staleResult       = false;

        CancellationTokenSource? timeout = null;
        try
        {
            lock (_sync)
            {
                // 认领制：只执行自己认领的排队项；已释放或不支持时归还计数。
                if (!_scheduled.Remove(sessionId)) return;

                if (_disposed || _unsupported)
                {
                    if (--_pending == 0) _idle.TrySetResult();
                    return;
                }

                _inFlight.Add(sessionId);
                generationAtIssue = _generation;
                activityAtIssue   = ActivityVersionOf(sessionId);
                lifetimeToken     = _lifetime.Token;
                timeout           = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
                timeout.CancelAfter(RequestTimeout);
            }

            await _gate.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                var value = await reader(sessionId, timeout.Token).ConfigureAwait(false);
                staleResult = ApplyResult(sessionId, value, generationAtIssue, activityAtIssue);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (lifetimeToken.IsCancellationRequested)
        {
            // 连接重置或服务释放：旧代请求作废，状态由新代重新核实。
        }
        catch (OperationCanceledException)
        {
            RegisterFailure(sessionId);
        }
        catch (HarnessRpcException exception) when (IsUnsupportedCode(exception.Code))
        {
            lock (_sync)
            {
                _unsupported = true;
            }
        }
        catch (Exception)
        {
            RegisterFailure(sessionId);
        }
        finally
        {
            timeout?.Dispose();
            var rescheduled = false;
            lock (_sync)
            {
                if (_inFlight.Remove(sessionId) && --_pending == 0) _idle.TrySetResult();

                // 在途记录移除后才能重新排队（结果作废或旧代请求结束时需要重验）。
                if (!_disposed && (staleResult || _generation != generationAtIssue))
                    rescheduled = ScheduleCore(sessionId);
            }

            if (rescheduled) DispatchScheduled();
        }
    }

    /// <summary>
    ///     应用一次读取结果（调用方持有锁之外）。返回是否因结果过期而需要重验：
    ///     重验排队必须等本请求移出在途记录后进行（由 <see cref="RunAsync" /> 的收尾处理）。
    /// </summary>
    private bool ApplyResult(
        string sessionId, SessionProjectionsValue? value, long generationAtIssue, long activityAtIssue)
    {
        var stored = false;
        var stale  = false;
        lock (_sync)
        {
            if (generationAtIssue != _generation)
                return false; // 旧连接响应不写入新代；收尾按代际变化重验。

            if (_activityVersions.GetValueOrDefault(sessionId) != activityAtIssue)
            {
                // 核实期间观察到会话活动：这份结果可能已过期，作废并重验。
                stale = true;
            }
            else if (isEngaged(sessionId))
            {
                // 本端已参与：不采纳空白结论（迟到的 blank=true 不能隐藏已开始的会话）。
                _results[sessionId] = new VerifiedEntry(SessionBlankState.Engaged, _generation);
                _failures.Remove(sessionId);
                stored = true;
            }
            else if (value is null)
            {
                // 后端确认会话不存在：不判定空白，仅抑制本轮重复扫描；重连或活动后重验。
                _results[sessionId] = new VerifiedEntry(null, _generation);
                _failures.Remove(sessionId);
            }
            else if (SessionProjectionsJson.TryParseListMetadata(value.Values) is not { } metadata)
            {
                // 元数据缺失或形状错误：不得判定，按失败退避后重试。
                RegisterFailureLocked(sessionId);
            }
            else
            {
                _results[sessionId] = new VerifiedEntry(metadata.Blank
                                                            ? SessionBlankState.ConfirmedBlank
                                                            : SessionBlankState.Engaged,
                                                        _generation);
                _failures.Remove(sessionId);
                stored = true;
            }
        }

        if (stored) changed();

        return stale;
    }

    private void RegisterFailure(string sessionId)
    {
        lock (_sync)
        {
            RegisterFailureLocked(sessionId);
        }
    }

    /// <summary>登记一次失败（调用方持有锁）：指数退避，超过每代尝试上限后不再自动重试。</summary>
    private void RegisterFailureLocked(string sessionId)
    {
        var attempts = _failures.TryGetValue(sessionId, out var failure) ? failure.Attempts + 1 : 1;
        var delay = TimeSpan.FromMilliseconds(Math.Min(RetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempts - 1),
                                                       RetryMaxDelay.TotalMilliseconds));
        _failures[sessionId] = (attempts, _now().Add(delay));
    }

    private long ActivityVersionOf(string sessionId)
    {
        return _activityVersions.GetValueOrDefault(sessionId, 0);
    }

    /// <summary>projections 接口级不可用：不支持该 RPC 或请求形状被拒（协议不兼容），停止全部核实。</summary>
    private static bool IsUnsupportedCode(string code)
    {
        return code is "session/projections-unavailable" or "gateway/bad-request";
    }

    private static TaskCompletionSource NewIdleSource()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
