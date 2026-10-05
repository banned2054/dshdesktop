namespace DshDesktop.ViewModels;

/// <summary>
///     过程组折叠的本地 UI 状态：按会话 + turn 身份隔离，只记忆组级展开态。
///     它不是后端状态，也不代表未加载历史已经完整。
/// </summary>
internal sealed class TurnProcessExpansionState
{
    /// <summary>过程组是否展开；运行中的新组默认展开，重建后按本值恢复。</summary>
    public bool IsExpanded { get; set; }

    /// <summary>是否已记录明确的展开态；用于区分新状态默认值和已确认状态。</summary>
    public bool HasRecordedExpansion { get; set; }

    /// <summary>是否由用户主动展开/折叠过；完成事件不能覆盖用户选择。</summary>
    public bool HasUserSetExpansion { get; set; }
}
