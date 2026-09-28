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

    public event EventHandler? WorkspacesChanged;

    public Task<IReadOnlyList<WorkspaceSummary>> GetWorkspacesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            IReadOnlyList<WorkspaceSummary> snapshot = _workspaces.ToArray();
            return Task.FromResult(snapshot);
        }
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
}
