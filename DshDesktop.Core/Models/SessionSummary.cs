namespace DshDesktop.Core.Models;

/// <summary>会话在列表中的概要；权威状态由 Harness 后端持有。</summary>
public sealed record SessionSummary(
    string            Id,
    string?           Title,
    DateTimeOffset    UpdatedAt,
    bool              Running,
    SessionBlankState BlankState,
    string?           Cwd = null);
