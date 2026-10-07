using DshDesktop.Core.Models;
using DshDesktop.Core.Services;
using System.Text.Json;

namespace DshDesktop.Services.Conversations;

/// <summary>交付文件面板的数据形态。</summary>
internal enum DeliverableViewKind
{
    /// <summary>同会话同轮 workspace/changes 摘要中的完整文件差异。</summary>
    FullDiff,

    /// <summary>已加载工具记录中已核实的历史编辑片段。</summary>
    Fragments,

    /// <summary>当前磁盘上的文件内容（非交付时快照）。</summary>
    CurrentText,

    /// <summary>没有可显示的数据；原因见 <see cref="DeliverableView.NoticeText" />。</summary>
    Unavailable
}

/// <summary>交付文件解析结果；面板显示当前文件内容或不可用状态。</summary>
internal sealed record DeliverableView(
    DeliverableViewKind Kind,
    string              SourceText,
    string              NoticeText,
    WorkspaceFileDiff? Diff,
    IReadOnlyList<DeliverableFragment> Fragments,
    string?             CurrentText,
    string              AbsolutePath,
    bool                FileExists);

internal sealed record DeliverableFragment(
    long Seq, string Kind, string? OldText, string NewText, string? Note);

/// <summary>
///     把一次交付文件单击解析为面板数据。当前文件可读时始终显示普通文本，不将其渲染为 diff；
///     文件缺失时才回退到已核实的历史编辑片段或不可用说明。
/// </summary>
internal static class DeliverableViewResolver
{
    /// <summary>应用内展示当前内容的大小上限；超限时引导「用默认程序打开」。</summary>
    public const long MaxCurrentFileBytes = 1024 * 1024;

    public static async Task<DeliverableView> ResolveAsync(
        string sessionId, long turn, string declaredPath, string? cwd,
        IReadOnlyList<ConversationEntry> entries,
        IWorkspaceChangesService? changesService,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var absolutePath = WorkspacePathResolver.TryResolve(declaredPath, cwd, out var resolveError);
        if (absolutePath is null)
            return new(DeliverableViewKind.Unavailable, "无法定位文件", resolveError,
                       null, [], null, declaredPath, false);

        cancellationToken.ThrowIfCancellationRequested();

        // 可读取时始终显示当前内容，不将工作区摘要或工具片段渲染成交付文件的差异。
        var fileExists = File.Exists(absolutePath);
        if (fileExists)
        {
            var (text, reason) = ReadCurrentText(absolutePath);
            if (text is not null)
                return new(DeliverableViewKind.CurrentText, "当前文件内容",
                           "会话记录未保留交付时的文件内容；以下为当前磁盘上的最新内容，可能与交付时不同。",
                           null, [], text, absolutePath, true);

            return new(DeliverableViewKind.Unavailable, "无法显示内容", reason,
                       null, [], null, absolutePath, true);
        }

        // 文件缺失时保留可核实的历史 edit/write 片段；不从当前状态反推旧内容。
        var fragments = ExtractFragments(entries, turn, absolutePath, cwd);
        if (fragments.Count > 0)
        {
            var callCount = fragments.Select(fragment => fragment.Seq).Distinct().Count();
            return new(DeliverableViewKind.Fragments, $"历史编辑片段（{callCount} 次编辑记录，按事件顺序）",
                       "当前文件不可用；以下片段来自已成功完成的编辑调用，行号为片段内行号，不代表原文件位置。",
                       null, fragments, null, absolutePath, false);
        }

        return new(DeliverableViewKind.Unavailable, "无法显示文件",
                   "文件缺失或当前不可访问；会话记录只保存文件声明，没有可显示的交付时内容。",
                   null, [], null, absolutePath, false);
    }


