namespace DshDesktop.Core.Models;

/// <summary>
///     deliverables/presented 声明的一个交付文件。声明只有路径与可选描述：
///     文件内容不随会话归档，也不提供新增/删除行数或修改前后 diff。
/// </summary>
/// <param name="Path">声明的路径，保留事件中的原始写法。</param>
/// <param name="Description">模型提供的可选描述；缺失为 null。</param>
/// <param name="Seq">所属 deliverables/presented 事件的 seq。</param>
/// <param name="Index">在该事件 files 数组中的原始下标（无效条目跳过时不重排）。</param>
public sealed record DeliveredFileDeclaration(string Path, string? Description, long Seq, int Index);

/// <summary>
///     一轮的交付文件声明（时间线条目）。同一轮可有多条声明事件，展示前须经
///     <see cref="TurnDeliverables.ForClosing" /> 合并；与 workspace/changes 摘要相互独立。
/// </summary>
public sealed record DeliverablesPresentedAnnouncement(
    long                                    Seq,
    long                                    Turn,
    DateTimeOffset                          CreatedAt,
    IReadOnlyList<DeliveredFileDeclaration> Files) : ConversationEntry(Seq, CreatedAt);

/// <summary>一轮交付文件声明的合并规则（对齐官方 presentedForClosing）。</summary>
public static class TurnDeliverables
{
    /// <summary>
    ///     合并一轮的原始声明：仅保留 seq 小于 closingSeq 的声明；同一路径保留
    ///     最后一次声明，顺序按路径首次出现排列。closingSeq 是该轮收束助手消息的 seq。
    /// </summary>
    public static IReadOnlyList<DeliveredFileDeclaration> ForClosing(
        IReadOnlyList<DeliveredFileDeclaration> declarations, long closingSeq)
    {
        var latestByPath = new Dictionary<string, DeliveredFileDeclaration>();
        var firstSeen    = new List<string>();
        foreach (var file in declarations)
        {
            if (file.Seq >= closingSeq) continue;
            if (!latestByPath.ContainsKey(file.Path)) firstSeen.Add(file.Path);
            latestByPath[file.Path] = file;
        }

        return firstSeen.Select(path => latestByPath[path]).ToList();
    }
}
