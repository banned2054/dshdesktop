using Avalonia.Controls;
using Avalonia.Threading;
using DshDesktop.ViewModels;
using System.Collections.Specialized;
using System.ComponentModel;

namespace DshDesktop.Presentation.Views.Conversation;

/// <summary>会话主区域。消息滚动行为（流式贴底、历史前插锚定）属于本视图的界面行为。</summary>
public partial class ConversationView : UserControl
{
    private const double AutoScrollBottomTolerance = 140;

    private MainWindowViewModel? _viewModel;
    private double               _messagesExtent;
    private bool                 _anchoringPrepend;

    public ConversationView()
    {
        InitializeComponent();
        MessagesScroll.ScrollChanged += OnMessagesScrollChanged;
    }

    // DataContext 由窗口在 XAML 组合时继承注入；订阅跟随其生命周期增减。
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.ConversationItems.CollectionChanged -= KeepScrolledToBottom;
            _viewModel.PropertyChanged                     -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel is not null)
        {
            _viewModel.ConversationItems.CollectionChanged += KeepScrolledToBottom;
            _viewModel.PropertyChanged                     += OnViewModelPropertyChanged;
        }
    }

    /// <summary>加载完成后解除前插锚定；延后到布局落地，覆盖插入后一次 extent 增高。</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsLoadingOlder) &&
            DataContext is MainWindowViewModel { IsLoadingOlder: false })
        {
            Dispatcher.UIThread.Post(() => _anchoringPrepend = false, DispatcherPriority.Background);
        }
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
        }

        var scroll = MessagesScroll;
        if (scroll is not null && WasNearBottom(scroll, scroll.Extent.Height))
        {
            PostScrollToEnd(scroll);
        }
    }

    /// <summary>
    /// 内容增高（流式追加、新气泡完成布局）时维持贴底。CollectionChanged 只覆盖增删条目：
    /// 气泡内的文本增高不触发它，只体现在 extent 变化上；贴底判断用增高前的 extent，
    /// 避免长气泡一次布局后越界超出容差被误判为用户上翻。
    /// 「加载更早」前插历史时改走锚定补偿：保持视觉位置不动，且不触发贴底。
    /// </summary>
    private void OnMessagesScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer scroll)
        {
            return;
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
        if (previousExtent > 0 &&
          !e.ExtentDelta.Y.Equals(0) &&
          WasNearBottom(scroll, previousExtent))
        {
            PostScrollToEnd(scroll);
        }
    }

    private static bool WasNearBottom(ScrollViewer scroll, double extent)
    {
        return extent - (scroll.Offset.Y + scroll.Viewport.Height) <= AutoScrollBottomTolerance;
    }

    private static void PostScrollToEnd(ScrollViewer scroll)
    {
        Dispatcher.UIThread.Post(scroll.ScrollToEnd, DispatcherPriority.Background);
    }
}
