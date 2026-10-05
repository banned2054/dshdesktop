using DshDesktop.Core.Models;
using DshDesktop.Core.Services;
using DshDesktop.Harness.Exceptions;
using DshDesktop.Harness.Json;
using DshDesktop.Harness.Models.Events;
using DshDesktop.Harness.Models.Requests;
using DshDesktop.Harness.Services.Connection;

namespace DshDesktop.Harness.Services.Workspaces;

/// <summary>
///     基于 workspace/follow 状态流的工作区服务：订阅帧维护投影，
///     快照语义对齐参考客户端 ClientWorkspaceModel（baseline 整体替换并落
///     registry 级归档全量集合、upsert 新行插头部且旧投影不覆盖新、
///     order 按给出的顺序重排）。置顶不消费后端集合（本端自有方案）。
/// </summary>
public sealed class HarnessWorkspaceService(HarnessConnection connection) : IWorkspaceService, IAsyncDisposable
{
    private readonly Lock _sync = new();

    private IReadOnlySet<string> _archivedSessionIds = new HashSet<string>();

    private IReadOnlyList<WorkspaceSummary> _items = [];

    private Task? _pump;

    private CancellationTokenSource? _pumpCancellation;

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
                // 泵生命周期属于服务自身 CTS（随 Dispose 取消），与调用方 token 无关：
                // 显式 None 声明有意不传播，避免调用方取消误杀后台泵。
                _pump = Task.Run(() => PumpAsync(cancellation.Token), CancellationToken.None);
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
            lock (_sync)
            {
                return _archivedSessionIds;
            }
        }
    }

    public async Task ArchiveSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var value = await connection.InvokeAsync("workspace/archiveSession",
                                                 new WorkspaceArchiveSessionRequest(sessionId),
                                                 HarnessJsonContext.Default.WorkspaceArchiveSessionRequest,
                                                 HarnessJsonContext.Default.WorkspaceArchiveValue,
                                                 cancellationToken)
                                    .ConfigureAwait(false);
        // 归档集合同理经 archived 帧回流；先行落投影让归档行立即从列表消失。
        ApplyLocalArchived(value.ArchivedSessionIds);
    }

    public async Task<WorkspaceSummary> RegisterWorkspaceAsync(
        string path, CancellationToken cancellationToken = default)
    {
        var value = await connection.InvokeAsync("workspace/create",
                                                 new WorkspaceCreateRequest(path),
                                                 HarnessJsonContext.Default.WorkspaceCreateRequest,
                                                 HarnessJsonContext.Default.WorkspaceCreateValue,
                                                 cancellationToken)
                                    .ConfigureAwait(false);
        // 新登记行由 workspace/follow 的 upsert 帧回流进投影；返回值仅供调用方即时反馈。
        return ToSummary(value.Workspace);
    }

    public async Task<WorkspaceSummary> RenameWorkspaceAsync(
        string workspaceId, string title, CancellationToken cancellationToken = default)
    {
        var value = await connection.InvokeAsync("workspace/rename",
                                                 new WorkspaceRenameRequest(workspaceId, title),
                                                 HarnessJsonContext.Default.WorkspaceRenameRequest,
                                                 HarnessJsonContext.Default.WorkspaceRenameValue,
                                                 cancellationToken)
                                    .ConfigureAwait(false);
        // 新标题正常由 workspace/follow 的 upsert 帧回流；这里按同一 upsert 语义先行落投影，
        // 让标题不经历无帧空窗（帧回流经 UpdatedAt 新者胜，天然幂等对齐）。
        ApplyLocalChange(items => ApplyFrame(items, new WorkspaceFollowFrame.Upsert(value.Workspace)));
        return ToSummary(value.Workspace);
    }

    public async Task DeleteWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        await connection.InvokeAsync("workspace/delete",
                                     new WorkspaceDeleteRequest(workspaceId),
                                     HarnessJsonContext.Default.WorkspaceDeleteRequest,
                                     HarnessJsonContext.Default.WorkspaceDeleteValue,
                                     cancellationToken)
                        .ConfigureAwait(false);
        // 移除由 follow 的 remove 帧回流确认；先行按同一语义落投影避免列表残留空窗。
        ApplyLocalChange(items => ApplyFrame(items, new WorkspaceFollowFrame.Removed(workspaceId)));
    }

    /// <summary>把本地变更按帧语义落进投影；与帧泵共用锁与通知，帧回流后幂等对齐。</summary>
    private void ApplyLocalChange(Func<IReadOnlyList<WorkspaceSummary>, IReadOnlyList<WorkspaceSummary>> apply)
    {
        bool changed;
        lock (_sync)
        {
            var next = apply(_items);
            changed = !ReferenceEquals(next, _items) && !SameItems(next, _items);
            _items  = next;
        }

        if (changed) RaiseWorkspacesChanged();
    }

    /// <summary>归档集合的本地变更（语义同 archived 帧：全量替换，变化即通知）。</summary>
    private void ApplyLocalArchived(IReadOnlyList<string> archivedSessionIds)
    {
        bool changed;
        lock (_sync)
        {
            var next = archivedSessionIds.ToHashSet();
            changed             = !next.SetEquals(_archivedSessionIds);
            _archivedSessionIds = next;
        }

        if (changed) RaiseWorkspacesChanged();
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

                return items.OrderBy(item => rank.GetValueOrDefault(item.Id, int.MaxValue))
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
            await foreach (var frame in connection.FollowWorkspacesAsync(cancellationToken).ConfigureAwait(false))
            {
                bool changed;
                lock (_sync)
                {
                    switch (frame)
                    {
                        case WorkspaceFollowFrame.Baseline baseline :
                        {
                            // 基线除工作区条目外还携带 registry 级归档全量集合
                            // （对照 feed.ts baseline()）：缺它冷启动会把已归档会话当正常行
                            // 展示（点归档被后端 gate 拒绝）。Baseline 无条件通知：
                            // 它标记一代投影就绪（即使内容为空）。
                            _archivedSessionIds = baseline.ArchivedSessionIds.ToHashSet();
                            _items              = ApplyFrame(_items, baseline);
                            changed             = true;
                            break;
                        }
                        case WorkspaceFollowFrame.Archived archived :
                        {
                            // 归档集合是 registry 级投影：全量替换，变化即通知。
                            var nextArchived = archived.ArchivedSessionIds.ToHashSet();
                            changed             = !nextArchived.SetEquals(_archivedSessionIds);
                            _archivedSessionIds = nextArchived;
                            break;
                        }
                        default :
                        {
                            var next = ApplyFrame(_items, frame);
                            changed = !ReferenceEquals(next, _items) && !SameItems(next, _items);
                            _items  = next;
                            break;
                        }
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

        return !left.Where((t, index) => t.Id != right[index].Id ||
                                         !t.SessionIds.SequenceEqual(right[index].SessionIds) ||
                                         t.Title != right[index].Title).Any();
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
