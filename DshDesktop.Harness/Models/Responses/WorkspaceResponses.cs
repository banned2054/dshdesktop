using DshDesktop.Harness.Models.Events;

namespace DshDesktop.Harness.Models.Responses;

/// <summary>workspace/create 返回值：创建或幂等解析出的工作区行与是否首次创建。</summary>
public sealed record WorkspaceCreateValue(WorkspaceViewWire Workspace, bool Created);

/// <summary>workspace/archiveSession 返回值：变更后的完整归档集合。</summary>
public sealed record WorkspaceArchiveValue(IReadOnlyList<string> ArchivedSessionIds);
