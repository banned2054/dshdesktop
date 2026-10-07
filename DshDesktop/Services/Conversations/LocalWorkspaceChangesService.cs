using DshDesktop.Core.Models;
using DshDesktop.Core.Services;
using System.Diagnostics;
using System.Text;

namespace DshDesktop.Services.Conversations;

/// <summary>负 seq 读取有界本地缓存，Host seq 交给可选的 changes 服务。</summary>
internal sealed class LocalWorkspaceChangesService(IWorkspaceChangesService? fallback = null) : IWorkspaceChangesService
{
    private const int MaxFileBytes = 1024 * 1024;
    private const int MaxSnapshotBytes = 16 * 1024 * 1024;
    private const long MaxStoredBytes = 64 * 1024 * 1024;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly HashSet<string> ExcludedDirectories =
        new([".git", "node_modules", "bin", "obj"], StringComparer.OrdinalIgnoreCase);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Dictionary<(string Session, long Turn), StoredTurn> _turns = [];
    private readonly Lock _gate = new();
    private long _nextSeq;
    private long _storedBytes;

    internal sealed record Snapshot(string Root, Dictionary<string, byte[]> Files, HashSet<string> Skipped);
    private sealed record StoredTurn(WorkspaceChangesAnnouncement Announcement, long EndSeq,
        WorkspaceChangesSummary Summary, IReadOnlyList<WorkspaceFileDiff> Diffs, long Bytes);

