using Avalonia;
using Avalonia.Headless;
using DshDesktop.Core.Models;
using DshDesktop.Core.Services;
using DshDesktop.Harness.Exceptions;
using DshDesktop.Infrastructure.Services;
using DshDesktop.Presentation.Views;
using DshDesktop.ViewModels;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace DshDesktop.Tests;

public sealed class MainWindowViewModelTests
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
        catch (AssertionException)
        {
            await TestContext.Out.WriteLineAsync($"等待超时现场：selected={viewModel.SelectedSession?.Id} "          +
                                                 $"usage={viewModel.Composer.Usage?.ToString() ?? "<null>"} " +
                                                 $"stats={viewModel.Composer.Stats?.ToString() ?? "<null>"} " +
                                                 $"error=\"{viewModel.ErrorText}\" items={viewModel.ConversationItems.Count}");
            throw;
        }
    }

    /// <summary>启动即停留新对话草稿页（无自动选中）：需要会话上下文的测试显式选中第一条可见会话。</summary>
    private static async Task<SessionItemViewModel> SelectFirstSessionAsync(MainWindowViewModel viewModel)
    {
        await WaitUntilAsync(() => viewModel.Sidebar.Sessions.Count > 0);
        var session = viewModel.Sidebar.Sessions[0];
        viewModel.SelectedSession = session;
        await WaitUntilAsync(() => ReferenceEquals(viewModel.SelectedSession, session));
        return session;
    }

    [Test]
    public async Task MainWindowReceivesTheComposedViewModelAsDataContext()
    {
        var viewModel = CreateViewModel();
        EnsureAvaloniaPlatform();
        var window = new MainWindow(viewModel);

        ClassicAssert.AreSame(viewModel, window.DataContext);

        window.Close();
        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task LoadOlderRebuildRaisesResetInsideLoadingWindow()
    {
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync();
        viewModel.SelectedSession = viewModel.Sidebar.Sessions.First(session => session.Id == "session-history");
        await WaitUntilAsync(() => viewModel.ConversationItems.Count > 0 && viewModel.HasMoreHistory);

        // 视图以前插锚定补偿翻页跳动，置锚判据是「IsLoadingOlder 窗口内到达的 Reset」：
        // 翻页重建（Clear + 整体重灌，见 RebuildTimeline）的 Reset 必须发生在窗口内，
        // 否则视图无法区分翻页前插与会话切换，锚定会失效或误触发。
        var events = new List<(NotifyCollectionChangedAction Action, bool LoadingOlder)>();
        viewModel.ConversationItems.CollectionChanged +=
            (_, e) => events.Add((e.Action, viewModel.IsLoadingOlder));

        viewModel.LoadOlderCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ConversationItems[0].Seq == 23 && !viewModel.IsLoadingOlder);

        Assert.That(events.Any(recorded => recorded is
                                   { Action: NotifyCollectionChangedAction.Reset, LoadingOlder: true }), Is.True);

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task SessionSwitchRebuildRaisesResetOutsideLoadingWindow()
    {
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync();
        var firstSessionId = (await SelectFirstSessionAsync(viewModel)).Id;
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

        ClassicAssert.IsTrue(sawReset);
        ClassicAssert.IsFalse(sawResetWhileLoadingOlder);

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task ApprovalPanelFiltersBySessionAndSendsAllowOnceDecision()
    {
        var sessionService  = new SimulatedSessionService();
        var approvalService = new SimulatedToolApprovalService();
        var viewModel = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(),
                                                new StaticWorkspaceService(), approvalService);
        await viewModel.InitializeAsync();

        var selectedSessionId = (await SelectFirstSessionAsync(viewModel)).Id;
        approvalService.PushRequest(selectedSessionId, "fs.write", "需要修改项目文件", "call-selected");
        approvalService.PushRequest("session-not-selected", "fs.read", "不应出现在当前会话", "call-other");

        await WaitUntilAsync(() => viewModel.SessionPendingApprovals.Count == 1);
        Assert.That(viewModel.SessionPendingApprovals, Has.Count.EqualTo(1));
        var approval = viewModel.SessionPendingApprovals.Single();
        ClassicAssert.AreEqual("fs.write", approval.ToolName);
        ClassicAssert.AreEqual("需要修改项目文件", approval.HeadlineText);

        viewModel.ApproveApprovalCommand.Execute(approval);
        await WaitUntilAsync(() => approvalService.Decisions.Count == 1);

        Assert.That(approvalService.Decisions, Has.Count.EqualTo(1));
        var decision = approvalService.Decisions.Single();
        ClassicAssert.AreEqual(approval.EventId, decision.EventId);
        ClassicAssert.IsTrue(decision.Allowed);
        Assert.That(approvalService.Pending, Has.Count.EqualTo(1));
        ClassicAssert.IsEmpty(viewModel.SessionPendingApprovals);

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task ApprovalDecisionLeavesComposerPermissionPresetUntouched()
    {
        var sessionService          = new SimulatedSessionService();
        var approvalService         = new SimulatedToolApprovalService();
        var permissionPresetService = new SimulatedPermissionPresetService();
        var viewModel = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(),
                                                new StaticWorkspaceService(), approvalService,
                                                permissionPresetService : permissionPresetService);
        await viewModel.InitializeAsync();

        var selectedSessionId = (await SelectFirstSessionAsync(viewModel)).Id;
        // 模拟 follow 基线给出 workspace-write：选择器显示投影权威值。
        await WaitUntilAsync(() => viewModel.PermissionSelector.PermissionPickerLabel == "工作区内修改");

        approvalService.PushRequest(selectedSessionId, "fs.write", "需要修改项目文件", "call-1");
        await WaitUntilAsync(() => viewModel.SessionPendingApprovals.Count == 1);
        viewModel.ApproveApprovalCommand.Execute(viewModel.SessionPendingApprovals.Single());
        await WaitUntilAsync(() => approvalService.Decisions.Count == 1);

        // 允许一次只裁决当前 tool call：不触碰 Composer 的当前权限预设。
        ClassicAssert.AreEqual("workspace-write", viewModel.PermissionSelector.CurrentValue);
        ClassicAssert.AreEqual("工作区内修改", viewModel.PermissionSelector.PermissionPickerLabel);
        ClassicAssert.IsEmpty(permissionPresetService.Switches);

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task PermissionProjectionFlowsToSelectorAndSwitchGoesThroughCommand()
    {
        var sessionService          = new SimulatedSessionService();
        var permissionPresetService = new SimulatedPermissionPresetService();
        var viewModel = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(),
                                                new StaticWorkspaceService(), new SimulatedToolApprovalService(),
                                                permissionPresetService : permissionPresetService);
        await viewModel.InitializeAsync();
        var selectedSessionId = (await SelectFirstSessionAsync(viewModel)).Id;

        // 快照投影基线 → 选择器显示当前预设；切换请求经服务下达，投影回流后才更新显示。
        await WaitUntilAsync(() => viewModel.PermissionSelector.PermissionPickerLabel == "工作区内修改");

        viewModel.PermissionSelector.SelectOptionCommand.Execute(
                                                                 viewModel.PermissionSelector.Options.Single(option =>
                                                                     option.Value == "read-only"));
        Assert.That(permissionPresetService.Switches, Has.Count.EqualTo(1));
        var (switchedSession, preset) = permissionPresetService.Switches.Single();
        ClassicAssert.AreEqual(selectedSessionId, switchedSession);
        ClassicAssert.AreEqual("read-only", preset);
        ClassicAssert.AreEqual("workspace-write", viewModel.PermissionSelector.CurrentValue);

        // 模拟后端接受切换后的 permissions 投影回流（session/control 整值更新路径）。
        sessionService.PushPermission(selectedSessionId, "read-only");
        await WaitUntilAsync(() => viewModel.PermissionSelector.PermissionPickerLabel == "仅可查看");
        ClassicAssert.AreEqual("read-only", viewModel.PermissionSelector.CurrentValue);

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task DraftFirstSendAppliesPreselectedPermissionPreset()
    {
        var sessionService          = new SimulatedSessionService();
        var permissionPresetService = new SimulatedPermissionPresetService();
        var viewModel = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(),
                                                new StaticWorkspaceService(), new SimulatedToolApprovalService(),
                                                permissionPresetService : permissionPresetService);
        await viewModel.InitializeAsync();
        ClassicAssert.IsTrue(viewModel.ShowNewConversationPage);

        // 草稿页本地预选（无会话，不发请求）：完全权限走确认门后才记入草稿。
        ClassicAssert.IsTrue(viewModel.PermissionSelector.IsVisible);
        viewModel.PermissionSelector.Options
                 .Single(option => option.Value == "danger-full-access")
                 .SelectCommand.Execute(null);
        ClassicAssert.IsTrue(viewModel.PermissionSelector.IsConfirmOpen);
        ClassicAssert.IsEmpty(permissionPresetService.Switches);

        viewModel.PermissionSelector.IsAcknowledged = true;
        viewModel.PermissionSelector.ConfirmSwitchCommand.Execute(null);
        ClassicAssert.AreEqual("完全权限", viewModel.PermissionSelector.PermissionPickerLabel);
        ClassicAssert.IsEmpty(permissionPresetService.Switches);

        // 首发送：创建会话后、首条消息前应用预选（预选随会话记账，确认由投影回流）。
        viewModel.SelectWorkspaceCommand.Execute(viewModel.WorkspaceOptions
                                                          .Single(option => option.IsWithoutWorkspace));
        viewModel.Composer.DraftMessage = "预选完全权限的首条消息";
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SelectedSession is { Id: not "session-history" });

        Assert.That(permissionPresetService.Switches, Has.Count.EqualTo(1));
        var (switchedSession, preset) = permissionPresetService.Switches.Single();
        ClassicAssert.AreEqual(viewModel.SelectedSession!.Id, switchedSession);
        ClassicAssert.AreEqual("danger-full-access", preset);

        // 模拟后端接受切换后的投影回流：选择器改由会话投影驱动并显示新值。
        sessionService.PushPermission(viewModel.SelectedSession.Id, "danger-full-access");
        await WaitUntilAsync(() => viewModel.PermissionSelector.PermissionPickerLabel == "完全权限");

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task DraftFirstSendWithoutPreselectionSkipsPermissionCommand()
    {
        var sessionService          = new SimulatedSessionService();
        var permissionPresetService = new SimulatedPermissionPresetService();
        var viewModel = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(),
                                                new StaticWorkspaceService(), new SimulatedToolApprovalService(),
                                                permissionPresetService : permissionPresetService);
        await viewModel.InitializeAsync();

        // 未预选权限的草稿首发送：不产生 /permission 命令（后端按其默认播种新会话）。
        viewModel.SelectWorkspaceCommand.Execute(viewModel.WorkspaceOptions
                                                          .Single(option => option.IsWithoutWorkspace));
        viewModel.Composer.DraftMessage = "未预选权限的首条消息";
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SelectedSession is { Id: not "session-history" });

        ClassicAssert.IsEmpty(permissionPresetService.Switches);

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task SwitchingSessionsLoadsEachSessionSnapshot()
    {
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync();

        viewModel.SelectedSession = viewModel.Sidebar.Sessions.First(session => session.Id == "session-native");
        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<MessageItemViewModel>().Count() == 2);

        ClassicAssert.IsFalse(viewModel.HasError);

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task SendingMessagePreservesDraftEnteredWhileRequestIsInFlight()
    {
        var sessionService = new BlockingSendSessionService();
        var backendService = new SimulatedBackendStatusService();
        var viewModel      = new MainWindowViewModel(sessionService, backendService, new StaticWorkspaceService());
        await viewModel.InitializeAsync();
        await SelectFirstSessionAsync(viewModel);

        viewModel.Composer.DraftMessage = "第一条消息";
        viewModel.Composer.SendMessageCommand.Execute(null);
        await sessionService.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        viewModel.Composer.DraftMessage = "发送期间的新草稿";
        sessionService.ReleaseSend.TrySetResult();
        await WaitUntilAsync(() => !viewModel.Composer.IsSending);

        ClassicAssert.AreEqual("发送期间的新草稿", viewModel.Composer.DraftMessage);

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task StreamingUpdatesAppendAssistantTextAndCommitReplacesBubble()
    {
        var sessionService = new SimulatedSessionService();
        var backendService = new SimulatedBackendStatusService();
        var viewModel      = new MainWindowViewModel(sessionService, backendService, new StaticWorkspaceService());
        await viewModel.InitializeAsync();
        var sessionId = (await SelectFirstSessionAsync(viewModel)).Id;
        var before    = viewModel.ConversationItems.Count;

        sessionService.PushAssistantReply(sessionId, "流式回复内容");

        // 等到正式消息（Seq >= 0）替换流式气泡且数量回落，避免轮询命中流式中间态。
        await WaitUntilAsync(() => viewModel.ConversationItems.Count == before + 1 && viewModel.ConversationItems
                                .OfType<MessageItemViewModel>()
                                .Any(message => message is { Content: "流式回复内容", Seq: >= 0 }));
        var committed = viewModel.ConversationItems.OfType<MessageItemViewModel>()
                                 .FirstOrDefault(message => message.Content == "流式回复内容");
        ClassicAssert.IsNotNull(committed);
        ClassicAssert.AreEqual(MessageRole.Assistant, committed.Role);
        Assert.That(viewModel.ConversationItems.OfType<MessageItemViewModel>().Any(message => message.IsStreaming),
                    Is.False);
        // Markdown 渲染源与字符串内容保持一致（快照构造路径）。
        ClassicAssert.AreEqual(committed.Content, committed.MarkdownBuilder.ToString());

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task AbandonedStreamMarksBubbleInterruptedInsteadOfHanging()
    {
        var sessionService = new SimulatedSessionService();
        var backendService = new SimulatedBackendStatusService();
        var viewModel      = new MainWindowViewModel(sessionService, backendService, new StaticWorkspaceService());
        await viewModel.InitializeAsync();
        var selectedSession = await SelectFirstSessionAsync(viewModel);

        sessionService.PushAbandonedStream(selectedSession.Id, "部分生成内容");

        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<MessageItemViewModel>()
                                            .Any(message => message.IsInterrupted));
        var interrupted = viewModel.ConversationItems.OfType<MessageItemViewModel>()
                                   .Single(message => message.IsInterrupted);
        ClassicAssert.IsFalse(interrupted.IsStreaming);
        // 中断是独立标注，不再混入正文；已生成内容原样保留。
        ClassicAssert.AreEqual("部分生成内容", interrupted.Content);
        ClassicAssert.IsFalse(viewModel.HasError);
        ClassicAssert.AreEqual(interrupted.Content, interrupted.MarkdownBuilder.ToString());

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task ModelCatalogPopulatesOptionsAndSnapshotCarriesCurrentModel()
    {
        var sessionService = new SimulatedSessionService();
        var backendService = new SimulatedBackendStatusService();
        var viewModel      = new MainWindowViewModel(sessionService, backendService, new StaticWorkspaceService());
        await viewModel.InitializeAsync();

        // 目录扁平投影：两个提供方共三个模型。
        await WaitUntilAsync(() => viewModel.Composer.ModelOptions.Count == 3);
        ClassicAssert.IsTrue(viewModel.Composer.IsModelPickerEnabled);

        // 选中最新会话（session-history，预置 sim/sim-chat）：快照投影生效。
        await SelectFirstSessionAsync(viewModel);
        await WaitUntilAsync(() => viewModel.Composer.SelectedModelOption is { Provider: "sim", Model: "sim-chat" });
        ClassicAssert.AreEqual(new ModelSelection("sim", "sim-chat"), viewModel.Composer.CurrentModel);

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task SelectingModelOptionEchoesThroughFollowAndSwitchesPerSession()
    {
        var sessionService = new SimulatedSessionService();
        var backendService = new SimulatedBackendStatusService();
        var viewModel      = new MainWindowViewModel(sessionService, backendService, new StaticWorkspaceService());
        await viewModel.InitializeAsync();
        await SelectFirstSessionAsync(viewModel);

        var reasoner = viewModel.Composer.ModelOptions.Single(option => option.Model == "alt-chat");
        viewModel.Composer.SelectedModelOption = reasoner;

        // 选型经 follow 流的 model/selection 回声生效（后端权威）。
        await WaitUntilAsync(() => viewModel.Composer.CurrentModel is { Provider: "sim-alt", Model: "alt-chat" });
        ClassicAssert.AreSame(reasoner, viewModel.Composer.SelectedModelOption);
        ClassicAssert.IsFalse(viewModel.HasError);

        // 切换会话：另一会话的快照携带各自的当前选型。
        viewModel.SelectedSession = viewModel.Sidebar.Sessions.First(session => session.Id == "session-native");
        await WaitUntilAsync(() => viewModel.SelectedSession!.Id == "session-native" &&
                                   viewModel.Composer.CurrentModel is { Provider: "sim", Model: "sim-reasoner" });
        ClassicAssert.IsFalse(viewModel.HasError);

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task FailedModelSelectionRevertsPickerAndReportsError()
    {
        var sessionService = new FailingSelectSessionService();
        var backendService = new SimulatedBackendStatusService();
        var viewModel      = new MainWindowViewModel(sessionService, backendService, new StaticWorkspaceService());
        await viewModel.InitializeAsync();
        await SelectFirstSessionAsync(viewModel);
        await WaitUntilAsync(() => viewModel.Composer.SelectedModelOption is { Model: "sim-chat" });

        viewModel.Composer.SelectedModelOption =
            viewModel.Composer.ModelOptions.Single(option => option.Model == "alt-chat");
        await WaitUntilAsync(() => viewModel.HasError);

        // 失败后回退到当前生效选型的显示，不停留在失败项。
        await WaitUntilAsync(() => viewModel.Composer.SelectedModelOption is { Model: "sim-chat" });
        Assert.That(viewModel.ErrorText, Does.Contain("选型失败"));

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task SessionUsageAndStatsFollowSelectedSession()
    {
        var sessionService = new SimulatedSessionService();
        var backendService = new SimulatedBackendStatusService();
        var viewModel      = new MainWindowViewModel(sessionService, backendService, new StaticWorkspaceService());
        await viewModel.InitializeAsync();

        // 选中长会话（4 轮 8 条助手消息）：快照统计基线到达，文案带真实数值。
        await SelectFirstSessionAsync(viewModel);
        await WaitUntilAsync(() => viewModel.Composer is { Usage : { OutputTokens: > 0 }, Stats.Steps: > 0 });
        Assert.That(viewModel.Composer.UsageValueText, Does.Not.Contain("—"));
        Assert.That(viewModel.Composer.SpeedValueText, Does.Not.Contain("—"));
        var longUsage = viewModel.Composer.Usage!;

        // 切到单条助手消息的会话：统计按会话重置并携带该会话的累计值。
        viewModel.SelectedSession = viewModel.Sidebar.Sessions.First(session => session.Id == "session-design");
        await WaitUntilAsync(() => viewModel.SelectedSession!.Id == "session-design" &&
                                   viewModel.Composer.Usage is { OutputTokens: > 0 });
        ClassicAssert.IsTrue(viewModel.Composer.Usage!.OutputTokens < longUsage.OutputTokens);
        ClassicAssert.AreEqual(viewModel.Composer.Usage.UncachedInputTokens + viewModel.Composer.Usage.CacheReadTokens +
                               viewModel.Composer.Usage.CacheWriteTokens    + viewModel.Composer.Usage.OutputTokens > 0,
                               !viewModel.Composer.UsageValueText.Contains('—'));

        // 新增一步计费后统计整值更新。
        var before = viewModel.Composer.Usage!.OutputTokens;
        sessionService.PushAssistantReply("session-design", "累计一步计费的回复");
        await WaitUntilAsync(() => viewModel.Composer.Usage is { } usage && usage.OutputTokens > before);
        ClassicAssert.IsFalse(viewModel.HasError);

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task StatsStripStaysHiddenForBlankSessionUntilUsageArrives()
    {
        var sessionService = new SimulatedSessionService();
        var backendService = new SimulatedBackendStatusService();
        var viewModel      = new MainWindowViewModel(sessionService, backendService, new StaticWorkspaceService());
        await viewModel.InitializeAsync();

        // 选中的长会话有计费步：统计条可见。
        await SelectFirstSessionAsync(viewModel);
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
        ClassicAssert.IsFalse(viewModel.Composer.HasStatsData);

        // 首条助手回复落地：投影转为非零，统计条出现。
        var blankSessionId = viewModel.SelectedSession!.Id;
        sessionService.PushAssistantReply(blankSessionId, "空会话的第一条回复");
        await WaitUntilAsync(() => viewModel.Composer.HasStatsData);
        ClassicAssert.IsFalse(viewModel.HasError);

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
        ClassicAssert.IsFalse(viewModel.Composer.HasStatsData);

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task SessionListRefreshKeepsSelectedInstanceAndStreamingBubble()
    {
        var sessionService = new SimulatedSessionService();
        var backendService = new SimulatedBackendStatusService();
        var viewModel      = new MainWindowViewModel(sessionService, backendService, new StaticWorkspaceService());
        await viewModel.InitializeAsync();
        var selected = await SelectFirstSessionAsync(viewModel);
        await WaitUntilAsync(() => viewModel.ConversationItems.Count > 0);
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
        ClassicAssert.AreSame(selected, viewModel.SelectedSession);
        Assert.That(viewModel.ConversationItems.OfType<MessageItemViewModel>()
                             .Any(message => message is { IsStreaming: true, Content: "正在生成的内容" }), Is.True);
        ClassicAssert.AreEqual(messageCountBefore + 2, viewModel.ConversationItems.Count);
        // 流式增量路径：渲染源跟随 AppendText 同步增长。
        var streaming = viewModel.ConversationItems.OfType<MessageItemViewModel>()
                                 .Single(message => message.IsStreaming);
        ClassicAssert.AreEqual(streaming.Content, streaming.MarkdownBuilder.ToString());

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task LoadingOlderPrependsEntriesAndFoldsCompleteTurnsAtTheTop()
    {
        var sessionService = new SimulatedSessionService();
        var backendService = new SimulatedBackendStatusService();
        var viewModel      = new MainWindowViewModel(sessionService, backendService, new StaticWorkspaceService());
        await viewModel.InitializeAsync();
        viewModel.SelectedSession = viewModel.Sidebar.Sessions.First(session => session.Id == "session-history");
        // 初始窗口是最近 3 条消息及其附随条目（说明、工具、思考、工具、总结、turn/end）；
        // turn/end 边界不产生可见条目，可见项为 5。
        await WaitUntilAsync(() => viewModel.ConversationItems.Count > 0 && viewModel.HasMoreHistory);

        // 首轮被窗口截断（turn 4 的用户消息与首个思考在窗口之外）：仍限制在过程组内显示，
        // 并标记部分加载；不能因历史窗口不完整而把过程重新铺成顶层项目。
        ClassicAssert.IsTrue(viewModel.HasMoreHistory);
        ClassicAssert.AreEqual(27, viewModel.ConversationItems[0].Seq);
        Assert.That(viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>()
                             .Any(group => group.IsPartialTurn), Is.True);

        viewModel.LoadOlderCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ConversationItems[0].Seq == 23 && !viewModel.IsLoadingOlder);
        ClassicAssert.IsTrue(viewModel.HasMoreHistory);

        // 窗口补全后 turn 4 的用户消息已可见：尽管更早历史未读，该轮即折叠——
        // 对齐 WebUI 实时会话的形态（其已加载窗口天然是全量，读全前也能折叠完整轮次）。
        var recentGroup = viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>()
                                   .Single(item => item.Seq == 26);
        ClassicAssert.AreEqual(2, recentGroup.ToolCallCount);
        ClassicAssert.AreEqual(1, recentGroup.MessageCount);
        ClassicAssert.AreEqual("2 次工具调用 · 1 条消息", recentGroup.SummaryText);

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
        ClassicAssert.AreEqual(new[] { 1, 2, 7, 9, 10, 15, 17, 18, 23, 25, 26, 31 },
                               viewModel.ConversationItems.Select(item => item.Seq));
        var group = viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>()
                             .Single(item => item.Seq == 2);
        ClassicAssert.AreEqual(2, group.ToolCallCount);
        ClassicAssert.AreEqual(1, group.MessageCount);
        ClassicAssert.AreEqual("2 次工具调用 · 1 条消息", group.SummaryText);
        // 过程组内的条目：两段中间思考、最终 reasoning 投影、中间说明与两枚工具；
        // 思考条目（包括投影）不算「消息」。
        ClassicAssert.AreEqual(6, group.Process.Count);
        ClassicAssert.AreEqual(3, group.Process.OfType<MessageItemViewModel>()
                                       .Count(message => message.HasReasoning &&
                                                         string.IsNullOrWhiteSpace(message.Content)));
        Assert.That(group.Process.Any(item => item is MessageItemViewModel { Content: "先查看第 1 轮的相关记录，再做定点更新。" }),
                    Is.True);
        // 最终回复保持独立气泡，且自带可折叠的思考行。
        var answer = viewModel.ConversationItems.OfType<MessageItemViewModel>()
                              .Single(item => item.Content == "历史回答 0：基于第 1 轮工具结果的整理。");
        ClassicAssert.AreEqual(7, answer.Seq);
        ClassicAssert.IsTrue(answer.HasReasoning);
        ClassicAssert.IsTrue(answer.IsReasoningProjected);
        ClassicAssert.IsFalse(answer.HasVisibleReasoning);
        var finalReasoning = group.Process.OfType<MessageItemViewModel>()
                                  .Single(item => item.Seq == answer.Seq &&
                                                  string.IsNullOrWhiteSpace(item.Content));
        ClassicAssert.AreEqual(answer.Id, finalReasoning.Id);
        ClassicAssert.AreEqual(answer.Reasoning, finalReasoning.Reasoning);

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task SnapshotTailInterruptedBoundaryKeepsOpenTurnUnfoldedUntilRealEnd()
    {
        var sessionService = new SyntheticBoundarySessionService();
        var backendService = new SimulatedBackendStatusService();
        var viewModel      = new MainWindowViewModel(sessionService, backendService, new StaticWorkspaceService());
        await viewModel.InitializeAsync();
        await SelectFirstSessionAsync(viewModel);

        // 中途 attach 到生成中的会话：快照尾部带 Host 合成的 interrupted 边界（seq 即 cursor，
        // 持久日志中不存在）。该边界不得结算当前轮；已有过程仍在活动过程组内。
        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<MessageItemViewModel>()
                                            .Any(message => message.Content == "阶段性回复"));
        Assert.That(viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>()
                             .Any(group => group.Process.Any(item => item.Seq == 2)), Is.True);

        // 生成继续：新工具与真正的最终回复落地，随后真实的 turn/end 到达——此时才折叠，
        // 且只折叠一次（合成边界若被结算会出现两个过程组/错误的最终回复）。
        sessionService.PushLiveTail();
        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<MessageItemViewModel>()
                                            .Any(message => message.Content == "真正的最终回复"));
        sessionService.PushRealTurnEnd();
        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>().Any());

        var group = viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>().Single();
        ClassicAssert.AreEqual(2, group.ToolCallCount);
        ClassicAssert.AreEqual(2, group.MessageCount);
        Assert.That(group.Process.Any(item => item is ToolActivityItemViewModel { Name: "fs.read" }), Is.True);
        var answer = viewModel.ConversationItems.OfType<MessageItemViewModel>()
                              .Single(message => message.Content == "真正的最终回复");
        ClassicAssert.AreEqual(7, answer.Seq);
        ClassicAssert.IsFalse(viewModel.HasError);

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task SwitchingAwayFromSessionResetsHistoryWindow()
    {
        var sessionService = new SimulatedSessionService();
        var backendService = new SimulatedBackendStatusService();
        var viewModel      = new MainWindowViewModel(sessionService, backendService, new StaticWorkspaceService());
        await viewModel.InitializeAsync();
        viewModel.SelectedSession = viewModel.Sidebar.Sessions.First(session => session.Id == "session-history");

        await WaitUntilAsync(() => viewModel.ConversationItems.Count > 0 && viewModel.HasMoreHistory);

        viewModel.SelectedSession = viewModel.Sidebar.Sessions.First(session => session.Id == "session-welcome");
        await WaitUntilAsync(() => viewModel.SelectedSession?.Id == "session-welcome" &&
                                   viewModel.ConversationItems.OfType<MessageItemViewModel>().Count() == 2,
                             10000);

        ClassicAssert.IsFalse(viewModel.HasMoreHistory);

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task RunningLongTurnShowsAllProcessAndTogglePreservesExpansionAcrossEnd()
    {
        var sessionService = new SimulatedSessionService();
        var backendService = new SimulatedBackendStatusService();
        var viewModel      = new MainWindowViewModel(sessionService, backendService, new StaticWorkspaceService());
        await viewModel.InitializeAsync();
        viewModel.SelectedSession = viewModel.Sidebar.Sessions.First(session => session.Id == "session-welcome");
        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<MessageItemViewModel>().Count() == 2);

        var sessionId = viewModel.SelectedSession!.Id;
        await sessionService.SendPromptAsync(sessionId, Guid.NewGuid().ToString(), "检查长轮次");

        // 工具先发起、结果稍后到达；其间的长过程通过真实 FollowSessionAsync 事件链进入 VM。
        var       pendingTool    = sessionService.BeginToolActivity(sessionId, "fs.read", 2)!;
        const int reasoningCount = 44;
        var       fullReasoning  = new string('思', 4096);
        for (var index = 0; index < reasoningCount; index++)
            sessionService.PushCommittedAssistantMessage(sessionId, string.Empty, 2,
                                                         reasoning : index == 0 ? fullReasoning : $"过程思考 {index}");

        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>()
                                            .Any(group => group.Process.Count == reasoningCount + 1), 10000);
        var group = viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>()
                             .Single(group => group.Process.Count == reasoningCount + 1);

        Assert.That(group.IsExpanded, Is.True, "运行组在首次用户 Toggle 前必须已默认展开。");
        Assert.That(group.Process, Has.Count.EqualTo(45));
        Assert.That(group.VisibleProcess, Has.Count.EqualTo(45), "运行中的已加载过程必须初始全部可见。");
        Assert.That(group.HiddenProcessCount, Is.Zero);
        Assert.That(group.HasEarlierProcess, Is.False);
        Assert.That(group.ShowEarlierProcessCommand.CanExecute(null), Is.False);

        group.ToggleCommand.Execute(null);
        Assert.That(group.IsExpanded, Is.False);
        Assert.That(group.VisibleProcess, Has.Count.EqualTo(45), "用户折叠不得裁剪过程条目。");

        group.ToggleCommand.Execute(null);
        Assert.That(group.IsExpanded, Is.True);
        Assert.That(group.HasUserSetExpansion, Is.True);

        var originalToolCard = group.Process.OfType<ToolActivityItemViewModel>().Single();

        sessionService.SettleToolActivity(sessionId, pendingTool.CallId, "完整工具结果", false);
        sessionService.PushCommittedAssistantMessage(sessionId, string.Empty, 2, reasoning : "追加思考 A");
        sessionService.PushCommittedAssistantMessage(sessionId, string.Empty, 2, reasoning : "追加思考 B");
        await WaitUntilAsync(() => group.Process.Count == 47 && group.VisibleProcess.Count == 47);

        Assert.That(group.HiddenProcessCount, Is.Zero);
        Assert.That(group.HasEarlierProcess, Is.False);
        var toolCard = group.Process.OfType<ToolActivityItemViewModel>().Single();
        Assert.That(toolCard, Is.SameAs(originalToolCard), "工具结果必须结算在原卡片对象上。");
        Assert.That(toolCard.CallId, Is.EqualTo(pendingTool.CallId));
        Assert.That(toolCard.IsSucceeded, Is.True);
        Assert.That(toolCard.ResultText, Is.EqualTo("完整工具结果"));
        var reasoningItem = group.Process.OfType<MessageItemViewModel>()
                                 .Single(message => message.Reasoning == fullReasoning);
        Assert.That(reasoningItem.Reasoning, Is.EqualTo(fullReasoning),
                    "过程窗口不能截断已存储的思考原文。");
        Assert.That(group.VisibleProcess, Does.Contain(toolCard),
                    "调用与结果必须仍是同一张工具卡片，并能随过程窗口显示。");

        const string finalAnswer = "完整终答原文";
        sessionService.BeginAssistantStream(sessionId, "正在生成完整终答");
        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<MessageItemViewModel>()
                                            .Any(message => message is { IsStreaming: true, Content: "正在生成完整终答" }));
        Assert.That(viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>()
                             .SelectMany(candidate => candidate.Process).OfType<MessageItemViewModel>()
                             .Any(message => message.IsStreaming), Is.False,
                    "当前流式气泡必须留在过程组外。");

        sessionService.PushCommittedAssistantMessage(sessionId, finalAnswer, 2, reasoning : "终答思考");
        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<MessageItemViewModel>()
                                            .Any(message => message.Content == finalAnswer));
        sessionService.PushTurnEnded(sessionId, 2);
        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>()
                                            .Any(candidate => candidate.Process.OfType<MessageItemViewModel>()
                                                                       .Any(message => message is
                                                                                { Content: "", Reasoning: "终答思考" })),
                             10000);

        var answer = viewModel.ConversationItems.OfType<MessageItemViewModel>()
                              .Single(message => message.Content == finalAnswer);
        var finalizedGroup = viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>().Single();
        Assert.That(answer.Content, Is.EqualTo(finalAnswer));
        Assert.That(finalizedGroup.IsExpanded, Is.True,
                    "用户展开旧过程后，turn/end 不应收回明确的阅读状态。");
        Assert.That(finalizedGroup.HasUserSetExpansion, Is.True);
        Assert.That(finalizedGroup.VisibleProcess, Has.Count.EqualTo(finalizedGroup.Process.Count),
                    "turn/end 后全部已加载过程仍必须可见。");
        Assert.That(finalizedGroup.HiddenProcessCount, Is.Zero);
        Assert.That(finalizedGroup.HasEarlierProcess, Is.False);
        Assert.That(finalizedGroup.VisibleProcess, Does.Contain(toolCard),
                    "工具结算必须保留在原过程位置并继续可见。");
        Assert.That(finalizedGroup.Process.OfType<MessageItemViewModel>()
                                  .Any(message => message.Content == finalAnswer), Is.False);

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task LoadingOlderPreservesCollapsedStateAndSessionProcessDataIsIsolated()
    {
        var sessionService = new SimulatedSessionService();
        var backendService = new SimulatedBackendStatusService();
        var viewModel      = new MainWindowViewModel(sessionService, backendService, new StaticWorkspaceService());
        await viewModel.InitializeAsync();
        viewModel.SelectedSession = viewModel.Sidebar.Sessions.First(session => session.Id == "session-history");
        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>().Any() &&
                                   viewModel.HasMoreHistory);

        var firstSessionId = viewModel.SelectedSession!.Id;
        await sessionService.SendPromptAsync(firstSessionId, Guid.NewGuid().ToString(), "保留过程锚点");
        const int processCount = 45;
        for (var index = 0; index < processCount; index++)
            sessionService.PushCommittedAssistantMessage(firstSessionId, string.Empty, 90,
                                                         reasoning : $"历史重建过程 {index}");

        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>()
                                            .Any(group => group.Process.Count == processCount), 10000);
        var expandedGroup = viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>()
                                     .Single(group => group.Process.Count == processCount);
        Assert.That(expandedGroup.IsExpanded, Is.True);
        Assert.That(expandedGroup.VisibleProcess, Has.Count.EqualTo(processCount));
        Assert.That(expandedGroup.HiddenProcessCount, Is.Zero);
        Assert.That(expandedGroup.VisibleProcess.OfType<MessageItemViewModel>().Select(item => item.Reasoning),
                    Does.Contain("历史重建过程 0"));

        var firstSeqBeforeLoad = viewModel.ConversationItems[0].Seq;

        expandedGroup.ToggleCommand.Execute(null);
        Assert.That(expandedGroup.IsExpanded, Is.False);
        Assert.That(expandedGroup.HasUserSetExpansion, Is.True);
        Assert.That(expandedGroup.VisibleProcess, Has.Count.EqualTo(processCount), "折叠不得裁剪历史重建过程。");

        viewModel.LoadOlderCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ConversationItems[0].Seq < firstSeqBeforeLoad &&
                                   !viewModel.IsLoadingOlder);

        var rebuiltGroup = viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>()
                                    .Single(group => group.Process.OfType<MessageItemViewModel>()
                                                                  .Any(item => item.Reasoning == "历史重建过程 0"));
        Assert.That(rebuiltGroup, Is.Not.SameAs(expandedGroup), "历史前插必须实际重建时间线组对象。");
        Assert.That(rebuiltGroup.IsExpanded, Is.False,
                    "历史前插与时间线重建必须保留用户折叠选择。");
        Assert.That(rebuiltGroup.HasUserSetExpansion, Is.True);
        Assert.That(rebuiltGroup.Process, Has.Count.EqualTo(processCount));
        Assert.That(rebuiltGroup.VisibleProcess, Has.Count.EqualTo(processCount));
        Assert.That(rebuiltGroup.HiddenProcessCount, Is.Zero);
        Assert.That(rebuiltGroup.HasEarlierProcess, Is.False);
        Assert.That(rebuiltGroup.Process.OfType<MessageItemViewModel>()
                         .Any(item => item.Reasoning == "另一会话过程 0"), Is.False);

        var otherSession = viewModel.Sidebar.Sessions.First(session => session.Id == "session-welcome");
        viewModel.SelectedSession = otherSession;
        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<MessageItemViewModel>().Count() == 2);
        await sessionService.SendPromptAsync(otherSession.Id, Guid.NewGuid().ToString(), "隔离展开状态");
        for (var index = 0; index < processCount; index++)
            sessionService.PushCommittedAssistantMessage(otherSession.Id, string.Empty, 90,
                                                         reasoning : $"另一会话过程 {index}");

        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>()
                                            .Any(group => group.Process.Count == processCount), 10000);
        var otherGroup = viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>()
                                  .Single(group => group.Process.Count == processCount);
        Assert.That(otherGroup.IsExpanded, Is.True, "另一会话的新运行组必须使用自己的默认展开状态。");
        Assert.That(otherGroup.VisibleProcess, Has.Count.EqualTo(processCount));
        Assert.That(otherGroup.HiddenProcessCount, Is.Zero);
        Assert.That(otherGroup.HasEarlierProcess, Is.False);
        Assert.That(otherGroup.VisibleProcess.OfType<MessageItemViewModel>().Select(item => item.Reasoning),
                    Does.Contain("另一会话过程 0"));
        Assert.That(otherGroup.Process.OfType<MessageItemViewModel>()
                         .Any(item => item.Reasoning == "历史重建过程 0"), Is.False);

        await viewModel.DisposeAsync();
    }

    [Test]
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

        // 运行中的工具卡片进入活动过程组，但调用和结果仍落在同一条目上。
        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>()
                                            .SelectMany(group => group.Process.OfType<ToolActivityItemViewModel>())
                                            .Any(tool => tool.Status == ToolActivityStatus.Succeeded));
        var card = viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>()
                            .SelectMany(group => group.Process.OfType<ToolActivityItemViewModel>())
                            .Single(tool => tool.Name == "fs.read");
        ClassicAssert.AreEqual(before + 1, viewModel.ConversationItems.Count);
        ClassicAssert.IsFalse(card.IsRunning);
        ClassicAssert.AreEqual("文件内容摘要", card.ResultText);
        ClassicAssert.IsTrue(card.HasDetails);

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task FailedToolActivitySurfacesErrorState()
    {
        var sessionService = new SimulatedSessionService();
        var backendService = new SimulatedBackendStatusService();
        var viewModel      = new MainWindowViewModel(sessionService, backendService, new StaticWorkspaceService());
        await viewModel.InitializeAsync();
        viewModel.SelectedSession = viewModel.Sidebar.Sessions.First(session => session.Id == "session-welcome");
        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<MessageItemViewModel>().Count() == 2);

        sessionService.PushToolActivity(viewModel.SelectedSession!.Id, "shell.run", null, true);

        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>()
                                            .SelectMany(group => group.Process.OfType<ToolActivityItemViewModel>())
                                            .Any(tool => tool.IsFailed));
        var card = viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>()
                            .SelectMany(group => group.Process.OfType<ToolActivityItemViewModel>())
                            .Single(tool => tool.IsFailed);
        ClassicAssert.AreEqual("失败", card.StatusText);
        Assert.That(card.ErrorReason, Does.Contain("simulated"));

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task TurnEndFoldsProcessIntoGroupAndKeepsFinalAnswer()
    {
        var sessionService = new SimulatedSessionService();
        var backendService = new SimulatedBackendStatusService();
        var viewModel      = new MainWindowViewModel(sessionService, backendService, new StaticWorkspaceService());
        await viewModel.InitializeAsync();
        viewModel.SelectedSession = viewModel.Sidebar.Sessions.First(session => session.Id == "session-welcome");
        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<MessageItemViewModel>().Count() == 2);
        var sessionId = viewModel.SelectedSession!.Id;
        await sessionService.SendPromptAsync(sessionId, Guid.NewGuid().ToString(), "开始完整工具轮次");

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
        // turn/end 前就建立活动过程组；最新正文候选仍独立、完整地显示在组外。
        var runningGroup = viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>().Single();
        ClassicAssert.AreEqual(5, runningGroup.Process.Count);
        ClassicAssert.IsTrue(runningGroup.IsExpanded);
        Assert.That(viewModel.ConversationItems.OfType<MessageItemViewModel>()
                             .Any(message => message.Content == "总结回答"), Is.True);
        ClassicAssert.AreEqual(2, runningGroup.Process.OfType<MessageItemViewModel>()
                                              .Count(message => message.HasReasoning &&
                                                                string.IsNullOrWhiteSpace(message.Content)));
        // 无正文助手提交不产生空气泡。
        Assert.That(viewModel.ConversationItems.OfType<MessageItemViewModel>()
                             .Any(message => message is { Role: MessageRole.Assistant } &&
                                             string.IsNullOrWhiteSpace(message.Content) && !message.HasReasoning),
                    Is.False);

        sessionService.PushTurnEnded(sessionId, 2);
        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>()
                                            .Any(group => !group.IsExpanded), 10000);

        var group = viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>().Single();
        ClassicAssert.AreEqual(2, group.ToolCallCount);
        ClassicAssert.AreEqual(1, group.MessageCount);
        ClassicAssert.AreEqual("2 次工具调用 · 1 条消息", group.SummaryText);
        ClassicAssert.IsFalse(group.IsExpanded);
        // 过程组包含两段中间思考、最终 reasoning 投影、中间说明与工具卡片；
        // 最终回复保持独立气泡。
        ClassicAssert.AreEqual(6, group.Process.Count);
        Assert.That(group.Process.Any(item => item is MessageItemViewModel
        {
            Content: "先重读当前文件，再做定点替换。"
        }), Is.True);
        var answer = viewModel.ConversationItems.OfType<MessageItemViewModel>()
                              .Single(message => message.Content == "总结回答");
        ClassicAssert.IsTrue(answer.HasReasoning);
        ClassicAssert.IsTrue(answer.IsReasoningProjected);
        ClassicAssert.IsFalse(answer.HasVisibleReasoning);
        ClassicAssert.IsFalse(answer.IsReasoningExpanded);
        var finalReasoning = group.Process.OfType<MessageItemViewModel>()
                                  .Single(item => item.Seq == answer.Seq &&
                                                  string.IsNullOrWhiteSpace(item.Content));
        ClassicAssert.AreEqual(answer.Id, finalReasoning.Id);
        ClassicAssert.AreEqual("两步都已完成，给出结论。", finalReasoning.Reasoning);

        // 用户消息开新一轮：之后的工具调用属于未收束的新轮次，另起显示不并入旧组。
        await sessionService.SendPromptAsync(sessionId, Guid.NewGuid().ToString(), "下一轮问题");
        sessionService.PushToolActivity(sessionId, "fs.read", "再次读取", false, 3);
        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>()
                                            .SelectMany(candidate =>
                                                            candidate.Process.OfType<ToolActivityItemViewModel>())
                                            .Any(tool => tool.ResultText == "再次读取"));
        Assert.That(viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>().Count(), Is.EqualTo(2));

        await viewModel.DisposeAsync();
    }

    [Test]
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
        // 该轮过程（含思考条目）仍保留在活动过程组中。
        sessionService.PushCommittedAssistantMessage(sessionId, string.Empty, 2, reasoning : "准备读取文件，先确认路径。");
        sessionService.PushToolActivity(sessionId, "fs.read", "文件内容摘要", false, 2);
        sessionService.PushCommittedAssistantMessage(sessionId, string.Empty, 2, reasoning : "读到一半被取消了。",
                                                     isInterrupted : true);
        sessionService.PushTurnEnded(sessionId, 2);

        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>()
                                            .SelectMany(group => group.Process.OfType<MessageItemViewModel>())
                                            .Any(message => message.IsInterrupted));
        var interruptedGroup = viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>().Single();
        // 中断标注在思考条目上，不产生只有「已中断」的空气泡。
        var interrupted = interruptedGroup.Process.OfType<MessageItemViewModel>()
                                          .Single(message => message.IsInterrupted);
        ClassicAssert.IsTrue(interrupted.HasReasoning);
        ClassicAssert.IsTrue(string.IsNullOrWhiteSpace(interrupted.Content));

        // 之后正常完成的轮次照常折叠。
        sessionService.PushCommittedAssistantMessage(sessionId, string.Empty, 3,
                                                     reasoning : "重新读取。");
        sessionService.PushToolActivity(sessionId, "fs.read", "文件内容摘要", false, 3);
        sessionService.PushCommittedAssistantMessage(sessionId, "恢复正常后的回答。", 3);
        sessionService.PushTurnEnded(sessionId, 3);

        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>()
                                            .Any(group => !group.IsExpanded));
        var group = viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>()
                             .Single(candidate => candidate.Process.OfType<MessageItemViewModel>()
                                                           .Any(message => message.Reasoning == "重新读取。"));
        ClassicAssert.AreEqual(1, group.ToolCallCount);
        Assert.That(viewModel.ConversationItems.OfType<MessageItemViewModel>()
                             .Any(message => message.Content == "恢复正常后的回答。"), Is.True);

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task TurnWithoutFinalAnswerStaysUnfoldedAfterBoundary()
    {
        var sessionService = new SimulatedSessionService();
        var backendService = new SimulatedBackendStatusService();
        var viewModel      = new MainWindowViewModel(sessionService, backendService, new StaticWorkspaceService());
        await viewModel.InitializeAsync();
        viewModel.SelectedSession = viewModel.Sidebar.Sessions.First(session => session.Id == "session-welcome");
        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<MessageItemViewModel>().Count() == 2);
        var sessionId = viewModel.SelectedSession!.Id;

        // 轮次以工具调用收尾、没有最终回复：仍以过程组呈现，工具卡片保留在完整过程内。
        sessionService.PushToolActivity(sessionId, "fs.read", "文件内容摘要", false, 2);
        sessionService.PushTurnEnded(sessionId, 2);
        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>()
                                            .Any(group => group.Process.OfType<ToolActivityItemViewModel>()
                                                               .Any(tool => tool.Status ==
                                                                            ToolActivityStatus.Succeeded)));
        Assert.That(viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>()
                             .SelectMany(group => group.Process).OfType<ToolActivityItemViewModel>().Any(), Is.True);

        await viewModel.DisposeAsync();
    }

    [Test]
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
        ClassicAssert.IsNotNull(first);
        ClassicAssert.IsNotNull(second);
        await WaitUntilAsync(() => viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>()
                                            .SelectMany(group => group.Process.OfType<ToolActivityItemViewModel>())
                                            .Count() == 2);
        var activeGroup = viewModel.ConversationItems.OfType<TurnProcessGroupViewModel>().Single();
        var cards       = activeGroup.Process.OfType<ToolActivityItemViewModel>().ToList();
        Assert.That(activeGroup.IsExpanded, Is.True);
        foreach (var card in cards) ClassicAssert.IsTrue(card.IsRunning);

        sessionService.SettleToolActivity(sessionId, first!.CallId, "文件内容摘要", false);
        sessionService.SettleToolActivity(sessionId, second!.CallId, null, true);
        await WaitUntilAsync(() => cards.All(card => !card.IsRunning));
        ClassicAssert.IsTrue(cards.Single(card => card.CallId == second.CallId).IsFailed);

        await viewModel.DisposeAsync();
    }

    private static MainWindowViewModel CreateViewModel()
    {
        return new MainWindowViewModel(new SimulatedSessionService(), new SimulatedBackendStatusService(),
                                       new SimulatedWorkspaceService());
    }

    [Test]
    public async Task SessionListDefaultsToGroupedViewWithCurrentHighlight()
    {
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync();
        await SelectFirstSessionAsync(viewModel);

        // 默认对齐参考客户端：按工作区分组（模拟服务登记了两个工作区）。
        ClassicAssert.AreEqual(1, viewModel.Sidebar.SessionListModeIndex);
        Assert.That(viewModel.Sidebar.SessionRows.Any(row => row is SessionGroupHeaderViewModel { TitleText: "示例工作区" }),
                    Is.True);
        ClassicAssert.IsTrue(viewModel.SelectedSession!.IsCurrent);
        foreach (var session in viewModel.Sidebar.Sessions.Where(session =>
                                                                     !ReferenceEquals(session,
                                                                         viewModel.SelectedSession)))
            ClassicAssert.IsFalse(session.IsCurrent);

        // 切回单列表：行投影就是会话顺序本身，无分组头。
        viewModel.Sidebar.SessionListModeIndex = 0;
        ClassicAssert.AreEqual(viewModel.Sidebar.Sessions,
                               viewModel.Sidebar.SessionRows.OfType<SessionItemViewModel>());
        ClassicAssert.IsEmpty(viewModel.Sidebar.SessionRows.OfType<SessionGroupHeaderViewModel>());

        await viewModel.DisposeAsync();
    }

    [Test]
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
        await SelectFirstSessionAsync(viewModel);

        viewModel.Sidebar.SessionListModeIndex = 1;

        // 未置顶工作区统一收进「工作区」分类，组序为后端顺序；组内成员按更新时间
        // 降序；空工作区仍显示；未被记账的会话落入「未分组」且该组仅在非空时出现。
        var shape = viewModel.Sidebar.SessionRows.Select(row => row switch
                              {
                                  SessionGroupHeaderViewModel header => $"header:{header.TitleText}",
                                  SessionItemViewModel session       => $"session:{session.Id}",
                                  _                                  => "other"
                              })
                             .ToArray();
        ClassicAssert.AreEqual(new[]
        {
            "header:工作区", "header:主工作区", "session:session-history", "session:session-native",
            "header:空工作区",
            "header:未分组", "session:session-welcome", "session:session-design", "session:session-todos",
            "session:session-deliverables"
        }, shape);
        // 模式切换不重建会话实例：选中与高亮保持。
        ClassicAssert.IsTrue(viewModel.SelectedSession!.IsCurrent);

        await viewModel.DisposeAsync();
    }

    [Test]
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

        Assert.That(viewModel.Sidebar.SessionRows.OfType<SessionItemViewModel>()
                             .Any(session => session.Id is "session-native" or "session-history"), Is.False);
        // 其他组的成员不受影响。
        Assert.That(viewModel.Sidebar.SessionRows.OfType<SessionItemViewModel>()
                             .Any(session => session.Id == "session-welcome"), Is.True);
        var collapsedHeader = viewModel.Sidebar.SessionRows.OfType<SessionGroupHeaderViewModel>()
                                       .Single(header => header.Key == "ws-main");
        ClassicAssert.IsFalse(collapsedHeader.IsExpanded);
        ClassicAssert.AreEqual(2, collapsedHeader.SessionCount);

        // 切回单列表再切回分组：收起状态按分组键保留。
        viewModel.Sidebar.SessionListModeIndex = 0;
        viewModel.Sidebar.SessionListModeIndex = 1;
        ClassicAssert.IsFalse(viewModel.Sidebar.SessionRows.OfType<SessionGroupHeaderViewModel>()
                                       .Single(header => header.Key == "ws-main")
                                       .IsExpanded);

        await viewModel.DisposeAsync();
    }

    [Test]
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
        ClassicAssert.IsEmpty(viewModel.Sidebar.SessionRows.OfType<SessionItemViewModel>()
                                       .Where(session => session is
                                                  { BlankState: SessionBlankState.ConfirmedBlank, IsCurrent: false }));
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
        ClassicAssert.AreSame(viewModel.SelectedSession, firstUngrouped);
        ClassicAssert.AreEqual(0, viewModel.Sidebar.SessionRows.IndexOf(firstUngrouped)  -
                                  viewModel.Sidebar.SessionRows.IndexOf(ungroupedHeader) - 1);
        ClassicAssert.IsTrue(firstUngrouped.IsCurrent);

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task WorkspacesChangedRebuildsGroupProjection()
    {
        var workspaces = new StaticWorkspaceService([]);
        var viewModel = new MainWindowViewModel(new SimulatedSessionService(), new SimulatedBackendStatusService(),
                                                workspaces);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.Sidebar.Sessions.Count > 0);
        viewModel.Sidebar.SessionListModeIndex = 1;

        // 基线未到达时只有未分组一组。
        Assert.That(viewModel.Sidebar.SessionRows.OfType<SessionGroupHeaderViewModel>()
                             .Count(header => header.TitleText == "未分组"), Is.EqualTo(1));

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
        ClassicAssert.AreEqual(new[]
        {
            "header:工作区",
            "header:后到的工作区", "session:session-welcome",
            "header:未分组", "session:session-history", "session:session-native",
            "session:session-design", "session:session-todos", "session:session-deliverables"
        }, shape);

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task BackgroundSessionAddRefreshesRowsWhileSelectionStays()
    {
        var sessionService = new SimulatedSessionService();
        var viewModel = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(),
                                                new StaticWorkspaceService());
        await viewModel.InitializeAsync();
        var selectedBefore = await SelectFirstSessionAsync(viewModel);
        viewModel.Sidebar.SessionListModeIndex = 0;

        // 后台新增并发送消息的会话（SessionsChanged → 延迟合并刷新）：选中实例不变，
        // 但行投影必须重建出新会话（回归：提前 return 曾跳过重建）。
        var created = await sessionService.CreateSessionAsync();
        await sessionService.SendPromptAsync(created.Id, "background-request", "后台消息");
        await WaitUntilAsync(() => viewModel.Sidebar.SessionRows.OfType<SessionItemViewModel>()
                                            .Any(session => session.Id == created.Id));
        ClassicAssert.AreSame(selectedBefore, viewModel.SelectedSession);
        ClassicAssert.IsTrue(selectedBefore!.IsCurrent);

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task NewConversationPageCreatesSessionOnlyOnFirstSend()
    {
        var sessionService = new SimulatedSessionService();
        var viewModel = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(),
                                                new StaticWorkspaceService());
        await viewModel.InitializeAsync();
        var previousSelection = await SelectFirstSessionAsync(viewModel);

        var historicalBlank = await sessionService.CreateSessionAsync();
        await WaitUntilAsync(() => viewModel.Sidebar.Sessions.All(session => session.Id != historicalBlank.Id));

        // 顶部新建进入新对话草稿页：不创建会话、不产生侧栏行；「不使用工作区」为显式兼容项。
        var createsBefore = sessionService.CreatedSessionCount;
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        ClassicAssert.AreEqual(createsBefore, sessionService.CreatedSessionCount);
        viewModel.SelectWorkspaceCommand.Execute(viewModel.WorkspaceOptions
                                                          .Single(option => option.IsWithoutWorkspace));
        viewModel.Composer.DraftMessage = "第一条消息";
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SelectedSession is not null &&
                                   !ReferenceEquals(viewModel.SelectedSession, previousSelection));
        ClassicAssert.AreEqual(createsBefore + 1, sessionService.CreatedSessionCount);
        var current = viewModel.SelectedSession!;
        Assert.That(viewModel.Sidebar.Sessions.Any(session => session.Id == current.Id), Is.True);
        ClassicAssert.AreEqual(SessionBlankState.Engaged, current.BlankState);

        var currentSummary = (await sessionService.GetSessionsAsync()).Single(summary => summary.Id == current.Id);
        ClassicAssert.AreEqual(SessionBlankState.Engaged, currentSummary.BlankState);

        await viewModel.DisposeAsync();
    }

    [Test]
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
        ClassicAssert.AreEqual("工作区一", viewModel.WorkspacePickerLabel);
        viewModel.Composer.DraftMessage = "未发送的草稿";
        await WaitUntilAsync(() => viewModel.CanSendDraft);
        ClassicAssert.AreEqual(createsBefore, sessionService.CreatedSessionCount);
        ClassicAssert.IsEmpty(sessionService.CreateRequests);
        ClassicAssert.IsTrue(viewModel.ShowNewConversationPage);
        Assert.That(viewModel.Sidebar.Sessions.Any(session => session.Id == "blank-a"), Is.False);

        viewModel.SelectWorkspaceCommand.Execute(viewModel.WorkspaceOptions
                                                          .Single(option => option.IsWithoutWorkspace));
        ClassicAssert.AreEqual("不使用工作区", viewModel.WorkspacePickerLabel);
        ClassicAssert.IsTrue(viewModel.CanSendDraft);
        ClassicAssert.AreEqual(createsBefore, sessionService.CreatedSessionCount);

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task GroupHeaderPlusEntersDraftPageWithWorkspacePreselected()
    {
        var workspaces = new StaticWorkspaceService([
            new WorkspaceSummary("ws-2", "工作区二", "C:/Code/WS2", [], DateTimeOffset.Now)
        ]);
        var sessionService = new AdoptionSessionService();
        var viewModel      = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(), workspaces);
        await viewModel.InitializeAsync();
        await SelectFirstSessionAsync(viewModel);

        // 工作区组头「+」：进入同一张新对话草稿页并把预选工作区改为对应工作区；
        // 不调用 session/create、不产生侧栏会话行。
        var createsBefore = sessionService.CreatedSessionCount;
        var header = viewModel.Sidebar.SessionRows.OfType<SessionGroupHeaderViewModel>()
                              .Single(item => item.Key == "ws-2");
        viewModel.Sidebar.CreateWorkspaceSessionCommand.Execute(header);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        ClassicAssert.AreEqual("工作区二", viewModel.WorkspacePickerLabel);
        ClassicAssert.AreEqual(createsBefore, sessionService.CreatedSessionCount);
        ClassicAssert.IsEmpty(sessionService.CreateRequests);
        viewModel.Composer.DraftMessage = "草稿文本";
        ClassicAssert.IsTrue(viewModel.CanSendDraft);

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task DraftPagePresetSelectionIsLocalOnlyAndSentWithCreate()
    {
        var sessionService = new AdoptionSessionService();
        var viewModel = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(),
                                                new StaticWorkspaceService([]));
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);

        // 模式始终有内置默认「标准模式」：初始勾选态与按钮文案对齐；下拉点选只改本地
        // 草稿预选，不调用 session/create；勾选随预选迁移。
        var presets = viewModel.AgentPresetOptions;
        ClassicAssert.AreEqual(4, presets.Count);
        ClassicAssert.AreEqual("标准模式", viewModel.PresetPickerLabel);
        ClassicAssert.IsTrue(presets.Single(option => option.Id                  == "standard").IsSelected);
        viewModel.SelectPresetCommand.Execute(presets.Single(option => option.Id == "ptc"));
        ClassicAssert.AreEqual("PTC 模式", viewModel.PresetPickerLabel);
        ClassicAssert.IsTrue(presets.Single(option => option.Id  == "ptc").IsSelected);
        ClassicAssert.IsFalse(presets.Single(option => option.Id == "standard").IsSelected);
        ClassicAssert.IsEmpty(sessionService.CreateRequests);

        // 发送前须显式选定工作区去向（含「不使用工作区」）；首发送把预选模式传入
        // session/create，成功后被整份消费——预选回到内置默认。
        viewModel.SelectWorkspaceCommand.Execute(viewModel.WorkspaceOptions
                                                          .Single(option => option.IsWithoutWorkspace));
        viewModel.Composer.DraftMessage = "首条消息";
        await WaitUntilAsync(() => viewModel.CanSendDraft);
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SelectedSession is { BlankState: SessionBlankState.Engaged });
        Assert.That(sessionService.CreateRequests, Has.Count.EqualTo(1));
        var (workspaceId, sessionId, agentPreset) = sessionService.CreateRequests.Single();
        ClassicAssert.IsNull(workspaceId);
        ClassicAssert.IsNull(sessionId);
        ClassicAssert.AreEqual("ptc", agentPreset);
        ClassicAssert.AreEqual("标准模式", viewModel.PresetPickerLabel);
        ClassicAssert.IsTrue(presets.Single(option => option.Id == "standard").IsSelected);

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task DraftPresetChangeAfterFailedSendInvalidatesPendingSession()
    {
        var sessionService = new AdoptionSessionService();
        var viewModel = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(),
                                                new StaticWorkspaceService([]));
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        viewModel.SelectWorkspaceCommand.Execute(viewModel.WorkspaceOptions
                                                          .Single(option => option.IsWithoutWorkspace));
        viewModel.Composer.DraftMessage = "首条消息";
        await WaitUntilAsync(() => viewModel.CanSendDraft);

        // 创建成功、发送失败：会话连同其创建目标与模式一起记账待复用。
        sessionService.EnqueueSendError(new InvalidOperationException("发送失败"));
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.HasError);
        ClassicAssert.AreEqual(1, sessionService.CreatedSessionCount);
        ClassicAssert.AreEqual(((string?, string?, string?))(null, null, "standard"), sessionService.CreateRequests[0]);

        // 改选模式后待复用失配失效（其 preset 已在创建时固定）：重试按新选择全新创建，
        // 不把首条消息送进旧模式会话。
        viewModel.SelectPresetCommand.Execute(viewModel.AgentPresetOptions
                                                       .Single(option => option.Id == "minimal"));
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SelectedSession is { BlankState: SessionBlankState.Engaged });
        ClassicAssert.AreEqual(2, sessionService.CreatedSessionCount);
        ClassicAssert.AreEqual(2, sessionService.CreateRequests.Count);
        ClassicAssert.AreEqual(((string?, string?, string?))(null, null, "minimal"), sessionService.CreateRequests[1]);

        await viewModel.DisposeAsync();
    }

    [Test]
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
        ClassicAssert.AreEqual(new ModelSelection("sim-alt", "alt-chat"), viewModel.Composer.CurrentModel);
        ClassicAssert.IsEmpty(sessionService.ModelSelectionRequests);

        viewModel.Composer.DraftMessage = "首条消息";
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SelectedSession is { BlankState: SessionBlankState.Engaged });
        var created = viewModel.SelectedSession!;

        // 创建携带工作区归属、无收养 id；预选模型在创建后对该 SessionId 应用；
        // 后端接受首条消息后才出现侧栏行（已开始），输入文本清空。
        Assert.That(sessionService.CreateRequests, Has.Count.EqualTo(1));
        var (workspaceId, adoptId, _) = sessionService.CreateRequests.Single();
        ClassicAssert.AreEqual("ws-1", workspaceId);
        ClassicAssert.IsNull(adoptId);
        ClassicAssert.AreEqual(new[] { created.Id }, sessionService.ModelSelectionRequests);
        Assert.That(viewModel.Sidebar.Sessions.Any(session => session.Id == created.Id), Is.True);
        ClassicAssert.IsFalse(viewModel.HasError);
        ClassicAssert.AreEqual(string.Empty, viewModel.Composer.DraftMessage);

        // 插入行后按工作区记账核对分组：新会话落在 ws-1 组内（默认分组视图）。
        var groupedRows = viewModel.Sidebar.SessionRows;
        var headerIndex = groupedRows.IndexOf(groupedRows.OfType<SessionGroupHeaderViewModel>()
                                                         .Single(header => header.Key == "ws-1"));
        var rowIndex = groupedRows.IndexOf(groupedRows.OfType<SessionItemViewModel>()
                                                      .Single(session => session.Id == created.Id));
        ClassicAssert.IsTrue(headerIndex >= 0 && rowIndex == headerIndex + 1);

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task DraftSendCreateFailureKeepsDraftAndSelections()
    {
        var workspaces = new StaticWorkspaceService([
            new WorkspaceSummary("ws-1", "工作区一", "C:/Code/WS1", [], DateTimeOffset.Now)
        ]);
        var sessionService = new AdoptionSessionService();
        sessionService.EnqueueCreateError(new HarnessRpcException("session/conflict", "创建失败（模拟）"));
        var viewModel = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(), workspaces);
        await viewModel.InitializeAsync();
        await SelectFirstSessionAsync(viewModel);
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        viewModel.SelectWorkspaceCommand.Execute(viewModel.WorkspaceOptions.Single(option => option.Id == "ws-1"));
        viewModel.Composer.DraftMessage = "失败保留草稿";
        var rowsBefore = viewModel.Sidebar.Sessions.Count;

        // 创建失败：文本与预选保留、停留草稿页、无新侧栏行。
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.HasError);
        ClassicAssert.IsNull(viewModel.SelectedSession);
        ClassicAssert.IsTrue(viewModel.ShowNewConversationPage);
        ClassicAssert.IsTrue(viewModel.CanSendDraft);
        ClassicAssert.AreEqual("失败保留草稿", viewModel.Composer.DraftMessage);
        ClassicAssert.AreEqual("工作区一", viewModel.WorkspacePickerLabel);
        ClassicAssert.AreEqual(rowsBefore, viewModel.Sidebar.Sessions.Count);
        Assert.That(sessionService.CreateRequests, Has.Count.EqualTo(1));

        // 重试按同一草稿再次创建（恰好一次成功），后端接受后进入普通会话。
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SelectedSession is { BlankState: SessionBlankState.Engaged });
        ClassicAssert.AreEqual(1, sessionService.CreatedSessionCount);
        ClassicAssert.AreEqual(2, sessionService.CreateRequests.Count);
        ClassicAssert.IsFalse(viewModel.HasError);

        await viewModel.DisposeAsync();
    }

    [Test]
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
        await SelectFirstSessionAsync(viewModel);
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        viewModel.SelectWorkspaceCommand.Execute(viewModel.WorkspaceOptions.Single(option => option.Id == "ws-2"));
        viewModel.Composer.DraftMessage = "首条消息";

        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.HasError);
        Assert.That(viewModel.ErrorText, Does.Contain("工作区关联失败"));
        // 保留已知 id：不导航、不重试创建，草稿文本保留。
        ClassicAssert.IsTrue(viewModel.ShowNewConversationPage);
        ClassicAssert.IsNull(viewModel.SelectedSession);
        ClassicAssert.AreEqual("首条消息", viewModel.Composer.DraftMessage);
        Assert.That(sessionService.CreateRequests, Has.Count.EqualTo(1));

        // 重试先经 session/create 收养（同一 sessionId + workspaceId）恢复关联，再发送；
        // 不产生第二个会话，发送成功后进入普通会话。
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SelectedSession?.Id == "created-9");
        ClassicAssert.AreEqual(SessionBlankState.Engaged, viewModel.SelectedSession!.BlankState);
        ClassicAssert.AreEqual(2, sessionService.CreateRequests.Count);
        ClassicAssert.AreEqual(("ws-2", (string?)null, "standard"), sessionService.CreateRequests[0]);
        ClassicAssert.AreEqual(("ws-2", "created-9", "standard"), sessionService.CreateRequests[1]);
        ClassicAssert.AreEqual(0, sessionService.CreatedSessionCount);
        ClassicAssert.AreEqual(new[] { "created-9" }, sessionService.SendRequests);
        ClassicAssert.IsFalse(viewModel.HasError);

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task LateDraftSendCompletionDoesNotStealNavigation()
    {
        var workspaces = new StaticWorkspaceService([
            new WorkspaceSummary("ws-2", "工作区二", "C:/Code/WS2", [], DateTimeOffset.Now)
        ]);
        var sessionService = new AdoptionSessionService();
        var viewModel      = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(), workspaces);
        await viewModel.InitializeAsync();
        var selectedBefore = await SelectFirstSessionAsync(viewModel);
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
        ClassicAssert.AreSame(selectedBefore, viewModel.SelectedSession);
        ClassicAssert.IsFalse(viewModel.HasError);
        var lateRow = viewModel.Sidebar.Sessions.FirstOrDefault(session => !knownIds.Contains(session.Id));
        ClassicAssert.IsNotNull(lateRow);
        ClassicAssert.AreEqual(SessionBlankState.Engaged, lateRow!.BlankState);
        // 草稿已被消费：重新进入草稿页不再有未发送文本，也不复用已发送内容。
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        ClassicAssert.AreEqual(string.Empty, viewModel.Composer.DraftMessage);

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task DraftSelectionsAndTextPersistAcrossSessionSwitches()
    {
        var workspaces = new StaticWorkspaceService([
            new WorkspaceSummary("ws-1", "工作区一", "C:/Code/WS1", [], DateTimeOffset.Now)
        ]);
        var sessionService = new AdoptionSessionService();
        var viewModel      = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(), workspaces);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.Composer.ModelOptions.Count > 0);
        var selectedBefore = await SelectFirstSessionAsync(viewModel);
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);

        // 草稿页填写文本、预选工作区与模型：全部只记本地草稿，无任何 RPC。
        viewModel.Composer.DraftMessage = "新对话草稿";
        viewModel.SelectWorkspaceCommand.Execute(viewModel.WorkspaceOptions.Single(option => option.Id == "ws-1"));
        viewModel.Composer.SelectedModelOption =
            viewModel.Composer.ModelOptions.Single(option => option.Model == "alt-chat");
        var createsBefore = sessionService.CreatedSessionCount;
        ClassicAssert.IsEmpty(sessionService.CreateRequests);

        // 切到旧会话再返回：文本、预选工作区与预选模型都恢复；已有会话各自草稿不受影响。
        viewModel.Sidebar.SelectSessionCommand.Execute(selectedBefore);
        await WaitUntilAsync(() => !viewModel.ShowNewConversationPage);
        ClassicAssert.AreEqual(string.Empty, viewModel.Composer.DraftMessage);
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        ClassicAssert.AreEqual("新对话草稿", viewModel.Composer.DraftMessage);
        ClassicAssert.AreEqual("工作区一", viewModel.WorkspacePickerLabel);
        ClassicAssert.AreEqual(new ModelSelection("sim-alt", "alt-chat"), viewModel.Composer.CurrentModel);
        ClassicAssert.AreEqual(createsBefore, sessionService.CreatedSessionCount);

        await viewModel.DisposeAsync();
    }

    [Test]
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
        await WaitUntilAsync(() => viewModel.Sidebar.Sessions.Any(session => session.Id == unknownId));
        viewModel.SelectedSession = viewModel.Sidebar.Sessions.First(session => session.Id == unknownId);

        // 未知状态的历史会话（元数据缺失）：可见但绝不显示新对话草稿页。
        ClassicAssert.AreEqual(unknownId, viewModel.SelectedSession!.Id);
        Assert.That(viewModel.Sidebar.Sessions.Any(session => session.Id == unknownId), Is.True);
        ClassicAssert.IsFalse(viewModel.ShowNewConversationPage);

        // 顶部新建：进入草稿页（不创建会话）；工作区下拉含「不使用工作区」显式兼容项。
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        ClassicAssert.AreEqual(0, sessionService.CreatedSessionCount);
        Assert.That(viewModel.WorkspaceOptions.Any(option => option.IsWithoutWorkspace), Is.True);

        // 选中任一会话：草稿页整体隐藏。
        var anySession = viewModel.Sidebar.Sessions.First(session => session.Id != unknownId);
        viewModel.Sidebar.SelectSessionCommand.Execute(anySession);
        await WaitUntilAsync(() => !viewModel.ShowNewConversationPage);

        await viewModel.DisposeAsync();
    }

    /// <summary>
    ///     创建/收养可控桩：记录每次 session/create 请求（workspaceId, sessionId），可注入
    ///     业务错误与阻塞门；会话列表可整体接管（SessionsOverride）。其余行为委托模拟实现。
    [Test]
    public async Task ListRefreshKeepsIntentionalDraftPageInsteadOfFallingBack()
    {
        var sessionService = new SimulatedSessionService();
        var viewModel = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(),
                                                new StaticWorkspaceService());
        await viewModel.InitializeAsync();
        // 启动即停留在新对话草稿页（守卫已生效），后台列表刷新不得把页面抢回旧会话。
        await WaitUntilAsync(() => viewModel.Sidebar.Sessions.Count > 0);
        ClassicAssert.IsNull(viewModel.SelectedSession);
        ClassicAssert.IsTrue(viewModel.ShowNewConversationPage);

        // 创建会话触发的列表刷新（SessionsChanged → 400ms 合并）同样不得抢回旧会话；
        // 草稿文本保持。
        viewModel.Composer.DraftMessage = "草稿文本";
        var created = await sessionService.CreateSessionAsync();
        await Task.Delay(900);
        ClassicAssert.IsNull(viewModel.SelectedSession);
        ClassicAssert.IsTrue(viewModel.ShowNewConversationPage);

        // 手动点击行不受守卫影响；再进草稿页文本仍在。
        viewModel.Sidebar.SelectSessionCommand.Execute(viewModel.Sidebar.Sessions.First(session => session.Id !=
                                                           created.Id));
        await WaitUntilAsync(() => !viewModel.ShowNewConversationPage);
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        ClassicAssert.AreEqual("草稿文本", viewModel.Composer.DraftMessage);

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task TargetChangeDuringFirstSendKeepsNewDraftAndSkipsStaleNavigation()
    {
        var workspaces = new StaticWorkspaceService([
            new WorkspaceSummary("ws-1", "工作区一", "C:/Code/WS1", [], DateTimeOffset.Now),
            new WorkspaceSummary("ws-2", "工作区二", "C:/Code/WS2", [], DateTimeOffset.Now.AddMinutes(-1))
        ]);
        var sessionService = new AdoptionSessionService();
        var viewModel      = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(), workspaces);
        await viewModel.InitializeAsync();
        await SelectFirstSessionAsync(viewModel);
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
        ClassicAssert.IsTrue(viewModel.ShowNewConversationPage);
        ClassicAssert.AreEqual("工作区二", viewModel.WorkspacePickerLabel);
        ClassicAssert.AreEqual("发送快照文本", viewModel.Composer.DraftMessage);
        var staleRow = viewModel.Sidebar.Sessions.Single(session => !knownIds.Contains(session.Id));
        ClassicAssert.AreEqual(SessionBlankState.Engaged, staleRow.BlankState);

        // 迟到会话未记为待复用：按新目标重试会创建新会话（ws-2）并正常导航收束。
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SelectedSession is { BlankState: SessionBlankState.Engaged } &&
                                   viewModel.SelectedSession.Id != staleRow.Id);
        ClassicAssert.AreEqual(2, sessionService.CreatedSessionCount);
        ClassicAssert.AreEqual(2, sessionService.CreateRequests.Count);
        ClassicAssert.AreEqual("ws-2", sessionService.CreateRequests[1].WorkspaceId);
        ClassicAssert.IsFalse(viewModel.HasError);

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task LateDraftSendDoesNotClearSwitchedSessionDraftWithIdenticalText()
    {
        var workspaces = new StaticWorkspaceService([
            new WorkspaceSummary("ws-1", "工作区一", "C:/Code/WS1", [], DateTimeOffset.Now)
        ]);
        var sessionService = new AdoptionSessionService();
        var viewModel      = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(), workspaces);
        await viewModel.InitializeAsync();
        var otherSession = await SelectFirstSessionAsync(viewModel);
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
        ClassicAssert.AreSame(otherSession, viewModel.SelectedSession);
        ClassicAssert.AreEqual("相同文本", viewModel.Composer.DraftMessage);
        ClassicAssert.IsFalse(viewModel.HasError);
        var lateRow = viewModel.Sidebar.Sessions.FirstOrDefault(session => !knownIds.Contains(session.Id));
        ClassicAssert.IsNotNull(lateRow);
        ClassicAssert.AreEqual(SessionBlankState.Engaged, lateRow!.BlankState);

        // 原草稿缓存已消费：再进草稿页不再有未发送文本。
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        ClassicAssert.AreEqual(string.Empty, viewModel.Composer.DraftMessage);

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task EmptySessionListStartupKeepsDraftModelPickerEnabledWithoutCreate()
    {
        var sessionService = new AdoptionSessionService { SessionsOverride = () => [] };
        var viewModel = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(),
                                                new StaticWorkspaceService());
        await viewModel.InitializeAsync();

        // 无历史会话启动：初始即草稿页（SelectedSession null→null 不触发 setter），模型
        // 目录加载完成后菜单即可用并支持本地预选（不发 RPC）；不发起任何创建/收养请求，
        // 再点「新建」也保持可用。
        ClassicAssert.IsNull(viewModel.SelectedSession);
        ClassicAssert.IsTrue(viewModel.ShowNewConversationPage);
        await WaitUntilAsync(() => viewModel.Composer.ModelOptions.Count > 0);
        ClassicAssert.IsTrue(viewModel.Composer.IsModelPickerEnabled);
        viewModel.Composer.SelectedModelOption =
            viewModel.Composer.ModelOptions.Single(option => option.Model == "alt-chat");
        ClassicAssert.AreEqual(new ModelSelection("sim-alt", "alt-chat"), viewModel.Composer.CurrentModel);
        ClassicAssert.IsEmpty(sessionService.ModelSelectionRequests);

        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        ClassicAssert.IsTrue(viewModel.Composer.IsModelPickerEnabled);
        ClassicAssert.IsEmpty(sessionService.CreateRequests);
        ClassicAssert.AreEqual(0, sessionService.CreatedSessionCount);

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task RepeatedNewPageEntryFromNullStatePreservesDraftAndPreselection()
    {
        var workspaces = new StaticWorkspaceService([
            new WorkspaceSummary("ws-1", "工作区一", "C:/Code/WS1", [], DateTimeOffset.Now)
        ]);
        var sessionService = new AdoptionSessionService();
        var viewModel      = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(), workspaces);
        await viewModel.InitializeAsync();
        await SelectFirstSessionAsync(viewModel);
        await WaitUntilAsync(() => viewModel.WorkspaceOptions.Any(option => option.Id == "ws-1"));

        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        viewModel.Composer.DraftMessage = "重复进入的草稿";
        viewModel.SelectWorkspaceCommand.Execute(viewModel.WorkspaceOptions.Single(option => option.Id == "ws-1"));
        viewModel.Composer.SelectedModelOption =
            viewModel.Composer.ModelOptions.Single(option => option.Model == "alt-chat");
        ClassicAssert.IsTrue(viewModel.Composer.IsModelPickerEnabled);

        // 已处于 null（草稿页）状态再次点「新建」：草稿页初始化可重入，草稿文本、工作区
        // 与模型预选全部保留，不触发任何 RPC。
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        ClassicAssert.IsNull(viewModel.SelectedSession);
        ClassicAssert.AreEqual("重复进入的草稿", viewModel.Composer.DraftMessage);
        ClassicAssert.AreEqual("工作区一", viewModel.WorkspacePickerLabel);
        ClassicAssert.AreEqual(new ModelSelection("sim-alt", "alt-chat"), viewModel.Composer.CurrentModel);
        ClassicAssert.IsTrue(viewModel.Composer.IsModelPickerEnabled);
        ClassicAssert.IsEmpty(sessionService.CreateRequests);
        ClassicAssert.IsEmpty(sessionService.ModelSelectionRequests);

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task LateSendCompletionAfterReturningToDraftPageClearsSentText()
    {
        var workspaces = new StaticWorkspaceService([
            new WorkspaceSummary("ws-2", "工作区二", "C:/Code/WS2", [], DateTimeOffset.Now)
        ]);
        var sessionService = new AdoptionSessionService();
        var viewModel      = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(), workspaces);
        await viewModel.InitializeAsync();
        var selectedBefore = await SelectFirstSessionAsync(viewModel);
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
        ClassicAssert.AreEqual("切走又回来的草稿", viewModel.Composer.DraftMessage);
        gate.TrySetResult();
        await WaitOrDumpAsync(viewModel,
                              () => viewModel.Sidebar.Sessions.Any(session => !knownIds.Contains(session.Id) &&
                                                                              session.BlankState ==
                                                                              SessionBlankState.Engaged), 5000);
        // 迟到结果不抢回页面：仍停留草稿页，输入框与工作区预选均已清空。
        ClassicAssert.IsNull(viewModel.SelectedSession);
        ClassicAssert.IsTrue(viewModel.ShowNewConversationPage);
        ClassicAssert.AreEqual(string.Empty, viewModel.Composer.DraftMessage);
        ClassicAssert.AreEqual("选择工作区", viewModel.WorkspacePickerLabel);
        ClassicAssert.IsFalse(viewModel.HasError);

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task RegisterWorkspaceAddsDraftMenuOptionWithoutError()
    {
        var viewModel = CreateViewModel();
        await viewModel.InitializeAsync();

        await viewModel.RegisterWorkspaceAsync("C:/Code/NewProject");

        // 登记成功无错误呈现；模拟服务经状态流回流投影，下拉选项随之包含新工作区。
        ClassicAssert.IsFalse(viewModel.HasError);
        var added = viewModel.WorkspaceOptions.Single(option => option.Path == "C:/Code/NewProject");
        ClassicAssert.AreEqual("NewProject", added.TitleText);
        Assert.That(viewModel.WorkspaceMenuOptions, Does.Contain(added));

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task RegisterWorkspaceFailureSurfacesWindowError()
    {
        var workspaces = new StaticWorkspaceService();
        var viewModel = new MainWindowViewModel(new SimulatedSessionService(), new SimulatedBackendStatusService(),
                                                workspaces);
        await viewModel.InitializeAsync();
        workspaces.RegisterError = new HarnessRpcException("workspace/invalid-path", "非法路径");

        await viewModel.RegisterWorkspaceAsync("C:/Not/A/Directory");

        // 登记失败呈现到窗口级错误条，选项集合不出现失败路径。
        Assert.That(viewModel.ErrorText, Does.Contain("添加工作区失败"));
        Assert.That(viewModel.WorkspaceOptions.Any(option => option.Path == "C:/Not/A/Directory"), Is.False);

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task TextEditedDuringSendThenSwitchAwayPreservesNewDraftSelections()
    {
        var workspaces = new StaticWorkspaceService([
            new WorkspaceSummary("ws-1", "工作区一", "C:/Code/WS1", [], DateTimeOffset.Now)
        ]);
        var sessionService = new AdoptionSessionService();
        var viewModel      = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(), workspaces);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.Composer.ModelOptions.Count > 0);
        var selectedBefore = await SelectFirstSessionAsync(viewModel);
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
        ClassicAssert.AreSame(selectedBefore, viewModel.SelectedSession);

        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        ClassicAssert.AreEqual("改写后的新草稿", viewModel.Composer.DraftMessage);
        ClassicAssert.AreEqual("工作区一", viewModel.WorkspacePickerLabel);
        ClassicAssert.AreEqual(new ModelSelection("sim-alt", "alt-chat"), viewModel.Composer.CurrentModel);
        ClassicAssert.IsFalse(viewModel.HasError);

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task ModelChangeDuringSendKeepsNewSelectionAndSkipsStaleNavigation()
    {
        var workspaces = new StaticWorkspaceService([
            new WorkspaceSummary("ws-1", "工作区一", "C:/Code/WS1", [], DateTimeOffset.Now)
        ]);
        var sessionService = new AdoptionSessionService();
        var viewModel      = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(), workspaces);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.Composer.ModelOptions.Count > 0);
        await SelectFirstSessionAsync(viewModel);
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
        ClassicAssert.IsNull(viewModel.SelectedSession);
        ClassicAssert.IsTrue(viewModel.ShowNewConversationPage);
        ClassicAssert.AreEqual("模型改选期间", viewModel.Composer.DraftMessage);
        ClassicAssert.AreEqual("工作区一", viewModel.WorkspacePickerLabel);
        ClassicAssert.AreEqual(new ModelSelection("sim", "sim-reasoner"), viewModel.Composer.CurrentModel);
        // 快照的选型（alt-chat）恰好应用一次到创建的会话；草稿保持新选型。
        ClassicAssert.AreEqual(new[] { $"select:{createdRow.Id}:sim-alt/alt-chat" },
                               sessionService.OperationLog
                                             .Where(log => log.StartsWith("select:", StringComparison.Ordinal))
                                             .ToArray());
        ClassicAssert.IsFalse(viewModel.HasError);

        await viewModel.DisposeAsync();
    }

    [Test]
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
        await SelectFirstSessionAsync(viewModel);
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
        ClassicAssert.AreEqual(("ws-2", "created-9", "standard"), sessionService.CreateRequests[1]);
        ClassicAssert.IsEmpty(sessionService.SendRequests);
        gate.TrySetResult();
        await WaitUntilAsync(() => viewModel.SelectedSession?.Id == "created-9");
        ClassicAssert.AreEqual(SessionBlankState.Engaged, viewModel.SelectedSession!.BlankState);

        // 顺序与记账：收养先于发送；关联确实写入工作区投影（非仅导航成功）。
        var attachIndex = sessionService.OperationLog.IndexOf("create:ws-2|created-9");
        var sendIndex   = sessionService.OperationLog.IndexOf("send:created-9");
        ClassicAssert.IsTrue(attachIndex >= 0 && sendIndex >= 0 && attachIndex < sendIndex);
        var accounted = (await workspaces.GetWorkspacesAsync()).Single(workspace => workspace.Id == "ws-2");
        Assert.That(accounted.SessionIds, Does.Contain("created-9"));
        ClassicAssert.AreEqual(0, sessionService.CreatedSessionCount);
        ClassicAssert.IsFalse(viewModel.HasError);

        await viewModel.DisposeAsync();
    }

    [Test]
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
        await SelectFirstSessionAsync(viewModel);
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        viewModel.SelectWorkspaceCommand.Execute(viewModel.WorkspaceOptions.Single(option => option.Id == "ws-2"));
        viewModel.Composer.DraftMessage = "关联失败重试的草稿";
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.HasError);

        // 关联恢复再次失败（收养调用同样抛 attach-failed）：保留草稿与可恢复状态，
        // 不发送、不重复创建，如实展示错误。
        sessionService.EnqueueCreateError(new HarnessRpcException("session/workspace-attach-failed", "再次挂接失败（模拟）",
                                                                  JsonDocument.Parse("""{"sessionId":"created-9"}""")
                                                                              .RootElement.Clone()), false);
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => sessionService.CreateRequests.Count == 2 && !viewModel.IsStartingConversation);
        ClassicAssert.IsEmpty(sessionService.SendRequests);
        ClassicAssert.AreEqual(0, sessionService.CreatedSessionCount);
        ClassicAssert.IsTrue(viewModel.ShowNewConversationPage);
        ClassicAssert.AreEqual("关联失败重试的草稿", viewModel.Composer.DraftMessage);
        Assert.That(viewModel.ErrorText, Does.Contain("工作区关联失败"));

        // 可恢复状态未丢：再次重试从恢复关联开始并最终完成发送。
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SelectedSession?.Id == "created-9");
        ClassicAssert.AreEqual(new[] { "created-9" }, sessionService.SendRequests);
        ClassicAssert.AreEqual(0, sessionService.CreatedSessionCount);

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task SelectModelFailureAfterAttachReusesSessionOnRetry()
    {
        var workspaces = new StaticWorkspaceService([
            new WorkspaceSummary("ws-1", "工作区一", "C:/Code/WS1", [], DateTimeOffset.Now)
        ]);
        var sessionService = new AdoptionSessionService(workspaces.AddSession);
        var viewModel      = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(), workspaces);
        await viewModel.InitializeAsync();
        await WaitUntilAsync(() => viewModel.Composer.ModelOptions.Count > 0);
        await SelectFirstSessionAsync(viewModel);
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
        Assert.That(viewModel.ErrorText, Does.Contain("选型失败"));
        Assert.That(sessionService.CreateRequests, Has.Count.EqualTo(1));
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SelectedSession is { BlankState: SessionBlankState.Engaged });
        var createdId = viewModel.SelectedSession!.Id;
        Assert.That(sessionService.CreateRequests, Has.Count.EqualTo(1));
        ClassicAssert.AreEqual(1, sessionService.CreatedSessionCount);
        ClassicAssert.AreEqual(new[] { createdId, createdId }, sessionService.ModelSelectionRequests);
        ClassicAssert.AreEqual(new[] { createdId }, sessionService.SendRequests);
        ClassicAssert.IsFalse(viewModel.HasError);

        await viewModel.DisposeAsync();
    }

    [Test]
    public async Task SendFailureAfterAttachReusesSessionOnRetry()
    {
        var workspaces = new StaticWorkspaceService([
            new WorkspaceSummary("ws-1", "工作区一", "C:/Code/WS1", [], DateTimeOffset.Now)
        ]);
        var sessionService = new AdoptionSessionService(workspaces.AddSession);
        var viewModel      = new MainWindowViewModel(sessionService, new SimulatedBackendStatusService(), workspaces);
        await viewModel.InitializeAsync();
        await SelectFirstSessionAsync(viewModel);
        viewModel.Sidebar.NewSessionCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ShowNewConversationPage);
        viewModel.SelectWorkspaceCommand.Execute(viewModel.WorkspaceOptions.Single(option => option.Id == "ws-1"));
        viewModel.Composer.DraftMessage = "发送失败重试";
        sessionService.EnqueueSendError(new InvalidOperationException("发送失败（模拟）"));

        // 关联已完成、仅发送失败：会话记为待复用，重试不再创建/收养，对同一会话再次发送。
        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.HasError);
        Assert.That(viewModel.ErrorText, Does.Contain("发送失败"));
        Assert.That(sessionService.CreateRequests, Has.Count.EqualTo(1));
        Assert.That(sessionService.SendRequests, Has.Count.EqualTo(1));

        viewModel.SendDraftCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.SelectedSession is { BlankState: SessionBlankState.Engaged });
        Assert.That(sessionService.CreateRequests, Has.Count.EqualTo(1));
        ClassicAssert.AreEqual(2, sessionService.SendRequests.Count);
        ClassicAssert.IsTrue(sessionService.SendRequests.All(id => id == viewModel.SelectedSession!.Id));
        ClassicAssert.AreEqual(1, sessionService.CreatedSessionCount);
        ClassicAssert.IsFalse(viewModel.HasError);

        await viewModel.DisposeAsync();
    }

    internal static void EnsureAvaloniaPlatform()
    {
        if (_avaloniaIsInitialized) return;

        lock (AvaloniaSetupLock)
        {
            if (_avaloniaIsInitialized) return;

            var originalContext = SynchronizationContext.Current;
            try
            {
                AppBuilder.Configure<App>()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                    .SetupWithoutStarting();
                _avaloniaIsInitialized = true;
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(originalContext);
            }
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

        ClassicAssert.IsTrue(TryCondition(condition), "预期的异步 ViewModel 状态未在超时前出现。");
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
            string?           workspaceId       = null, string? sessionId = null, string? agentPreset = null,
            CancellationToken cancellationToken = default)
        {
            return _inner.CreateSessionAsync(workspaceId, sessionId, cancellationToken : cancellationToken);
        }

        public Task<string> ForkSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            return _inner.ForkSessionAsync(sessionId, cancellationToken);
        }

        public Task<string> RenameSessionAsync(
            string sessionId, string title, CancellationToken cancellationToken = default)
        {
            return _inner.RenameSessionAsync(sessionId, title, cancellationToken);
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
    ///     创建/收养可控桩：记录每次 session/create 请求（workspaceId, sessionId, agentPreset），
    ///     可注入业务错误与阻塞门；会话列表可整体接管（SessionsOverride）。其余行为委托模拟实现。
    /// </summary>
    private sealed class AdoptionSessionService(Action<string, string>? onSessionCreatedInWorkspace = null)
        : ISessionService
    {
        private readonly Queue<(Exception Error, bool OnlyForNewSession)> _createErrors = [];
        private readonly SimulatedSessionService                          _inner = new(onSessionCreatedInWorkspace);
        private readonly Queue<Exception>                                 _selectModelErrors = [];
        private readonly Queue<Exception>                                 _sendErrors = [];

        /// <summary>下一次 create 阻塞到手动放行（并发合并与迟到结果测试用）。</summary>
        public TaskCompletionSource? BlockNextCreate { get; set; }

        public List<(string? WorkspaceId, string? SessionId, string? AgentPreset)> CreateRequests { get; } = [];

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
            string?           workspaceId       = null, string? sessionId = null, string? agentPreset = null,
            CancellationToken cancellationToken = default)
        {
            CreateRequests.Add((workspaceId, sessionId, agentPreset));
            OperationLog.Add($"create:{workspaceId ?? "-"}|{sessionId ?? "-"}");
            if (BlockNextCreate is { } gate)
            {
                BlockNextCreate = null;
                await gate.Task.WaitAsync(cancellationToken);
            }

            if (!_createErrors.TryPeek(out var entry) || (entry.OnlyForNewSession && sessionId is not null))
                return await _inner.CreateSessionAsync(workspaceId, sessionId, cancellationToken : cancellationToken);
            _createErrors.Dequeue();
            throw entry.Error;
        }

        public Task<string> ForkSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            return _inner.ForkSessionAsync(sessionId, cancellationToken);
        }

        public Task<string> RenameSessionAsync(
            string sessionId, string title, CancellationToken cancellationToken = default)
        {
            return _inner.RenameSessionAsync(sessionId, title, cancellationToken);
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
            // .Result 是同步委托边界（SessionsOverride 无法 await）下的受控使用：
            // 内层 GetSessionsAsync 为 Task.FromResult 同步完成，无死锁风险。
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

    /// <summary>可控工作区桩：静态集合 + 手动触发变更事件；AddSession 模拟创建记账回流，登记可编程结果。</summary>
    private sealed class StaticWorkspaceService(IReadOnlyList<WorkspaceSummary>? items = null) : IWorkspaceService
    {
        private IReadOnlyList<WorkspaceSummary> _items = items ?? [];

        /// <summary>登记编排注入点：非 null 时抛出（模拟业务失败），否则登记成功。</summary>
        public Exception? RegisterError { get; set; }

        /// <summary>重命名/删除编排注入点：非 null 时抛出（模拟业务失败）。</summary>
        public Exception? RenameError { get; set; }

        public Exception? DeleteError { get; set; }

        public string? LastRenamedWorkspaceId { get; private set; }

        public string? LastRenamedTitle { get; private set; }

        public string? LastDeletedWorkspaceId { get; private set; }

        public IReadOnlySet<string> ArchivedSessionIds { get; private set; } = new HashSet<string>();

        public event EventHandler? WorkspacesChanged;

        public Task<IReadOnlyList<WorkspaceSummary>> GetWorkspacesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_items);
        }

        /// <summary>模拟 workspace/create 回流：新行插头部并广播（对齐 follow upsert 语义）。</summary>
        public Task<WorkspaceSummary> RegisterWorkspaceAsync(string path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RegisterError is not null) throw RegisterError;

            var workspace = new WorkspaceSummary($"workspace-{Guid.NewGuid():N}", "新登记工作区", path, [],
                                                 DateTimeOffset.Now);
            _items = [workspace, .. _items];
            RaiseChanged();
            return Task.FromResult(workspace);
        }

        /// <summary>模拟 workspace/rename 回流：本地改标题并广播（follow upsert 幂等对齐）。</summary>
        public Task<WorkspaceSummary> RenameWorkspaceAsync(
            string workspaceId, string title, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RenameError is not null) throw RenameError;

            LastRenamedWorkspaceId = workspaceId;
            LastRenamedTitle       = title;
            var renamed = _items.Single(workspace => workspace.Id == workspaceId) with { Title = title };
            _items = _items.Select(workspace => workspace.Id == workspaceId ? renamed : workspace).ToArray();
            RaiseChanged();
            return Task.FromResult(renamed);
        }

        /// <summary>模拟 workspace/delete 回流：本地移除并广播（follow remove 幂等对齐）。</summary>
        public Task DeleteWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DeleteError is not null) throw DeleteError;

            LastDeletedWorkspaceId = workspaceId;
            _items                 = _items.Where(workspace => workspace.Id != workspaceId).ToArray();
            RaiseChanged();
            return Task.CompletedTask;
        }

        /// <summary>模拟 workspace/archiveSession 回流：并入归档并广播。</summary>
        public Task ArchiveSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArchivedSessionIds = new HashSet<string>(ArchivedSessionIds) { sessionId };
            RaiseChanged();
            return Task.CompletedTask;
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
            string?           workspaceId       = null, string? sessionId = null, string? agentPreset = null,
            CancellationToken cancellationToken = default)
        {
            return _inner.CreateSessionAsync(workspaceId, sessionId, cancellationToken : cancellationToken);
        }

        public Task<string> ForkSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            return _inner.ForkSessionAsync(sessionId, cancellationToken);
        }

        public Task<string> RenameSessionAsync(
            string sessionId, string title, CancellationToken cancellationToken = default)
        {
            return _inner.RenameSessionAsync(sessionId, title, cancellationToken);
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
            string?           workspaceId       = null, string? sessionId = null, string? agentPreset = null,
            CancellationToken cancellationToken = default)
        {
            return _inner.CreateSessionAsync(workspaceId, sessionId, cancellationToken : cancellationToken);
        }

        public Task<string> ForkSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            return _inner.ForkSessionAsync(sessionId, cancellationToken);
        }

        public Task<string> RenameSessionAsync(
            string sessionId, string title, CancellationToken cancellationToken = default)
        {
            return _inner.RenameSessionAsync(sessionId, title, cancellationToken);
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
