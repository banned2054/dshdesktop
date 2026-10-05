using System.Collections.ObjectModel;

namespace DshDesktop.ViewModels;

/// <summary>
///     一轮对话的过程组（对应参考 Web 客户端的 turn-process 投影）：轮内最终回复之前的
///     中间助手消息与工具调用折叠为一行摘要（「N 次工具调用 · M 条消息 · K 个 subagent」，
///     全为零时显示「已思考」）。默认收起，可展开查看内部条目；子代理委派调用单独计数。
/// </summary>
public sealed class TurnProcessGroupViewModel : ConversationItemViewModel
{
    /// <summary>新过程组默认暴露的最近过程条目数。</summary>
    public const int InitialVisibleProcessCount = 20;

    /// <summary>每次展开更早过程时向前放行的条目数。</summary>
    public const int ProcessExpandStep = 20;

    private readonly TurnProcessExpansionState? _expansionState;
    private          long?                      _earliestVisibleSeq;

    private bool _isExpanded;
    private bool _isPartialTurn;
    private bool _isRestorePending;

    public TurnProcessGroupViewModel(long seq) : this(seq, null)
    {
    }

    internal TurnProcessGroupViewModel(
        long                       seq,
        long?                      turn           = null,
        TurnProcessExpansionState? expansionState = null,
        bool                       startExpanded  = false) : base(seq)
    {
        Turn                = turn;
        _expansionState     = expansionState;
        IsFollowingLatest   = _expansionState?.IsFollowingLatest ?? true;
        _earliestVisibleSeq = _expansionState?.EarliestVisibleSeq;
        _isRestorePending   = _expansionState?.IsRestorePending    ?? false;
        HasUserSetExpansion = _expansionState?.HasUserSetExpansion ?? false;
        _isExpanded = _expansionState is { HasRecordedExpansion: true }
            ? _expansionState.IsExpanded
            : startExpanded;
        if (_expansionState is not null && !_expansionState.HasRecordedExpansion)
        {
            _expansionState.IsExpanded           = _isExpanded;
            _expansionState.HasRecordedExpansion = true;
        }

        ToggleCommand = new RelayCommand(ToggleExpansionByUser);
        ShowEarlierProcessCommand =
            new RelayCommand(ShowEarlierProcess, () => HasEarlierProcess);
    }

    /// <summary>本组的 turn 身份；后端未提供时调用方不得把它当作完整轮次。</summary>
    internal long? Turn { get; }

    internal bool HasUserSetExpansion { get; private set; }

    internal bool IsFollowingLatest { get; private set; }

    /// <summary>该轮是否只覆盖已加载窗口的一部分（例如历史首页截断的首轮）。</summary>
    internal bool IsPartialTurn
    {
        get => _isPartialTurn;
        private set => SetProperty(ref _isPartialTurn, value);
    }

    /// <summary>过程条目的完整本地投影输入，按时间线顺序混排。</summary>
    public ObservableCollection<ConversationItemViewModel> Process { get; } = [];

    /// <summary>实际渲染的过程条目窗口：默认最近 N 条，展开更早后保留最早锚点。</summary>
    public ObservableCollection<ConversationItemViewModel> VisibleProcess { get; } = [];

    /// <summary>已加载但未渲染的过程条目数；不包含后端尚未返回的历史。</summary>
    public int HiddenProcessCount => Process.Count - VisibleProcess.Count;

    public bool HasEarlierProcess => HiddenProcessCount > 0;

    /// <summary>顶部展开文案；计数只描述本地已加载条目。</summary>
    public string HiddenProcessCountText => $"显示更早过程（{HiddenProcessCount}）";

    /// <summary>折叠态只保留轮组摘要；分页控件属于展开后的过程视图。</summary>
    public bool ShowEarlierProcessPrompt => IsExpanded && HasEarlierProcess;

    public RelayCommand ToggleCommand { get; }

