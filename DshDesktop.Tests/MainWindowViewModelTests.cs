using Avalonia;
using DshDesktop.Core.Models;
using DshDesktop.Core.Services;
using DshDesktop.Harness.Exceptions;
using DshDesktop.Infrastructure.Services;
using DshDesktop.Presentation.Views;
using DshDesktop.ViewModels;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace DshDesktop.Tests;

public sealed class MainWindowViewModelTests(ITestOutputHelper output)
{
    private static readonly Lock AvaloniaSetupLock = new();

    private static bool _avaloniaIsInitialized;

    /// <summary>带现场转储的等待：超时前输出 ViewModel 关键状态，便于定位偶发竞态。</summary>
    private async Task WaitOrDumpAsync(MainWindowViewModel viewModel, Func<bool> condition, int timeoutMilliseconds)
    {
        try
        {
            await WaitUntilAsync(condition, timeoutMilliseconds);
        }
        catch (XunitException)
        {
            output.WriteLine($"等待超时现场：selected={viewModel.SelectedSession?.Id} "          +
                             $"usage={viewModel.Composer.Usage?.ToString() ?? "<null>"} " +
                             $"stats={viewModel.Composer.Stats?.ToString() ?? "<null>"} " +
                             $"error=\"{viewModel.ErrorText}\" items={viewModel.ConversationItems.Count}");
            throw;
        }
    }

    [Fact]
    public async Task MainWindowReceivesTheComposedViewModelAsDataContext()
    {
        var viewModel = CreateViewModel();
        EnsureAvaloniaPlatform();
        var window = new MainWindow(viewModel);

        Assert.Same(viewModel, window.DataContext);

        window.Close();
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task LoadOlderRebuildRaisesResetInsideLoadingWindow()
    {
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync();
        viewModel.SelectedSession = viewModel.Sidebar.Sessions.First(session => session.Id == "session-history");
        await WaitUntilAsync(() => viewModel.ConversationItems.Count                       == 5);

        // 视图以前插锚定补偿翻页跳动，置锚判据是「IsLoadingOlder 窗口内到达的 Reset」：
        // 翻页重建（Clear + 整体重灌，见 RebuildTimeline）的 Reset 必须发生在窗口内，
        // 否则视图无法区分翻页前插与会话切换，锚定会失效或误触发。
        var events = new List<(NotifyCollectionChangedAction Action, bool LoadingOlder)>();
        viewModel.ConversationItems.CollectionChanged +=
            (_, e) => events.Add((e.Action, viewModel.IsLoadingOlder));

        viewModel.LoadOlderCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ConversationItems[0].Seq == 23 && !viewModel.IsLoadingOlder);

        Assert.Contains(events,
                        recorded => recorded is { Action: NotifyCollectionChangedAction.Reset, LoadingOlder: true });

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task SessionSwitchRebuildRaisesResetOutsideLoadingWindow()
    {
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.SelectedSession is not null);
        var firstSessionId = viewModel.SelectedSession!.Id;
        await WaitUntilAsync(() => viewModel.ConversationItems.Count > 0);

        // 切换会话同样以 Reset 重建时间线，但不得处于 IsLoadingOlder 窗口内：
        // 会话切换的偏移归零属预期行为，进入锚定窗口会把视口抬到错误位置。
        var sawReset                  = false;
        var sawResetWhileLoadingOlder = false;
        viewModel.ConversationItems.CollectionChanged += (_, e) =>
        {
            if (e.Action != NotifyCollectionChangedAction.Reset) return;
            sawReset = true;
            if (viewModel.IsLoadingOlder) sawResetWhileLoadingOlder = true;
        };

        viewModel.SelectedSession =
            viewModel.Sidebar.Sessions.First(session => session.Id != firstSessionId);
        await WaitUntilAsync(() => viewModel.ConversationItems.Count > 0);

        Assert.True(sawReset);
        Assert.False(sawResetWhileLoadingOlder);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task ApprovalPanelFiltersBySessionAndSendsAllowOnceDecision()
    {
        var sessionService  = new SimulatedSessionService();
        var approvalService = new SimulatedToolApprovalService();
        var viewModel = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(),
                                                new StaticWorkspaceService(), approvalService);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.SelectedSession is not null);

        var selectedSessionId = viewModel.SelectedSession!.Id;
        approvalService.PushRequest(selectedSessionId, "fs.write", "需要修改项目文件", "call-selected");
        approvalService.PushRequest("session-not-selected", "fs.read", "不应出现在当前会话", "call-other");

        await WaitUntilAsync(() => viewModel.SessionPendingApprovals.Count == 1);
        var approval = Assert.Single(viewModel.SessionPendingApprovals);
        Assert.Equal("fs.write", approval.ToolName);
        Assert.Equal("需要修改项目文件", approval.HeadlineText);

        viewModel.ApproveApprovalCommand.Execute(approval);
        await WaitUntilAsync(() => approvalService.Decisions.Count == 1);

        var decision = Assert.Single(approvalService.Decisions);
        Assert.Equal(approval.EventId, decision.EventId);
        Assert.True(decision.Allowed);
        Assert.Single(approvalService.Pending);
        Assert.Empty(viewModel.SessionPendingApprovals);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task SwitchingSessionsLoadsEachSessionSnapshot()
    {
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.ConversationItems.Count > 0);

