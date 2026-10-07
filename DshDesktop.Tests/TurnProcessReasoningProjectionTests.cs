using DshDesktop.Core.Models;
using DshDesktop.Services.Conversations;
using DshDesktop.ViewModels;
using NUnit.Framework;
using System.Collections.ObjectModel;

namespace DshDesktop.Tests;

public sealed class TurnProcessReasoningProjectionTests
{
    private const           string         Reasoning = "**完整思考第一行**\n第二行包含推理细节。";
    private const           string         Content   = "回答原文\n\n**保留 Markdown 与换行**";
    private static readonly DateTimeOffset CreatedAt = DateTimeOffset.UnixEpoch;

    [TestCase(20)]
    [TestCase(21)]
    [TestCase(40)]
    [TestCase(41)]
    public void ProcessShowsAllLoadedItemsAndAppendsWithObjectReuse(int totalCount)
    {
        var group = new TurnProcessGroupViewModel(1, 1);
        for (var seq = 1; seq <= totalCount; seq++) group.Add(new TestConversationItem(seq));

        Assert.That(group.Process, Has.Count.EqualTo(totalCount));
        Assert.That(group.VisibleProcess, Has.Count.EqualTo(totalCount));
        Assert.That(group.HiddenProcessCount, Is.Zero);
        Assert.That(group.HasEarlierProcess, Is.False);
        Assert.That(group.ShowEarlierProcessCommand.CanExecute(null), Is.False);
        Assert.That(group.VisibleProcess[0].Seq, Is.EqualTo(1));
        Assert.That(group.VisibleProcess[^1].Seq, Is.EqualTo(totalCount));
        Assert.That(group.VisibleProcess.Select(item => item.Seq),
                    Is.EqualTo(Enumerable.Range(1, totalCount).Select(seq => (long)seq)));
        Assert.That(group.VisibleProcess.All(item => group.Process.Any(process => ReferenceEquals(item, process))),
                    Is.True, "可见投影必须复用完整过程中的同一批条目对象。");

        var appended = new TestConversationItem(totalCount + 1);
        group.Add(appended);

        Assert.That(group.Process, Has.Count.EqualTo(totalCount + 1));
        Assert.That(group.VisibleProcess, Has.Count.EqualTo(totalCount + 1), "追加条目必须立即全部可见。");
        Assert.That(group.HiddenProcessCount, Is.Zero);
        Assert.That(group.HasEarlierProcess, Is.False);
        Assert.That(group.ShowEarlierProcessCommand.CanExecute(null), Is.False);
        Assert.That(group.VisibleProcess.Select(item => item.Seq),
                    Is.EqualTo(Enumerable.Range(1, totalCount + 1).Select(seq => (long)seq)));
        Assert.That(group.VisibleProcess[^1], Is.SameAs(appended));
    }

    [Test]
    public void ToggleCollapsesWithoutTrimmingAndAppendStaysVisible()
    {
        var state = new TurnProcessExpansionState();
        var group = new TurnProcessGroupViewModel(1, 1, state, true);
        for (var seq = 1; seq <= 41; seq++) group.Add(new TestConversationItem(seq));

        Assert.That(group.IsExpanded, Is.True);
        Assert.That(group.Process, Has.Count.EqualTo(41));
        Assert.That(group.VisibleProcess, Has.Count.EqualTo(41));
        Assert.That(group.HiddenProcessCount, Is.Zero);

        group.ToggleCommand.Execute(null);

        Assert.That(state.IsExpanded, Is.False);
        Assert.That(state.HasRecordedExpansion, Is.True);
        Assert.That(state.HasUserSetExpansion, Is.True);
        Assert.That(group.IsExpanded, Is.False);
        Assert.That(group.Process, Has.Count.EqualTo(41), "折叠不得裁剪完整过程。");
        Assert.That(group.VisibleProcess, Has.Count.EqualTo(41), "折叠不得裁剪可见过程。");

        var appended = new TestConversationItem(42);
        group.Add(appended);

        Assert.That(group.Process, Has.Count.EqualTo(42));
        Assert.That(group.VisibleProcess, Has.Count.EqualTo(42), "折叠中的追加必须同步可见。");
        Assert.That(group.VisibleProcess[^1], Is.SameAs(appended));
        Assert.That(group.HiddenProcessCount, Is.Zero);
        Assert.That(group.HasEarlierProcess, Is.False);
        Assert.That(group.ShowEarlierProcessCommand.CanExecute(null), Is.False);

        group.ToggleCommand.Execute(null);

        Assert.That(group.IsExpanded, Is.True);
        Assert.That(group.VisibleProcess, Has.Count.EqualTo(42));
    }