    internal static bool SameRoot(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right) ||
            !Path.IsPathFullyQualified(left) || !Path.IsPathFullyQualified(right)) return false;
        try
        {
            return PathComparer.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or
                                          IOException or System.Security.SecurityException)
        {
            return false;
        }
    }

    // 全局超限放弃整轮；单个不可读路径在两侧都排除，不能误报新增/删除。
    internal static Snapshot? Capture(string? cwd, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cwd) || !Path.IsPathFullyQualified(cwd)) return null;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd));
            for (var ancestor = new DirectoryInfo(root); ancestor is not null; ancestor = ancestor.Parent)
                if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0) return null;

            var files = new Dictionary<string, byte[]>(PathComparer);
            var skipped = new HashSet<string>(PathComparer);
            var directories = new Stack<string>();
            directories.Push(root);
            var clock = Stopwatch.StartNew();
            var count = 0;
            var totalBytes = 0;
            while (directories.TryPop(out var directory))
            {
                try
                {
                    foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (++count > 8000 || clock.Elapsed > TimeSpan.FromSeconds(2)) return null;
                        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal)) return null;
                        try
                        {
                            var attributes = File.GetAttributes(path);
                            if ((attributes & FileAttributes.ReparsePoint) != 0)
                            {
                                // 若另一侧原本是普通文件/目录，同样不推断其中的删除。
                                skipped.Add(relative);
                                continue;
                            }
                            if ((attributes & FileAttributes.Directory) != 0)
                            {
                                if (!ExcludedDirectories.Contains(Path.GetFileName(path))) directories.Push(path);
                                continue;
                            }
                            if (files.Count + skipped.Count >= 4000) return null;
                            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                FileShare.ReadWrite | FileShare.Delete);
                            if (stream.Length > MaxFileBytes)
                            {
                                skipped.Add(relative);
                                continue;
                            }
                            var length = (int)stream.Length;
                            if (totalBytes + length > MaxSnapshotBytes) return null;
                            var bytes = new byte[length];
                            stream.ReadExactly(bytes);
                            if (stream.ReadByte() != -1)
                            {
                                skipped.Add(relative);
                                continue;
                            }
                            totalBytes += length;
                            files.Add(relative, bytes);
                        }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                          System.Security.SecurityException)
                        {
                            skipped.Add(relative);
                        }
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                  System.Security.SecurityException)
                {
                    if (PathComparer.Equals(directory, root)) return null;
                    skipped.Add(Path.GetRelativePath(root, directory).Replace('\\', '/'));
                }
            }
            return new Snapshot(root, files, skipped);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    internal WorkspaceChangesAnnouncement? Complete(string sessionId, long turn, long endSeq,
        Snapshot before, Snapshot after, CancellationToken cancellationToken)
    {
        if (!PathComparer.Equals(before.Root, after.Root)) return null;
        var infos = new List<WorkspaceChangedFileInfo>();
        var diffs = new List<WorkspaceFileDiff>();
        long size = 0;
        var workBudget = 4_000_000L;
        var clock = Stopwatch.StartNew();
        var skipped = before.Skipped.Concat(after.Skipped).ToArray();
        foreach (var path in before.Files.Keys.Union(after.Files.Keys, PathComparer).Order(PathComparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (clock.Elapsed > TimeSpan.FromSeconds(2)) return null;
            if (skipped.Any(item => PathComparer.Equals(item, path) ||
                path.StartsWith(item + "/", OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))) continue;
            var existedBefore = before.Files.TryGetValue(path, out var oldBytes);
            var existedAfter = after.Files.TryGetValue(path, out var newBytes);
            if (existedBefore == existedAfter && oldBytes!.AsSpan().SequenceEqual(newBytes)) continue;
            var oldText = Decode(oldBytes);
            var newText = Decode(newBytes);
            var binary = oldText is null || newText is null;
            var coarse = false;
            IReadOnlyList<WorkspaceDiffHunk> hunks = binary ? [] :
                CreateHunks(oldText!, newText!, ref workBudget, out coarse);
            // 空文本的创建/删除仍是文件差异；纯编码变化可没有行级差异。
            long added = hunks.Sum(h => (long)h.Lines.Count(line => line.StartsWith('+')));
            long deleted = hunks.Sum(h => (long)h.Lines.Count(line => line.StartsWith('-')));
            infos.Add(new(path, path, added, deleted, binary, false));
            diffs.Add(new(binary ? WorkspaceDiffKind.Binary : WorkspaceDiffKind.Text, path, path,
                existedBefore, existedAfter, hunks, coarse));
            size += (oldBytes?.Length ?? 0) + (newBytes?.Length ?? 0) +
                    hunks.Sum(h => h.Lines.Sum(line => (long)line.Length * 2));
            if (size > MaxStoredBytes) return null;
        }
        if (infos.Count == 0) return null;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_turns.TryGetValue((sessionId, turn), out var existing)) return existing.Announcement;
            var announcement = new WorkspaceChangesAnnouncement(--_nextSeq, turn, DateTimeOffset.Now);
            var summary = new WorkspaceChangesSummary(turn, infos, infos.Count,
                infos.Sum(file => file.Added), infos.Sum(file => file.Deleted));
            _turns.Add((sessionId, turn), new(announcement, endSeq, summary, diffs, size));
            _storedBytes += size;
            while (_turns.Count > 128 || _storedBytes > MaxStoredBytes)
            {
                var oldest = _turns.First();
                _storedBytes -= oldest.Value.Bytes;
                _turns.Remove(oldest.Key);
            }
            return _turns.ContainsKey((sessionId, turn)) ? announcement : null;
        }
    }

    internal bool Contains(string sessionId, long seq)
    {
        lock (_gate) return _turns.Any(pair => pair.Key.Session == sessionId && pair.Value.Announcement.Seq == seq);
    }

    internal bool HasTurn(string sessionId, long turn)
    {
        lock (_gate) return _turns.ContainsKey((sessionId, turn));
    }

    internal bool HasFallback => fallback is not null;

    internal IReadOnlyList<(long EndSeq, WorkspaceChangesAnnouncement Announcement)> Announcements(
        string sessionId, long firstSeq, long cursor)
    {
        lock (_gate) return _turns.Where(pair => pair.Key.Session == sessionId &&
                pair.Value.EndSeq >= firstSeq && pair.Value.EndSeq <= cursor)
            .Select(pair => (pair.Value.EndSeq, pair.Value.Announcement)).OrderBy(item => item.EndSeq).ToArray();
    }

    public Task<WorkspaceChangesSummary?> GetSummaryAsync(string sessionId, long seq,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (seq >= 0) return fallback?.GetSummaryAsync(sessionId, seq, cancellationToken)
            ?? Task.FromResult<WorkspaceChangesSummary?>(null);
        lock (_gate) return Task.FromResult(_turns.FirstOrDefault(pair =>
            pair.Key.Session == sessionId && pair.Value.Announcement.Seq == seq).Value?.Summary);
    }

    public Task<WorkspaceFileDiff?> GetDiffAsync(string sessionId, long seq, int index,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (seq >= 0) return fallback?.GetDiffAsync(sessionId, seq, index, cancellationToken)
            ?? Task.FromResult<WorkspaceFileDiff?>(null);
        lock (_gate)
        {
            var value = _turns.FirstOrDefault(pair => pair.Key.Session == sessionId &&
                pair.Value.Announcement.Seq == seq).Value;
            return Task.FromResult(value is not null && index >= 0 && index < value.Diffs.Count
                ? value.Diffs[index] : null);
        }
    }

    // 供交付文件面板的当前内容读取复用：UTF-8/UTF-16 BOM 文本可显示；不猜测无 BOM 的传统编码。
    internal static string? Decode(byte[]? bytes)
    {
        if (bytes is null) return string.Empty;
        try
        {
            // UTF-8/UTF-16 BOM 文本可显示；不猜测无 BOM 的传统编码。
            string text;
            if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }))
                text = new UnicodeEncoding(false, true, true).GetString(bytes, 2, bytes.Length - 2);
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
                text = new UnicodeEncoding(true, true, true).GetString(bytes, 2, bytes.Length - 2);
            else
                text = StrictUtf8.GetString(bytes).TrimStart('\uFEFF');
            return text.Contains('\0') ? null : text;
        }
        catch (DecoderFallbackException) { return null; }
    }

    private static string[] SplitLines(string text)
    {
        if (text.Length == 0) return [];
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return text.EndsWith('\n') ? lines[..^1] : lines;
    }

    // 供交付文件片段的片段内行级对比复用（含 coarse 预算口径）。
    internal static IReadOnlyList<WorkspaceDiffHunk> CreateHunks(string oldText, string newText,
        ref long workBudget, out bool coarse)
    {
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);
        var prefix = 0;
        while (prefix < oldLines.Length && prefix < newLines.Length && oldLines[prefix] == newLines[prefix]) prefix++;
        var suffix = 0;
        while (suffix < oldLines.Length - prefix && suffix < newLines.Length - prefix &&
               oldLines[^(suffix + 1)] == newLines[^(suffix + 1)]) suffix++;
        var n = oldLines.Length - prefix - suffix;
        var m = newLines.Length - prefix - suffix;
        var work = (long)(n + 1) * (m + 1);
        coarse = work > 1_000_000 || work > workBudget;
        if (!coarse) workBudget -= work;
        if (n == 0 && m == 0) return [];
        var lines = new List<string>();
        var contextBefore = Math.Min(3, prefix);
        var contextAfter = Math.Min(3, suffix);
        for (var k = prefix - contextBefore; k < prefix; k++) lines.Add(" " + oldLines[k]);
        if (coarse)
        {
            for (var k = 0; k < n; k++) lines.Add("-" + oldLines[prefix + k]);
            for (var k = 0; k < m; k++) lines.Add("+" + newLines[prefix + k]);
        }
        else
        {
            var lcs = new int[n + 1, m + 1];
            for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                lcs[i, j] = oldLines[prefix + i] == newLines[prefix + j]
                    ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            var iOld = 0;
            var iNew = 0;
            while (iOld < n || iNew < m)
            {
                if (iOld < n && iNew < m && oldLines[prefix + iOld] == newLines[prefix + iNew])
                {
                    lines.Add(" " + oldLines[prefix + iOld++]);
                    iNew++;
                }
                else if (iNew < m && (iOld == n || lcs[iOld, iNew + 1] >= lcs[iOld + 1, iNew]))
                    lines.Add("+" + newLines[prefix + iNew++]);
                else lines.Add("-" + oldLines[prefix + iOld++]);
            }
        }
        for (var k = oldLines.Length - suffix; k < oldLines.Length - suffix + contextAfter; k++)
            lines.Add(" " + oldLines[k]);
        var oldCount = n + contextBefore + contextAfter;
        var newCount = m + contextBefore + contextAfter;
        return [new WorkspaceDiffHunk(oldCount == 0 ? prefix : prefix - contextBefore + 1, oldCount,
            newCount == 0 ? prefix : prefix - contextBefore + 1, newCount, lines)];
    }
}