        viewModel.SelectedSession = viewModel.Sidebar.Sessions.First(session => session.Id == "session-native");
        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<MessageItemViewModel>().Count() == 2);

        Assert.False(viewModel.HasError);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task SendingMessagePreservesDraftEnteredWhileRequestIsInFlight()
    {
        var sessionService = new BlockingSendSessionService();
        var backendService = new SimulatedBackendStatusService();
        var viewModel      = new MainWindowViewModel(sessionService, backendService, new StaticWorkspaceService());
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.SelectedSession is not null);

        viewModel.Composer.DraftMessage = "第一条消息";
        viewModel.Composer.SendMessageCommand.Execute(null);
        await sessionService.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        viewModel.Composer.DraftMessage = "发送期间的新草稿";
        sessionService.ReleaseSend.TrySetResult();
        await WaitUntilAsync(() => !viewModel.Composer.IsSending);

        Assert.Equal("发送期间的新草稿", viewModel.Composer.DraftMessage);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task StreamingUpdatesAppendAssistantTextAndCommitReplacesBubble()
    {
        var sessionService = new SimulatedSessionService();
        var backendService = new SimulatedBackendStatusService();
        var viewModel      = new MainWindowViewModel(sessionService, backendService, new StaticWorkspaceService());
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.SelectedSession is not null);
        var sessionId = viewModel.SelectedSession!.Id;
        var before    = viewModel.ConversationItems.Count;

        sessionService.PushAssistantReply(sessionId, "流式回复内容");

        // 等到正式消息（Seq >= 0）替换流式气泡且数量回落，避免轮询命中流式中间态。
        await WaitUntilAsync(() => viewModel.ConversationItems.Count == before + 1 && viewModel.ConversationItems
                                .OfType<MessageItemViewModel>()
                                .Any(message => message is { Content: "流式回复内容", Seq: >= 0 }));
        var committed = viewModel.ConversationItems.OfType<MessageItemViewModel>()
                                 .FirstOrDefault(message => message.Content == "流式回复内容");
        Assert.NotNull(committed);
        Assert.Equal(MessageRole.Assistant, committed.Role);
        Assert.DoesNotContain(viewModel.ConversationItems.OfType<MessageItemViewModel>(),
                              message => message.IsStreaming);
        // Markdown 渲染源与字符串内容保持一致（快照构造路径）。
        Assert.Equal(committed.Content, committed.MarkdownBuilder.ToString());

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task AbandonedStreamMarksBubbleInterruptedInsteadOfHanging()
    {
        var sessionService = new SimulatedSessionService();
        var backendService = new SimulatedBackendStatusService();
        var viewModel      = new MainWindowViewModel(sessionService, backendService, new StaticWorkspaceService());
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.SelectedSession is not null);

        sessionService.PushAbandonedStream(viewModel.SelectedSession!.Id, "部分生成内容");

        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<MessageItemViewModel>()
                                            .Any(message => message.IsInterrupted));
        var interrupted = viewModel.ConversationItems.OfType<MessageItemViewModel>()
                                   .Single(message => message.IsInterrupted);
        Assert.False(interrupted.IsStreaming);
        // 中断是独立标注，不再混入正文；已生成内容原样保留。
        Assert.Equal("部分生成内容", interrupted.Content);
        Assert.False(viewModel.HasError);
        Assert.Equal(interrupted.Content, interrupted.MarkdownBuilder.ToString());

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task ModelCatalogPopulatesOptionsAndSnapshotCarriesCurrentModel()
    {
        var sessionService = new SimulatedSessionService();
        var backendService = new SimulatedBackendStatusService();
        var viewModel      = new MainWindowViewModel(sessionService, backendService, new StaticWorkspaceService());
        await viewModel.InitializeAsync();

        // 目录扁平投影：两个提供方共三个模型。
        await WaitUntilAsync(() => viewModel.Composer.ModelOptions.Count == 3);
        Assert.True(viewModel.Composer.IsModelPickerEnabled);

        // 默认选中最新会话（session-history，预置 sim/sim-chat）：快照投影生效。
        await WaitUntilAsync(() => viewModel.Composer.SelectedModelOption is { Provider: "sim", Model: "sim-chat" });
        Assert.Equal(new ModelSelection("sim", "sim-chat"), viewModel.Composer.CurrentModel);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task SelectingModelOptionEchoesThroughFollowAndSwitchesPerSession()
    {
        var sessionService = new SimulatedSessionService();
        var backendService = new SimulatedBackendStatusService();
        var viewModel      = new MainWindowViewModel(sessionService, backendService, new StaticWorkspaceService());
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.SelectedSession is not null);

        var reasoner = viewModel.Composer.ModelOptions.Single(option => option.Model == "alt-chat");
        viewModel.Composer.SelectedModelOption = reasoner;

        // 选型经 follow 流的 model/selection 回声生效（后端权威）。
        await WaitUntilAsync(() => viewModel.Composer.CurrentModel is { Provider: "sim-alt", Model: "alt-chat" });
        Assert.Same(reasoner, viewModel.Composer.SelectedModelOption);
        Assert.False(viewModel.HasError);

        // 切换会话：另一会话的快照携带各自的当前选型。
        viewModel.SelectedSession = viewModel.Sidebar.Sessions.First(session => session.Id == "session-native");
        await WaitUntilAsync(() => viewModel.SelectedSession!.Id == "session-native" &&
                                   viewModel.Composer.CurrentModel is { Provider: "sim", Model: "sim-reasoner" });
        Assert.False(viewModel.HasError);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task FailedModelSelectionRevertsPickerAndReportsError()
    {
        var sessionService = new FailingSelectSessionService();
        var backendService = new SimulatedBackendStatusService();
        var viewModel      = new MainWindowViewModel(sessionService, backendService, new StaticWorkspaceService());
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.Composer.SelectedModelOption is { Model: "sim-chat" });

        viewModel.Composer.SelectedModelOption =
            viewModel.Composer.ModelOptions.Single(option => option.Model == "alt-chat");
        await WaitUntilAsync(() => viewModel.HasError);

        // 失败后回退到当前生效选型的显示，不停留在失败项。
        await WaitUntilAsync(() => viewModel.Composer.SelectedModelOption is { Model: "sim-chat" });
        Assert.Contains("选型失败", viewModel.ErrorText, StringComparison.Ordinal);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task SessionUsageAndStatsFollowSelectedSession()
    {
        var sessionService = new SimulatedSessionService();
        var backendService = new SimulatedBackendStatusService();
        var viewModel      = new MainWindowViewModel(sessionService, backendService, new StaticWorkspaceService());
        await viewModel.InitializeAsync();

        // 默认选中长会话（4 轮 8 条助手消息）：快照统计基线到达，文案带真实数值。
        await WaitUntilAsync(() => viewModel.Composer is { Usage : { OutputTokens: > 0 }, Stats.Steps: > 0 });
        Assert.DoesNotContain("—", viewModel.Composer.UsageValueText, StringComparison.Ordinal);
        Assert.DoesNotContain("—", viewModel.Composer.SpeedValueText, StringComparison.Ordinal);
        var longUsage = viewModel.Composer.Usage!;

        // 切到单条助手消息的会话：统计按会话重置并携带该会话的累计值。
        viewModel.SelectedSession = viewModel.Sidebar.Sessions.First(session => session.Id == "session-design");
        await WaitUntilAsync(() => viewModel.SelectedSession!.Id == "session-design" &&
                                   viewModel.Composer.Usage is { OutputTokens: > 0 });
        Assert.True(viewModel.Composer.Usage!.OutputTokens < longUsage.OutputTokens);
        Assert.Equal(viewModel.Composer.Usage.UncachedInputTokens + viewModel.Composer.Usage.CacheReadTokens +
                     viewModel.Composer.Usage.CacheWriteTokens    + viewModel.Composer.Usage.OutputTokens > 0,
                     !viewModel.Composer.UsageValueText.Contains('—'));

        // 新增一步计费后统计整值更新。
        var before = viewModel.Composer.Usage!.OutputTokens;
        sessionService.PushAssistantReply("session-design", "累计一步计费的回复");
        await WaitUntilAsync(() => viewModel.Composer.Usage is { } usage && usage.OutputTokens > before);
        Assert.False(viewModel.HasError);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task StatsStripStaysHiddenForBlankSessionUntilUsageArrives()
    {
        var sessionService = new SimulatedSessionService();
        var backendService = new SimulatedBackendStatusService();
        var viewModel      = new MainWindowViewModel(sessionService, backendService, new StaticWorkspaceService());
        await viewModel.InitializeAsync();

        // 默认选中的长会话有计费步：统计条可见。
        await WaitUntilAsync(() => viewModel.Composer.HasStatsData);

        // 首发送经新对话草稿页进入新会话：无工作区环境显式选择「不使用工作区」后发送。
        // follow 快照会回填全 0 的 usage/stats 整值（对齐真实后端冷会话携带投影 wire
        // 视图的口径），统计条保持隐藏，不显示占位「—」。
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        viewModel.SelectWorkspaceCommand.Execute(viewModel.WorkspaceOptions
                                                          .Single(option => option.IsWithoutWorkspace));
        viewModel.Composer.DraftMessage = "第一条消息";
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SelectedSession is { Id: not "session-history" });
        // 等待零值基线本身：仅判非 null 可能命中上一会话尚未清空的旧值（短暂可见性窗口）。
        await WaitOrDumpAsync(viewModel,
                              () => viewModel.Composer is
                                  { Usage : { OutputTokens: 0, UncachedInputTokens: 0 }, Stats.Steps: 0 }, 5000);
        Assert.False(viewModel.Composer.HasStatsData);

        // 首条助手回复落地：投影转为非零，统计条出现。
        var blankSessionId = viewModel.SelectedSession!.Id;
        sessionService.PushAssistantReply(blankSessionId, "空会话的第一条回复");
        await WaitUntilAsync(() => viewModel.Composer.HasStatsData);
        Assert.False(viewModel.HasError);

        // 再次经草稿页首发送进入另一个空会话：按会话重置后重新隐藏。
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        viewModel.SelectWorkspaceCommand.Execute(viewModel.WorkspaceOptions
                                                          .Single(option => option.IsWithoutWorkspace));
        viewModel.Composer.DraftMessage = "第二条消息";
        viewModel.SendDraftCommand.Execute(null);
        await WaitOrDumpAsync(viewModel,
                              () => viewModel.SelectedSession is { Id: not "session-history" } &&
                                    viewModel.SelectedSession.Id != blankSessionId, 5000);
        await WaitOrDumpAsync(viewModel,
                              () => viewModel.Composer is
                                  { Usage : { OutputTokens: 0, UncachedInputTokens: 0 }, Stats.Steps: 0 }, 5000);
        Assert.False(viewModel.Composer.HasStatsData);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task SessionListRefreshKeepsSelectedInstanceAndStreamingBubble()
    {
        var sessionService = new SimulatedSessionService();
        var backendService = new SimulatedBackendStatusService();
        var viewModel      = new MainWindowViewModel(sessionService, backendService, new StaticWorkspaceService());
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.SelectedSession is not null);
        var selected           = viewModel.SelectedSession;
        var messageCountBefore = viewModel.ConversationItems.Count;

        // 生成中触发一次会话列表刷新（SessionsChanged → 400ms 合并 → 刷新）。
        sessionService.BeginAssistantStream(selected!.Id, "正在生成的内容");
        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<MessageItemViewModel>()
                                            .Any(message => message.IsStreaming));
        await sessionService.SendPromptAsync(selected.Id, Guid.NewGuid().ToString(), "触发列表刷新");
        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<MessageItemViewModel>()
                                            .Any(message => message is { Role: MessageRole.User, Content: "触发列表刷新" }));
        await Task.Delay(1200); // 400ms 事件合并 + 列表刷新完成。

        // 选中实例未被替换：不重开订阅，流式气泡不被新快照清掉。
        Assert.Same(selected, viewModel.SelectedSession);
        Assert.Contains(viewModel.ConversationItems.OfType<MessageItemViewModel>(),
                        message => message is { IsStreaming: true, Content: "正在生成的内容" });
        Assert.Equal(messageCountBefore + 2, viewModel.ConversationItems.Count);
        // 流式增量路径：渲染源跟随 AppendText 同步增长。
        var streaming = viewModel.ConversationItems.OfType<MessageItemViewModel>()
                                 .Single(message => message.IsStreaming);
        Assert.Equal(streaming.Content, streaming.MarkdownBuilder.ToString());

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task LoadingOlderPrependsEntriesAndFoldsCompleteTurnsAtTheTop()
    {
        var sessionService = new SimulatedSessionService();
        var backendService = new SimulatedBackendStatusService();
        var viewModel      = new MainWindowViewModel(sessionService, backendService, new StaticWorkspaceService());
        await viewModel.InitializeAsync();
        viewModel.SelectedSession = viewModel.Sidebar.Sessions.First(session => session.Id == "session-history");
        // 初始窗口是最近 3 条消息及其附随条目（说明、工具、思考、工具、总结、turn/end）；
        // turn/end 边界不产生可见条目，可见项为 5。
        await WaitUntilAsync(() => viewModel.ConversationItems.Count == 5);

        // 首轮被窗口截断（turn 4 的用户消息与首个思考在窗口之外）：起点未观察到，不折叠。
        Assert.True(viewModel.HasMoreHistory);
        Assert.Equal(27, viewModel.ConversationItems[0].Seq);
        Assert.DoesNotContain(viewModel.ConversationItems, item => item is TurnProcessGroupViewModel);

        viewModel.LoadOlderCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ConversationItems[0].Seq == 23 && !viewModel.IsLoadingOlder);
        Assert.True(viewModel.HasMoreHistory);

        // 窗口补全后 turn 4 的用户消息已可见：尽管更早历史未读，该轮即折叠——
        // 对齐 WebUI 实时会话的形态（其已加载窗口天然是全量，读全前也能折叠完整轮次）。
        var recentGroup = viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>()
                                   .Single(item => item.Seq == 26);
        Assert.Equal(2, recentGroup.ToolCallCount);
        Assert.Equal(1, recentGroup.MessageCount);
        Assert.Equal("2 次工具调用 · 1 条消息", recentGroup.SummaryText);

        // 逐页点击「加载更早」直到读全：每次前插一页（首条 Seq 前移），折叠状态随 HasMoreHistory 变化。
        var guard = 0;
        while (viewModel.HasMoreHistory && guard++ < 10)
        {
            var firstBefore = viewModel.ConversationItems[0].Seq;
            viewModel.LoadOlderCommand.Execute(null);
            await WaitUntilAsync(() => viewModel.ConversationItems[0].Seq < firstBefore);
            await WaitUntilAsync(() => !viewModel.IsLoadingOlder);
        }

        // 读全后整体折叠：每轮「用户、过程组、带思考的总结」三项。
        await WaitUntilAsync(() => viewModel.ConversationItems.Count == 12 && !viewModel.HasMoreHistory);
        Assert.Equal([1, 2, 7, 9, 10, 15, 17, 18, 23, 25, 26, 31],
                     viewModel.ConversationItems.Select(item => item.Seq));
        var group = viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>()
                             .Single(item => item.Seq == 2);
        Assert.Equal(2, group.ToolCallCount);
        Assert.Equal(1, group.MessageCount);
        Assert.Equal("2 次工具调用 · 1 条消息", group.SummaryText);
        // 过程组内的条目：两段思考、中间说明与两枚工具；思考条目不算「消息」。
        Assert.Equal(5, group.Process.Count);
        Assert.Equal(2, group.Process.OfType<MessageItemViewModel>()
                             .Count(message => message.HasReasoning && string.IsNullOrWhiteSpace(message.Content)));
        Assert.Contains(group.Process, item => item is MessageItemViewModel { Content: "先查看第 1 轮的相关记录，再做定点更新。" });
        // 最终回复保持独立气泡，且自带可折叠的思考行。
        var answer = viewModel.ConversationItems.OfType<MessageItemViewModel>()
                              .Single(item => item.Content == "历史回答 0：基于第 1 轮工具结果的整理。");
        Assert.Equal(7, answer.Seq);
        Assert.True(answer.HasReasoning);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task SnapshotTailInterruptedBoundaryKeepsOpenTurnUnfoldedUntilRealEnd()
    {
        var sessionService = new SyntheticBoundarySessionService();
        var backendService = new SimulatedBackendStatusService();
        var viewModel      = new MainWindowViewModel(sessionService, backendService, new StaticWorkspaceService());
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.SelectedSession is not null);

        // 中途 attach 到生成中的会话：快照尾部带 Host 合成的 interrupted 边界（seq 即 cursor，
        // 持久日志中不存在）。该边界不得结算当前轮——条目保持逐项显示，无过程组。
        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<MessageItemViewModel>()
                                            .Any(message => message.Content == "阶段性回复"));
        Assert.DoesNotContain(viewModel.ConversationItems, item => item is TurnProcessGroupViewModel);

        // 生成继续：新工具与真正的最终回复落地，随后真实的 turn/end 到达——此时才折叠，
        // 且只折叠一次（合成边界若被结算会出现两个过程组/错误的最终回复）。
        sessionService.PushLiveTail();
        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<MessageItemViewModel>()
                                            .Any(message => message.Content == "真正的最终回复"));
        sessionService.PushRealTurnEnd();
        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>().Any());

        var group = viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>().Single();
        Assert.Equal(2, group.ToolCallCount);
        Assert.Equal(2, group.MessageCount);
        Assert.Contains(group.Process, item => item is ToolActivityItemViewModel { Name: "fs.read" });
        var answer = viewModel.ConversationItems.OfType<MessageItemViewModel>()
                              .Single(message => message.Content == "真正的最终回复");
        Assert.Equal(7, answer.Seq);
        Assert.False(viewModel.HasError);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task SwitchingAwayFromSessionResetsHistoryWindow()
    {
        var sessionService = new SimulatedSessionService();
        var backendService = new SimulatedBackendStatusService();
        var viewModel      = new MainWindowViewModel(sessionService, backendService, new StaticWorkspaceService());
        await viewModel.InitializeAsync();
        viewModel.SelectedSession = viewModel.Sidebar.Sessions.First(session => session.Id == "session-history");

        await WaitUntilAsync(() => viewModel.ConversationItems.Count == 5);

        viewModel.SelectedSession = viewModel.Sidebar.Sessions.First(session => session.Id == "session-welcome");
        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<MessageItemViewModel>().Count() == 2);

        Assert.False(viewModel.HasMoreHistory);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task ToolActivityRendersCardAndSettlesInPlace()
    {
        var sessionService = new SimulatedSessionService();
        var backendService = new SimulatedBackendStatusService();
        var viewModel      = new MainWindowViewModel(sessionService, backendService, new StaticWorkspaceService());
        await viewModel.InitializeAsync();
        // 选一个没有预置工具条目的会话，避免与演示数据中的 fs.read 混淆。
        viewModel.SelectedSession = viewModel.Sidebar.Sessions.First(session => session.Id == "session-welcome");
        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<MessageItemViewModel>().Count() == 2);
        var before = viewModel.ConversationItems.Count;

        sessionService.PushToolActivity(viewModel.SelectedSession!.Id, "fs.read", "文件内容摘要", false);

        // 运行中的轮次条目逐项显示：工具卡片直接位于时间线顶层。
        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<ToolActivityItemViewModel>()
                                            .Any(tool => tool.Status == ToolActivityStatus.Succeeded));
        var card = viewModel.ConversationItems.OfType<ToolActivityItemViewModel>()
                            .Single(tool => tool.Name == "fs.read");
        Assert.Equal(before + 1, viewModel.ConversationItems.Count);
        Assert.False(card.IsRunning);
        Assert.Equal("文件内容摘要", card.ResultText);
        Assert.True(card.HasDetails);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task FailedToolActivitySurfacesErrorState()
    {
        var sessionService = new SimulatedSessionService();
        var backendService = new SimulatedBackendStatusService();
        var viewModel      = new MainWindowViewModel(sessionService, backendService, new StaticWorkspaceService());
        await viewModel.InitializeAsync();
        viewModel.SelectedSession = viewModel.Sidebar.Sessions.First(session => session.Id == "session-welcome");
        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<MessageItemViewModel>().Count() == 2);

        sessionService.PushToolActivity(viewModel.SelectedSession!.Id, "shell.run", null, true);

        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<ToolActivityItemViewModel>()
                                            .Any(tool => tool.IsFailed));
        var card = viewModel.ConversationItems.OfType<ToolActivityItemViewModel>()
                            .Single(tool => tool.IsFailed);
        Assert.Equal("失败", card.StatusText);
        Assert.Contains("simulated", card.ErrorReason);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task TurnEndFoldsProcessIntoGroupAndKeepsFinalAnswer()
    {
        var sessionService = new SimulatedSessionService();
        var backendService = new SimulatedBackendStatusService();
        var viewModel      = new MainWindowViewModel(sessionService, backendService, new StaticWorkspaceService());
        await viewModel.InitializeAsync();
        viewModel.SelectedSession = viewModel.Sidebar.Sessions.First(session => session.Id == "session-welcome");
        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<MessageItemViewModel>().Count() == 2);
        var sessionId = viewModel.SelectedSession!.Id;

        // 一轮真实形态的工具执行：空提交 → 思考 → 中间说明 → 工具 → 再思考 → 工具 → 带思考的总结
        // → turn/end。
        sessionService.PushCommittedAssistantMessage(sessionId, string.Empty, 2, true);
        sessionService.PushCommittedAssistantMessage(sessionId, string.Empty, 2,
                                                     reasoning : "先重读当前文件，确认结构后再替换。");
        sessionService.PushCommittedAssistantMessage(sessionId, "先重读当前文件，再做定点替换。", 2);
        sessionService.PushToolActivity(sessionId, "fs.read", "文件内容摘要", false, 2);
        sessionService.PushCommittedAssistantMessage(sessionId, string.Empty, 2,
                                                     reasoning : "内容已确认，直接替换目标行。");
        sessionService.PushToolActivity(sessionId, "fs.edit", "已更新", false, 2);
        sessionService.PushCommittedAssistantMessage(sessionId, "总结回答", 2,
                                                     reasoning : "两步都已完成，给出结论。");

        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<MessageItemViewModel>()
                                            .Any(message => message.Content == "总结回答"));
        // turn/end 之前条目逐项显示（生成中的轮次不折叠）；思考条目与工具卡片一样逐项出现。
        Assert.DoesNotContain(viewModel.ConversationItems, item => item is TurnProcessGroupViewModel);
        Assert.Equal(2, viewModel.ConversationItems.OfType<MessageItemViewModel>()
                                 .Count(message => message.HasReasoning && string.IsNullOrWhiteSpace(message.Content)));
        // 无正文助手提交不产生空气泡。
        Assert.DoesNotContain(viewModel.ConversationItems.OfType<MessageItemViewModel>(),
                              message => message is { Role: MessageRole.Assistant } &&
                                         string.IsNullOrWhiteSpace(message.Content) && !message.HasReasoning);

        sessionService.PushTurnEnded(sessionId, 2);

        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>().Any());
        var group = viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>().Single();
        Assert.Equal(2, group.ToolCallCount);
        Assert.Equal(1, group.MessageCount);
        Assert.Equal("2 次工具调用 · 1 条消息", group.SummaryText);
        Assert.False(group.IsExpanded);
        // 过程组包含两段思考、中间说明与工具卡片；最终回复保持独立气泡。
        Assert.Equal(5, group.Process.Count);
        Assert.Contains(group.Process, item => item is MessageItemViewModel
        {
            Content: "先重读当前文件，再做定点替换。"
        });
        var answer = viewModel.ConversationItems.OfType<MessageItemViewModel>()
                              .Single(message => message.Content == "总结回答");
        Assert.True(answer.HasReasoning);
        Assert.False(answer.IsReasoningExpanded);

        // 用户消息开新一轮：之后的工具调用属于未收束的新轮次，另起显示不并入旧组。
        await sessionService.SendPromptAsync(sessionId, Guid.NewGuid().ToString(), "下一轮问题");
        sessionService.PushToolActivity(sessionId, "fs.read", "再次读取", false, 3);
        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<ToolActivityItemViewModel>()
                                            .Any(tool => tool.ResultText == "再次读取"));
        Assert.Single(viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>());

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task InterruptedTurnWithoutReplyStaysUnfoldedAfterBoundary()
    {
        var sessionService = new SimulatedSessionService();
        var backendService = new SimulatedBackendStatusService();
        var viewModel      = new MainWindowViewModel(sessionService, backendService, new StaticWorkspaceService());
        await viewModel.InitializeAsync();
        viewModel.SelectedSession = viewModel.Sidebar.Sessions.First(session => session.Id == "session-welcome");
        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<MessageItemViewModel>().Count() == 2);
        var sessionId = viewModel.SelectedSession!.Id;

        // 被打断的轮次：思考与说明之后直接中断，没有可展示的最终回复；
        // 该轮过程（含思考条目）保持逐项展示，不折叠。
        sessionService.PushCommittedAssistantMessage(sessionId, string.Empty, 2, reasoning : "准备读取文件，先确认路径。");
        sessionService.PushToolActivity(sessionId, "fs.read", "文件内容摘要", false, 2);
        sessionService.PushCommittedAssistantMessage(sessionId, string.Empty, 2, reasoning : "读到一半被取消了。",
                                                     isInterrupted : true);
        sessionService.PushTurnEnded(sessionId, 2);

        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<MessageItemViewModel>()
                                            .Any(message => message.IsInterrupted));
        Assert.DoesNotContain(viewModel.ConversationItems, item => item is TurnProcessGroupViewModel);
        // 中断标注在思考条目上，不产生只有「已中断」的空气泡。
        var interrupted = viewModel.ConversationItems.OfType<MessageItemViewModel>()
                                   .Single(message => message.IsInterrupted);
        Assert.True(interrupted.HasReasoning);
        Assert.True(string.IsNullOrWhiteSpace(interrupted.Content));

        // 之后正常完成的轮次照常折叠。
        sessionService.PushCommittedAssistantMessage(sessionId, string.Empty, 3,
                                                     reasoning : "重新读取。");
        sessionService.PushToolActivity(sessionId, "fs.read", "文件内容摘要", false, 3);
        sessionService.PushCommittedAssistantMessage(sessionId, "恢复正常后的回答。", 3);
        sessionService.PushTurnEnded(sessionId, 3);

        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>().Any());
        var group = viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>().Single();
        Assert.Equal(1, group.ToolCallCount);
        Assert.Contains(viewModel.ConversationItems.OfType<MessageItemViewModel>(),
                        message => message.Content == "恢复正常后的回答。");

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task TurnWithoutFinalAnswerStaysUnfoldedAfterBoundary()
    {
        var sessionService = new SimulatedSessionService();
        var backendService = new SimulatedBackendStatusService();
        var viewModel      = new MainWindowViewModel(sessionService, backendService, new StaticWorkspaceService());
        await viewModel.InitializeAsync();
        viewModel.SelectedSession = viewModel.Sidebar.Sessions.First(session => session.Id == "session-welcome");
        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<MessageItemViewModel>().Count() == 2);
        var sessionId = viewModel.SelectedSession!.Id;

        // 轮次以工具调用收尾、没有最终回复：与参考实现一致，不折叠，条目保持逐项。
        sessionService.PushToolActivity(sessionId, "fs.read", "文件内容摘要", false, 2);
        sessionService.PushTurnEnded(sessionId, 2);
        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<ToolActivityItemViewModel>()
                                            .Any(tool => tool.Status == ToolActivityStatus.Succeeded));
        Assert.DoesNotContain(viewModel.ConversationItems, item => item is TurnProcessGroupViewModel);
        Assert.Contains(viewModel.ConversationItems, item => item is ToolActivityItemViewModel);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task RunningToolsStayAsIndividualCardsAndSettleInPlace()
    {
        var sessionService = new SimulatedSessionService();
        var backendService = new SimulatedBackendStatusService();
        var viewModel      = new MainWindowViewModel(sessionService, backendService, new StaticWorkspaceService());
        await viewModel.InitializeAsync();
        viewModel.SelectedSession = viewModel.Sidebar.Sessions.First(session => session.Id == "session-welcome");
        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<MessageItemViewModel>().Count() == 2);
        var sessionId = viewModel.SelectedSession!.Id;

        var first  = sessionService.BeginToolActivity(sessionId, "fs.read", 2);
        var second = sessionService.BeginToolActivity(sessionId, "fs.edit", 2);
        Assert.NotNull(first);
        Assert.NotNull(second);
        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<ToolActivityItemViewModel>().Count() == 2);
        var cards = viewModel.ConversationItems.OfType<ToolActivityItemViewModel>().ToList();
        // 生成中的轮次不折叠：运行中的卡片逐项显示。
        Assert.DoesNotContain(viewModel.ConversationItems, item => item is TurnProcessGroupViewModel);
        Assert.All(cards, card => Assert.True(card.IsRunning));

        sessionService.SettleToolActivity(sessionId, first!.CallId, "文件内容摘要", false);
        sessionService.SettleToolActivity(sessionId, second!.CallId, null, true);
        await WaitUntilAsync(() => cards.All(card => !card.IsRunning));
        Assert.True(cards.Single(card => card.CallId == second.CallId).IsFailed);

        await viewModel.DisposeAsync();
    }

    private static MainWindowViewModel CreateViewModel()
    {
        return new MainWindowViewModel(new SimulatedSessionService(), new SimulatedBackendStatusService(),
                                       new SimulatedWorkspaceService());
    }

    [Fact]
    public async Task SessionListDefaultsToGroupedViewWithCurrentHighlight()
    {
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.Sidebar.Sessions.Count > 0);

        // 默认对齐参考客户端：按工作区分组（模拟服务登记了两个工作区）。
        Assert.Equal(1, viewModel.Sidebar.SessionListModeIndex);
        Assert.Contains(viewModel.Sidebar.SessionRows,
                        row => row is SessionGroupHeaderViewModel { TitleText: "示例工作区" });
        Assert.True(viewModel.SelectedSession!.IsCurrent);
        Assert.All(viewModel.Sidebar.Sessions.Where(session => !ReferenceEquals(session, viewModel.SelectedSession)),
                   session => Assert.False(session.IsCurrent));

        // 切回单列表：行投影就是会话顺序本身，无分组头。
        viewModel.Sidebar.SessionListModeIndex = 0;
        Assert.Equal(viewModel.Sidebar.Sessions, viewModel.Sidebar.SessionRows.OfType<SessionItemViewModel>());
        Assert.Empty(viewModel.Sidebar.SessionRows.OfType<SessionGroupHeaderViewModel>());

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task GroupingByWorkspaceProjectsHeadersMembersAndUngrouped()
    {
        var workspaces = new StaticWorkspaceService([
            new WorkspaceSummary("ws-main", "主工作区", "C:/Code/Main", ["session-native", "session-history"],
                                 DateTimeOffset.Now),
            new WorkspaceSummary("ws-empty", "空工作区", "C:/Code/Empty", [], DateTimeOffset.Now)
        ]);
        var viewModel = new MainWindowViewModel(new SimulatedSessionService(), new SimulatedBackendStatusService(),
                                                workspaces);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.Sidebar.Sessions.Count > 0);

        viewModel.Sidebar.SessionListModeIndex = 1;

        // 组序为后端顺序；组内成员按更新时间降序；空工作区仍显示；
        // 未被记账的会话落入「未分组」且该组仅在非空时出现。
        var shape = viewModel.Sidebar.SessionRows.Select(row => row switch
                              {
                                  SessionGroupHeaderViewModel header => $"header:{header.TitleText}",
                                  SessionItemViewModel session       => $"session:{session.Id}",
                                  _                                  => "other"
                              })
                             .ToArray();
        Assert.Equal(
        [
            "header:主工作区", "session:session-history", "session:session-native",
            "header:空工作区",
            "header:未分组", "session:session-welcome", "session:session-design"
        ], shape);
        // 模式切换不重建会话实例：选中与高亮保持。
        Assert.True(viewModel.SelectedSession!.IsCurrent);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task TogglingGroupCollapsesMembersOnlyForThatGroup()
    {
        var workspaces = new StaticWorkspaceService([
            new WorkspaceSummary("ws-main", "主工作区", "C:/Code/Main", ["session-native", "session-history"],
                                 DateTimeOffset.Now)
        ]);
        var viewModel = new MainWindowViewModel(new SimulatedSessionService(), new SimulatedBackendStatusService(),
                                                workspaces);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.Sidebar.Sessions.Count > 0);
        viewModel.Sidebar.SessionListModeIndex = 1;

        var mainHeader = viewModel.Sidebar.SessionRows.OfType<SessionGroupHeaderViewModel>()
                                  .Single(header => header.Key == "ws-main");
        viewModel.Sidebar.ToggleGroupCommand.Execute(mainHeader);

        Assert.DoesNotContain(viewModel.Sidebar.SessionRows.OfType<SessionItemViewModel>(),
                              session => session.Id is "session-native" or "session-history");
        // 其他组的成员不受影响。
        Assert.Contains(viewModel.Sidebar.SessionRows.OfType<SessionItemViewModel>(),
                        session => session.Id == "session-welcome");
        var collapsedHeader = viewModel.Sidebar.SessionRows.OfType<SessionGroupHeaderViewModel>()
                                       .Single(header => header.Key == "ws-main");
        Assert.False(collapsedHeader.IsExpanded);
        Assert.Equal("2 个会话", collapsedHeader.CountText);

        // 切回单列表再切回分组：收起状态按分组键保留。
        viewModel.Sidebar.SessionListModeIndex = 0;
        viewModel.Sidebar.SessionListModeIndex = 1;
        Assert.False(viewModel.Sidebar.SessionRows.OfType<SessionGroupHeaderViewModel>()
                              .Single(header => header.Key == "ws-main")
                              .IsExpanded);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task FirstSendFromDraftPageAppearsUnderUngroupedAndStaysSelected()
    {
        var workspaces = new StaticWorkspaceService([
            new WorkspaceSummary("ws-main", "主工作区", "C:/Code/Main", ["session-native"], DateTimeOffset.Now)
        ]);
        var viewModel = new MainWindowViewModel(new SimulatedSessionService(), new SimulatedBackendStatusService(),
                                                workspaces);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.Sidebar.Sessions.Count > 0);
        viewModel.Sidebar.SessionListModeIndex = 1;

        // 顶部新建进入草稿页（不产生会话行）；显式选择「不使用工作区」并首发送后，
        // 新会话落入未分组顶部且实例即当前选中。
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        Assert.DoesNotContain(viewModel.Sidebar.SessionRows.OfType<SessionItemViewModel>(),
                              session => session is { BlankState: SessionBlankState.ConfirmedBlank, IsCurrent: false });
        viewModel.SelectWorkspaceCommand.Execute(
                                                 viewModel.WorkspaceOptions
                                                          .Single(option => option.IsWithoutWorkspace));
        viewModel.Composer.DraftMessage = "未分组新会话的第一条消息";
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SelectedSession is
                                 { Id: not "session-native", BlankState: SessionBlankState.Engaged });

        var ungroupedHeader = viewModel.Sidebar.SessionRows.OfType<SessionGroupHeaderViewModel>()
                                       .Single(header => header.Key == "$ungrouped");
        var firstUngrouped = viewModel.Sidebar.SessionRows.OfType<SessionItemViewModel>()
                                      .First(session => session.Id == viewModel.SelectedSession!.Id);
        Assert.Same(viewModel.SelectedSession, firstUngrouped);
        Assert.Equal(0, viewModel.Sidebar.SessionRows.IndexOf(firstUngrouped)  -
                        viewModel.Sidebar.SessionRows.IndexOf(ungroupedHeader) - 1);
        Assert.True(firstUngrouped.IsCurrent);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task WorkspacesChangedRebuildsGroupProjection()
    {
        var workspaces = new StaticWorkspaceService([]);
        var viewModel = new MainWindowViewModel(new SimulatedSessionService(), new SimulatedBackendStatusService(),
                                                workspaces);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.Sidebar.Sessions.Count > 0);
        viewModel.Sidebar.SessionListModeIndex = 1;

        // 基线未到达时只有未分组一组。
        Assert.Single(viewModel.Sidebar.SessionRows.OfType<SessionGroupHeaderViewModel>(),
                      header => header.TitleText == "未分组");

        workspaces.Replace([
            new WorkspaceSummary("ws-late", "后到的工作区", "C:/Code/Late", ["session-welcome"], DateTimeOffset.Now)
        ]);
        workspaces.RaiseChanged();

        await WaitUntilAsync(() => viewModel.Sidebar.SessionRows.OfType<SessionGroupHeaderViewModel>()
                                            .Any(header => header.TitleText == "后到的工作区"));
        var shape = viewModel.Sidebar.SessionRows.Select(row => row switch
                              {
                                  SessionGroupHeaderViewModel header => $"header:{header.TitleText}",
                                  SessionItemViewModel session       => $"session:{session.Id}",
                                  _                                  => "other"
                              })
                             .ToArray();
        Assert.Equal([
            "header:后到的工作区", "session:session-welcome",
            "header:未分组", "session:session-history", "session:session-native", "session:session-design"
        ], shape);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task BackgroundSessionAddRefreshesRowsWhileSelectionStays()
    {
        var sessionService = new SimulatedSessionService();
        var viewModel = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(),
                                                new StaticWorkspaceService());
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.Sidebar.Sessions.Count > 0);
        viewModel.Sidebar.SessionListModeIndex = 0;
        var selectedBefore = viewModel.SelectedSession;

        // 后台新增并发送消息的会话（SessionsChanged → 延迟合并刷新）：选中实例不变，
        // 但行投影必须重建出新会话（回归：提前 return 曾跳过重建）。
        var created = await sessionService.CreateSessionAsync();
        await sessionService.SendPromptAsync(created.Id, "background-request", "后台消息");
        await WaitUntilAsync(() => viewModel.Sidebar.SessionRows.OfType<SessionItemViewModel>()
                                            .Any(session => session.Id == created.Id));
        Assert.Same(selectedBefore, viewModel.SelectedSession);
        Assert.True(selectedBefore!.IsCurrent);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task NewConversationPageCreatesSessionOnlyOnFirstSend()
    {
        var sessionService = new SimulatedSessionService();
        var viewModel = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(),
                                                new StaticWorkspaceService());
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.SelectedSession is not null);

        var historicalBlank = await sessionService.CreateSessionAsync();
        await WaitUntilAsync(() => viewModel.Sidebar.Sessions.All(session => session.Id != historicalBlank.Id));

        // 顶部新建进入新对话草稿页：不创建会话、不产生侧栏行；「不使用工作区」为显式兼容项。
        var createsBefore     = sessionService.CreatedSessionCount;
        var previousSelection = viewModel.SelectedSession;
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        Assert.Equal(createsBefore, sessionService.CreatedSessionCount);
        viewModel.SelectWorkspaceCommand.Execute(viewModel.WorkspaceOptions
                                                          .Single(option => option.IsWithoutWorkspace));
        viewModel.Composer.DraftMessage = "第一条消息";
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SelectedSession is not null &&
                                   !ReferenceEquals(viewModel.SelectedSession, previousSelection));
        Assert.Equal(createsBefore + 1, sessionService.CreatedSessionCount);
        var current = viewModel.SelectedSession!;
        Assert.Contains(viewModel.Sidebar.Sessions, session => session.Id == current.Id);
        Assert.Equal(SessionBlankState.Engaged, current.BlankState);

        var currentSummary = (await sessionService.GetSessionsAsync()).Single(summary => summary.Id == current.Id);
        Assert.Equal(SessionBlankState.Engaged, currentSummary.BlankState);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task DraftPageWorkspaceSelectionIsLocalOnlyAndDoesNotCreate()
    {
        var workspaces = new StaticWorkspaceService([
            new WorkspaceSummary("ws-1", "工作区一", "C:/Code/WS1", ["blank-a"], DateTimeOffset.Now)
        ]);
        var sessionService = new AdoptionSessionService();
        sessionService.SeedBlankSession("blank-a", "C:/Code/WS1");
        var viewModel = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(), workspaces);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.WorkspaceOptions.Any(option => option.Id == "ws-1"));
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);

        // 下拉点选只改本地草稿预选：不调用 session/create、不收养空白会话、不导航、
        // 不产生侧栏行；可随时改选（含显式「不使用工作区」）。
        var createsBefore = sessionService.CreatedSessionCount;
        viewModel.SelectWorkspaceCommand.Execute(viewModel.WorkspaceOptions.Single(option => option.Id == "ws-1"));
        Assert.Equal("工作区一", viewModel.WorkspacePickerLabel);
        viewModel.Composer.DraftMessage = "未发送的草稿";
        await WaitUntilAsync(() => viewModel.CanSendDraft);
        Assert.Equal(createsBefore, sessionService.CreatedSessionCount);
        Assert.Empty(sessionService.CreateRequests);
        Assert.True(viewModel.ShowNewConversationPage);
        Assert.DoesNotContain(viewModel.Sidebar.Sessions, session => session.Id == "blank-a");

        viewModel.SelectWorkspaceCommand.Execute(viewModel.WorkspaceOptions
                                                          .Single(option => option.IsWithoutWorkspace));
        Assert.Equal("不使用工作区", viewModel.WorkspacePickerLabel);
        Assert.True(viewModel.CanSendDraft);
        Assert.Equal(createsBefore, sessionService.CreatedSessionCount);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task GroupHeaderPlusEntersDraftPageWithWorkspacePreselected()
    {
        var workspaces = new StaticWorkspaceService([
            new WorkspaceSummary("ws-2", "工作区二", "C:/Code/WS2", [], DateTimeOffset.Now)
        ]);
        var sessionService = new AdoptionSessionService();
        var viewModel      = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(), workspaces);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.SelectedSession is not null);

        // 工作区组头「+」：进入同一张新对话草稿页并把预选工作区改为对应工作区；
        // 不调用 session/create、不产生侧栏会话行。
        var createsBefore = sessionService.CreatedSessionCount;
        var header = viewModel.Sidebar.SessionRows.OfType<SessionGroupHeaderViewModel>()
                              .Single(item => item.Key == "ws-2");
        viewModel.Sidebar.CreateWorkspaceSessionCommand.Execute(header);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        Assert.Equal("工作区二", viewModel.WorkspacePickerLabel);
        Assert.Equal(createsBefore, sessionService.CreatedSessionCount);
        Assert.Empty(sessionService.CreateRequests);
        viewModel.Composer.DraftMessage = "草稿文本";
        Assert.True(viewModel.CanSendDraft);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task FirstSendCreatesSessionAppliesPreselectedModelAndShowsRow()
    {
        var workspaces = new StaticWorkspaceService([
            new WorkspaceSummary("ws-1", "工作区一", "C:/Code/WS1", [], DateTimeOffset.Now)
        ]);
        var sessionService = new AdoptionSessionService(workspaces.AddSession);
        var viewModel      = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(), workspaces);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.Composer.ModelOptions.Count > 0);
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);

        // 草稿页本地预选模型（无 SessionId、不发 RPC），选型即时反映在下拉显示中。
        viewModel.SelectWorkspaceCommand.Execute(viewModel.WorkspaceOptions.Single(option => option.Id == "ws-1"));
        viewModel.Composer.SelectedModelOption =
            viewModel.Composer.ModelOptions.Single(option => option.Model == "alt-chat");
        Assert.Equal(new ModelSelection("sim-alt", "alt-chat"), viewModel.Composer.CurrentModel);
        Assert.Empty(sessionService.ModelSelectionRequests);

        viewModel.Composer.DraftMessage = "首条消息";
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SelectedSession is { BlankState: SessionBlankState.Engaged });
        var created = viewModel.SelectedSession!;

        // 创建携带工作区归属、无收养 id；预选模型在创建后对该 SessionId 应用；
        // 后端接受首条消息后才出现侧栏行（已开始），输入文本清空。
        var (workspaceId, adoptId) = Assert.Single(sessionService.CreateRequests);
        Assert.Equal("ws-1", workspaceId);
        Assert.Null(adoptId);
        Assert.Equal(new[] { created.Id }, sessionService.ModelSelectionRequests);
        Assert.Contains(viewModel.Sidebar.Sessions, session => session.Id == created.Id);
        Assert.False(viewModel.HasError);
        Assert.Equal(string.Empty, viewModel.Composer.DraftMessage);

        // 插入行后按工作区记账核对分组：新会话落在 ws-1 组内（默认分组视图）。
        var groupedRows = viewModel.Sidebar.SessionRows;
        var headerIndex = groupedRows.IndexOf(groupedRows.OfType<SessionGroupHeaderViewModel>()
                                                         .Single(header => header.Key == "ws-1"));
        var rowIndex = groupedRows.IndexOf(groupedRows.OfType<SessionItemViewModel>()
                                                      .Single(session => session.Id == created.Id));
        Assert.True(headerIndex >= 0 && rowIndex == headerIndex + 1);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task DraftSendCreateFailureKeepsDraftAndSelections()
    {
        var workspaces = new StaticWorkspaceService([
            new WorkspaceSummary("ws-1", "工作区一", "C:/Code/WS1", [], DateTimeOffset.Now)
        ]);
        var sessionService = new AdoptionSessionService();
        sessionService.EnqueueCreateError(new HarnessRpcException("session/conflict", "创建失败（模拟）"));
        var viewModel = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(), workspaces);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.SelectedSession is not null);
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        viewModel.SelectWorkspaceCommand.Execute(viewModel.WorkspaceOptions.Single(option => option.Id == "ws-1"));
        viewModel.Composer.DraftMessage = "失败保留草稿";
        var rowsBefore = viewModel.Sidebar.Sessions.Count;

        // 创建失败：文本与预选保留、停留草稿页、无新侧栏行。
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.HasError);
        Assert.Null(viewModel.SelectedSession);
        Assert.True(viewModel.ShowNewConversationPage);
        Assert.True(viewModel.CanSendDraft);
        Assert.Equal("失败保留草稿", viewModel.Composer.DraftMessage);
        Assert.Equal("工作区一", viewModel.WorkspacePickerLabel);
        Assert.Equal(rowsBefore, viewModel.Sidebar.Sessions.Count);
        Assert.Single(sessionService.CreateRequests);

        // 重试按同一草稿再次创建（恰好一次成功），后端接受后进入普通会话。
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SelectedSession is { BlankState: SessionBlankState.Engaged });
        Assert.Equal(1, sessionService.CreatedSessionCount);
        Assert.Equal(2, sessionService.CreateRequests.Count);
        Assert.False(viewModel.HasError);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task WorkspaceAttachFailureKeepsPendingSessionIdAndRetryReusesIt()
    {
        var workspaces = new StaticWorkspaceService([
            new WorkspaceSummary("ws-2", "工作区二", "C:/Code/WS2", [], DateTimeOffset.Now)
        ]);
        var sessionService = new AdoptionSessionService();
        // 会话本体已创建（错误 details 携带 sessionId）：种子该会话供后续发送命中。
        sessionService.SeedBlankSession("created-9", null);
        sessionService.EnqueueCreateError(new HarnessRpcException("session/workspace-attach-failed", "工作区挂接失败（模拟）",
                                                                  JsonDocument.Parse("""{"sessionId":"created-9"}""")
                                                                              .RootElement.Clone()));
        var viewModel = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(), workspaces);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.SelectedSession is not null);
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        viewModel.SelectWorkspaceCommand.Execute(viewModel.WorkspaceOptions.Single(option => option.Id == "ws-2"));
        viewModel.Composer.DraftMessage = "首条消息";

        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.HasError);
        Assert.Contains("工作区关联失败", viewModel.ErrorText, StringComparison.Ordinal);
        // 保留已知 id：不导航、不重试创建，草稿文本保留。
        Assert.True(viewModel.ShowNewConversationPage);
        Assert.Null(viewModel.SelectedSession);
        Assert.Equal("首条消息", viewModel.Composer.DraftMessage);
        Assert.Single(sessionService.CreateRequests);

        // 重试先经 session/create 收养（同一 sessionId + workspaceId）恢复关联，再发送；
        // 不产生第二个会话，发送成功后进入普通会话。
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SelectedSession?.Id == "created-9");
        Assert.Equal(SessionBlankState.Engaged, viewModel.SelectedSession!.BlankState);
        Assert.Equal(2, sessionService.CreateRequests.Count);
        Assert.Equal(("ws-2", (string?)null), sessionService.CreateRequests[0]);
        Assert.Equal(("ws-2", "created-9"), sessionService.CreateRequests[1]);
        Assert.Equal(0, sessionService.CreatedSessionCount);
        Assert.Equal(new[] { "created-9" }, sessionService.SendRequests);
        Assert.False(viewModel.HasError);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task LateDraftSendCompletionDoesNotStealNavigation()
    {
        var workspaces = new StaticWorkspaceService([
            new WorkspaceSummary("ws-2", "工作区二", "C:/Code/WS2", [], DateTimeOffset.Now)
        ]);
        var sessionService = new AdoptionSessionService();
        var viewModel      = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(), workspaces);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.SelectedSession is not null);
        var selectedBefore = viewModel.SelectedSession!;
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        viewModel.SelectWorkspaceCommand.Execute(viewModel.WorkspaceOptions.Single(option => option.Id == "ws-2"));
        viewModel.Composer.DraftMessage = "用户草稿";
        var knownIds = viewModel.Sidebar.Sessions.Select(session => session.Id).ToHashSet();

        // 挂起创建后用户切回历史会话：迟到的首发送结果不抢回界面、不动其他会话的草稿；
        // 消息已发出，草稿页文本被消费，新会话以已开始状态进入侧栏。
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sessionService.BlockNextCreate = gate;
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => sessionService.CreateRequests.Count == 1);
        viewModel.Sidebar.SelectSessionCommand.Execute(selectedBefore);
        gate.TrySetResult();
        // 等待首发送编排与一次列表合并刷新完全落定（400ms 合并窗 + 余量）。
        await Task.Delay(900);
        Assert.Same(selectedBefore, viewModel.SelectedSession);
        Assert.False(viewModel.HasError);
        var lateRow = viewModel.Sidebar.Sessions.FirstOrDefault(session => !knownIds.Contains(session.Id));
        Assert.NotNull(lateRow);
        Assert.Equal(SessionBlankState.Engaged, lateRow!.BlankState);
        // 草稿已被消费：重新进入草稿页不再有未发送文本，也不复用已发送内容。
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        Assert.Equal(string.Empty, viewModel.Composer.DraftMessage);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task DraftSelectionsAndTextPersistAcrossSessionSwitches()
    {
        var workspaces = new StaticWorkspaceService([
            new WorkspaceSummary("ws-1", "工作区一", "C:/Code/WS1", [], DateTimeOffset.Now)
        ]);
        var sessionService = new AdoptionSessionService();
        var viewModel      = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(), workspaces);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.Composer.ModelOptions.Count > 0);
        var selectedBefore = viewModel.SelectedSession;
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);

        // 草稿页填写文本、预选工作区与模型：全部只记本地草稿，无任何 RPC。
        viewModel.Composer.DraftMessage = "新对话草稿";
        viewModel.SelectWorkspaceCommand.Execute(viewModel.WorkspaceOptions.Single(option => option.Id == "ws-1"));
        viewModel.Composer.SelectedModelOption =
            viewModel.Composer.ModelOptions.Single(option => option.Model == "alt-chat");
        var createsBefore = sessionService.CreatedSessionCount;
        Assert.Empty(sessionService.CreateRequests);

        // 切到旧会话再返回：文本、预选工作区与预选模型都恢复；已有会话各自草稿不受影响。
        viewModel.Sidebar.SelectSessionCommand.Execute(selectedBefore);
        await WaitUntilAsync(() => !viewModel.ShowNewConversationPage);
        Assert.Equal(string.Empty, viewModel.Composer.DraftMessage);
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        Assert.Equal("新对话草稿", viewModel.Composer.DraftMessage);
        Assert.Equal("工作区一", viewModel.WorkspacePickerLabel);
        Assert.Equal(new ModelSelection("sim-alt", "alt-chat"), viewModel.Composer.CurrentModel);
        Assert.Equal(createsBefore, sessionService.CreatedSessionCount);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task NewConversationPageVisibilityFollowsSelection()
    {
        var workspaces = new StaticWorkspaceService([
            new WorkspaceSummary("ws-1", "工作区一", "C:/Code/WS1", ["blank-a"], DateTimeOffset.Now)
        ]);
        var sessionService = new AdoptionSessionService();
        sessionService.SeedBlankSession("blank-a", "C:/Code/WS1");
        var unknownId = "session-unknown-legacy";
        sessionService.SessionsOverride = () =>
        [
            new SessionSummary(unknownId, null, DateTimeOffset.Now, false, SessionBlankState.Unknown),
            .. sessionService.Snapshot()
        ];
        var viewModel = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(), workspaces);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.SelectedSession is not null);

        // 未知状态的历史会话（元数据缺失）：可见但绝不显示新对话草稿页。
        Assert.Equal(unknownId, viewModel.SelectedSession!.Id);
        Assert.Contains(viewModel.Sidebar.Sessions, session => session.Id == unknownId);
        Assert.False(viewModel.ShowNewConversationPage);

        // 顶部新建：进入草稿页（不创建会话）；工作区下拉含「不使用工作区」显式兼容项。
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        Assert.Equal(0, sessionService.CreatedSessionCount);
        Assert.Contains(viewModel.WorkspaceOptions, option => option.IsWithoutWorkspace);

        // 选中任一会话：草稿页整体隐藏。
        var anySession = viewModel.Sidebar.Sessions.First(session => session.Id != unknownId);
        viewModel.Sidebar.SelectSessionCommand.Execute(anySession);
        await WaitUntilAsync(() => !viewModel.ShowNewConversationPage);

        await viewModel.DisposeAsync();
    }

    /// <summary>
    ///     创建/收养可控桩：记录每次 session/create 请求（workspaceId, sessionId），可注入
    ///     业务错误与阻塞门；会话列表可整体接管（SessionsOverride）。其余行为委托模拟实现。
    [Fact]
    public async Task ListRefreshKeepsIntentionalDraftPageInsteadOfFallingBack()
    {
        var sessionService = new SimulatedSessionService();
        var viewModel = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(),
                                                new StaticWorkspaceService());
        await viewModel.InitializeAsync();
        // 初始化默认选中仍由首次刷新完成（不受草稿页守卫影响）。
        await WaitUntilAsync(() => viewModel.SelectedSession is not null);

        // 主动进入草稿页后，创建会话等触发的列表刷新（SessionsChanged → 400ms 合并）
        // 不得把草稿页抢回旧会话；草稿文本保持。
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        viewModel.Composer.DraftMessage = "草稿文本";
        var created = await sessionService.CreateSessionAsync();
        await Task.Delay(900);
        Assert.Null(viewModel.SelectedSession);
        Assert.True(viewModel.ShowNewConversationPage);

        // 手动点击行不受守卫影响；再进草稿页文本仍在。
        viewModel.Sidebar.SelectSessionCommand.Execute(viewModel.Sidebar.Sessions.First(session => session.Id !=
                                                                    created.Id));
        await WaitUntilAsync(() => !viewModel.ShowNewConversationPage);
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        Assert.Equal("草稿文本", viewModel.Composer.DraftMessage);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task TargetChangeDuringFirstSendKeepsNewDraftAndSkipsStaleNavigation()
    {
        var workspaces = new StaticWorkspaceService([
            new WorkspaceSummary("ws-1", "工作区一", "C:/Code/WS1", [], DateTimeOffset.Now),
            new WorkspaceSummary("ws-2", "工作区二", "C:/Code/WS2", [], DateTimeOffset.Now.AddMinutes(-1))
        ]);
        var sessionService = new AdoptionSessionService();
        var viewModel      = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(), workspaces);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.SelectedSession is not null);
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        viewModel.SelectWorkspaceCommand.Execute(viewModel.WorkspaceOptions.Single(option => option.Id == "ws-1"));
        viewModel.Composer.DraftMessage = "发送快照文本";
        var knownIds = viewModel.Sidebar.Sessions.Select(session => session.Id).ToHashSet();

        // 创建挂起期间把目标改到 ws-2：旧发送按快照继续（消息进入 ws-1 的会话），
        // 完成后不抢回界面、不清空新目标草稿，迟到会话不记为待复用。
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sessionService.BlockNextCreate = gate;
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => sessionService.CreateRequests.Count == 1);
        viewModel.SelectWorkspaceCommand.Execute(viewModel.WorkspaceOptions.Single(option => option.Id == "ws-2"));
        gate.TrySetResult();
        await WaitOrDumpAsync(viewModel,
                              () => viewModel.Sidebar.Sessions.Any(session => !knownIds.Contains(session.Id) &&
                                                                              session.BlankState ==
                                                                              SessionBlankState.Engaged), 5000);
        Assert.True(viewModel.ShowNewConversationPage);
        Assert.Equal("工作区二", viewModel.WorkspacePickerLabel);
        Assert.Equal("发送快照文本", viewModel.Composer.DraftMessage);
        var staleRow = viewModel.Sidebar.Sessions.Single(session => !knownIds.Contains(session.Id));
        Assert.Equal(SessionBlankState.Engaged, staleRow.BlankState);

        // 迟到会话未记为待复用：按新目标重试会创建新会话（ws-2）并正常导航收束。
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SelectedSession is { BlankState: SessionBlankState.Engaged } &&
                                   viewModel.SelectedSession.Id != staleRow.Id);
        Assert.Equal(2, sessionService.CreatedSessionCount);
        Assert.Equal(2, sessionService.CreateRequests.Count);
        Assert.Equal("ws-2", sessionService.CreateRequests[1].WorkspaceId);
        Assert.False(viewModel.HasError);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task LateDraftSendDoesNotClearSwitchedSessionDraftWithIdenticalText()
    {
        var workspaces = new StaticWorkspaceService([
            new WorkspaceSummary("ws-1", "工作区一", "C:/Code/WS1", [], DateTimeOffset.Now)
        ]);
        var sessionService = new AdoptionSessionService();
        var viewModel      = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(), workspaces);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.SelectedSession is not null);
        var otherSession = viewModel.SelectedSession!;
        viewModel.Composer.DraftMessage = "相同文本";

        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        viewModel.SelectWorkspaceCommand.Execute(viewModel.WorkspaceOptions.Single(option => option.Id == "ws-1"));
        viewModel.Composer.DraftMessage = "相同文本";
        var knownIds = viewModel.Sidebar.Sessions.Select(session => session.Id).ToHashSet();

        // 首发送挂起期间切回旧会话，且该会话草稿恰好与发送快照相同：
        // 迟到的成功发送只消费原草稿缓存，不得清空旧会话自己的输入框文本。
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sessionService.BlockNextCreate = gate;
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => sessionService.CreateRequests.Count == 1);
        viewModel.Sidebar.SelectSessionCommand.Execute(otherSession);
        gate.TrySetResult();
        await Task.Delay(900);
        Assert.Same(otherSession, viewModel.SelectedSession);
        Assert.Equal("相同文本", viewModel.Composer.DraftMessage);
        Assert.False(viewModel.HasError);
        var lateRow = viewModel.Sidebar.Sessions.FirstOrDefault(session => !knownIds.Contains(session.Id));
        Assert.NotNull(lateRow);
        Assert.Equal(SessionBlankState.Engaged, lateRow!.BlankState);

        // 原草稿缓存已消费：再进草稿页不再有未发送文本。
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        Assert.Equal(string.Empty, viewModel.Composer.DraftMessage);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task EmptySessionListStartupKeepsDraftModelPickerEnabledWithoutCreate()
    {
        var sessionService = new AdoptionSessionService { SessionsOverride = () => [] };
        var viewModel = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(),
                                                new StaticWorkspaceService());
        await viewModel.InitializeAsync();

        // 无历史会话启动：初始即草稿页（SelectedSession null→null 不触发 setter），模型
        // 目录加载完成后菜单即可用并支持本地预选（不发 RPC）；不发起任何创建/收养请求，
        // 再点「新建」也保持可用。
        Assert.Null(viewModel.SelectedSession);
        Assert.True(viewModel.ShowNewConversationPage);
        await WaitUntilAsync(() => viewModel.Composer.ModelOptions.Count > 0);
        Assert.True(viewModel.Composer.IsModelPickerEnabled);
        viewModel.Composer.SelectedModelOption =
            viewModel.Composer.ModelOptions.Single(option => option.Model == "alt-chat");
        Assert.Equal(new ModelSelection("sim-alt", "alt-chat"), viewModel.Composer.CurrentModel);
        Assert.Empty(sessionService.ModelSelectionRequests);

        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        Assert.True(viewModel.Composer.IsModelPickerEnabled);
        Assert.Empty(sessionService.CreateRequests);
        Assert.Equal(0, sessionService.CreatedSessionCount);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task RepeatedNewPageEntryFromNullStatePreservesDraftAndPreselection()
    {
        var workspaces = new StaticWorkspaceService([
            new WorkspaceSummary("ws-1", "工作区一", "C:/Code/WS1", [], DateTimeOffset.Now)
        ]);
        var sessionService = new AdoptionSessionService();
        var viewModel      = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(), workspaces);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.SelectedSession is not null);
        await WaitUntilAsync(() => viewModel.WorkspaceOptions.Any(option => option.Id == "ws-1"));

        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        viewModel.Composer.DraftMessage = "重复进入的草稿";
        viewModel.SelectWorkspaceCommand.Execute(viewModel.WorkspaceOptions.Single(option => option.Id == "ws-1"));
        viewModel.Composer.SelectedModelOption =
            viewModel.Composer.ModelOptions.Single(option => option.Model == "alt-chat");
        Assert.True(viewModel.Composer.IsModelPickerEnabled);

        // 已处于 null（草稿页）状态再次点「新建」：草稿页初始化可重入，草稿文本、工作区
        // 与模型预选全部保留，不触发任何 RPC。
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        Assert.Null(viewModel.SelectedSession);
        Assert.Equal("重复进入的草稿", viewModel.Composer.DraftMessage);
        Assert.Equal("工作区一", viewModel.WorkspacePickerLabel);
        Assert.Equal(new ModelSelection("sim-alt", "alt-chat"), viewModel.Composer.CurrentModel);
        Assert.True(viewModel.Composer.IsModelPickerEnabled);
        Assert.Empty(sessionService.CreateRequests);
        Assert.Empty(sessionService.ModelSelectionRequests);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task LateSendCompletionAfterReturningToDraftPageClearsSentText()
    {
        var workspaces = new StaticWorkspaceService([
            new WorkspaceSummary("ws-2", "工作区二", "C:/Code/WS2", [], DateTimeOffset.Now)
        ]);
        var sessionService = new AdoptionSessionService();
        var viewModel      = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(), workspaces);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.SelectedSession is not null);
        var selectedBefore = viewModel.SelectedSession!;
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        viewModel.SelectWorkspaceCommand.Execute(viewModel.WorkspaceOptions.Single(option => option.Id == "ws-2"));
        viewModel.Composer.DraftMessage = "切走又回来的草稿";
        var knownIds = viewModel.Sidebar.Sessions.Select(session => session.Id).ToHashSet();

        // 首发送挂起期间切到旧会话又返回草稿页（草稿未改）：发送完成后缓存与当前显示
        // 一并收束——已发送文字不残留在输入框，也不在再次进入时复活。
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sessionService.BlockNextCreate = gate;
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => sessionService.CreateRequests.Count == 1);
        viewModel.Sidebar.SelectSessionCommand.Execute(selectedBefore);
        await WaitUntilAsync(() => !viewModel.ShowNewConversationPage);
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        Assert.Equal("切走又回来的草稿", viewModel.Composer.DraftMessage);
        gate.TrySetResult();
        await WaitOrDumpAsync(viewModel,
                              () => viewModel.Sidebar.Sessions.Any(session => !knownIds.Contains(session.Id) &&
                                                                              session.BlankState ==
                                                                              SessionBlankState.Engaged), 5000);
        // 迟到结果不抢回页面：仍停留草稿页，输入框与工作区预选均已清空。
        Assert.Null(viewModel.SelectedSession);
        Assert.True(viewModel.ShowNewConversationPage);
        Assert.Equal(string.Empty, viewModel.Composer.DraftMessage);
        Assert.Equal("选择工作区", viewModel.WorkspacePickerLabel);
        Assert.False(viewModel.HasError);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task TextEditedDuringSendThenSwitchAwayPreservesNewDraftSelections()
    {
        var workspaces = new StaticWorkspaceService([
            new WorkspaceSummary("ws-1", "工作区一", "C:/Code/WS1", [], DateTimeOffset.Now)
        ]);
        var sessionService = new AdoptionSessionService();
        var viewModel      = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(), workspaces);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.Composer.ModelOptions.Count > 0);
        await WaitUntilAsync(() => viewModel.SelectedSession is not null);
        var selectedBefore = viewModel.SelectedSession!;
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        viewModel.SelectWorkspaceCommand.Execute(viewModel.WorkspaceOptions.Single(option => option.Id == "ws-1"));
        viewModel.Composer.SelectedModelOption =
            viewModel.Composer.ModelOptions.Single(option => option.Model == "alt-chat");
        viewModel.Composer.DraftMessage = "原始快照";
        var knownIds = viewModel.Sidebar.Sessions.Select(session => session.Id).ToHashSet();

        // 发送挂起期间改写文本形成新草稿再切走：旧发送按快照完成，返回草稿页时新草稿的
        // 文本、工作区与模型预选完整保留（不能只保留文字却重置工作区）。
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sessionService.BlockNextCreate = gate;
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => sessionService.CreateRequests.Count == 1);
        viewModel.Composer.DraftMessage = "改写后的新草稿";
        viewModel.Sidebar.SelectSessionCommand.Execute(selectedBefore);
        await WaitUntilAsync(() => !viewModel.ShowNewConversationPage);
        gate.TrySetResult();
        await WaitOrDumpAsync(viewModel,
                              () => viewModel.Sidebar.Sessions.Any(session => !knownIds.Contains(session.Id) &&
                                                                              session.BlankState ==
                                                                              SessionBlankState.Engaged), 5000);
        Assert.Same(selectedBefore, viewModel.SelectedSession);

        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        Assert.Equal("改写后的新草稿", viewModel.Composer.DraftMessage);
        Assert.Equal("工作区一", viewModel.WorkspacePickerLabel);
        Assert.Equal(new ModelSelection("sim-alt", "alt-chat"), viewModel.Composer.CurrentModel);
        Assert.False(viewModel.HasError);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task ModelChangeDuringSendKeepsNewSelectionAndSkipsStaleNavigation()
    {
        var workspaces = new StaticWorkspaceService([
            new WorkspaceSummary("ws-1", "工作区一", "C:/Code/WS1", [], DateTimeOffset.Now)
        ]);
        var sessionService = new AdoptionSessionService();
        var viewModel      = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(), workspaces);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.Composer.ModelOptions.Count > 0);
        await WaitUntilAsync(() => viewModel.SelectedSession is not null);
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        viewModel.SelectWorkspaceCommand.Execute(viewModel.WorkspaceOptions.Single(option => option.Id == "ws-1"));
        viewModel.Composer.SelectedModelOption =
            viewModel.Composer.ModelOptions.Single(option => option.Model == "alt-chat");
        viewModel.Composer.DraftMessage = "模型改选期间";
        var knownIds = viewModel.Sidebar.Sessions.Select(session => session.Id).ToHashSet();

        // 创建挂起期间改选模型（新草稿意图）：旧发送按快照把预选模型应用到创建的会话并
        // 完成，但不抢回页面、不覆盖草稿的新选型、文本与工作区。
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sessionService.BlockNextCreate = gate;
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => sessionService.CreateRequests.Count == 1);
        viewModel.Composer.SelectedModelOption =
            viewModel.Composer.ModelOptions.Single(option => option.Model == "sim-reasoner");
        gate.TrySetResult();
        await WaitOrDumpAsync(viewModel,
                              () => viewModel.Sidebar.Sessions.Any(session => !knownIds.Contains(session.Id) &&
                                                                              session.BlankState ==
                                                                              SessionBlankState.Engaged), 5000);
        var createdRow = viewModel.Sidebar.Sessions.Single(session => !knownIds.Contains(session.Id));
        Assert.Null(viewModel.SelectedSession);
        Assert.True(viewModel.ShowNewConversationPage);
        Assert.Equal("模型改选期间", viewModel.Composer.DraftMessage);
        Assert.Equal("工作区一", viewModel.WorkspacePickerLabel);
        Assert.Equal(new ModelSelection("sim", "sim-reasoner"), viewModel.Composer.CurrentModel);
        // 快照的选型（alt-chat）恰好应用一次到创建的会话；草稿保持新选型。
        Assert.Equal(new[] { $"select:{createdRow.Id}:sim-alt/alt-chat" },
                     sessionService.OperationLog.Where(log => log.StartsWith("select:", StringComparison.Ordinal))
                                   .ToArray());
        Assert.False(viewModel.HasError);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task AttachFailureRetryRecoversAssociationWithSameIdsBeforeSending()
    {
        var workspaces = new StaticWorkspaceService([
            new WorkspaceSummary("ws-2", "工作区二", "C:/Code/WS2", [], DateTimeOffset.Now)
        ]);
        var sessionService = new AdoptionSessionService(workspaces.AddSession);
        // 中间状态：会话本体已存在（created-9，确认空白），但尚未归属任何工作区。
        sessionService.SeedBlankSession("created-9", null);
        sessionService.EnqueueCreateError(new HarnessRpcException("session/workspace-attach-failed",
                                                                  "工作区挂接失败（模拟）",
                                                                  JsonDocument.Parse("""{"sessionId":"created-9"}""")
                                                                              .RootElement.Clone()));
        var viewModel = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(), workspaces);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.SelectedSession is not null);
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        viewModel.SelectWorkspaceCommand.Execute(viewModel.WorkspaceOptions.Single(option => option.Id == "ws-2"));
        viewModel.Composer.DraftMessage = "关联恢复后发送";
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.HasError);

        // 重试：恢复关联的收养调用（同一 sessionId + workspaceId）被门挂住期间不得发送。
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sessionService.BlockNextCreate = gate;
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => sessionService.CreateRequests.Count == 2);
        Assert.Equal(("ws-2", "created-9"), sessionService.CreateRequests[1]);
        Assert.Empty(sessionService.SendRequests);
        gate.TrySetResult();
        await WaitUntilAsync(() => viewModel.SelectedSession?.Id == "created-9");
        Assert.Equal(SessionBlankState.Engaged, viewModel.SelectedSession!.BlankState);

        // 顺序与记账：收养先于发送；关联确实写入工作区投影（非仅导航成功）。
        var attachIndex = sessionService.OperationLog.IndexOf("create:ws-2|created-9");
        var sendIndex   = sessionService.OperationLog.IndexOf("send:created-9");
        Assert.True(attachIndex >= 0 && sendIndex >= 0 && attachIndex < sendIndex);
        var accounted = (await workspaces.GetWorkspacesAsync()).Single(workspace => workspace.Id == "ws-2");
        Assert.Contains("created-9", accounted.SessionIds);
        Assert.Equal(0, sessionService.CreatedSessionCount);
        Assert.False(viewModel.HasError);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task AttachRecoveryFailureAgainKeepsRecoverableDraftWithoutSending()
    {
        var workspaces = new StaticWorkspaceService([
            new WorkspaceSummary("ws-2", "工作区二", "C:/Code/WS2", [], DateTimeOffset.Now)
        ]);
        var sessionService = new AdoptionSessionService();
        sessionService.SeedBlankSession("created-9", null);
        sessionService.EnqueueCreateError(new HarnessRpcException("session/workspace-attach-failed",
                                                                  "工作区挂接失败（模拟）",
                                                                  JsonDocument.Parse("""{"sessionId":"created-9"}""")
                                                                              .RootElement.Clone()));
        var viewModel = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(), workspaces);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.SelectedSession is not null);
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        viewModel.SelectWorkspaceCommand.Execute(viewModel.WorkspaceOptions.Single(option => option.Id == "ws-2"));
        viewModel.Composer.DraftMessage = "关联失败重试的草稿";
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.HasError);

        // 关联恢复再次失败（收养调用同样抛 attach-failed）：保留草稿与可恢复状态，
        // 不发送、不重复创建，如实展示错误。
        sessionService.EnqueueCreateError(new HarnessRpcException("session/workspace-attach-failed",
                                                                  "再次挂接失败（模拟）",
                                                                  JsonDocument.Parse("""{"sessionId":"created-9"}""")
                                                                              .RootElement.Clone()),
                                          onlyForNewSession: false);
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => sessionService.CreateRequests.Count == 2 && !viewModel.IsStartingConversation);
        Assert.Empty(sessionService.SendRequests);
        Assert.Equal(0, sessionService.CreatedSessionCount);
        Assert.True(viewModel.ShowNewConversationPage);
        Assert.Equal("关联失败重试的草稿", viewModel.Composer.DraftMessage);
        Assert.Contains("工作区关联失败", viewModel.ErrorText, StringComparison.Ordinal);

        // 可恢复状态未丢：再次重试从恢复关联开始并最终完成发送。
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SelectedSession?.Id == "created-9");
        Assert.Equal(new[] { "created-9" }, sessionService.SendRequests);
        Assert.Equal(0, sessionService.CreatedSessionCount);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task SelectModelFailureAfterAttachReusesSessionOnRetry()
    {
        var workspaces = new StaticWorkspaceService([
            new WorkspaceSummary("ws-1", "工作区一", "C:/Code/WS1", [], DateTimeOffset.Now)
        ]);
        var sessionService = new AdoptionSessionService(workspaces.AddSession);
        var viewModel      = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(), workspaces);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.Composer.ModelOptions.Count > 0);
        await WaitUntilAsync(() => viewModel.SelectedSession is not null);
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        viewModel.SelectWorkspaceCommand.Execute(viewModel.WorkspaceOptions.Single(option => option.Id == "ws-1"));
        viewModel.Composer.SelectedModelOption =
            viewModel.Composer.ModelOptions.Single(option => option.Model == "alt-chat");
        viewModel.Composer.DraftMessage = "选型失败重试";
        sessionService.EnqueueSelectModelError(new InvalidOperationException("选型失败（模拟）"));

        // 创建与关联已成功、仅选型失败：重试不得再次创建/收养，直接复用会话完成选型与发送。
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.HasError);
        Assert.Contains("选型失败", viewModel.ErrorText, StringComparison.Ordinal);
        Assert.Single(sessionService.CreateRequests);
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SelectedSession is { BlankState: SessionBlankState.Engaged });
        var createdId = viewModel.SelectedSession!.Id;
        Assert.Single(sessionService.CreateRequests);
        Assert.Equal(1, sessionService.CreatedSessionCount);
        Assert.Equal(new[] { createdId, createdId }, sessionService.ModelSelectionRequests);
        Assert.Equal(new[] { createdId }, sessionService.SendRequests);
        Assert.False(viewModel.HasError);

        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task SendFailureAfterAttachReusesSessionOnRetry()
    {
        var workspaces = new StaticWorkspaceService([
            new WorkspaceSummary("ws-1", "工作区一", "C:/Code/WS1", [], DateTimeOffset.Now)
        ]);
        var sessionService = new AdoptionSessionService(workspaces.AddSession);
        var viewModel      = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(), workspaces);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.SelectedSession is not null);
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        viewModel.SelectWorkspaceCommand.Execute(viewModel.WorkspaceOptions.Single(option => option.Id == "ws-1"));
        viewModel.Composer.DraftMessage = "发送失败重试";
        sessionService.EnqueueSendError(new InvalidOperationException("发送失败（模拟）"));

        // 关联已完成、仅发送失败：会话记为待复用，重试不再创建/收养，对同一会话再次发送。
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.HasError);
        Assert.Contains("发送失败", viewModel.ErrorText, StringComparison.Ordinal);
        Assert.Single(sessionService.CreateRequests);
        Assert.Single(sessionService.SendRequests);

        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SelectedSession is { BlankState: SessionBlankState.Engaged });
        Assert.Single(sessionService.CreateRequests);
        Assert.Equal(2, sessionService.SendRequests.Count);
        Assert.True(sessionService.SendRequests.All(id => id == viewModel.SelectedSession!.Id));
        Assert.Equal(1, sessionService.CreatedSessionCount);
        Assert.False(viewModel.HasError);

        await viewModel.DisposeAsync();
    }

    private static void EnsureAvaloniaPlatform()
    {
        if (_avaloniaIsInitialized) return;

        lock (AvaloniaSetupLock)
        {
            if (_avaloniaIsInitialized) return;

            AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
            _avaloniaIsInitialized = true;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds = 2000)
    {
        var timeout = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (DateTime.UtcNow < timeout)
        {
            var satisfied = TryCondition(condition);
            if (satisfied) return;

            await Task.Delay(10);
        }

        Assert.True(TryCondition(condition), "预期的异步 ViewModel 状态未在超时前出现。");
    }

    /// <summary>条件枚举集合时可能撞上更新线程的并发修改：视为未满足，下一轮重试。</summary>
    private static bool TryCondition(Func<bool> condition)
    {
        try
        {
            return condition();
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    ///     中途 attach 桩：快照窗口含未完结轮的条目，尾部是 Host 合成的 interrupted 边界；
    ///     随后可推送该轮的真实收尾事件（工具、最终回复与 turn/end）。
    /// </summary>
    private sealed class SyntheticBoundarySessionService : ISessionService
    {
        private readonly SimulatedSessionService _inner = new();

        private readonly Channel<SessionUpdate> _liveTail = Channel.CreateUnbounded<SessionUpdate>();

        public event EventHandler? SessionsChanged
        {
            add => _inner.SessionsChanged += value;
            remove => _inner.SessionsChanged -= value;
        }

        public Task<IReadOnlyList<SessionSummary>> GetSessionsAsync(CancellationToken cancellationToken = default)
        {
            return _inner.GetSessionsAsync(cancellationToken);
        }

        public Task<SessionSummary> CreateSessionAsync(
            string? workspaceId = null, string? sessionId = null, CancellationToken cancellationToken = default)
        {
            return _inner.CreateSessionAsync(workspaceId, sessionId, cancellationToken);
        }

        public void MarkSessionEngaged(string sessionId)
        {
            _inner.MarkSessionEngaged(sessionId);
        }

        public Task<ModelCatalog> GetModelCatalogAsync(CancellationToken cancellationToken = default)
        {
            return _inner.GetModelCatalogAsync(cancellationToken);
        }

        public Task<ModelSelection> SelectModelAsync(
            string  sessionId,              string            provider, string model,
            string? reasoningEffort = null, CancellationToken cancellationToken = default)
        {
            return _inner.SelectModelAsync(sessionId, provider, model, reasoningEffort, cancellationToken);
        }

        public Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(
            string sessionId, CancellationToken cancellationToken = default)
        {
            return _inner.GetMessagesAsync(sessionId, cancellationToken);
        }

        public Task<SessionHistoryPage> LoadOlderAsync(
            string sessionId, long throughSeq, long beforeSeq, CancellationToken cancellationToken = default)
        {
            return _inner.LoadOlderAsync(sessionId, throughSeq, beforeSeq, cancellationToken);
        }

        public Task SendPromptAsync(
            string sessionId, string requestId, string content, CancellationToken cancellationToken = default)
        {
            return _inner.SendPromptAsync(sessionId, requestId, content, cancellationToken);
        }

        public Task CancelAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            return _inner.CancelAsync(sessionId, cancellationToken);
        }

        public async IAsyncEnumerable<SessionUpdate> FollowSessionAsync(
            string sessionId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.Now;
            ConversationEntry[] entries =
            [
                new ConversationMessage(1, "attach-user", MessageRole.User, "中途 attach 的问题", now, 1),
                new ConversationMessage(2, "attach-interim", MessageRole.Assistant, "先看快照形状。", now, 1),
                new ToolActivity(3, "call-attach-1", "fs.read", "{}", ToolActivityStatus.Succeeded,
                                 "读取结果", null, now, now.AddSeconds(1), 1),
                new ConversationMessage(4, "attach-mid", MessageRole.Assistant, "阶段性回复", now, 1),
                // Host 为开放轮合成的边界：interrupted、seq 即 cursor，持久日志中不存在。
                new TurnBoundary(5, 1, now, "interrupted")
            ];
            yield return new SessionUpdate.Snapshot(entries, 5, 1, false, "中途 attach");
            await foreach (var update in _liveTail.Reader.ReadAllAsync(cancellationToken)) yield return update;
        }

        public void PushLiveTail()
        {
            var now = DateTimeOffset.Now;
            _liveTail.Writer.TryWrite(new SessionUpdate.ToolCallStarted(new ToolActivity(6, "call-attach-2", "fs.read",
                                                                                 "{}", ToolActivityStatus.Running, null,
                                                                                 null, now, Turn : 1)));
            _liveTail.Writer.TryWrite(new SessionUpdate.MessageAppended(new ConversationMessage(7, "attach-final",
                                                                                 MessageRole.Assistant, "真正的最终回复", now,
                                                                                 1)));
        }

        public void PushRealTurnEnd()
        {
            _liveTail.Writer.TryWrite(new SessionUpdate.TurnEnded(1, 8, "completed"));
        }
    }

    /// <summary>
    ///     创建/收养可控桩：记录每次 session/create 请求（workspaceId, sessionId），可注入
    ///     业务错误与阻塞门；会话列表可整体接管（SessionsOverride）。其余行为委托模拟实现。
    /// </summary>
    private sealed class AdoptionSessionService(Action<string, string>? onSessionCreatedInWorkspace = null)
        : ISessionService
    {
        private readonly Queue<(Exception Error, bool OnlyForNewSession)> _createErrors      = [];
        private readonly Queue<Exception>                                _selectModelErrors = [];
        private readonly Queue<Exception>                                _sendErrors        = [];
        private readonly SimulatedSessionService                          _inner             = new(onSessionCreatedInWorkspace);

        /// <summary>下一次 create 阻塞到手动放行（并发合并与迟到结果测试用）。</summary>
        public TaskCompletionSource? BlockNextCreate { get; set; }

        public List<(string? WorkspaceId, string? SessionId)> CreateRequests { get; } = [];

        public List<string> ModelSelectionRequests { get; } = [];

        /// <summary>发送调用目标记录（attach 恢复顺序断言用）。</summary>
        public List<string> SendRequests { get; } = [];

        /// <summary>
        ///     调用顺序日志：create:&lt;ws&gt;|&lt;sessionId&gt;、select:&lt;sessionId&gt;:&lt;provider&gt;/&lt;model&gt;、
        ///     send:&lt;sessionId&gt;。用于断言「关联恢复先于发送」等顺序约束。
        /// </summary>
        public List<string> OperationLog { get; } = [];

        /// <summary>接管会话列表输出（未知状态会话测试用）；null 时走模拟实现。</summary>
        public Func<IReadOnlyList<SessionSummary>>? SessionsOverride { get; set; }

        /// <summary>模拟实现的真实新建（非收养）计数。</summary>
        public int CreatedSessionCount => _inner.CreatedSessionCount;

        public event EventHandler? SessionsChanged
        {
            add => _inner.SessionsChanged += value;
            remove => _inner.SessionsChanged -= value;
        }

        public async Task<IReadOnlyList<SessionSummary>> GetSessionsAsync(
            CancellationToken cancellationToken = default)
        {
            if (SessionsOverride is { } @override) return @override();

            return await _inner.GetSessionsAsync(cancellationToken);
        }

        public async Task<SessionSummary> CreateSessionAsync(
            string? workspaceId = null, string? sessionId = null, CancellationToken cancellationToken = default)
        {
            CreateRequests.Add((workspaceId, sessionId));
            OperationLog.Add($"create:{workspaceId ?? "-"}|{sessionId ?? "-"}");
            if (BlockNextCreate is { } gate)
            {
                BlockNextCreate = null;
                await gate.Task.WaitAsync(cancellationToken);
            }

            if (_createErrors.TryPeek(out var entry) && (!entry.OnlyForNewSession || sessionId is null))
            {
                _createErrors.Dequeue();
                throw entry.Error;
            }

            return await _inner.CreateSessionAsync(workspaceId, sessionId, cancellationToken);
        }

        public void MarkSessionEngaged(string sessionId)
        {
            _inner.MarkSessionEngaged(sessionId);
        }

        public async Task<ModelSelection> SelectModelAsync(
            string  sessionId,              string            provider, string model,
            string? reasoningEffort = null, CancellationToken cancellationToken = default)
        {
            ModelSelectionRequests.Add(sessionId);
            OperationLog.Add($"select:{sessionId}:{provider}/{model}");
            if (_selectModelErrors.Count > 0) throw _selectModelErrors.Dequeue();

            return await _inner.SelectModelAsync(sessionId, provider, model, reasoningEffort, cancellationToken);
        }

        public Task<ModelCatalog> GetModelCatalogAsync(CancellationToken cancellationToken = default)
        {
            return _inner.GetModelCatalogAsync(cancellationToken);
        }

        public Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(
            string sessionId, CancellationToken cancellationToken = default)
        {
            return _inner.GetMessagesAsync(sessionId, cancellationToken);
        }

        public Task<SessionHistoryPage> LoadOlderAsync(
            string sessionId, long throughSeq, long beforeSeq, CancellationToken cancellationToken = default)
        {
            return _inner.LoadOlderAsync(sessionId, throughSeq, beforeSeq, cancellationToken);
        }

        public async Task SendPromptAsync(
            string sessionId, string requestId, string content, CancellationToken cancellationToken = default)
        {
            SendRequests.Add(sessionId);
            OperationLog.Add($"send:{sessionId}");
            if (_sendErrors.Count > 0) throw _sendErrors.Dequeue();

            await _inner.SendPromptAsync(sessionId, requestId, content, cancellationToken);
        }

        public Task CancelAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            return _inner.CancelAsync(sessionId, cancellationToken);
        }

        public IAsyncEnumerable<SessionUpdate> FollowSessionAsync(
            string sessionId, CancellationToken cancellationToken = default)
        {
            return _inner.FollowSessionAsync(sessionId, cancellationToken);
        }

        public void SeedBlankSession(string sessionId, string? cwd)
        {
            _inner.SeedBlankSession(sessionId, cwd);
        }

        /// <summary>模拟实现当前目录快照（供 SessionsOverride 组合）。</summary>
        public IReadOnlyList<SessionSummary> Snapshot()
        {
            return _inner.GetSessionsAsync().Result;
        }

        /// <summary>
        ///     注入一次 create 业务错误。onlyForNewSession 为 true 时只作用于新建调用
        ///     （sessionId 为空），收养调用（复用候选）不受影响。
        /// </summary>
        public void EnqueueCreateError(Exception exception, bool onlyForNewSession = true)
        {
            _createErrors.Enqueue((exception, onlyForNewSession));
        }

        /// <summary>注入一次选型错误（关联完成后的失败重试路径测试用）。</summary>
        public void EnqueueSelectModelError(Exception exception)
        {
            _selectModelErrors.Enqueue(exception);
        }

        /// <summary>注入一次发送错误（关联完成后的失败重试路径测试用）。</summary>
        public void EnqueueSendError(Exception exception)
        {
            _sendErrors.Enqueue(exception);
        }
    }

    /// <summary>可控工作区桩：静态集合 + 手动触发变更事件；AddSession 模拟创建记账回流。</summary>
    private sealed class StaticWorkspaceService(IReadOnlyList<WorkspaceSummary>? items = null) : IWorkspaceService
    {
        private IReadOnlyList<WorkspaceSummary> _items = items ?? [];

        public IReadOnlySet<string> ArchivedSessionIds { get; private set; } = new HashSet<string>();

        public event EventHandler? WorkspacesChanged;

        public Task<IReadOnlyList<WorkspaceSummary>> GetWorkspacesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_items);
        }

        /// <summary>模拟创建后的工作区记账（对齐 SimulatedWorkspaceService.AddSession 语义）。</summary>
        public void AddSession(string workspaceId, string sessionId)
        {
            _items = _items.Select(workspace => workspace.Id == workspaceId
                                       ? workspace with { SessionIds = [.. workspace.SessionIds, sessionId] }
                                       : workspace)
                           .ToArray();
        }

        public void Replace(IReadOnlyList<WorkspaceSummary> next)
        {
            _items = next;
        }

        public void ReplaceArchived(IReadOnlySet<string> archived)
        {
            ArchivedSessionIds = archived;
        }

        public void RaiseChanged()
        {
            WorkspacesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>选型失败桩：其余行为走模拟实现，SelectModelAsync 固定抛错。</summary>
    private sealed class FailingSelectSessionService : ISessionService
    {
        private readonly SimulatedSessionService _inner = new();

        public event EventHandler? SessionsChanged
        {
            add => _inner.SessionsChanged += value;
            remove => _inner.SessionsChanged -= value;
        }

        public Task<IReadOnlyList<SessionSummary>> GetSessionsAsync(CancellationToken cancellationToken = default)
        {
            return _inner.GetSessionsAsync(cancellationToken);
        }

        public Task<SessionSummary> CreateSessionAsync(
            string? workspaceId = null, string? sessionId = null, CancellationToken cancellationToken = default)
        {
            return _inner.CreateSessionAsync(workspaceId, sessionId, cancellationToken);
        }

        public void MarkSessionEngaged(string sessionId)
        {
            _inner.MarkSessionEngaged(sessionId);
        }

        public Task<ModelCatalog> GetModelCatalogAsync(CancellationToken cancellationToken = default)
        {
            return _inner.GetModelCatalogAsync(cancellationToken);
        }

        public Task<ModelSelection> SelectModelAsync(
            string  sessionId,              string            provider, string model,
            string? reasoningEffort = null, CancellationToken cancellationToken = default)
        {
            return Task.FromException<ModelSelection>(new InvalidOperationException("选型失败（模拟）"));
        }

        public Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(
            string sessionId, CancellationToken cancellationToken = default)
        {
            return _inner.GetMessagesAsync(sessionId, cancellationToken);
        }

        public Task<SessionHistoryPage> LoadOlderAsync(
            string sessionId, long throughSeq, long beforeSeq, CancellationToken cancellationToken = default)
        {
            return _inner.LoadOlderAsync(sessionId, throughSeq, beforeSeq, cancellationToken);
        }

        public Task SendPromptAsync(
            string sessionId, string requestId, string content, CancellationToken cancellationToken = default)
        {
            return _inner.SendPromptAsync(sessionId, requestId, content, cancellationToken);
        }

        public Task CancelAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            return _inner.CancelAsync(sessionId, cancellationToken);
        }

        public IAsyncEnumerable<SessionUpdate> FollowSessionAsync(
            string sessionId, CancellationToken cancellationToken = default)
        {
            return _inner.FollowSessionAsync(sessionId, cancellationToken);
        }
    }

    private sealed class BlockingSendSessionService : ISessionService
    {
        private readonly SimulatedSessionService _inner = new();

        public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseSend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event EventHandler? SessionsChanged
        {
            add => _inner.SessionsChanged += value;
            remove => _inner.SessionsChanged -= value;
        }

        public Task<IReadOnlyList<SessionSummary>> GetSessionsAsync(CancellationToken cancellationToken = default)
        {
            return _inner.GetSessionsAsync(cancellationToken);
        }

        public Task<SessionSummary> CreateSessionAsync(
            string? workspaceId = null, string? sessionId = null, CancellationToken cancellationToken = default)
        {
            return _inner.CreateSessionAsync(workspaceId, sessionId, cancellationToken);
        }

        public void MarkSessionEngaged(string sessionId)
        {
            _inner.MarkSessionEngaged(sessionId);
        }

        public Task<ModelCatalog> GetModelCatalogAsync(CancellationToken cancellationToken = default)
        {
            return _inner.GetModelCatalogAsync(cancellationToken);
        }

        public Task<ModelSelection> SelectModelAsync(
            string  sessionId,              string            provider, string model,
            string? reasoningEffort = null, CancellationToken cancellationToken = default)
        {
            return _inner.SelectModelAsync(sessionId, provider, model, reasoningEffort, cancellationToken);
        }

        public Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(
            string sessionId, CancellationToken cancellationToken = default)
        {
            return _inner.GetMessagesAsync(sessionId, cancellationToken);
        }

        public Task<SessionHistoryPage> LoadOlderAsync(
            string sessionId, long throughSeq, long beforeSeq, CancellationToken cancellationToken = default)
        {
            return _inner.LoadOlderAsync(sessionId, throughSeq, beforeSeq, cancellationToken);
        }

        public async Task SendPromptAsync(
            string sessionId, string requestId, string content, CancellationToken cancellationToken = default)
        {
            SendStarted.TrySetResult();
            await ReleaseSend.Task.WaitAsync(cancellationToken);
            await _inner.SendPromptAsync(sessionId, requestId, content, cancellationToken);
        }

        public Task CancelAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            return _inner.CancelAsync(sessionId, cancellationToken);
        }

        public IAsyncEnumerable<SessionUpdate> FollowSessionAsync(
            string sessionId, CancellationToken cancellationToken = default)
        {
            return _inner.FollowSessionAsync(sessionId, cancellationToken);
        }
    }
}
