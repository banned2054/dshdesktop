namespace DshDesktop.Core.Models;

/// <summary>一轮改动中的一个文件；行数来自 git 快照对比或工具调用前后的整文件捕获。</summary>
public sealed record WorkspaceChangedFileInfo(
    string Path,
    string Display,
    long   Added,
    long   Deleted,
    bool   IsBinary,
    bool   IsOversized);

/// <summary>
///     一轮会话改动的文件摘要，由 workspace/changes 事件按 seq 宣告。
///     内容由后端持有到会话销毁为止，不持久化。
/// </summary>
public sealed record WorkspaceChangesSummary(
    long                                    Turn,
    IReadOnlyList<WorkspaceChangedFileInfo> Files,
    long                                    Total,
    long                                    Added,
    long                                    Deleted);

/// <summary>一个 unified diff hunk；行保留 +/-/空格前缀。</summary>
public sealed record WorkspaceDiffHunk(
    int                   OldStart,
    int                   OldLines,
    int                   NewStart,
    int                   NewLines,
    IReadOnlyList<string> Lines);

/// <summary>文件对比的形态：文本 hunk、二进制或超过后端大小上限。</summary>
public enum WorkspaceDiffKind
{
    Text,
    Binary,
    Oversized
}

/// <summary>单个文件轮首/轮末内容的对比；非文本形态不带 hunk。</summary>
public sealed record WorkspaceFileDiff(
    WorkspaceDiffKind                Kind,
    string                           Path,
    string                           Display,
    bool                             ExistedBefore,
    bool                             ExistedAfter,
    IReadOnlyList<WorkspaceDiffHunk> Hunks,
    bool                             IsCoarse);
