using DshDesktop.Core.Models;
using DshDesktop.Core.Services;
using DshDesktop.ViewModels;
using Xunit;

namespace DshDesktop.Tests;

/// <summary>
///     侧栏搜索过滤与分组方式弹窗的针对性验证：搜索词只影响呈现投影、
///     关闭搜索清词恢复全量、过滤时空组整体隐藏、弹窗选项切换并收起。
/// </summary>
public sealed class SidebarViewModelTests
{
    [Fact]
    public async Task SearchText_FiltersTitlesCaseInsensitive()
    {
        var sidebar = CreateSidebar(out var sessionService);
        sessionService.Sessions =
        [
            Summary("s1", "Alpha 会话"),
            Summary("s2", "Beta 会话"),
            Summary("s3", "Gamma 会话")
        ];
        await sidebar.RefreshSessionsAsync(CancellationToken.None);
        sidebar.SessionListModeIndex = 0;

        sidebar.SessionSearchText = "BETA";

        var row = Assert.IsType<SessionItemViewModel>(Assert.Single(sidebar.SessionRows));
        Assert.Equal("s2", row.Id);
    }

    [Fact]
    public async Task CloseSearch_ClearsTextAndRestoresAllRows()
    {
        var sidebar = CreateSidebar(out var sessionService);
        sessionService.Sessions = [Summary("s1", "Alpha 会话"), Summary("s2", "Beta 会话")];
        await sidebar.RefreshSessionsAsync(CancellationToken.None);
        sidebar.SessionListModeIndex = 0;
        sidebar.OpenSearchCommand.Execute(null);
        sidebar.SessionSearchText = "Alpha";
        Assert.Single(sidebar.SessionRows);

        sidebar.CloseSearchCommand.Execute(null);

        Assert.False(sidebar.IsSearchOpen);
        Assert.Equal(string.Empty, sidebar.SessionSearchText);
        Assert.Equal(2, sidebar.SessionRows.Count);
    }

    [Fact]
    public async Task SearchText_InGroupedModeHidesEmptyGroups()
    {
        var sidebar = CreateSidebar(out var sessionService, out var workspaceService);
        sessionService.Sessions = [Summary("s1", "前端修复"), Summary("s2", "后端排查")];
        workspaceService.Workspaces =
        [
            new WorkspaceSummary("ws1", "工作区一", "/tmp/one", ["s1"], DateTimeOffset.Now),
            new WorkspaceSummary("ws2", "工作区二", "/tmp/two", ["s2"], DateTimeOffset.Now)
        ];
        await sidebar.RefreshSessionsAsync(CancellationToken.None);
        await sidebar.RefreshWorkspacesAsync(CancellationToken.None);

        sidebar.SessionSearchText = "前端";

        // 只剩工作区一的组头与其成员；工作区二与「未分组」不残留空组头。
        Assert.Equal(2, sidebar.SessionRows.Count);
        var header = Assert.IsType<SessionGroupHeaderViewModel>(sidebar.SessionRows[0]);
        Assert.Equal("ws1", header.Key);
        Assert.IsType<SessionItemViewModel>(sidebar.SessionRows[1]);
    }

    [Fact]
    public async Task GroupMenuCommands_SwitchModeAndCloseMenu()
    {
        var sidebar = CreateSidebar(out _);
        sidebar.IsGroupMenuOpen = true;

        sidebar.SetGroupFlatCommand.Execute(null);

        Assert.Equal(0, sidebar.SessionListModeIndex);
        Assert.True(sidebar.IsGroupFlat);
        Assert.False(sidebar.IsGroupByWorkspace);
        Assert.False(sidebar.IsGroupMenuOpen);

        sidebar.IsGroupMenuOpen = true;
        sidebar.SetGroupByWorkspaceCommand.Execute(null);

        Assert.Equal(1, sidebar.SessionListModeIndex);
        Assert.True(sidebar.IsGroupByWorkspace);
        Assert.False(sidebar.IsGroupMenuOpen);
    }

    private static async Task<SessionGroupHeaderViewModel> CreateGroupedHeaderAsync(
        SidebarViewModel sidebar, FakeSessionService sessionService, FakeWorkspaceService workspaceService,
        string key = "ws1", string title = "工作区一", string sessionId = "s1")
    {
        sessionService.Sessions = [Summary(sessionId, $"{title}的会话")];
        workspaceService.Workspaces =
        [
            new WorkspaceSummary(key, title, "/tmp/one", [sessionId], DateTimeOffset.Now),
            new WorkspaceSummary("ws2", "工作区二", "/tmp/two", [], DateTimeOffset.Now)
        ];
        await sidebar.RefreshSessionsAsync(CancellationToken.None);
        await sidebar.RefreshWorkspacesAsync(CancellationToken.None);
        return sidebar.SessionRows.OfType<SessionGroupHeaderViewModel>().Single(header => header.Key == key);
    }

