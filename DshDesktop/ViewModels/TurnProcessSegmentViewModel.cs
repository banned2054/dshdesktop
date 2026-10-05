using Avalonia.Media;
using DshDesktop.Utils;
using System.Collections.ObjectModel;

namespace DshDesktop.ViewModels;

/// <summary>
///     过程组内的段级折叠：两条有正文的过程消息之间（或最后一条之后）连续的
///     思考条目与工具调用合并为一个区块。收起时只显示图标与摘要行，
///     展开后按时间线顺序复用消息与工具视图。
/// </summary>
public sealed class TurnProcessSegmentViewModel : ConversationItemViewModel
{
    private bool _isExpanded;

    public TurnProcessSegmentViewModel(IReadOnlyList<ConversationItemViewModel> items) : base(
         items.Count > 0 ? items[0].Seq : -1)
    {
        Items         = new ObservableCollection<ConversationItemViewModel>(items);
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        RefreshSummary();
    }

    /// <summary>段内原始条目；保持时间线顺序，视图层按类型套用消息/工具模板。</summary>
    public ObservableCollection<ConversationItemViewModel> Items { get; }

    /// <summary>段级折叠默认收起；组内窗口重建时按段首条目保留展开状态。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public RelayCommand ToggleCommand { get; }

    /// <summary>折叠行摘要：思考与段内工具标题按出现顺序去重拼接。</summary>
    public string SummaryText { get; private set; } = string.Empty;

    /// <summary>折叠行图标：有工具时取首个工具图标，否则用思考图标。</summary>
    public StreamGeometry LeadingIconStroke { get; private set; } = ToolCallText.ThinkingIconStroke;

    public StreamGeometry? LeadingIconFill { get; private set; }

    /// <summary>段内工具标题/状态变化后重算摘要；由过程组统一触发。</summary>
    public void RefreshSummary()
    {
        var tools       = Items.OfType<ToolActivityItemViewModel>().ToList();
        var hasThinking = Items.OfType<MessageItemViewModel>().Any(message => message.HasReasoning);

        var labels = new List<string>();
        if (hasThinking) labels.Add("已思考");
        labels.AddRange(tools.Select(tool => tool.Title).Distinct());
        SummaryText = labels.Count == 0 ? "已思考" : string.Join(" · ", labels);

        var lead = tools.Count > 0 ? tools[0] : null;
        LeadingIconStroke = lead?.IconStroke ?? ToolCallText.ThinkingIconStroke;
        LeadingIconFill   = lead?.IconFill   ?? (hasThinking ? ToolCallText.ThinkingIconFill : null);
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(LeadingIconStroke));
        OnPropertyChanged(nameof(LeadingIconFill));
    }
}