    [Test]
    public void ExpansionStateNotifiesDerivedPropertiesAndKeepsTurnsSeparate()
    {
        var firstState         = new TurnProcessExpansionState();
        var secondState        = new TurnProcessExpansionState();
        var firstTurn          = new TurnProcessGroupViewModel(1, 1, firstState, true);
        var secondTurn         = new TurnProcessGroupViewModel(1, 2, secondState);
        var firstNotifications = new List<string?>();
        firstTurn.PropertyChanged += (_, args) => firstNotifications.Add(args.PropertyName);

        Assert.That(firstTurn.IsExpanded, Is.True);
        Assert.That(firstState.IsExpanded, Is.True,
                    "新建时采用的默认展开态应写入状态对象，确保重建后仍可恢复。");

        firstTurn.IsExpanded = false;
        firstTurn.IsExpanded = true;

        for (var seq = 1; seq <= 41; seq++)
        {
            firstTurn.Add(new TestConversationItem(seq));
            secondTurn.Add(new TestConversationItem(seq));
        }

        Assert.That(firstTurn.Process, Has.Count.EqualTo(41));
        Assert.That(firstTurn.VisibleProcess, Has.Count.EqualTo(41));
        Assert.That(firstTurn.HiddenProcessCount, Is.Zero);
        Assert.That(firstTurn.HasEarlierProcess, Is.False);
        Assert.That(firstTurn.ShowEarlierProcessCommand.CanExecute(null), Is.False);

        firstTurn.ToggleCommand.Execute(null);

        Assert.That(firstNotifications, Does.Contain(nameof(TurnProcessGroupViewModel.HiddenProcessCount)));
        Assert.That(firstNotifications, Does.Contain(nameof(TurnProcessGroupViewModel.HasEarlierProcess)));
        Assert.That(firstNotifications, Does.Contain(nameof(TurnProcessGroupViewModel.HiddenProcessCountText)));
        Assert.That(firstNotifications, Does.Contain(nameof(TurnProcessGroupViewModel.IsExpanded)));
        Assert.That(firstTurn.IsExpanded, Is.False);
        Assert.That(firstState.IsExpanded, Is.False);
        Assert.That(firstState.HasRecordedExpansion, Is.True);
        Assert.That(firstState.HasUserSetExpansion, Is.True);

        secondTurn.ToggleCommand.Execute(null);

        Assert.That(secondTurn.IsExpanded, Is.True);
        Assert.That(secondState.IsExpanded, Is.True);
        Assert.That(secondState.HasRecordedExpansion, Is.True);
        Assert.That(secondState.HasUserSetExpansion, Is.True);

        var rebuiltFirstTurn = new TurnProcessGroupViewModel(1, 1, firstState);
        for (var seq = 1; seq <= 41; seq++) rebuiltFirstTurn.Add(new TestConversationItem(seq));
        var rebuiltSecondTurn = new TurnProcessGroupViewModel(1, 2, secondState);
        for (var seq = 1; seq <= 41; seq++) rebuiltSecondTurn.Add(new TestConversationItem(seq));

        Assert.That(rebuiltFirstTurn.IsExpanded, Is.False, "重建必须保留各 turn 的用户折叠选择。");
        Assert.That(rebuiltFirstTurn.Process, Has.Count.EqualTo(41));
        Assert.That(rebuiltFirstTurn.VisibleProcess, Has.Count.EqualTo(41));
        Assert.That(rebuiltFirstTurn.HiddenProcessCount, Is.Zero);
        Assert.That(rebuiltSecondTurn.IsExpanded, Is.True, "不同 turn 的展开状态必须独立重建。");
        Assert.That(rebuiltSecondTurn.Process, Has.Count.EqualTo(41));
        Assert.That(rebuiltSecondTurn.VisibleProcess, Has.Count.EqualTo(41));
        Assert.That(rebuiltSecondTurn.HiddenProcessCount, Is.Zero);
    }

