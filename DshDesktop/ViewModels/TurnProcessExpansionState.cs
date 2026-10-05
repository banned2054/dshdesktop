namespace DshDesktop.ViewModels;

/// <summary>
///     过程组可见窗口的本地 UI 状态：按会话 + turn 身份隔离，只记忆已加载条目中的
///     最早可见锚点与展开态。它不是后端状态，也不代表未加载历史已经完整。
/// </summary>
internal sealed class TurnProcessExpansionState
{
    /// <summary>当前可见窗口最早条目的 Seq。</summary>
    public long? EarliestVisibleSeq { get; set; }

    /// <summary>是否仍跟随最新窗口；用户展开更早过程后固定为 false。</summary>
    public bool IsFollowingLatest { get; set; } = true;

    /// <summary>过程组是否展开；运行中的新组默认展开，重建后按本值恢复。</summary>
    public bool IsExpanded { get; set; }

    /// <summary>是否已记录明确的展开态；用于区分新状态默认值和已确认状态。</summary>
    public bool HasRecordedExpansion { get; set; }

    /// <summary>是否由用户主动展开/折叠过；完成事件不能覆盖用户选择。</summary>
    public bool HasUserSetExpansion { get; set; }

    /// <summary>true 表示重建尚未完成，旧锚点未到达前不得被当前尾部窗口覆盖。</summary>
    public bool IsRestorePending { get; set; }
}
