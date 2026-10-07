using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Banned.CodeDiff.Avalonia.Models;
using Banned.CodeDiff.Avalonia.Views;
using DshDesktop.ViewModels;
using System.Collections.Specialized;
using System.ComponentModel;

namespace DshDesktop.Presentation.Views.Conversation;

/// <summary>会话主区域。消息滚动行为（流式贴底、历史前插锚定）属于本视图的界面行为。</summary>
public partial class ConversationView : UserControl
{
    private const double AutoScrollBottomTolerance = 140;
    private const double DiffPanelMinWidth          = 300;
    private const double DiffPanelMaxWidth          = 960;
    private const double DiffPanelDefaultWidth      = 480;
    private const double ConversationMinWidth       = 560;
    private const double DiffSplitModeMinWidth      = 720;

    private bool   _anchoringPrepend;
    private double _lastSettleExtent = double.NaN;
    private double _lastSettleY      = double.NaN;
    private double _messagesExtent;
    private double _prependAnchorExtent;
    private double _prependAnchorOffset;
    private double _prependAnchorY;
    private bool   _restoringPrependAnchor;
    private bool   _isDiffPanelOpen;
    private int    _stableSettlePasses;
    private double _lastDiffPanelWidth = DiffPanelDefaultWidth;

    private ConversationItemViewModel? _prependAnchorItem;
    private MainWindowViewModel?       _viewModel;

    public ConversationView()
    {
        InitializeComponent();
        MessagesScroll.ScrollChanged += OnMessagesScrollChanged;
        ConversationLayoutGrid.SizeChanged += OnConversationLayoutSizeChanged;
    }

