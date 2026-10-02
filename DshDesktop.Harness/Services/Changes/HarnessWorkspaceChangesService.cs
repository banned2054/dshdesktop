using DshDesktop.Core.Models;
using DshDesktop.Core.Services;
using DshDesktop.Harness.Exceptions;
using DshDesktop.Harness.Json;
using DshDesktop.Harness.Models.Responses;
using DshDesktop.Harness.Services.Connection;

namespace DshDesktop.Harness.Services.Changes;

/// <summary>
///     经认证 GET 路由读取后端按轮次持有的文件改动（changes.summary/changes.diff）。
///     404 归一为 null：调用方不区分「会话已销毁」「Host 重启」与「索引越界」。
/// </summary>
public sealed class HarnessWorkspaceChangesService(HarnessConnection connection) : IWorkspaceChangesService
{
    public async Task<WorkspaceChangesSummary?> GetSummaryAsync(string sessionId, long seq,
                                                                CancellationToken cancellationToken = default)
    {
        var wire = await connection
                        .GetAsync("api/changes.summary",
                                  $"sessionId={Uri.EscapeDataString(sessionId)}&seq={seq}",
                                  HarnessJsonContext.Default.WorkspaceChangesSummaryWire,
                                  cancellationToken)
                        .ConfigureAwait(false);
        return wire is null ? null : ToSummary(wire);
    }

    public async Task<WorkspaceFileDiff?> GetDiffAsync(string sessionId, long seq, int index,
                                                       CancellationToken cancellationToken = default)
    {
        var wire = await connection
                        .GetAsync("api/changes.diff",
                                  $"sessionId={Uri.EscapeDataString(sessionId)}&seq={seq}&index={index}",
                                  HarnessJsonContext.Default.WorkspaceFileDiffWire,
                                  cancellationToken)
                        .ConfigureAwait(false);
        return wire is null ? null : ToDiff(wire);
    }

    internal static WorkspaceChangesSummary ToSummary(WorkspaceChangesSummaryWire wire)
    {
        // STJ 对缺失字段填 null，先补空集合并过滤空元素，保证应用模型不接收空引用。
        return new WorkspaceChangesSummary(wire.Turn,
                                           [.. (wire.Files ?? [])
                                               .Where(file => file is not null)
                                               .Select(ToFile)],
                                           wire.Total,
                                           wire.Added,
                                           wire.Deleted);
    }

    internal static WorkspaceFileDiff ToDiff(WorkspaceFileDiffWire wire)
    {
        var kind = wire.Kind switch
        {
            "text"      => WorkspaceDiffKind.Text,
            "binary"    => WorkspaceDiffKind.Binary,
            "oversized" => WorkspaceDiffKind.Oversized,
            _ => throw new HarnessConnectionException($"changes.diff 返回了未知形态 {wire.Kind}。")
        };
        return new WorkspaceFileDiff(kind,
                                     wire.Path,
                                     wire.Display,
                                     wire.Before  ?? false,
                                     wire.After   ?? false,
                                     wire.Hunks is null
                                         ? []
                                         : [.. wire.Hunks.Where(hunk => hunk is not null)
                                                         .Select(ToHunk)],
                                     wire.Coarse ?? false);
    }

    private static WorkspaceDiffHunk ToHunk(WorkspaceDiffHunkWire hunk)
    {
        return new WorkspaceDiffHunk(hunk.OldStart, hunk.OldLines, hunk.NewStart,
                                     hunk.NewLines, hunk.Lines ?? []);
    }

    private static WorkspaceChangedFileInfo ToFile(WorkspaceChangedFileWire wire)
    {
        return new WorkspaceChangedFileInfo(wire.Path,
                                            wire.Display,
                                            wire.Added,
                                            wire.Deleted,
                                            wire.Binary    == true,
                                            wire.Oversized == true);
    }
}