    [Test]
    public void CompletedAnswerProjectsExpandableReasoningWithoutChangingOriginalData()
    {
        var items    = new ObservableCollection<ConversationItemViewModel>();
        var assembly = new TimelineAssembly(items);
        assembly.Add(new ConversationMessage(1, "user", MessageRole.User, "问题", CreatedAt));
        assembly.Add(new ConversationMessage(2, "answer", MessageRole.Assistant, Content,
                                             CreatedAt, 1, Reasoning : Reasoning));
        var answer = (MessageItemViewModel)items[1];

        assembly.Add(new TurnBoundary(3, 1, CreatedAt));

        Assert.That(items, Has.Count.EqualTo(3));
        var group = (TurnProcessGroupViewModel)items[1];
        Assert.That(group.SummaryText, Is.EqualTo("已思考"));
        Assert.That(group.MessageCount, Is.Zero);
        Assert.That(group.IsPartialTurn, Is.False);
        Assert.That(group.IsExpanded, Is.False);
        Assert.That(group.Process, Has.Count.EqualTo(1));
        Assert.That(items[2], Is.SameAs(answer));
        Assert.That(answer.HasVisibleReasoning, Is.False);
        Assert.That(answer.Reasoning, Is.EqualTo(Reasoning));
        Assert.That(answer.Content, Is.EqualTo(Content));

        var projection = (MessageItemViewModel)group.Process[0];
        Assert.That(projection, Is.Not.SameAs(answer));
        Assert.That(projection.Id, Is.EqualTo(answer.Id));
        Assert.That(projection.Seq, Is.EqualTo(answer.Seq));
        Assert.That(projection.Content, Is.Empty);
        Assert.That(projection.Reasoning, Is.EqualTo(Reasoning));
        Assert.That(projection.HasVisibleReasoning, Is.True);
        Assert.That(projection.IsReasoningExpanded, Is.False);
        group.ToggleCommand.Execute(null);
        Assert.That(group.IsExpanded, Is.True);
        projection.ToggleReasoningCommand.Execute(null);
        Assert.That(projection.IsReasoningExpanded, Is.True);
        Assert.That(projection.Reasoning, Is.EqualTo(Reasoning));
        projection.ToggleReasoningCommand.Execute(null);
        Assert.That(projection.IsReasoningExpanded, Is.False);
    }

