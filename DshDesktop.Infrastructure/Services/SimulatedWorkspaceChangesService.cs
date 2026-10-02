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

    public Task<WorkspaceChangesSummary?> GetSummaryAsync(
        string sessionId, long seq, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<WorkspaceChangesSummary?>(SeedSummary);
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
