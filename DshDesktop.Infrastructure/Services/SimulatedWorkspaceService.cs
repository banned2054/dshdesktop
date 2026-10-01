using DshDesktop.Core.Models;
using DshDesktop.Core.Services;

namespace DshDesktop.Infrastructure.Services;

/// <summary>模拟工作区服务：登记两个演示工作区并同步模拟的新会话归属。</summary>
public sealed class SimulatedWorkspaceService : IWorkspaceService
{
    private readonly Lock _syncRoot = new();

    private readonly List<WorkspaceSummary> _workspaces =
    [
        new("workspace-sample", "示例工作区", "C:/Code/Sample", ["session-native", "session-history"],
            DateTimeOffset.Now.AddHours(-2)),
        new("workspace-docs", "文档整理", "C:/Code/Docs", [], DateTimeOffset.Now.AddHours(-3))
    ];

    public IReadOnlySet<string> ArchivedSessionIds { get; private set; } = new HashSet<string>();
    public event EventHandler?  WorkspacesChanged;

    public Task<IReadOnlyList<WorkspaceSummary>> GetWorkspacesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            IReadOnlyList<WorkspaceSummary> snapshot = _workspaces.ToArray();
            return Task.FromResult(snapshot);
        }
    }

    /// <summary>模拟 workspace/create：按路径幂等去重，新行插头部（对齐后端 prepend）并广播变化。</summary>
    public Task<WorkspaceSummary> RegisterWorkspaceAsync(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = path.Trim();

        WorkspaceSummary workspace;
        lock (_syncRoot)
        {
            var existing = _workspaces.FirstOrDefault(candidate =>
                                                          string.Equals(candidate.Path, normalized,
                                                                        StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return Task.FromResult(existing);

            var title = System.IO.Path.GetFileName(normalized.TrimEnd('/', '\\'));
            workspace = new WorkspaceSummary($"workspace-{Guid.NewGuid():N}",
                                             string.IsNullOrEmpty(title) ? normalized : title,
                                             normalized, [], DateTimeOffset.Now);
            _workspaces.Insert(0, workspace);
        }

        WorkspacesChanged?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(workspace);
    }

    /// <summary>模拟 session/create 的后端副作用：把新会话记入工作区并广播投影变化。</summary>
    public void AddSession(string workspaceId, string sessionId)
    {
        var changed = false;
        lock (_syncRoot)
        {
            var index = _workspaces.FindIndex(workspace => workspace.Id == workspaceId);
            if (index >= 0 && !_workspaces[index].SessionIds.Contains(sessionId))
            {
                var workspace = _workspaces[index];
                _workspaces[index] = workspace with
                {
                    SessionIds = [..workspace.SessionIds, sessionId],
                    UpdatedAt = DateTimeOffset.Now
                };
                changed = true;
            }
        }

        if (changed) WorkspacesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>模拟 workspace/rename：按 id 重命名显示名并广播；工作区不存在对齐 not-found 业务错误。</summary>
    public Task<WorkspaceSummary> RenameWorkspaceAsync(
        string workspaceId, string title, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkspaceSummary workspace;
        lock (_syncRoot)
        {
            var index = _workspaces.FindIndex(candidate => candidate.Id == workspaceId);
            if (index < 0) throw new InvalidOperationException($"工作区不存在：{workspaceId}");

            workspace          = _workspaces[index] with { Title = title.Trim(), UpdatedAt = DateTimeOffset.Now };
            _workspaces[index] = workspace;
        }

        WorkspacesChanged?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(workspace);
    }

    /// <summary>模拟 workspace/delete：只删注册（会话记账不受影响，回流后落「未分组」）并广播。</summary>
    public Task DeleteWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool removed;
        lock (_syncRoot)
        {
            removed = _workspaces.RemoveAll(candidate => candidate.Id == workspaceId) > 0;
        }

        if (!removed) throw new InvalidOperationException($"工作区不存在：{workspaceId}");

        WorkspacesChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    /// <summary>模拟 workspace/archiveSession：并入归档集合并广播。</summary>
    public Task ArchiveSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            ArchivedSessionIds = new HashSet<string>(ArchivedSessionIds) { sessionId };
        }

        WorkspacesChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }
}
