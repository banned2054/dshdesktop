namespace DshDesktop.Harness.Models.Responses;

/// <summary>GET /api/changes.summary 返回值（线上形态）；Host 保留 cwd 与 snapshot id 不下发。</summary>
public sealed record WorkspaceChangesSummaryWire(
    long                                    Turn,
    IReadOnlyList<WorkspaceChangedFileWire> Files,
    long                                    Total,
    long                                    Added,
    long                                    Deleted);

/// <summary>单个改动文件条目（线上形态）；binary/oversized 二选一出现，此时行数为 0。</summary>
public sealed record WorkspaceChangedFileWire(
    string Path,
    string Display,
    long   Added,
    long   Deleted,
    bool?  Binary    = null,
    bool?  Oversized = null);

/// <summary>unified diff hunk（线上形态）；行保留 +/-/空格前缀。</summary>
public sealed record WorkspaceDiffHunkWire(
    int                   OldStart,
    int                   OldLines,
    int                   NewStart,
    int                   NewLines,
    IReadOnlyList<string> Lines);

/// <summary>
///     GET /api/changes.diff 返回值（线上形态）。kind 为 text 时携带
///     before/after/hunks/coarse；binary/oversized 只带路径信息。
/// </summary>
public sealed record WorkspaceFileDiffWire(
    string                                Kind,
    string                                Path,
    string                                Display,
    bool?                                 Before = null,
    bool?                                 After  = null,
    IReadOnlyList<WorkspaceDiffHunkWire>? Hunks  = null,
    bool?                                 Coarse = null);
