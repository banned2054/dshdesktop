using System.Text.Json;

namespace DshDesktop.Harness.Models.Events;

/// <summary>workspace/follow 下行的线形工作区视图。</summary>
public sealed record WorkspaceViewWire(
    string                WorkspaceId,
    string                Path,
    string                Title,
    IReadOnlyList<string> SessionIds,
    DateTimeOffset        UpdatedAt);

/// <summary>
///     workspace/follow 的下行帧；每代第一帧必为 Baseline，之后为增量。
///     Baseline 的 value 除工作区条目外还携带 registry 级归档全量集合
///     （对照 workspace-controller feed.ts 的 baseline()，缺失即冷启动把已归档
///     会话当正常行展示）；Archived 增量帧同携全量集合。置顶集合（pinned 帧）
///     属上游协议但本端已改为自有置顶方案，不再消费。
/// </summary>
public abstract record WorkspaceFollowFrame
{
    public sealed record Baseline(IReadOnlyList<WorkspaceViewWire> Items, IReadOnlyList<string> ArchivedSessionIds)
        : WorkspaceFollowFrame;

    public sealed record Upsert(WorkspaceViewWire Workspace) : WorkspaceFollowFrame;

    public sealed record Removed(string WorkspaceId) : WorkspaceFollowFrame;

    public sealed record Reordered(IReadOnlyList<string> WorkspaceIds) : WorkspaceFollowFrame;

    public sealed record Archived(IReadOnlyList<string> ArchivedSessionIds) : WorkspaceFollowFrame;
}

/// <summary>workspace/follow 帧解析（对照 workspace-controller types.ts 的 WorkspaceFollowFrame）。</summary>
public static class WorkspaceFrameJson
{
    public static WorkspaceFollowFrame? Parse(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object            ||
            !element.TryGetProperty("type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String)
            return null;

        switch (typeElement.GetString())
        {
            case "baseline" :
            {
                var items       = new List<WorkspaceViewWire>();
                var archivedIds = new List<string>();
                if (!element.TryGetProperty("value", out var valueElement) ||
                    valueElement.ValueKind != JsonValueKind.Object)
                    return new WorkspaceFollowFrame.Baseline(items, archivedIds);

                if (valueElement.TryGetProperty("items", out var itemsElement) &&
                    itemsElement.ValueKind == JsonValueKind.Array)
                    foreach (var item in itemsElement.EnumerateArray())
                        if (ParseView(item) is { } view)
                            items.Add(view);

                archivedIds = ParseIdArray(valueElement, "archivedSessionIds");

                return new WorkspaceFollowFrame.Baseline(items, archivedIds);
            }

            case "upsert" :
            {
                return element.TryGetProperty("workspace", out var workspaceElement) &&
                       ParseView(workspaceElement) is { } workspace
                    ? new WorkspaceFollowFrame.Upsert(workspace)
                    : null;
            }

            case "remove" :
            {
                return element.TryGetProperty("workspaceId", out var idElement) &&
                       idElement.ValueKind == JsonValueKind.String              &&
                       idElement.GetString() is { Length: > 0 } removedId
                    ? new WorkspaceFollowFrame.Removed(removedId)
                    : null;
            }

            case "order" :
            {
                var ids = new List<string>();
                if (!element.TryGetProperty("workspaceIds", out var idsElement) ||
                    idsElement.ValueKind != JsonValueKind.Array)
                    return new WorkspaceFollowFrame.Reordered(ids);
                foreach (var id in idsElement.EnumerateArray())
                    if (id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } parsed)
                        ids.Add(parsed);

                return new WorkspaceFollowFrame.Reordered(ids);
            }

            case "archived" :
                return new WorkspaceFollowFrame.Archived(ParseIdArray(element, "archivedSessionIds"));

            default :
                return null;
        }
    }

    /// <summary>解析字符串 id 数组属性（缺属性或非数组返回空集合，非空字符串才收录）。</summary>
    private static List<string> ParseIdArray(JsonElement parent, string propertyName)
    {
        var ids = new List<string>();
        if (!parent.TryGetProperty(propertyName, out var arrayElement) ||
            arrayElement.ValueKind != JsonValueKind.Array)
            return ids;

        foreach (var item in arrayElement.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } id)
                ids.Add(id);

        return ids;
    }

    private static WorkspaceViewWire? ParseView(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object                 ||
            !element.TryGetProperty("workspaceId", out var idElement) ||
            idElement.ValueKind != JsonValueKind.String               ||
            idElement.GetString() is not { Length: > 0 } id)
            return null;

        var path = element.TryGetProperty("path", out var pathElement) && pathElement.ValueKind == JsonValueKind.String
            ? pathElement.GetString() ?? string.Empty
            : string.Empty;
        var title = element.TryGetProperty("title", out var titleElement) &&
                    titleElement.ValueKind == JsonValueKind.String
            ? titleElement.GetString() ?? string.Empty
            : string.Empty;
        var sessionIds = new List<string>();
        if (element.TryGetProperty("sessionIds", out var sessionsElement) &&
            sessionsElement.ValueKind == JsonValueKind.Array)
            foreach (var session in sessionsElement.EnumerateArray())
                if (session.ValueKind == JsonValueKind.String && session.GetString() is { Length: > 0 } sessionId)
                    sessionIds.Add(sessionId);

        var updatedAt = element.TryGetProperty("updatedAt", out var updatedElement) &&
                        updatedElement.ValueKind == JsonValueKind.String            &&
                        DateTimeOffset.TryParse(updatedElement.GetString(), out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

        return new WorkspaceViewWire(id, path, title, sessionIds, updatedAt);
    }
}