    [Fact]
    public async Task OpenWorkspaceRename_PrefillsDraftAndBlocksUnchangedConfirm()
    {
        var sidebar = CreateSidebar(out var sessionService, out var workspaceService);
        var header  = await CreateGroupedHeaderAsync(sidebar, sessionService, workspaceService);

        sidebar.OpenWorkspaceRenameCommand.Execute(header);

        Assert.True(sidebar.IsRenameOpen);
        Assert.Equal("工作区一", sidebar.RenameDraftText);
        // 未变更时确认不可用：与 DSH 重命名弹窗同一语义。
        Assert.False(sidebar.CanConfirmRename);
    }

    [Fact]
    public async Task ConfirmWorkspaceRename_GuardsBlankAndConflict()
    {
        Task RenameCallback(string workspaceId, string title)
        {
            throw new NotSupportedException("不应触发回调");
        }

        var sidebar = CreateSidebar(out var sessionService, out var workspaceService, RenameCallback, null);
        var header  = await CreateGroupedHeaderAsync(sidebar, sessionService, workspaceService);
        sidebar.OpenWorkspaceRenameCommand.Execute(header);

        // 空白名与重名（忽略大小写、含同名）都被本地校验拦下，回调不触发。
        sidebar.RenameDraftText = "   ";
        Assert.False(sidebar.CanConfirmRename);
        sidebar.RenameDraftText = "工作区二";
        Assert.False(sidebar.CanConfirmRename);
        Assert.Equal("已存在名为“工作区二”的工作区。", sidebar.RenameErrorText);

        sidebar.RenameDraftText = "新名字";
        Assert.True(sidebar.CanConfirmRename);
    }

    [Fact]
    public async Task ConfirmWorkspaceRename_InvokesCallbackAndCloses()
    {
        string? renamedWorkspaceId = null;
        string? renamedTitle       = null;
        var sidebar = CreateSidebar(out var sessionService, out var workspaceService,
                                    (workspaceId, title) =>
                                    {
                                        renamedWorkspaceId = workspaceId;
                                        renamedTitle       = title;
                                        return Task.CompletedTask;
                                    }, null);
        var header = await CreateGroupedHeaderAsync(sidebar, sessionService, workspaceService);
        sidebar.OpenWorkspaceRenameCommand.Execute(header);
        sidebar.RenameDraftText = "  改名后  ";

        sidebar.ConfirmWorkspaceRenameCommand.Execute(null);
        await WaitUntilAsync(() => !sidebar.IsRenameOpen);

        // 回调收到 trim 后的目标名；成功后弹窗关闭且无错误残留。
        Assert.Equal("ws1", renamedWorkspaceId);
        Assert.Equal("改名后", renamedTitle);
        Assert.False(sidebar.HasRenameError);
    }

    [Fact]
    public async Task ConfirmWorkspaceRename_ServiceErrorStaysOpenWithMessage()
    {
        var sidebar = CreateSidebar(out var sessionService, out var workspaceService,
                                    (_, _) => throw new InvalidOperationException("重名冲突"), null);
        var header = await CreateGroupedHeaderAsync(sidebar, sessionService, workspaceService);
        sidebar.OpenWorkspaceRenameCommand.Execute(header);
        sidebar.RenameDraftText = "另一个名字";

        sidebar.ConfirmWorkspaceRenameCommand.Execute(null);
        await WaitUntilAsync(() => sidebar.HasRenameError);

        // 服务端错误留在弹窗内呈现，弹窗保持打开供修正重试。
        Assert.True(sidebar.IsRenameOpen);
        Assert.Equal("重名冲突", sidebar.RenameErrorText);

        sidebar.CancelWorkspaceRenameCommand.Execute(null);
        Assert.False(sidebar.IsRenameOpen);
    }

    [Fact]
    public async Task OpenWorkspaceDelete_ShowsDescriptionAndConfirmInvokesCallback()
    {
        string? deletedWorkspaceId = null;
        var sidebar = CreateSidebar(out var sessionService, out var workspaceService, null,
                                    workspaceId =>
                                    {
                                        deletedWorkspaceId = workspaceId;
                                        return Task.CompletedTask;
                                    });
        var header = await CreateGroupedHeaderAsync(sidebar, sessionService, workspaceService);

        sidebar.OpenWorkspaceDeleteCommand.Execute(header);

        Assert.True(sidebar.IsDeleteConfirmOpen);
        // 确认描述说明工作区名与会话去向（对齐 DSH delete.desc 语义）。
        Assert.Contains("工作区一", sidebar.DeleteConfirmText);
        Assert.Contains("未分组", sidebar.DeleteConfirmText);

        sidebar.ConfirmWorkspaceDeleteCommand.Execute(null);
        await WaitUntilAsync(() => !sidebar.IsDeleteConfirmOpen);

        Assert.Equal("ws1", deletedWorkspaceId);
        Assert.False(sidebar.HasDeleteError);
    }

