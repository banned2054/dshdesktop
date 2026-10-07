using DshDesktop.Core.Models;
using DshDesktop.Core.Services;
using DshDesktop.Services.Conversations;
using DshDesktop.ViewModels;
using NUnit.Framework;
using System.Text;
using System.Text.Json;

namespace DshDesktop.Tests;

/// <summary>
///     交付文件面板的数据关联规则：完整差异优先、同会话同轮同路径才匹配、
///     只有成功落定的 edit/write 产生片段、多次编辑按事件顺序分列、
///     当前内容与不可用的降级路径。
/// </summary>
public sealed class DeliverableViewResolverTests
{
    private static readonly DateTimeOffset CreatedAt = DateTimeOffset.UnixEpoch;
    private string _root = string.Empty;

    [SetUp]
    public void CreateRoot()
    {
        _root = Path.Combine(Path.GetTempPath(), $"dsh-deliverable-view-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void DeleteRoot()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Test]
    public async Task CurrentFileIsShownAsPlainTextEvenWhenSameTurnDiffAndEditsExist()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "a.txt"), "plain current line\nsecond line\n");
        var diff = TextDiff("src/a.txt");
        var changes = new StubChangesService();
        changes.SetSummary(100, Summary("src/a.txt"));
        changes.SetDiff((100, 0), diff);
        var entries = new ConversationEntry[]
        {
            new WorkspaceChangesAnnouncement(100, 1, CreatedAt),
            Edit(90, 1, Abs("src/a.txt"), "old", "new")
        };

        var view = await DeliverableViewResolver.ResolveAsync(
            "session-a", 1, "src/a.txt", _root, entries, changes);