    // DataContext 由窗口在 XAML 组合时继承注入；订阅跟随其生命周期增减。
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.ConversationItems.CollectionChanged -= KeepScrolledToBottom;
            _viewModel.PropertyChanged                     -= OnViewModelPropertyChanged;
            _viewModel.DiffPanel.PropertyChanged           -= OnDiffPanelPropertyChanged;
        }

        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel is null)
        {
            SetDiffPanelOpen(false);
            return;
        }
        _viewModel.ConversationItems.CollectionChanged += KeepScrolledToBottom;
        _viewModel.PropertyChanged                     += OnViewModelPropertyChanged;
        _viewModel.DiffPanel.PropertyChanged           += OnDiffPanelPropertyChanged;
        SetDiffPanelOpen(_viewModel.DiffPanel.IsOpen);
    }

    private ColumnDefinition DiffPanelColumn => ConversationLayoutGrid.ColumnDefinitions[2];

    private void OnDiffPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceDiffPanelViewModel.IsOpen))
            SetDiffPanelOpen(_viewModel?.DiffPanel.IsOpen == true);
    }

    private void SetDiffPanelOpen(bool isOpen)
    {
        var column = DiffPanelColumn;
        if (isOpen)
        {
            _isDiffPanelOpen = true;
            column.Width = new GridLength(_lastDiffPanelWidth, GridUnitType.Pixel);
            DiffPanelSplitter.IsVisible = true;
            ClampDiffPanelWidth();
            return;
        }

        if (_isDiffPanelOpen && column.Width.IsAbsolute && column.Width.Value > 0)
            _lastDiffPanelWidth = column.Width.Value;

        _isDiffPanelOpen = false;
        column.MinWidth = 0;
        column.MaxWidth = double.PositiveInfinity;
        column.Width = new GridLength(0);
        DiffPanelSplitter.IsVisible = false;
    }

    private void OnConversationLayoutSizeChanged(object? sender, SizeChangedEventArgs e) =>
        ClampDiffPanelWidth(e.NewSize.Width);

    /// <summary>将右侧面板限制在会话区最小宽度之外，并记住最近一次可用宽度。</summary>
    private void ClampDiffPanelWidth(double? availableWidth = null)
    {
        if (!_isDiffPanelOpen) return;

        var available = availableWidth ?? ConversationLayoutGrid.Bounds.Width;
        if (available <= 0) return;

        var upper = Math.Max(0, Math.Min(DiffPanelMaxWidth, available - ConversationMinWidth));
        var lower = Math.Min(DiffPanelMinWidth, upper);
        var column = DiffPanelColumn;
        var current = column.Width.IsAbsolute ? column.Width.Value : _lastDiffPanelWidth;

        column.MinWidth = lower;
        column.MaxWidth = upper;
        column.Width = new GridLength(Math.Clamp(current, lower, upper), GridUnitType.Pixel);
        _lastDiffPanelWidth = column.Width.Value;
    }

    /// <summary>每个差异控件按自身布局宽度选择双栏或统一视图。</summary>
    private void OnDiffViewSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is not DiffView diffView || e.NewSize.Width <= 0) return;

        var mode = e.NewSize.Width >= DiffSplitModeMinWidth ? DiffViewMode.Split : DiffViewMode.Unified;
        if (diffView.ViewMode != mode) diffView.ViewMode = mode;
    }

    /// <summary>加载完成后解除前插锚定；延后到布局落地，覆盖插入后一次 extent 增高。</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsLoadingOlder) &&
            DataContext is MainWindowViewModel { IsLoadingOlder: false })
            Dispatcher.UIThread.Post(SettlePrependAnchor, DispatcherPriority.Render);
    }

    /// <summary>新消息到达时，若用户本就停在底部附近则继续贴底；用户上翻时不打扰。</summary>
    private void KeepScrolledToBottom(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 翻页以 Clear + 整体重灌实现前插（ViewModel.RebuildTimeline）：IsLoadingOlder
        // 窗口内的 Reset 即前插起点，随后一次布局的 extent 增量需要锚定补偿，
        // 否则视口按原偏移落在新内容上（跳到已加载历史的顶端）。会话切换等其它
        // Reset 不置锚——其偏移归零属预期，补偿反而会把视口抬到错误位置。
        if (e.Action == NotifyCollectionChangedAction.Reset &&
            DataContext is MainWindowViewModel { IsLoadingOlder: true })
        {
            _anchoringPrepend = true;
            if (_prependAnchorItem is null) CapturePrependAnchor();
            return;
        }

        // 重建时间线会在 Reset 后连续发 Add；这些只是前插批次的一部分，不能排队贴底。
        if (_anchoringPrepend) return;

        var scroll = MessagesScroll;
        if (scroll is not null && WasNearBottom(scroll, scroll.Extent.Height)) PostScrollToEnd(scroll);
    }

    /// <summary>
    ///     内容增高（流式追加、新气泡完成布局）时维持贴底。CollectionChanged 只覆盖增删条目：
    ///     气泡内的文本增高不触发它，只体现在 extent 变化上；贴底判断用增高前的 extent，
    ///     避免长气泡一次布局后越界超出容差被误判为用户上翻。
    ///     「加载更早」前插历史时改走锚定补偿：保持视觉位置不动，且不触发贴底。
    /// </summary>
    private void OnMessagesScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer scroll) return;

        if (_anchoringPrepend)
        {
            if (!_restoringPrependAnchor && !e.OffsetDelta.Y.Equals(0) && e.ExtentDelta.Y.Equals(0))
            {
                // 翻页等待期间用户主动滚动，立即让用户操作接管视口。
                ClearPrependAnchor();
            }
            else
            {
                _messagesExtent = scroll.Extent.Height;
                RestorePrependAnchor();
                return;
            }
        }

        if (_anchoringPrepend && e.ExtentDelta.Y > 0)
        {
            // 顶部插入内容把既有内容向下推；同步抬高偏移，用户看到的位置保持不变。
            // 补偿分支同样推进 extent 基线，避免后续流式增高拿过期 extent 误判贴底。
            _messagesExtent = scroll.Extent.Height;
            scroll.Offset   = scroll.Offset.WithY(scroll.Offset.Y + e.ExtentDelta.Y);
            return;
        }

        var previousExtent = _messagesExtent;
        _messagesExtent = scroll.Extent.Height;
        if (previousExtent > 0         &&
            !e.ExtentDelta.Y.Equals(0) &&
            WasNearBottom(scroll, previousExtent))
            PostScrollToEnd(scroll);
    }

    private static bool WasNearBottom(ScrollViewer scroll, double extent)
    {
        return extent - (scroll.Offset.Y + scroll.Viewport.Height) <= AutoScrollBottomTolerance;
    }

    private static void PostScrollToEnd(ScrollViewer scroll)
    {
        Dispatcher.UIThread.Post(scroll.ScrollToEnd, DispatcherPriority.Background);
    }

    /// <summary>按钮执行命令前记录首个当前可见时间线项及其相对 ScrollViewer 的 Y 坐标。</summary>
    private void OnLoadOlderClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || !_viewModel.HasMoreHistory || _viewModel.IsLoadingOlder) return;

        CapturePrependAnchor();
        _viewModel.LoadOlderCommand.Execute(null);
    }

    private void CapturePrependAnchor()
    {
        var scroll = MessagesScroll;
        if (scroll is null || _viewModel is null) return;

        _prependAnchorItem   = null;
        _prependAnchorOffset = scroll.Offset.Y;
        _prependAnchorExtent = scroll.Extent.Height;
        _lastSettleY         = double.NaN;
        _lastSettleExtent    = double.NaN;
        _stableSettlePasses  = 0;

        foreach (var item in _viewModel.ConversationItems)
        {
            if (ConversationItemsControl.ContainerFromItem(item) is not Control container ||
                container.TranslatePoint(default, scroll) is not { } point                ||
                point.Y + container.Bounds.Height <= 0)
                continue;

            _prependAnchorItem = item;
            _prependAnchorY    = point.Y;
            break;
        }
    }

    /// <summary>跨整表重建按稳定 Seq/turn 找回原条目，再抵消其实际屏幕位移。</summary>
    private void RestorePrependAnchor()
    {
        if (!_anchoringPrepend || _viewModel is null || _prependAnchorItem is null) return;

        var item = ResolvePrependAnchor(_prependAnchorItem);
        if (item is null || ConversationItemsControl.ContainerFromItem(item) is not Control container ||
            container.TranslatePoint(default, MessagesScroll) is not { } point)
        {
            var fallbackOffset = _prependAnchorOffset + MessagesScroll.Extent.Height - _prependAnchorExtent;
            _restoringPrependAnchor = true;
            try
            {
                MessagesScroll.Offset = MessagesScroll.Offset.WithY(fallbackOffset);
            }
            finally
            {
                _restoringPrependAnchor = false;
            }

            return;
        }

        var delta = point.Y - _prependAnchorY;
        if (Math.Abs(delta) < 0.5) return;

        _restoringPrependAnchor = true;
        try
        {
            MessagesScroll.Offset = MessagesScroll.Offset.WithY(MessagesScroll.Offset.Y + delta);
        }
        finally
        {
            _restoringPrependAnchor = false;
        }
    }

    private ConversationItemViewModel? ResolvePrependAnchor(ConversationItemViewModel anchor)
    {
        if (_viewModel is null) return null;

        if (anchor is TurnProcessGroupViewModel anchorGroup)
            return _viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>().FirstOrDefault(group =>
                         anchorGroup.Turn is { } turn
                             ? group.Turn == turn
                             : group.Process.Any(item => item.Seq == anchorGroup.Seq));

        var direct = _viewModel.ConversationItems.FirstOrDefault(item =>
                                                                     item.GetType() == anchor.GetType() &&
                                                                     item.Seq       == anchor.Seq       &&
                                                                     (item is not MessageItemViewModel message ||
                                                                      (anchor is MessageItemViewModel oldMessage &&
                                                                       message.Id == oldMessage.Id)) &&
                                                                     (item is not ToolActivityItemViewModel tool ||
                                                                      (anchor is ToolActivityItemViewModel oldTool &&
                                                                       tool.CallId == oldTool.CallId)));
        if (direct is not null) return direct;

        // 补齐历史后，先前单独显示的工具/消息可能归入完整 turn 过程组；组头仍是稳定锚点。
        return _viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>()
                         .FirstOrDefault(group => group.Process.Any(item => item.Seq == anchor.Seq));
    }

    private void SettlePrependAnchor()
    {
        if (!_anchoringPrepend) return;

        RestorePrependAnchor();
        var extent = MessagesScroll.Extent.Height;
        var y = _prependAnchorItem is { } anchor                                          && _viewModel is not null &&
                ResolvePrependAnchor(anchor) is { } resolved                              &&
                ConversationItemsControl.ContainerFromItem(resolved) is Control container &&
                container.TranslatePoint(default, MessagesScroll) is { } point
            ? point.Y
            : _prependAnchorOffset + extent - _prependAnchorExtent;

        if (Math.Abs(extent - _lastSettleExtent) < 0.5 && Math.Abs(y - _lastSettleY) < 0.5)
            _stableSettlePasses++;
        else
            _stableSettlePasses = 0;

        _lastSettleExtent = extent;
        _lastSettleY      = y;
        if (_stableSettlePasses >= 1)
        {
            ClearPrependAnchor();
            return;
        }

        Dispatcher.UIThread.Post(SettlePrependAnchor, DispatcherPriority.Background);
    }

    private void ClearPrependAnchor()
    {
        _anchoringPrepend   = false;
        _prependAnchorItem  = null;
        _stableSettlePasses = 0;
    }
}