    [Test]
    public void CompletedAnswerWithoutReasoningOrOtherProcessDoesNotCreateGroup()
    {
        var items    = new ObservableCollection<ConversationItemViewModel>();
        var assembly = new TimelineAssembly(items);
        assembly.Add(new ConversationMessage(1, "user", MessageRole.User, "问题", CreatedAt));
        assembly.Add(new ConversationMessage(2, "answer", MessageRole.Assistant, Content,
                                             CreatedAt, 1));
        var answer = (MessageItemViewModel)items[1];

        assembly.Add(new TurnBoundary(3, 1, CreatedAt));

        Assert.That(items, Has.Count.EqualTo(2));
        Assert.That(items.OfType<TurnProcessGroupViewModel>(), Is.Empty);
        Assert.That(items[1], Is.SameAs(answer));
        Assert.That(answer.IsReasoningProjected, Is.False);
        Assert.That(answer.Content, Is.EqualTo(Content));
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    public void OpenTurnOrTruncatedWindowKeepsReasoningVisible(bool includeUser, bool endTurn)
    {
        var items    = new ObservableCollection<ConversationItemViewModel>();
        var assembly = new TimelineAssembly(items);
        if (includeUser)
            assembly.Add(new ConversationMessage(1, "user", MessageRole.User, "问题", CreatedAt));
        assembly.Add(new ConversationMessage(2, "answer", MessageRole.Assistant, Content,
                                             CreatedAt, 1, Reasoning : Reasoning));
        var answer = (MessageItemViewModel)items[^1];
        if (endTurn) assembly.Add(new TurnBoundary(3, 1, CreatedAt));

        Assert.That(items, Has.Count.EqualTo(1 + (includeUser ? 1 : 0) + (endTurn ? 1 : 0)));
        Assert.That(items[^1], Is.SameAs(answer));
        if (endTurn)
        {
            var group = (TurnProcessGroupViewModel)items[^2];
            Assert.That(group.IsPartialTurn, Is.EqualTo(!includeUser));
            var reasoningProjection = group.Process.OfType<MessageItemViewModel>().Single();
            Assert.That(reasoningProjection.Id, Is.EqualTo(answer.Id));
            Assert.That(reasoningProjection.Reasoning, Is.EqualTo(Reasoning));
            Assert.That(answer.IsReasoningProjected, Is.True);
            Assert.That(answer.HasVisibleReasoning, Is.False);
        }
        else
        {
            Assert.That(items.OfType<TurnProcessGroupViewModel>(), Is.Empty);
            Assert.That(answer.IsReasoningProjected, Is.False);
            Assert.That(answer.HasVisibleReasoning, Is.True);
        }

        Assert.That(answer.Reasoning, Is.EqualTo(Reasoning));
        Assert.That(answer.Content, Is.EqualTo(Content));
    }

    [Test]
    public void RepeatedTurnBoundaryDoesNotDuplicateProjection()
    {
        var items    = new ObservableCollection<ConversationItemViewModel>();
        var assembly = new TimelineAssembly(items);
        assembly.Add(new ConversationMessage(1, "user", MessageRole.User, "问题", CreatedAt));
        assembly.Add(new ConversationMessage(2, "answer", MessageRole.Assistant, Content,
                                             CreatedAt, 1, Reasoning : Reasoning));
        var boundary = new TurnBoundary(3, 1, CreatedAt);
        assembly.Add(boundary);
        var group      = (TurnProcessGroupViewModel)items[1];
        var projection = group.Process[0];
        var answer     = items[2];

        assembly.Add(boundary);
        assembly.Add(new TurnBoundary(4, 1, CreatedAt));

        Assert.That(items, Has.Count.EqualTo(3));
        Assert.That(items.OfType<TurnProcessGroupViewModel>().Count(), Is.EqualTo(1));
        Assert.That(items[1], Is.SameAs(group));
        Assert.That(items[2], Is.SameAs(answer));
        Assert.That(group.Process, Has.Count.EqualTo(1));
        Assert.That(group.Process[0], Is.SameAs(projection));
    }

    [Test]
    public void NullableTurnIdentityIsEnrichedAndLateKnownBoundaryCannotCloseAnotherTurn()
    {
        var items    = new ObservableCollection<ConversationItemViewModel>();
        var assembly = new TimelineAssembly(items);
        assembly.Add(new ConversationMessage(1, "user", MessageRole.User, "问题", CreatedAt));
        assembly.Add(new ConversationMessage(2, "reasoning", MessageRole.Assistant, string.Empty,
                                             CreatedAt, Reasoning : "先检查工具结果。"));
        assembly.Add(new ToolActivity(3, "call", "fs.read", "{}", ToolActivityStatus.Running,
                                      null, null, CreatedAt, Turn : 7));
        assembly.Add(new ConversationMessage(4, "answer", MessageRole.Assistant, Content,
                                             CreatedAt, Reasoning : Reasoning));
        assembly.Add(new TurnBoundary(5, 7, CreatedAt));

        var completedGroup  = items.OfType<TurnProcessGroupViewModel>().Single();
        var completedAnswer = items.OfType<MessageItemViewModel>().Single(item => item.Id == "answer");
        Assert.That(completedGroup.IsPartialTurn, Is.False,
                    "Turn 缺失的用户/消息之后由工具补上的身份，不得抹掉已观察的轮次起点。");
        Assert.That(completedGroup.IsExpanded, Is.False);
        Assert.That(completedGroup.Process.OfType<ToolActivityItemViewModel>().Count(), Is.EqualTo(1));
        Assert.That(completedGroup.Process.OfType<MessageItemViewModel>()
                                  .Any(item => item.Id == completedAnswer.Id && item.Reasoning == Reasoning), Is.True);
        Assert.That(completedAnswer.IsReasoningProjected, Is.True);

        var nextItems    = new ObservableCollection<ConversationItemViewModel>();
        var nextAssembly = new TimelineAssembly(nextItems);
        nextAssembly.Add(new ConversationMessage(10, "next-user", MessageRole.User, "下一轮", CreatedAt,
                                                 8));
        nextAssembly.Add(new ConversationMessage(11, "next-reasoning", MessageRole.Assistant,
                                                 string.Empty, CreatedAt, 8,
                                                 Reasoning : "新轮进行中。"));
        var nextGroup = nextItems.OfType<TurnProcessGroupViewModel>().Single();

        nextAssembly.Add(new TurnBoundary(12, 7, CreatedAt));
        Assert.That(nextGroup.IsExpanded, Is.True,
                    "迟到的旧 Turn 边界不得收束当前已知的不同 Turn。");
        Assert.That(nextItems.OfType<TurnProcessGroupViewModel>().Count(), Is.EqualTo(1));

        nextAssembly.Add(new ConversationMessage(13, "next-answer", MessageRole.Assistant, Content,
                                                 CreatedAt, 8));
        nextAssembly.Add(new TurnBoundary(14, 8, CreatedAt));
        Assert.That(nextGroup.IsExpanded, Is.False);
        Assert.That(nextGroup.Process.OfType<MessageItemViewModel>()
                             .Any(item => item.Id == "next-reasoning"), Is.True);
        Assert.That(nextItems.OfType<MessageItemViewModel>()
                             .Single(item => item.Id == "next-answer").IsReasoningProjected, Is.False);

        var isolatedItems    = new ObservableCollection<ConversationItemViewModel>();
        var isolatedAssembly = new TimelineAssembly(isolatedItems);
        isolatedAssembly.Add(new ConversationMessage(20, "first-user", MessageRole.User, "第一轮", CreatedAt,
                                                     20));
        isolatedAssembly.Add(new ConversationMessage(21, "first-reasoning", MessageRole.Assistant,
                                                     string.Empty, CreatedAt, 20,
                                                     Reasoning : "第一轮过程。"));
        isolatedAssembly.Add(new ToolActivity(22, "other-turn-call", "fs.read", "{}",
                                              ToolActivityStatus.Running, null, null, CreatedAt, Turn : 21));
        var isolatedGroups = isolatedItems.OfType<TurnProcessGroupViewModel>().ToArray();
        Assert.That(isolatedGroups, Has.Length.EqualTo(2));
        Assert.That(isolatedGroups[0].Process.OfType<ToolActivityItemViewModel>(), Is.Empty);
        Assert.That(isolatedGroups[1].Process.OfType<ToolActivityItemViewModel>().Count(), Is.EqualTo(1));
        Assert.That(isolatedGroups.All(group => group.IsPartialTurn), Is.True,
                    "两个已知身份不同时必须拆组，并对缺少完整起点/结尾的片段保持保守标记。");
    }

    [TestCase("interrupted")]
    [TestCase("aborted")]
    [TestCase("future-reason")]
    public void InterruptedOrUnknownBoundaryDoesNotClaimCompleteTurn(string reason)
    {
        var items    = new ObservableCollection<ConversationItemViewModel>();
        var assembly = new TimelineAssembly(items);
        assembly.Add(new ConversationMessage(1, "user", MessageRole.User, "问题", CreatedAt));
        assembly.Add(new ConversationMessage(2, "answer", MessageRole.Assistant, Content,
                                             CreatedAt, 1, Reasoning : Reasoning, IsInterrupted : true));
        var answer = (MessageItemViewModel)items[1];

        assembly.Add(new TurnBoundary(3, 1, CreatedAt, reason));

        Assert.That(items, Has.Count.EqualTo(3));
        Assert.That(items[2], Is.SameAs(answer));
        Assert.That(answer.IsInterrupted, Is.True);
        Assert.That(answer.HasStatusHint, Is.True);
        Assert.That(answer.Content, Is.EqualTo(Content));
        Assert.That(answer.Reasoning, Is.EqualTo(Reasoning));
        Assert.That(answer.HasVisibleReasoning, Is.False);
        var group = (TurnProcessGroupViewModel)items[1];
        Assert.That(group.IsPartialTurn, Is.True,
                    "只有兼容的无原因边界或 completed 才能表示完整结束。");
        Assert.That(group.Process, Has.Count.EqualTo(1));
        var projection = (MessageItemViewModel)group.Process[0];
        Assert.That(projection.Content, Is.Empty);
        Assert.That(projection.Reasoning, Is.EqualTo(Reasoning));
        Assert.That(projection.HasVisibleReasoning, Is.True);
        Assert.That(projection.IsInterrupted, Is.False);
        Assert.That(projection.HasStatusHint, Is.False);
    }

    [Test]
    public void LongTruncatedTurnShowsAllProcessesOutsideFinalAnswer()
    {
        var items    = new ObservableCollection<ConversationItemViewModel>();
        var assembly = new TimelineAssembly(items);
        assembly.Add(new ConversationMessage(1, "user", MessageRole.User, "问题", CreatedAt, 4));
        for (var seq = 2; seq <= 22; seq++)
            assembly.Add(new ConversationMessage(seq, $"reason-{seq}", MessageRole.Assistant,
                                                 string.Empty, CreatedAt, 4,
                                                 Reasoning : $"思考步骤 {seq}"));
        assembly.Add(new ConversationMessage(23, "final", MessageRole.Assistant, Content,
                                             CreatedAt, 4, Reasoning : Reasoning));
        assembly.Add(new TurnBoundary(24, 4, CreatedAt));

        var group  = items.OfType<TurnProcessGroupViewModel>().Single();
        var answer = items.OfType<MessageItemViewModel>().Single(message => message.Id == "final");
        Assert.That(group.Process, Has.Count.EqualTo(22));
        Assert.That(group.VisibleProcess, Has.Count.EqualTo(22), "截断轮的全部已加载过程都必须可见。");
        Assert.That(group.HiddenProcessCount, Is.Zero);
        Assert.That(group.HasEarlierProcess, Is.False);
        Assert.That(group.ShowEarlierProcessCommand.CanExecute(null), Is.False);
        Assert.That(group.VisibleProcess.Select(item => item.Seq),
                    Is.EqualTo(Enumerable.Range(2, 22).Select(seq => (long)seq)));
        Assert.That(group.VisibleProcess.All(item => group.Process.Any(process => ReferenceEquals(item, process))),
                    Is.True, "可见投影必须复用完整过程中的同一批条目对象。");
        Assert.That(group.Process.OfType<MessageItemViewModel>()
                         .Any(message => message.Content == Content), Is.False,
                    "过程组可以保留同 Id 的思考投影，但不能收纳或替代最终正文。");
        Assert.That(items[^1], Is.SameAs(answer));
        Assert.That(answer.Content, Is.EqualTo(Content));
        Assert.That(answer.IsReasoningProjected, Is.True);
        Assert.That(group.Process.OfType<MessageItemViewModel>()
                         .Any(message => message.Id == answer.Id && message.Reasoning == Reasoning), Is.True);
    }

    private sealed class TestConversationItem(long seq) : ConversationItemViewModel(seq);
}