    public RelayCommand ShowEarlierProcessCommand { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value)) return;
            PersistExpansionState();
            OnPropertyChanged(nameof(ShowEarlierProcessPrompt));
        }
    }

    /// <summary>轮内工具调用数（不含子代理委派）。</summary>
    public int ToolCallCount => Process.OfType<ToolActivityItemViewModel>()
                                       .Count(tool => !IsSubagentDelegationTool(tool.Name));

    /// <summary>轮内子代理委派调用数。</summary>
    public int SubagentCount => Process.OfType<ToolActivityItemViewModel>()
                                       .Count(tool => IsSubagentDelegationTool(tool.Name));

    /// <summary>轮内折叠的中间消息数（有正文的提交；思考-only 条目不算消息，与参考实现口径一致）。</summary>
    public int MessageCount => Process.OfType<MessageItemViewModel>()
                                      .Count(message => !string.IsNullOrWhiteSpace(message.Content));

    public bool HasFailed => Process.OfType<ToolActivityItemViewModel>().Any(tool => tool.IsFailed);

    public string FailedText => $"{Process.OfType<ToolActivityItemViewModel>().Count(tool => tool.IsFailed)} 失败";

    public string SummaryText
    {
        get
        {
            var labels = new List<string>();
            if (_isPartialTurn) labels.Add("已加载");
            if (ToolCallCount > 0) labels.Add($"{ToolCallCount} 次工具调用");

            if (MessageCount > 0) labels.Add($"{MessageCount} 条消息");

            if (SubagentCount > 0) labels.Add($"{SubagentCount} 个 subagent");

            return labels.Count == 0 ? "已思考" : string.Join(" · ", labels);
        }
    }

    /// <summary>子代理委派工具名（与参考实现 isSubagentDelegationTool 一致）。</summary>
    private static bool IsSubagentDelegationTool(string name)
    {
        return name == "subagent" || name.StartsWith("subagent_", StringComparison.Ordinal);
    }

    /// <summary>并入一个过程条目；计数经 Count 属性通知。</summary>
    public void Add(ConversationItemViewModel item)
    {
        Process.Add(item);
        AppendToVisibleProcess(item);
        NotifyWindowChanged();
        RefreshSummary();
    }

    /// <summary>结算最终回复时把它移出过程输入；最终回复始终独立于过程组显示。</summary>
    public bool Remove(ConversationItemViewModel item)
    {
        var index = Process.IndexOf(item);
        if (index < 0) return false;

        Process.RemoveAt(index);
        RefreshVisibleProcess();
        NotifyWindowChanged();
        RefreshSummary();
        return true;
    }

    /// <summary>每次向前展开一批已加载过程；展开后新事件不再移动最早锚点。</summary>
    public void ShowEarlierProcess()
    {
        if (!HasEarlierProcess) return;

        HasUserSetExpansion                  = true;
        _expansionState?.HasUserSetExpansion = true;
        IsExpanded                           = true;

        var firstVisibleIndex = VisibleProcess.Count == 0
            ? Process.Count
            : IndexOfProcess(VisibleProcess[0]);
        var hiddenCount = Math.Max(0, firstVisibleIndex);
        if (hiddenCount == 0) return;

        var newIndex = Math.Max(0, firstVisibleIndex - ProcessExpandStep);
        IsFollowingLatest   = false;
        _earliestVisibleSeq = Process[newIndex].Seq;
        if (_expansionState is not null)
        {
            _expansionState.IsFollowingLatest  = false;
            _expansionState.EarliestVisibleSeq = _earliestVisibleSeq;
        }

        RefreshVisibleProcess();
    }

    /// <summary>用户离开底部时固定当前可见窗口，不扩展窗口，也不淘汰正在阅读的条目。</summary>
    internal void PinVisibleWindow()
    {
        if (!IsFollowingLatest || VisibleProcess.Count == 0) return;

        IsFollowingLatest   = false;
        _earliestVisibleSeq = VisibleProcess[0].Seq;
        PersistVisibleAnchor();
    }

    /// <summary>用户回到底部时恢复默认尾窗；显式展开/折叠过的阅读状态保持不变。</summary>
    internal void ResumeLatestWindow()
    {
        if (IsFollowingLatest || HasUserSetExpansion) return;

        IsFollowingLatest   = true;
        _earliestVisibleSeq = null;
        RefreshVisibleProcess();
    }

    /// <summary>刷新派生的摘要展示。</summary>
    public void RefreshSummary()
    {
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(HasFailed));
        OnPropertyChanged(nameof(FailedText));
    }

    /// <summary>标记轮次只覆盖已加载窗口的一部分；摘要明示口径，不虚构完整轮。</summary>
    internal void MarkPartialTurn()
    {
        IsPartialTurn = true;
        OnPropertyChanged(nameof(SummaryText));
    }

    /// <summary>历史重建结束：解析仍缺失的旧锚点，此后才允许常规窗口状态持久化。</summary>
    internal void CompleteRestoredProjection()
    {
        if (!_isRestorePending) return;

        if (IsFollowingLatest)
        {
            _isRestorePending                 = false;
            _expansionState?.IsRestorePending = false;
            return;
        }

        // Resolve while the complete rebuilt turn is available. If the exact Seq is absent,
        // use the first later item or the normal tail window instead of indexing -1.
        _isRestorePending                 = false;
        _expansionState?.IsRestorePending = false;
        var resolvedIndex                    = ResolveRestoredAnchorIndex();
        if (resolvedIndex < 0) resolvedIndex = Math.Max(0, Process.Count - InitialVisibleProcessCount);
        _earliestVisibleSeq                 = Process.Count == 0 ? null : Process[resolvedIndex].Seq;
        _expansionState?.EarliestVisibleSeq = _earliestVisibleSeq;

        RefreshVisibleProcess();
    }

    /// <summary>按 CallId 查找组内工具卡片（落定结果就地合并）。</summary>
    public ToolActivityItemViewModel? FindTool(string callId)
    {
        return Process.OfType<ToolActivityItemViewModel>()
                      .FirstOrDefault(tool => tool.CallId == callId);
    }

    private void AppendToVisibleProcess(ConversationItemViewModel item)
    {
        if (IsFollowingLatest)
        {
            if (VisibleProcess.Count == InitialVisibleProcessCount) VisibleProcess.RemoveAt(0);

            VisibleProcess.Add(item);
            _earliestVisibleSeq = VisibleProcess[0].Seq;
            PersistVisibleAnchor();
            return;
        }

        var anchorIndex = FindDisplayAnchorIndex();
        if (VisibleProcess.Count > 0 &&
            ReferenceEquals(Process[Math.Min(anchorIndex, Process.Count - 1)], VisibleProcess[0]))
            VisibleProcess.Add(item);
        else
            RefreshVisibleProcess();

        PersistAnchorIfResolved();
    }

    private void RefreshVisibleProcess()
    {
        var anchorIndex = FindDisplayAnchorIndex();
        VisibleProcess.Clear();
        for (var index = anchorIndex; index < Process.Count; index++)
            VisibleProcess.Add(Process[index]);

        PersistAnchorIfResolved();
        NotifyWindowChanged();
    }

    private void NotifyWindowChanged()
    {
        OnPropertyChanged(nameof(HiddenProcessCount));
        OnPropertyChanged(nameof(HasEarlierProcess));
        OnPropertyChanged(nameof(HiddenProcessCountText));
        OnPropertyChanged(nameof(ShowEarlierProcessPrompt));
        ShowEarlierProcessCommand.RaiseCanExecuteChanged();
    }

    private int FindDisplayAnchorIndex()
    {
        if (IsFollowingLatest)
            return Math.Max(0, Process.Count - InitialVisibleProcessCount);

        var restored = ResolveRestoredAnchorIndex();
        if (restored >= 0) return restored;

        return Math.Max(0, Process.Count - InitialVisibleProcessCount);
    }

    private int ResolveRestoredAnchorIndex()
    {
        var anchorSeq = _expansionState?.EarliestVisibleSeq ?? _earliestVisibleSeq;
        if (anchorSeq is null) return -1;

        var exact = IndexOfProcess(item => item.Seq == anchorSeq.Value);
        if (exact >= 0) return exact;

        // 重建从更早条目开始时，先全部显示；原锚点到达后再收紧，中途不改持久锚点。
        if (_isRestorePending && Process.Count > 0 && Process[0].Seq < anchorSeq.Value)
            return 0;

        var nearest = IndexOfProcess(item => item.Seq > anchorSeq.Value);
        if (nearest >= 0) return nearest;

        return -1;
    }

    private void PersistAnchorIfResolved()
    {
        var anchorSeq = _expansionState?.EarliestVisibleSeq ?? _earliestVisibleSeq;
        if (!IsFollowingLatest && _isRestorePending &&
            (anchorSeq is null || IndexOfProcess(item => item.Seq == anchorSeq.Value) < 0))
            return;

        _earliestVisibleSeq = VisibleProcess.Count == 0 ? null : VisibleProcess[0].Seq;
        PersistVisibleAnchor();
    }

    private void PersistVisibleAnchor()
    {
        if (_expansionState is null) return;

        _expansionState.EarliestVisibleSeq = _earliestVisibleSeq;
        _expansionState.IsFollowingLatest  = IsFollowingLatest;
    }

    private void PersistExpansionState()
    {
        _expansionState?.IsExpanded = IsExpanded;
    }

    private void ToggleExpansionByUser()
    {
        HasUserSetExpansion                  = true;
        _expansionState?.HasUserSetExpansion = true;
        IsExpanded                           = !IsExpanded;
    }

    private int IndexOfProcess(ConversationItemViewModel item)
    {
        for (var index = 0; index < Process.Count; index++)
            if (ReferenceEquals(Process[index], item))
                return index;

        return -1;
    }

    private int IndexOfProcess(Func<ConversationItemViewModel, bool> predicate)
    {
        for (var index = 0; index < Process.Count; index++)
            if (predicate(Process[index]))
                return index;

        return -1;
    }
}
