using DshDesktop.Core.Models;
using DshDesktop.Core.Services;

namespace DshDesktop.Infrastructure.Services;

/// <summary>
///     模拟文件改动服务：对任意定位键返回固定种子数据，仅用于 simulated profile
///     演示接口形状；索引越界返回 null，与真实后端 404 的归一语义对齐。
/// </summary>
public sealed class SimulatedWorkspaceChangesService : IWorkspaceChangesService
{
    private static readonly WorkspaceChangesSummary SeedSummary =
        new(3, [
            new WorkspaceChangedFileInfo("src/app/view-model.ts", "src/app/view-model.ts", 24, 6, false, false),
            new WorkspaceChangedFileInfo("assets/logo.bin", "assets/logo.bin", 0, 0, true, false)
        ], 2, 24, 6);

    /// <summary>交付演示会话（session-deliverables）中同轮 changes 宣告的 seq，
    ///     演示摘要与其对应；SimulatedSessionService 的演示条目引用此常量。</summary>
    public const long DeliverablesDemoChangesSeq = 8;

    /// <summary>交付演示轮的摘要：alpha/beta 与声明路径吻合提供计数，today/gone 不在
    ///     快照里，演示「有计数 / 无计数 / 缺失文件」三种行形态。</summary>
    private static readonly WorkspaceChangesSummary DeliverablesDemoSummary =
        new(1, [
            new WorkspaceChangedFileInfo("notes-alpha.md", "notes-alpha.md", 4, 2, false, false),
            new WorkspaceChangedFileInfo("notes-beta.md", "notes-beta.md", 2, 0, false, false)
        ], 2, 6, 2);

    public Task<WorkspaceChangesSummary?> GetSummaryAsync(
        string sessionId, long seq, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<WorkspaceChangesSummary?>(
            seq == DeliverablesDemoChangesSeq ? DeliverablesDemoSummary : SeedSummary);
    }

    public Task<WorkspaceFileDiff?> GetDiffAsync(
        string sessionId, long seq, int index, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diff = index switch
        {
            0 => new WorkspaceFileDiff(WorkspaceDiffKind.Text, SeedSummary.Files[0].Path, SeedSummary.Files[0].Display,
                                       true, true,
                                       [
                                           new WorkspaceDiffHunk(12, 3, 12, 6,
                                           [
                                               "  const title = resolve();",
                                               "-  return baseTitle;",
                                               "+  if (isPinned) {",
                                               "+    return pinnedTitle;",
                                               "+  }",
                                               "+  return baseTitle;",
                                               " }"
                                           ])
                                       ], false),
            1 => new WorkspaceFileDiff(WorkspaceDiffKind.Binary, SeedSummary.Files[1].Path,
                                       SeedSummary.Files[1].Display, true, true, [], false),
            _ => null
        };
        return Task.FromResult(diff);
    }
}
