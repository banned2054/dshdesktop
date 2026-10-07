using DshDesktop.Core.Models;
using DshDesktop.Core.Services;
using DshDesktop.Services.Conversations;
using DshDesktop.ViewModels;
using NUnit.Framework;
using System.Collections.ObjectModel;

namespace DshDesktop.Tests;

public sealed class DeliverablesTimelineTests
{
    private static readonly DateTimeOffset CreatedAt = DateTimeOffset.UnixEpoch;

    [Test]
    public void EightFilesAppearAfterClosingAnswerAndCanExpandAndCollapse()
    {
        var declarations = Enumerable.Range(0, 8)
            .Select(index => new DeliveredFileDeclaration($"file-{index}.txt", null, 20, index))
            .ToArray();
        var announcement = new DeliverablesPresentedAnnouncement(20, 1, CreatedAt, declarations);
        var items = Build("session-a", [
            new ConversationMessage(1, "user", MessageRole.User, "Question", CreatedAt),
            announcement,
            new ConversationMessage(30, "answer", MessageRole.Assistant, "Done", CreatedAt, 1),
            new TurnBoundary(31, 1, CreatedAt)
        ]);

        var card = items.OfType<DeliverablesCardViewModel>().Single();
        Assert.That(items.IndexOf(card), Is.GreaterThan(items.ToList().FindIndex(item => item is MessageItemViewModel message && message.Seq == 30)));
        Assert.That(card.Files.Select(file => file.Name), Is.EqualTo(Enumerable.Range(0, 8).Select(index => $"file-{index}.txt")));
        Assert.That(card.TitleText, Is.EqualTo("已编辑 8 个文件"));
        Assert.That(card.VisibleFiles, Has.Count.EqualTo(3));

        card.ToggleCommand.Execute(null);
        Assert.That(card.VisibleFiles, Has.Count.EqualTo(8));
        card.ToggleCommand.Execute(null);
        Assert.That(card.VisibleFiles, Has.Count.EqualTo(3));
    }

    [Test]
    public void ReplayedAndRepeatedDeclarationsDeduplicateByEventAndPathAndKeepWorkspaceCard()
    {
        var first = new DeliverablesPresentedAnnouncement(20, 1, CreatedAt,
        [
            new("a.txt", "first", 20, 0),
            new("b.txt", null, 20, 1)
        ]);
        var second = new DeliverablesPresentedAnnouncement(21, 1, CreatedAt,
        [
            new("a.txt", "latest", 21, 0),
            new("c.txt", null, 21, 1)
        ]);
        var items = Build("session-a", [
            new ConversationMessage(1, "user", MessageRole.User, "Question", CreatedAt),
            first,
            first, // follow snapshot entry plus its replay update
            second,
            new ConversationMessage(30, "answer", MessageRole.Assistant, "Done", CreatedAt, 1),
            new TurnBoundary(31, 1, CreatedAt),
            new DeliverablesPresentedAnnouncement(40, 1, CreatedAt,
                [new("d.txt", null, 40, 0), new("a.txt", "too late", 40, 1)]),
            new WorkspaceChangesAnnouncement(41, 1, CreatedAt)
        ]);

        var card = items.OfType<DeliverablesCardViewModel>().Single();
        Assert.That(card.Files.Select(file => file.Name), Is.EqualTo(new[] { "a.txt", "b.txt", "c.txt" }));
        Assert.That(card.Files[0].Description, Is.EqualTo("latest"));
        Assert.That(items.OfType<WorkspaceChangesCardViewModel>().Count(), Is.EqualTo(1));
        Assert.That(items.IndexOf(items.OfType<WorkspaceChangesCardViewModel>().Single()), Is.LessThan(items.IndexOf(card)));

        // A history rebuild uses a fresh projection state and still produces one card per turn.
        var rebuilt = Build("session-a", [
            new ConversationMessage(1, "user", MessageRole.User, "Question", CreatedAt),
            first,
            first,
            second,
            new ConversationMessage(30, "answer", MessageRole.Assistant, "Done", CreatedAt, 1),
            new TurnBoundary(31, 1, CreatedAt)
        ]);
        Assert.That(rebuilt.OfType<DeliverablesCardViewModel>().Count(), Is.EqualTo(1));
        Assert.That(rebuilt.OfType<DeliverablesCardViewModel>().Single().Files, Has.Count.EqualTo(3));

        // The same turn number in another session owns a separate projection.
        var otherSession = Build("session-b", [
            new ConversationMessage(1, "user", MessageRole.User, "Question", CreatedAt),
            new DeliverablesPresentedAnnouncement(20, 1, CreatedAt, [new("other.txt", null, 20, 0)]),
            new ConversationMessage(30, "answer", MessageRole.Assistant, "Done", CreatedAt, 1),
            new TurnBoundary(31, 1, CreatedAt)
        ]);
        Assert.That(otherSession.OfType<DeliverablesCardViewModel>().Single().Files.Single().Name, Is.EqualTo("other.txt"));
        Assert.That(rebuilt.OfType<DeliverablesCardViewModel>().Single().SessionId, Is.EqualTo("session-a"));
    }

