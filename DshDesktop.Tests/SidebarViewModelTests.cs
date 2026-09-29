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

    private static SidebarViewModel CreateSidebar(out FakeSessionService sessionService)
    {
        return CreateSidebar(out sessionService, out _);
    }

    private static SidebarViewModel CreateSidebar(
        out FakeSessionService sessionService, out FakeWorkspaceService workspaceService)
    {
        sessionService   = new FakeSessionService();
        workspaceService = new FakeWorkspaceService();
        return new SidebarViewModel(sessionService, workspaceService,
                                    _ => { }, _ => Task.CompletedTask, _ => { });
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
            string? workspaceId = null, string? sessionId = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public void MarkSessionEngaged(string sessionId) => throw new NotSupportedException();

        public Task<ModelCatalog> GetModelCatalogAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ModelSelection> SelectModelAsync(
            string  sessionId,              string            provider, string model,
            string? reasoningEffort = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(
            string sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SessionHistoryPage> LoadOlderAsync(
            string sessionId, long throughSeq, long beforeSeq, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SendPromptAsync(
            string sessionId, string requestId, string content, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CancelAsync(string sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<SessionUpdate> FollowSessionAsync(
            string sessionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
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
    }
}
