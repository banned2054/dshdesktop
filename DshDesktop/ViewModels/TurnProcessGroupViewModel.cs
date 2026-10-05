using System.Collections.ObjectModel;

namespace DshDesktop.ViewModels;

/// <summary>
///     一轮对话的过程组（对应参考 Web 客户端的 turn-process 投影）：轮内最终回复之前的
///     中间助手消息与工具调用折叠为一行摘要（「N 次工具调用 · M 条消息 · K 个 subagent」，
///     全为零时显示「已思考」）。默认收起，可展开查看内部条目；子代理委派调用单独计数。
/// </summary>
public sealed class TurnProcessGroupViewModel : ConversationItemViewModel
{
    /// <summary>兼容常量；过程组现在展示所有已加载条目。</summary>
    public const int InitialVisibleProcessCount = 20;

    /// <summary>兼容常量；过程组内不再分页。</summary>
    public const int ProcessExpandStep = 20;

    private readonly TurnProcessExpansionState? _expansionState;

    private readonly Dictionary<ConversationItemViewModel, bool> _segmentExpansion = new();

    private bool _isExpanded;
    private bool _isPartialTurn;

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

    /// <summary>该轮是否只覆盖已加载窗口的一部分（例如历史首页截断的首轮）。</summary>
    internal bool IsPartialTurn
    {
        get => _isPartialTurn;
        private set => SetProperty(ref _isPartialTurn, value);
    }

    /// <summary>过程条目的完整本地投影输入，按时间线顺序混排。</summary>
    public ObservableCollection<ConversationItemViewModel> Process { get; } = [];

    /// <summary>与 Process 同步的当前已加载条目投影；不裁剪或分页。</summary>
    public ObservableCollection<ConversationItemViewModel> VisibleProcess { get; } = [];

    /// <summary>
    ///     当前已加载过程的段级投影：相邻思考/工具条目合并为段折叠，有正文的消息独立成行。
    /// </summary>
    public ObservableCollection<ConversationItemViewModel> SegmentedProcess { get; } = [];

    /// <summary>过程组内没有额外隐藏条目；计数仅针对当前已加载 Process。</summary>
    public int HiddenProcessCount => Process.Count - VisibleProcess.Count;

    public bool HasEarlierProcess => false;

    /// <summary>兼容属性：组内不再提供分页按钮。</summary>
    public string HiddenProcessCountText => $"显示更早过程（{HiddenProcessCount}）";

    /// <summary>兼容属性：过程组内分页按钮已停用。</summary>
    public bool ShowEarlierProcessPrompt => false;

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
        switch (item)
        {
            case MessageItemViewModel message :
                message.MarkInProcessGroup();
                break;
            case ToolActivityItemViewModel tool :
                tool.MarkInProcessGroup();
                break;
        }

        Process.Add(item);
        AppendToVisibleProcess(item);
        RefreshSegments();
        NotifyWindowChanged();
        RefreshSummary();
    }

    /// <summary>结算最终回复时把它移出过程输入；最终回复始终独立于过程组显示。</summary>
    public bool Remove(ConversationItemViewModel item)
    {
        var index = Process.IndexOf(item);
        if (index < 0) return false;

        Process.RemoveAt(index);
        switch (item)
        {
            case MessageItemViewModel message :
                message.UnmarkInProcessGroup();
                break;
            case ToolActivityItemViewModel tool :
                tool.UnmarkInProcessGroup();
                break;
        }

        RefreshVisibleProcess();
        NotifyWindowChanged();
        RefreshSummary();
        return true;
    }

    /// <summary>兼容入口：全部已加载过程已显示，调用时只展开过程组。</summary>
    public void ShowEarlierProcess()
    {
        IsExpanded = true;
    }

    /// <summary>刷新派生的摘要展示。</summary>
    public void RefreshSummary()
    {
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(HasFailed));
        OnPropertyChanged(nameof(FailedText));
        foreach (var segment in SegmentedProcess.OfType<TurnProcessSegmentViewModel>())
            segment.RefreshSummary();
    }

    /// <summary>标记轮次只覆盖已加载窗口的一部分；摘要明示口径，不虚构完整轮。</summary>
    internal void MarkPartialTurn()
    {
        IsPartialTurn = true;
        OnPropertyChanged(nameof(SummaryText));
    }

    /// <summary>历史重建结束：同步完整的当前已加载过程并刷新 reasoning 段投影。</summary>
    internal void CompleteRestoredProjection()
    {
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
        VisibleProcess.Add(item);
    }

    private void RefreshVisibleProcess()
    {
        VisibleProcess.Clear();
        foreach (var item in Process)
            VisibleProcess.Add(item);

        RefreshSegments();
        NotifyWindowChanged();
    }

    /// <summary>
    ///     把可见窗口投影为段序列：有正文的消息保持独立，连续思考/工具合并成段；
    ///     段的展开状态以段首条目为键在重建间保留。
    /// </summary>
    private void RefreshSegments()
    {
        foreach (var segment in SegmentedProcess.OfType<TurnProcessSegmentViewModel>())
            if (segment.Items.Count > 0)
                _segmentExpansion[segment.Items[0]] = segment.IsExpanded;

        SegmentedProcess.Clear();

        List<ConversationItemViewModel>? run = null;
        foreach (var item in VisibleProcess)
        {
            if (IsSegmentAnchor(item))
            {
                if (item is MessageItemViewModel { HasReasoning: true } message &&
                    message.GetProcessReasoningProjection() is { } reasoning)
                    (run ??= []).Add(reasoning);

                AppendSegmentRun(run);
                run = null;
                SegmentedProcess.Add(item);
                continue;
            }

            (run ??= []).Add(item);
        }

        AppendSegmentRun(run);

        void AppendSegmentRun(List<ConversationItemViewModel>? entries)
        {
            if (entries is null || entries.Count == 0) return;

            var segment = new TurnProcessSegmentViewModel(entries);
            if (_segmentExpansion.TryGetValue(entries[0], out var expanded)) segment.IsExpanded = expanded;
            SegmentedProcess.Add(segment);
        }
    }

    private static bool IsSegmentAnchor(ConversationItemViewModel item)
    {
        return item is MessageItemViewModel { Content: var content } &&
               !string.IsNullOrWhiteSpace(content);
    }

    private void NotifyWindowChanged()
    {
        OnPropertyChanged(nameof(HiddenProcessCount));
        OnPropertyChanged(nameof(HasEarlierProcess));
        OnPropertyChanged(nameof(HiddenProcessCountText));
        OnPropertyChanged(nameof(ShowEarlierProcessPrompt));
        ShowEarlierProcessCommand.RaiseCanExecuteChanged();
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
}