        Assert.That(view.Kind, Is.EqualTo(DeliverableViewKind.CurrentText));
        Assert.That(view.CurrentText, Is.EqualTo("plain current line\nsecond line\n"));
        Assert.That(view.Diff, Is.Null);
        Assert.That(view.Fragments, Is.Empty);
        Assert.That(view.SourceText, Is.EqualTo("当前文件内容"));
    }

    [Test]
    public async Task CurrentFileIsShownAsPlainTextInsteadOfHistoricalEditFragments()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "a.txt"), "new current line\n");
        var entries = new ConversationEntry[]
        {
            Edit(90, 1, Abs("src/a.txt"), "old line\n", "new line\n")
        };

        var view = await DeliverableViewResolver.ResolveAsync(
            "session-a", 1, "src/a.txt", _root, entries, null);

        Assert.That(view.Kind, Is.EqualTo(DeliverableViewKind.CurrentText));
        Assert.That(view.CurrentText, Is.EqualTo("new current line\n"));
        Assert.That(view.Fragments, Is.Empty);
    }

    [Test]
    public async Task ChangesAnnouncementFromAnotherTurnDoesNotAffectCurrentContent()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "a.txt"), "current");
        var changes = new StubChangesService();
        changes.SetSummary(100, Summary("src/a.txt"));
        var entries = new ConversationEntry[]
        {
            new WorkspaceChangesAnnouncement(100, 2, CreatedAt),
            Edit(90, 1, Abs("src/a.txt"), "old", "new")
        };

        var view = await DeliverableViewResolver.ResolveAsync(
            "session-a", 1, "src/a.txt", _root, entries, changes);

        Assert.That(view.Kind, Is.EqualTo(DeliverableViewKind.CurrentText));
        Assert.That(view.CurrentText, Is.EqualTo("current"));
        Assert.That(view.Fragments, Is.Empty);
    }

    [Test]
    public async Task SameNameInDifferentDirectoryDoesNotMatch()
    {
        var changes = new StubChangesService();
        changes.SetSummary(100, Summary("other/a.txt"));
        var entries = new ConversationEntry[]
        {
            new WorkspaceChangesAnnouncement(100, 1, CreatedAt),
            Edit(90, 1, Abs("other/a.txt"), "old", "new")
        };

        var view = await DeliverableViewResolver.ResolveAsync(
            "session-a", 1, "src/a.txt", _root, entries, changes);

        // 改动摘要与编辑记录都指向 other/，与交付的 src/a.txt 不同路径，不得误匹配。
        Assert.That(view.Kind, Is.EqualTo(DeliverableViewKind.Unavailable));
        Assert.That(view.Fragments, Is.Empty);
    }

    [Test]
    public async Task MissingFileCanStillShowVerifiedHistoricalEditFragment()
    {
        var entries = new ConversationEntry[]
        {
            Edit(90, 1, Abs("src/a.txt"), "old line\n", "new line\n")
        };

        var view = await DeliverableViewResolver.ResolveAsync(
            "session-a", 1, "src/a.txt", _root, entries, null);

        Assert.That(view.Kind, Is.EqualTo(DeliverableViewKind.Fragments));
        var fragment = view.Fragments.Single();
        Assert.That(fragment.OldText, Is.EqualTo("old line\n"));
        Assert.That(fragment.NewText, Is.EqualTo("new line\n"));
        Assert.That(fragment.Seq, Is.EqualTo(90));
        Assert.That(fragment.Note, Is.Null);
    }

    [Test]
    public async Task FailedOrUnsettledEditsProduceNoFragments()
    {
        var entries = new ConversationEntry[]
        {
            Edit(80, 1, Abs("src/a.txt"), "old", "new", ToolActivityStatus.Failed),
            Edit(90, 1, Abs("src/a.txt"), "old", "new", ToolActivityStatus.Running)
        };

        var view = await DeliverableViewResolver.ResolveAsync(
            "session-a", 1, "src/a.txt", _root, entries, null);

        Assert.That(view.Kind, Is.EqualTo(DeliverableViewKind.Unavailable));
        Assert.That(view.Fragments, Is.Empty);
        Assert.That(view.NoticeText, Does.Contain("缺失").Or.Contain("不可访问"));
    }

    [Test]
    public async Task MetaDiffsAreUsedAsVerifiedFragmentsIncludingPureInsertion()
    {
        var meta = new[] { new ToolFileDiff(Abs("src/a.txt"), null, "inserted line") };
        var entries = new ConversationEntry[]
        {
            Edit(90, 1, Abs("src/a.txt"), "old", "new", metaDiffs : meta)
        };

        var view = await DeliverableViewResolver.ResolveAsync(
            "session-a", 1, "src/a.txt", _root, entries, null);

        var fragment = view.Fragments.Single();
        Assert.That(fragment.OldText, Is.Null);
        Assert.That(fragment.NewText, Is.EqualTo("inserted line"));
    }

    [Test]
    public async Task EmptyMetaDiffsDoNotFallBackToEditArguments()
    {
        var entries = new ConversationEntry[]
        {
            Edit(90, 1, Abs("src/a.txt"), "old", "new", metaDiffs: [])
        };

        var view = await DeliverableViewResolver.ResolveAsync(
            "session-a", 1, "src/a.txt", _root, entries, null);

        Assert.That(view.Kind, Is.EqualTo(DeliverableViewKind.Unavailable));
        Assert.That(view.Fragments, Is.Empty);
    }

    [Test]
    public async Task MetaDiffWithDifferentPathIsFiltered()
    {
        var meta = new[] { new ToolFileDiff(Abs("other/a.txt"), "foreign old", "foreign new") };
        var entries = new ConversationEntry[]
        {
            Edit(90, 1, Abs("src/a.txt"), "argument old", "argument new", metaDiffs: meta)
        };

        var view = await DeliverableViewResolver.ResolveAsync(
            "session-a", 1, "src/a.txt", _root, entries, null);

        Assert.That(view.Kind, Is.EqualTo(DeliverableViewKind.Unavailable));
        Assert.That(view.Fragments, Is.Empty);
    }

    [Test]
    public async Task MultipleEditsStaySeparateInEventOrder()
    {
        var entries = new ConversationEntry[]
        {
            Edit(90, 1, Abs("src/a.txt"), "first old", "first new"),
            Edit(120, 1, Abs("src/a.txt"), "second old", "second new")
        };

        var view = await DeliverableViewResolver.ResolveAsync(
            "session-a", 1, "src/a.txt", _root, entries, null);

        // 两次编辑分列展示；不合并、不拼接成「整轮净差异」。
        Assert.That(view.Fragments, Has.Count.EqualTo(2));
        Assert.That(view.Fragments[0].Seq, Is.LessThan(view.Fragments[1].Seq));
        Assert.That(view.Fragments[0].OldText, Is.EqualTo("first old"));
        Assert.That(view.Fragments[0].NewText, Is.EqualTo("first new"));
        Assert.That(view.Fragments[1].OldText, Is.EqualTo("second old"));
        Assert.That(view.Fragments[1].NewText, Is.EqualTo("second new"));
        Assert.That(view.SourceText, Does.Contain("2 次编辑记录"));
    }

    [Test]
    public async Task ReplaceAllArgumentFallbackCarriesLimitationNote()
    {
        var entries = new ConversationEntry[]
        {
            Edit(90, 1, Abs("src/a.txt"), "old", "new", replaceAll : true)
        };

        var view = await DeliverableViewResolver.ResolveAsync(
            "session-a", 1, "src/a.txt", _root, entries, null);

        Assert.That(view.Fragments.Single().Note, Does.Contain("replace_all"));
    }

    [Test]
    public async Task WriteCreationProducesWholeContentFragmentFromResultMarker()
    {
        var entries = new ConversationEntry[]
        {
            Write(90, 1, Abs("new-file.cs"), "created content",
                  "<path>…</path>\n<type>file</type>\n<content>\nCreated file\n</content>")
        };

        var view = await DeliverableViewResolver.ResolveAsync(
            "session-a", 1, "new-file.cs", _root, entries, null);

        var fragment = view.Fragments.Single();
        Assert.That(fragment.Kind, Is.EqualTo("write"));
        Assert.That(fragment.OldText, Is.Empty);
        Assert.That(fragment.NewText, Is.EqualTo("created content"));
        Assert.That(fragment.Note, Does.Contain("新建文件"));
    }

    [Test]
    public async Task WriteCreationWithEmptyMetaDiffsStillUsesCreatedFileContent()
    {
        var entries = new ConversationEntry[]
        {
            Write(90, 1, Abs("new-file.cs"), "created content",
                  "<path>…</path>\n<type>file</type>\n<content>\nCreated file\n</content>", metaDiffs: [])
        };

        var view = await DeliverableViewResolver.ResolveAsync(
            "session-a", 1, "new-file.cs", _root, entries, null);

        var fragment = view.Fragments.Single();
        Assert.That(fragment.Kind, Is.EqualTo("write"));
        Assert.That(fragment.NewText, Is.EqualTo("created content"));
        Assert.That(fragment.Note, Does.Contain("新建文件"));
    }

    [Test]
    public async Task WriteUpdateWithoutRecordedBeforeProducesNoFragment()
    {
        var entries = new ConversationEntry[]
        {
            Write(90, 1, Abs("src/a.txt"), "updated content",
                  "<content>\nUpdated file\n</content>")
        };

        var view = await DeliverableViewResolver.ResolveAsync(
            "session-a", 1, "src/a.txt", _root, entries, null);

        // 覆盖写无法恢复改写前内容，不得用当前文件或参数内容冒充历史原文。
        Assert.That(view.Kind, Is.EqualTo(DeliverableViewKind.Unavailable));
        Assert.That(view.Fragments, Is.Empty);
    }

    [Test]
    public async Task ReadableFileWithoutHistoryDegradesToCurrentContent()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "a.txt"), "current body");

        var view = await DeliverableViewResolver.ResolveAsync(
            "session-a", 1, "src/a.txt", _root, [], null);

        Assert.That(view.Kind, Is.EqualTo(DeliverableViewKind.CurrentText));
        Assert.That(view.CurrentText, Is.EqualTo("current body"));
        Assert.That(view.NoticeText, Does.Contain("当前磁盘上的最新内容"));
        Assert.That(view.FileExists, Is.True);
    }

    [Test]
    public async Task MissingFileWithoutHistoryReportsUnavailableAndKeepsCardPath()
    {
        var view = await DeliverableViewResolver.ResolveAsync(
            "session-a", 1, "src/a.txt", _root, [], null);

        Assert.That(view.Kind, Is.EqualTo(DeliverableViewKind.Unavailable));
        Assert.That(view.FileExists, Is.False);
        Assert.That(view.NoticeText, Does.Contain("缺失"));
    }

    [Test]
    public async Task OversizedOrBinaryCurrentFilesReportSpecificReason()
    {
        var oversized = Path.Combine(_root, "big.txt");
        File.WriteAllBytes(oversized, new byte[DeliverableViewResolver.MaxCurrentFileBytes + 1]);
        var oversizedView = await DeliverableViewResolver.ResolveAsync(
            "session-a", 1, "big.txt", _root, [], null);
        Assert.That(oversizedView.Kind, Is.EqualTo(DeliverableViewKind.Unavailable));
        Assert.That(oversizedView.FileExists, Is.True);
        Assert.That(oversizedView.NoticeText, Does.Contain("超过"));

        var binary = Path.Combine(_root, "blob.bin");
        File.WriteAllBytes(binary, [0x00, 0x01, 0x02]);
        var binaryView = await DeliverableViewResolver.ResolveAsync(
            "session-a", 1, "blob.bin", _root, [], null);
        Assert.That(binaryView.Kind, Is.EqualTo(DeliverableViewKind.Unavailable));
        Assert.That(binaryView.FileExists, Is.True);
        Assert.That(binaryView.NoticeText, Does.Contain("二进制"));
    }

    [Test]
    public async Task RelativeDeclaredPathResolvesAgainstSessionCwd()
    {
        // 官方样例形态：交付声明是相对路径，edit 参数是绝对路径，二者应经 cwd 关联。
        var entries = new ConversationEntry[]
        {
            Edit(90, 1, Abs("Banned.Qbittorrent/Services/X.cs"), "old", "new")
        };

        var view = await DeliverableViewResolver.ResolveAsync(
            "session-a", 1, "Banned.Qbittorrent/Services/X.cs", _root, entries, null);

        Assert.That(view.Kind, Is.EqualTo(DeliverableViewKind.Fragments));
        Assert.That(view.Fragments, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task EditsFromAnotherTurnProduceNoFragments()
    {
        var entries = new ConversationEntry[]
        {
            Edit(90, 2, Abs("src/a.txt"), "old", "new")
        };

        var view = await DeliverableViewResolver.ResolveAsync(
            "session-a", 1, "src/a.txt", _root, entries, null);

        Assert.That(view.Kind, Is.EqualTo(DeliverableViewKind.Unavailable));
        Assert.That(view.Fragments, Is.Empty);
    }

    [Test]
    public void FragmentDiffFileRendersFragmentRelativeHunk()
    {
        var fragment = new DeliverableFragment(90, "edit", "old\n", "new\n", null);
        var item = new DeliverableFragmentItemViewModel(fragment.Seq, fragment.Kind,
            fragment.OldText, fragment.NewText, fragment.Note, "a.txt");

        Assert.That(item.HeaderText, Does.Contain("编辑"));
        Assert.That(item.HeaderText, Does.Contain("90"));
        var diffText = item.DiffFile.DiffList.Single();
        Assert.That(diffText, Does.Contain("+new"));
        Assert.That(diffText, Does.Contain("-old"));
        // 行号来自片段自身的行级对比，不代表原文件位置。
        Assert.That(diffText, Does.Contain("@@ -1,1 +1,1 @@"));
    }

    private string Abs(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static WorkspaceChangesSummary Summary(string path)
    {
        return new WorkspaceChangesSummary(1,
            [new WorkspaceChangedFileInfo(path, path, 1, 1, false, false)], 1, 1, 1);
    }

    private static WorkspaceFileDiff TextDiff(string path)
    {
        return new WorkspaceFileDiff(WorkspaceDiffKind.Text, path, path, true, true,
            [new WorkspaceDiffHunk(1, 1, 1, 1, ["-old", "+new"])], false);
    }

    private static ToolActivity Edit(long seq, long turn, string filePath, string oldText, string newText,
        ToolActivityStatus status = ToolActivityStatus.Succeeded, bool replaceAll = false,
        IReadOnlyList<ToolFileDiff>? metaDiffs = null)
    {
        var arguments = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["file_path"] = filePath,
            ["old_string"] = oldText,
            ["new_string"] = newText,
            ["replace_all"] = replaceAll
        });
        var resultText = status == ToolActivityStatus.Succeeded
            ? $"The file {filePath} has been updated successfully."
            : status == ToolActivityStatus.Failed
                ? $"Error: old_string was not found in \"{filePath}\""
                : null;
        return new ToolActivity(seq, $"call-{seq}", "edit", arguments, status, resultText,
            status == ToolActivityStatus.Failed ? "FS_EDIT_NOT_FOUND" : null,
            CreatedAt, CreatedAt, turn, metaDiffs);
    }

    private static ToolActivity Write(long seq, long turn, string filePath, string content, string resultText,
        ToolActivityStatus status = ToolActivityStatus.Succeeded, IReadOnlyList<ToolFileDiff>? metaDiffs = null)
    {
        var arguments = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["file_path"] = filePath,
            ["content"] = content
        });
        return new ToolActivity(seq, $"call-{seq}", "write", arguments, status, resultText,
            null, CreatedAt, CreatedAt, turn, metaDiffs);
    }

    private sealed class StubChangesService : IWorkspaceChangesService
    {
        private readonly Dictionary<long, WorkspaceChangesSummary> _summaries = [];
        private readonly Dictionary<(long Seq, int Index), WorkspaceFileDiff> _diffs = [];

        public void SetSummary(long seq, WorkspaceChangesSummary summary) => _summaries[seq] = summary;

        public void SetDiff((long Seq, int Index) key, WorkspaceFileDiff diff) => _diffs[key] = diff;

        public Task<WorkspaceChangesSummary?> GetSummaryAsync(string sessionId, long seq,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_summaries.GetValueOrDefault(seq));
        }

        public Task<WorkspaceFileDiff?> GetDiffAsync(string sessionId, long seq, int index,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_diffs.GetValueOrDefault((seq, index)));
        }
    }
}