    private static List<DeliverableFragment> ExtractFragments(
        IReadOnlyList<ConversationEntry> entries, long turn, string absolutePath, string? cwd)
    {
        var fragments = new List<DeliverableFragment>();
        foreach (var entry in entries)
        {
            if (entry is not ToolActivity tool || tool.Status != ToolActivityStatus.Succeeded ||
                tool.Turn != turn || tool.Name is not ("edit" or "write"))
                continue;

            using var arguments = TryParseArguments(tool.ArgumentsJson);
            if (arguments is null) continue;
            var root = arguments.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !TryGetStringProperty(root, "file_path", out var filePath))
                continue;

            var resolved = WorkspacePathResolver.TryResolve(filePath, cwd, out _);
            if (resolved is null || !WorkspacePathResolver.SamePath(resolved, absolutePath)) continue;

            if (tool.MetaDiffs is { Count: > 0 })
            {
                foreach (var diff in tool.MetaDiffs)
                {
                    var diffPath = WorkspacePathResolver.TryResolve(diff.Path, cwd, out _);
                    if (diffPath is not null && WorkspacePathResolver.SamePath(diffPath, absolutePath))
                        fragments.Add(new(tool.Seq, tool.Name, diff.OldText, diff.NewText, null));
                }

                continue;
            }

            if (tool.Name == "edit")
            {
                if (tool.MetaDiffs is not null ||
                    !TryGetStringProperty(root, "old_string", out var oldText) ||
                    !TryGetStringProperty(root, "new_string", out var newText))
                    continue;

                var note = root.TryGetProperty("replace_all", out var replaceAll) &&
                           replaceAll.ValueKind == JsonValueKind.True
                    ? "replace_all：替换确实发生，但记录未保留匹配次数与位置。"
                    : null;
                fragments.Add(new(tool.Seq, tool.Name, oldText, newText, note));
            }
            else if (tool.ResultText?.Contains("Created file", StringComparison.Ordinal) == true &&
                     TryGetStringProperty(root, "content", out var content))
            {
                fragments.Add(new(tool.Seq, tool.Name, string.Empty, content, "新建文件：以下为交付时写入的完整内容。"));
            }
        }

        return fragments;
    }

    private static JsonDocument? TryParseArguments(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return null;
        try { return JsonDocument.Parse(argumentsJson); }
        catch (JsonException) { return null; }
    }

    private static bool TryGetStringProperty(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private static (string? Text, string Reason) ReadCurrentText(string absolutePath)
    {
        try
        {
            using var stream = new FileStream(absolutePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxCurrentFileBytes)
                return (null, $"文件超过 {MaxCurrentFileBytes / 1024 / 1024} MB，应用内不显示全文；可用下方按钮在默认程序中打开。");

            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            var text = LocalWorkspaceChangesService.Decode(bytes);
            return text is null
                ? (null, "文件是二进制内容或使用了不受支持的文本编码，应用内无法显示；可用下方按钮在默认程序中打开。")
                : (text, string.Empty);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          System.Security.SecurityException)
        {
            return (null, $"无法读取当前文件：{exception.Message}");
        }
    }
}

/// <summary>
///     交付/改动路径的解析与比较：相对路径按会话 cwd 解析，绝对路径原样规范化；
///     路径比较在 Windows 不区分大小写，其他平台区分（与本地快照采集同一口径）。
/// </summary>
internal static class WorkspacePathResolver
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string? TryResolve(string path, string? cwd, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "交付文件路径无效或不可解析。";
            return null;
        }

        try
        {
            if (Path.IsPathFullyQualified(path)) return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            error = "交付文件路径无效或不可解析。";
            return null;
        }

        if (string.IsNullOrWhiteSpace(cwd))
        {
            error = "缺少会话工作目录，无法定位此交付文件。";
            return null;
        }

        try
        {
            return Path.GetFullPath(path, cwd);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            error = "交付文件路径无效或不可解析。";
            return null;
        }
    }

    /// <summary>比较两个已解析的绝对路径；不处理符号链接别名（与快照采集口径一致）。</summary>
    public static bool SamePath(string left, string right)
    {
        try
        {
            return PathComparer.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }
}
