using DshDesktop.Core.Models;
using DshDesktop.Core.Services;
using DshDesktop.Services.Conversations;
using DshDesktop.ViewModels;
using NUnit.Framework;

namespace DshDesktop.Tests;

/// <summary>
///     右侧对比面板的行为规则：改动文件与交付文件共用面板；requestId 保证
///     快速切换时迟到的读取结果不覆盖当前显示；「用默认程序打开」使用解析后的
///     绝对路径并把错误反馈在面板内。
/// </summary>
public sealed class WorkspaceDiffPanelViewModelTests
{
    private static readonly DateTimeOffset CreatedAt = DateTimeOffset.UnixEpoch;

    [Test]
    public async Task LateWorkspaceDiffDoesNotOverrideNewerSelection()
    {
        var service = new StubChangesService();
        var panel = new WorkspaceDiffPanelViewModel(service, null, action => action());
        var gate = service.GateSummary(100);
        var fileA = ChangedFile("a.txt", 100, 0);
        var taskA = panel.OpenAsync(fileA);

        // A 仍在读取中时选择 B（服务立即返回）；释放 A 后不得覆盖 B 的显示。
        service.SetSummary(200, Summary("b.txt", 1));
        service.SetDiff(200, new WorkspaceFileDiff(WorkspaceDiffKind.Text, "b.txt", "b.txt", true, true,
            [new WorkspaceDiffHunk(1, 1, 1, 1, ["+b-body"])], false));
        var fileB = ChangedFile("b.txt", 200, 0);
        await panel.OpenAsync(fileB);
        Assert.That(panel.TitleText, Is.EqualTo("b.txt"));
        Assert.That(panel.HasDiff, Is.True);

        gate.TrySetResult(Summary("a.txt", 100));
        await taskA;
        Assert.That(panel.TitleText, Is.EqualTo("b.txt"));
        Assert.That(panel.DiffFile, Is.Not.Null);
        Assert.That(panel.DiffFile!.DiffList.Single(), Does.Contain("b-body"));
    }

