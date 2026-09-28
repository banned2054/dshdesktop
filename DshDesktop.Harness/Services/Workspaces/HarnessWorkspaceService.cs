using DshDesktop.Core.Models;
using DshDesktop.Core.Services;
using DshDesktop.Harness.Exceptions;
using DshDesktop.Harness.Models.Events;
using DshDesktop.Harness.Services.Connection;

namespace DshDesktop.Harness.Services.Workspaces;

/// <summary>
///     基于 workspace/follow 状态流的工作区服务：订阅帧维护投影，
///     快照语义对齐参考客户端 ClientWorkspaceModel（baseline 整体替换、
///     upsert 新行插头部且旧投影不覆盖新、order 按给出的顺序重排）。
/// </summary>
public sealed class HarnessWorkspaceService : IWorkspaceService, IAsyncDisposable
{
    private readonly HarnessConnection _connection;
    private readonly Lock              _sync = new();

    private IReadOnlyList<WorkspaceSummary> _items = [];

    private IReadOnlySet<string> _archivedSessionIds = new HashSet<string>();

    private Task?                           _pump;
    private CancellationTokenSource?        _pumpCancellation;

    public HarnessWorkspaceService(HarnessConnection connection)
    {
        _connection = connection;
    }

    public async ValueTask DisposeAsync()
    {
        Task? pump;
        lock (_sync)
        {
            pump = _pump;
        }

        if (_pumpCancellation is not null) await _pumpCancellation.CancelAsync().ConfigureAwait(false);

        if (pump is not null)
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 关闭期的泵异常无需上抛。
            }

        _pumpCancellation?.Dispose();
    }

    public event EventHandler? WorkspacesChanged;

    public Task<IReadOnlyList<WorkspaceSummary>> GetWorkspacesAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_pump is null)
            {
                var cancellation = new CancellationTokenSource();
                _pumpCancellation = cancellation;
                _pump             = Task.Run(() => PumpAsync(cancellation.Token));
            }
        }

        // 基线未到达时先返回空集合，消费方靠 WorkspacesChanged 增量更新；
        // 与参考客户端 workspacePhase pending 期的表现一致。
        lock (_sync)
        {
            return Task.FromResult(_items);
        }
    }

    public IReadOnlySet<string> ArchivedSessionIds
    {
        get
        {
            lock (_sync) return _archivedSessionIds;
        }
    }

    /// <summary>帧应用到投影；纯逻辑抽出便于直接测试。</summary>
    internal static IReadOnlyList<WorkspaceSummary> ApplyFrame(
        IReadOnlyList<WorkspaceSummary> items, WorkspaceFollowFrame frame)
    {
        switch (frame)
        {
            case WorkspaceFollowFrame.Baseline baseline :
                return baseline.Items.Select(ToSummary).ToArray();

            case WorkspaceFollowFrame.Upsert upsert :
            {
                var incoming = ToSummary(upsert.Workspace);
                for (var index = 0; index < items.Count; index++)
                    if (items[index].Id == incoming.Id)
                    {
                        if (incoming.UpdatedAt < items[index].UpdatedAt) return items;

                        var replaced = new List<WorkspaceSummary>(items)
                        {
                            [index] = incoming
                        };
                        return replaced;
                    }

                return [incoming, .. items];
            }

            case WorkspaceFollowFrame.Removed removed :
                return items.Where(item => item.Id != removed.WorkspaceId).ToArray();

            case WorkspaceFollowFrame.Reordered reordered :
            {
                var rank = new Dictionary<string, int>();
                for (var index = 0; index < reordered.WorkspaceIds.Count; index++)
                    rank[reordered.WorkspaceIds[index]] = index;

                return items.OrderBy(item => rank.TryGetValue(item.Id, out var position) ? position : int.MaxValue)
                            .ToArray();
            }

            default :
                return items;
        }
    }

    internal static WorkspaceSummary ToSummary(WorkspaceViewWire view)
    {
        return new WorkspaceSummary(view.WorkspaceId, view.Title, view.Path, view.SessionIds, view.UpdatedAt);
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in _connection.FollowWorkspacesAsync(cancellationToken).ConfigureAwait(false))
            {
                bool changed;
                lock (_sync)
                {
                    if (frame is WorkspaceFollowFrame.Archived archived)
                    {
                        // 归档集合是 registry 级投影：全量替换，变化即通知。
                        var nextArchived = archived.ArchivedSessionIds.ToHashSet();
                        changed          = !nextArchived.SetEquals(_archivedSessionIds);
                        _archivedSessionIds = nextArchived;
                    }
                    else
                    {
                        var next = ApplyFrame(_items, frame);
                        // Baseline 无条件通知：它标记一代投影就绪（即使内容为空），
                        // 消费方以此区分「基线未到达」与「确无工作区」。
                        changed = frame is WorkspaceFollowFrame.Baseline
                               || (!ReferenceEquals(next, _items) && !SameItems(next, _items));
                        _items = next;
                    }
                }

                if (changed) RaiseWorkspacesChanged();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (HarnessRpcException)
        {
            // 业务终态（如后端版本无 workspace 命名空间）：保持当前投影，不再重试。
        }
        catch (Exception)
        {
            // 其余异常（载波恢复耗尽等）：保持当前投影，列表退化为未分组展示。
        }
    }

    /// <summary>投影是否逐项一致（同 id 同记账）；用于抑制无变化的事件。</summary>
    private static bool SameItems(IReadOnlyList<WorkspaceSummary> left, IReadOnlyList<WorkspaceSummary> right)
    {
        if (left.Count != right.Count) return false;

        for (var index = 0; index < left.Count; index++)
            if (left[index].Id != right[index].Id
             || !left[index].SessionIds.SequenceEqual(right[index].SessionIds)
             || left[index].Title != right[index].Title)
                return false;

        return true;
    }

    private void RaiseWorkspacesChanged()
    {
        try
        {
            WorkspacesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            // 事件处理器异常不影响订阅循环。
        }
    }
}