    [Fact]
    public async Task OpenWorkspaceDelete_RejectsUngroupedAndServiceErrorStaysOpen()
    {
        var sidebar = CreateSidebar(out var sessionService, out var workspaceService, null,
                                    _ => throw new InvalidOperationException("删除失败"));
        // 一条记账会话 + 一条未记账会话：未分组组头仅在非空时显示（投影规则）。
        sessionService.Sessions = [Summary("s1", "会话一"), Summary("s2", "未记账会话")];
        workspaceService.Workspaces =
        [
            new WorkspaceSummary("ws1", "工作区一", "/tmp/one", ["s1"], DateTimeOffset.Now)
        ];
        await sidebar.RefreshSessionsAsync(CancellationToken.None);
        await sidebar.RefreshWorkspacesAsync(CancellationToken.None);

        var ungrouped = sidebar.SessionRows.OfType<SessionGroupHeaderViewModel>()
                                .Single(header => header.Key == "$ungrouped");
        // 未分组不是工作区：CanExecute 拒绝，Execute 也直接返回（双保险一致）。
        Assert.False(sidebar.OpenWorkspaceDeleteCommand.CanExecute(ungrouped));
        sidebar.OpenWorkspaceDeleteCommand.Execute(ungrouped);
        Assert.False(sidebar.IsDeleteConfirmOpen);

        // 工作区行的删除在服务失败时保持弹窗打开并呈现错误。
        var header = sidebar.SessionRows.OfType<SessionGroupHeaderViewModel>()
                             .Single(item => item.Key == "ws1");
        sidebar.OpenWorkspaceDeleteCommand.Execute(header);
        Assert.True(sidebar.IsDeleteConfirmOpen);
        sidebar.ConfirmWorkspaceDeleteCommand.Execute(null);
        await WaitUntilAsync(() => sidebar.HasDeleteError);
        Assert.True(sidebar.IsDeleteConfirmOpen);
        Assert.Equal("删除失败", sidebar.DeleteErrorText);
    }

    private static SidebarViewModel CreateSidebar(out FakeSessionService sessionService)
    {
        return CreateSidebar(out sessionService, out _);
    }

    private static SidebarViewModel CreateSidebar(
        out FakeSessionService sessionService, out FakeWorkspaceService workspaceService)
    {
        return CreateSidebar(out sessionService, out workspaceService, null, null);
    }

    private static SidebarViewModel CreateSidebar(
        out FakeSessionService      sessionService,
        out FakeWorkspaceService    workspaceService,
        Func<string, string, Task>? renameWorkspace,
        Func<string, Task>?         deleteWorkspace)
    {
        sessionService   = new FakeSessionService();
        workspaceService = new FakeWorkspaceService();
        return new SidebarViewModel(sessionService, workspaceService,
                                    _ => { }, _ => Task.CompletedTask, _ => { },
                                    null, renameWorkspace, deleteWorkspace);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition());
    }

    private static SessionSummary Summary(string id, string title)
    {
        return new SessionSummary(id, title, DateTimeOffset.FromUnixTimeMilliseconds(1_000), false,
                                  SessionBlankState.Engaged);
    }

    /// <summary>最小会话服务桩：侧栏只消费目录读取与变更事件，其余成员不参与。</summary>
    private sealed class FakeSessionService : ISessionService
    {
        public IReadOnlyList<SessionSummary> Sessions { get; set; } = [];

        /// <summary>桩不触发变更事件；显式空访问器避免 CS0067。</summary>
        public event EventHandler? SessionsChanged
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<SessionSummary>> GetSessionsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Sessions);
        }

        public Task<SessionSummary> CreateSessionAsync(
            string?           workspaceId       = null, string? sessionId = null, string? agentPreset = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public void MarkSessionEngaged(string sessionId)
        {
            throw new NotSupportedException();
        }

        public Task<ModelCatalog> GetModelCatalogAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ModelSelection> SelectModelAsync(
            string  sessionId,              string            provider, string model,
            string? reasoningEffort = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(
            string sessionId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<SessionHistoryPage> LoadOlderAsync(
            string sessionId, long throughSeq, long beforeSeq, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task SendPromptAsync(
            string sessionId, string requestId, string content, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task CancelAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public IAsyncEnumerable<SessionUpdate> FollowSessionAsync(
            string sessionId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>最小工作区服务桩：静态集合，不触发变更事件。</summary>
    private sealed class FakeWorkspaceService : IWorkspaceService
    {
        public IReadOnlyList<WorkspaceSummary> Workspaces { get; set; } = [];

        public IReadOnlySet<string> ArchivedSessionIds { get; } = new HashSet<string>();

        /// <summary>桩不触发变更事件；显式空访问器避免 CS0067。</summary>
        public event EventHandler? WorkspacesChanged
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<WorkspaceSummary>> GetWorkspacesAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Workspaces);
        }

        public Task<WorkspaceSummary> RegisterWorkspaceAsync(string path, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<WorkspaceSummary> RenameWorkspaceAsync(string            workspaceId,
                                                           string            title,
                                                           CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task DeleteWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