    [Test]
    public async Task LateDeliverableResolutionDoesNotOverrideNewerRequest()
    {
        var service = new StubChangesService();
        var opener = new RecordingFileOpener();
        var panel = new WorkspaceDiffPanelViewModel(service, opener, action => action());
        var root = Path.Combine(Path.GetTempPath(), $"dsh-panel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "b.txt"), "b current");

            // 请求 A：同轮改动摘要被门住，解析长期挂起。
            var gate = service.GateSummary(100);
            var requestA = new DeliverableViewRequest("session-a", 1, "a.txt", root,
                [new WorkspaceChangesAnnouncement(100, 1, CreatedAt)]);
            var taskA = panel.OpenDeliverableAsync(requestA);

            // 请求 B：无摘要、无片段、文件可读 → 立即落到当前内容视图。
            var requestB = new DeliverableViewRequest("session-a", 1, "b.txt", root, []);
            await panel.OpenDeliverableAsync(requestB);
            Assert.That(panel.SourceText, Is.EqualTo("当前文件内容"));
            Assert.That(panel.ContentText, Is.EqualTo("b current"));

            gate.TrySetResult(Summary("a.txt", 100));
            await taskA;
            Assert.That(panel.SourceText, Is.EqualTo("当前文件内容"));
            Assert.That(panel.TitleText, Is.EqualTo("b.txt"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task DeliverableFragmentsReachPanelWithNotices()
    {
        var panel = new WorkspaceDiffPanelViewModel(null, null, action => action());
        var root = TempRoot();
        var edit = new ToolActivity(90, "call-90", "edit",
            "{\"file_path\":\"" + Path.Combine(root, "a.txt").Replace("\\", "\\\\") + "\"," +
            "\"old_string\":\"old\",\"new_string\":\"new\"}",
            ToolActivityStatus.Succeeded, "The file a.txt has been updated successfully.",
            null, CreatedAt, CreatedAt, 1);
        var request = new DeliverableViewRequest("session-a", 1, "a.txt", root, [edit]);

        await panel.OpenDeliverableAsync(request);

        Assert.That(panel.HasFragments, Is.True);
        Assert.That(panel.Fragments, Has.Count.EqualTo(1));
        Assert.That(panel.SourceText, Does.Contain("历史编辑片段"));
        Assert.That(panel.NoticeText, Does.Contain("片段内行号"));
        Assert.That(panel.CanOpenExternally, Is.False);
    }

    [Test]
    public async Task DefaultAppOpenUsesResolvedPathAndReportsErrorsInPanel()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dsh-panel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var opener = new RecordingFileOpener();
            var panel = new WorkspaceDiffPanelViewModel(null, opener, action => action());
            File.WriteAllText(Path.Combine(root, "a.txt"), "body");

            await panel.OpenDeliverableAsync(
                new DeliverableViewRequest("session-a", 1, "a.txt", root, []));
            Assert.That(panel.CanOpenExternally, Is.True);

            panel.OpenInDefaultAppCommand.Execute(null);
            Assert.That(opener.OpenedPath, Is.EqualTo(Path.Combine(root, "a.txt")));
            Assert.That(panel.HasError, Is.False);

            // 文件随后被删除：按钮隐藏，兜底命令给出面板内错误。
            File.Delete(Path.Combine(root, "a.txt"));
            panel.OpenInDefaultAppCommand.Execute(null);
            Assert.That(panel.HasError, Is.True);
            Assert.That(panel.ErrorText, Does.Contain("无法在默认程序中打开"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string TempRoot() => Path.Combine(Path.GetTempPath(), $"dsh-panel-args-{Guid.NewGuid():N}");

    private static WorkspaceChangedFileViewModel ChangedFile(string display, long seq, int index)
    {
        return new WorkspaceChangedFileViewModel(
            new WorkspaceChangedFileInfo(display, display, 1, 1, false, false), index)
        {
            AnnouncementSeq = seq,
            SessionId = "session-a"
        };
    }

    private static WorkspaceChangesSummary Summary(string path, long turn)
    {
        return new WorkspaceChangesSummary(turn,
            [new WorkspaceChangedFileInfo(path, path, 1, 1, false, false)], 1, 1, 1);
    }

    private sealed class RecordingFileOpener : IWorkspaceFileOpener
    {
        public string? OpenedPath { get; private set; }

        public void Open(string absolutePath) => OpenedPath = absolutePath;
    }

    private sealed class StubChangesService : IWorkspaceChangesService
    {
        private readonly Dictionary<long, TaskCompletionSource<WorkspaceChangesSummary?>> _gates = [];
        private readonly Dictionary<long, WorkspaceChangesSummary> _summaries = [];
        private readonly Dictionary<long, WorkspaceFileDiff> _diffs = [];

        /// <summary>让指定 seq 的摘要读取挂起，直到测试显式放行（迟到结果竞态用）。</summary>
        public TaskCompletionSource<WorkspaceChangesSummary?> GateSummary(long seq)
        {
            var gate = new TaskCompletionSource<WorkspaceChangesSummary?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _gates[seq] = gate;
            return gate;
        }

        public void SetSummary(long seq, WorkspaceChangesSummary summary) => _summaries[seq] = summary;

        public void SetDiff(long seq, WorkspaceFileDiff diff) => _diffs[seq] = diff;

        public Task<WorkspaceChangesSummary?> GetSummaryAsync(string sessionId, long seq,
            CancellationToken cancellationToken = default)
        {
            if (_gates.TryGetValue(seq, out var gate)) return gate.Task;
            return Task.FromResult(_summaries.GetValueOrDefault(seq));
        }

        public Task<WorkspaceFileDiff?> GetDiffAsync(string sessionId, long seq, int index,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_diffs.GetValueOrDefault(seq));
        }
    }
}