    [Test]
    public void FileClickRequestsInAppViewAndReportsStaleSessionWithoutDroppingDeclaration()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dsh-deliverables-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var existingPath = Path.Combine(root, "current.txt");
            File.WriteAllText(existingPath, "minimal test fixture");
            var requested = new List<DeliveredFileViewModel>();
            var activeSessionId = "session-a";
            var card = new DeliverablesCardViewModel("session-a", root,
                new DeliverablesPresentedAnnouncement(20, 1, CreatedAt,
                [
                    new("current.txt", null, 20, 0),
                    new("missing.txt", null, 20, 1)
                ]), null, action => action(), null,
                file => requested.Add(file), id => id == activeSessionId);

            // 缺失文件的提示在卡片上保留，但单击仍交给面板显示具体原因。
            Assert.That(card.Files[1].StatusText, Does.Contain("缺失"));
            card.Files[0].OpenCommand.Execute(null);
            Assert.That(requested, Has.Count.EqualTo(1));
            Assert.That(requested[0].Path, Is.EqualTo("current.txt"));
            Assert.That(requested[0].Turn, Is.EqualTo(1));

            activeSessionId = "session-b";
            card.Files[0].OpenCommand.Execute(null);
            Assert.That(requested, Has.Count.EqualTo(1));
            Assert.That(card.Files[0].StatusText, Does.Contain("会话已切换"));

            activeSessionId = "session-a";
            card.Files[1].OpenCommand.Execute(null);
            Assert.That(requested, Has.Count.EqualTo(2));
            Assert.That(requested[1].Path, Is.EqualTo("missing.txt"));
            Assert.That(card.Files, Has.Count.EqualTo(2));
            Assert.That(card.Files[1].StatusText, Does.Contain("缺失"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ObservableCollection<ConversationItemViewModel> Build(
        string sessionId, IReadOnlyList<ConversationEntry> entries)
    {
        var items = new ObservableCollection<ConversationItemViewModel>();
        var assembly = new TimelineAssembly(items,
            changesCardFactory: announcement => new WorkspaceChangesCardViewModel(sessionId, announcement,
                null, _ => { }, action => action()),
            deliverablesCardFactory: (announcement, changesSeq) => new DeliverablesCardViewModel(sessionId,
                Path.GetTempPath(), announcement, null, action => action(), changesSeq,
                null, id => id == sessionId));
        foreach (var entry in entries) assembly.Add(entry);
        assembly.CompleteRestoredProjection();
        return items;
    }

    /// <summary>固定摘要的改动服务：验证交付卡按解析路径匹配计数并降级。</summary>
    private sealed class StubChangesService(WorkspaceChangesSummary summary) : IWorkspaceChangesService
    {
        public Task<WorkspaceChangesSummary?> GetSummaryAsync(
            string sessionId, long seq, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkspaceChangesSummary?>(summary);

        public Task<WorkspaceFileDiff?> GetDiffAsync(
            string sessionId, long seq, int index, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkspaceFileDiff?>(null);
    }

    [Test]
    public void CountsMatchDeclarationsByResolvedPathAndDegradeSilently()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dsh-deliverables-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            // 摘要：current.txt 有行级计数，binary.bin 是二进制；notes.txt 不在快照里。
            var summary = new WorkspaceChangesSummary(1,
            [
                new WorkspaceChangedFileInfo("current.txt", "current.txt", 7, 2, false, false),
                new WorkspaceChangedFileInfo("binary.bin", "binary.bin", 0, 0, true, false)
            ], 2, 7, 2);
            var card = new DeliverablesCardViewModel("session-a", root,
                new DeliverablesPresentedAnnouncement(20, 1, CreatedAt,
                [
                    new("current.txt", null, 20, 0),
                    new("binary.bin", null, 20, 1),
                    new("notes.txt", null, 20, 2)
                ]), new StubChangesService(summary), action => action(), 41,
                _ => { }, _ => true);

            // 摘要任务已完成：取数在构造内联完成，无需等待。
            Assert.That(card.HasTotals, Is.True);
            Assert.That(card.HeaderAddedText, Is.EqualTo("+7"));
            Assert.That(card.HeaderDeletedText, Is.EqualTo("−2"));
            Assert.That(card.Files, Has.Count.EqualTo(3));
            Assert.That(card.Files[0].HasLineCounts, Is.True);
            Assert.That(card.Files[0].AddedText, Is.EqualTo("+7"));
            Assert.That(card.Files[0].DeletedText, Is.EqualTo("−2"));
            Assert.That(card.Files[1].HasCountText, Is.True);
            Assert.That(card.Files[1].CountText, Is.EqualTo("二进制"));
            // 声明不在摘要里：无计数、无标注，行右侧留空。
            Assert.That(card.Files[2].HasCountData, Is.False);
            Assert.That(card.Files[2].HasLineCounts, Is.False);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void SingleFileCardExposesEditedTitleAndCounts()
    {
        var card = new DeliverablesCardViewModel("session-a", null,
            new DeliverablesPresentedAnnouncement(20, 1, CreatedAt,
                [new("src/notes.txt", null, 20, 0)]),
            null, action => action(), null, _ => { }, _ => true);

        Assert.That(card.IsSingleFile, Is.True);
        Assert.That(card.SingleTitleText, Is.EqualTo("已编辑 notes.txt"));
        Assert.That(card.SingleHasLineCounts, Is.False);
        Assert.That(card.SingleHasCountText, Is.False);
        Assert.That(card.HasTotals, Is.False);
    }
}
