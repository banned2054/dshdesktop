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
    public async Task UnchangedRefresh_DoesNotReplaceRowsOrNotifyCollection()
    {
        var sidebar = CreateSidebar(out var sessionService, out var workspaceService);
        await CreateGroupedHeaderAsync(sidebar, sessionService, workspaceService);
        var rows          = sidebar.SessionRows.ToArray();
        var notifications = 0;
        sidebar.SessionRows.CollectionChanged += (_, _) => notifications++;

        await sidebar.RefreshSessionsAsync(CancellationToken.None);
        await sidebar.RefreshWorkspacesAsync(CancellationToken.None);

        Assert.Equal(0, notifications);
        Assert.Equal(rows, sidebar.SessionRows);
    }

    [Fact]
    public async Task SelectionChange_UpdatesGroupInPlaceWithoutRebuildingControls()
    {
        var sidebar       = CreateSidebar(out var sessionService, out var workspaceService);
        var header        = await CreateGroupedHeaderAsync(sidebar, sessionService, workspaceService);
        var session       = Assert.Single(sidebar.Sessions);
        var notifications = 0;
        sidebar.SessionRows.CollectionChanged += (_, _) => notifications++;

        sidebar.ApplySelectedSession(session);

        Assert.True(header.IsCurrent);
        Assert.Equal(0, notifications);
        // 行首为「工作区」分类头，工作区组头跟随其后。
        Assert.Same(header, sidebar.SessionRows[1]);
    }

    [Fact]
    public async Task WorkspaceRenameAndCollapse_NotifyExistingHeader()
    {
        var sidebar    = CreateSidebar(out var sessionService, out var workspaceService);
        var header     = await CreateGroupedHeaderAsync(sidebar, sessionService, workspaceService);
        var properties = new List<string?>();
        header.PropertyChanged += (_, e) => properties.Add(e.PropertyName);
        workspaceService.Workspaces = workspaceService.Workspaces
                                                      .Select(workspace =>
                                                                  workspace.Id == header.Key
                                                                      ? workspace with { Title = "改名后的工作区" }
                                                                      : workspace)
                                                      .ToArray();

        await sidebar.RefreshWorkspacesAsync(CancellationToken.None);
        sidebar.ToggleGroupCommand.Execute(header);

        // 行首为「工作区」分类头，折叠后的工作区组头保持原相对位置。
        Assert.Same(header, sidebar.SessionRows[1]);
        Assert.Equal("改名后的工作区", header.TitleText);
        Assert.False(header.IsExpanded);
        Assert.Contains(nameof(header.TitleText), properties);
        Assert.Contains(nameof(header.IsExpanded), properties);
        Assert.DoesNotContain(sidebar.SessionRows, row => row is SessionItemViewModel);
    }

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

        // 「工作区」分类头 + 工作区一组头与其成员；工作区二与「未分组」不残留空组头，
        // 分类下仍有可见组，分类头保留。
        Assert.Equal(3, sidebar.SessionRows.Count);
        var category = Assert.IsType<SessionGroupHeaderViewModel>(sidebar.SessionRows[0]);
        Assert.Equal("$workspaces", category.Key);
        Assert.True(category.IsCategory);
        var header = Assert.IsType<SessionGroupHeaderViewModel>(sidebar.SessionRows[1]);
        Assert.Equal("ws1", header.Key);
        Assert.IsType<SessionItemViewModel>(sidebar.SessionRows[2]);
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
        SidebarViewModel sidebar,     FakeSessionService sessionService, FakeWorkspaceService workspaceService,
        string           key = "ws1", string             title = "工作区一", string               sessionId = "s1")
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

    [Fact]
    public async Task ToggleSessionPin_MarksRowAndFloatsPinnedToTopInFlatList()
    {
        var sidebar = CreateSidebar(out var sessionService, out _, out var pinService);
        sessionService.Sessions = [Summary("s1", "会话一"), Summary("s2", "会话二"), Summary("s3", "会话三")];
        await sidebar.RefreshSessionsAsync(CancellationToken.None);
        sidebar.SessionListModeIndex = 0;
        var row2 = sidebar.SessionRows.OfType<SessionItemViewModel>().Single(row => row.Id == "s2");

        sidebar.ToggleSessionPinCommand.Execute(row2);

        // 置顶写入本地注册表：行标记与文案切换，s2 提取到列表最前，其余保持目录顺序。
        Assert.Equal(["s2"], pinService.PinnedSessionIds);
        Assert.True(row2.Pinned);
        Assert.Equal("取消置顶", row2.PinActionText);
        Assert.Equal(["s2", "s1", "s3"],
                     sidebar.SessionRows.OfType<SessionItemViewModel>().Select(row => row.Id).ToArray());

        sidebar.ToggleSessionPinCommand.Execute(row2);

        Assert.Empty(pinService.PinnedSessionIds);
        Assert.False(row2.Pinned);
        Assert.Equal("置顶会话", row2.PinActionText);
        Assert.Equal(["s1", "s2", "s3"],
                     sidebar.SessionRows.OfType<SessionItemViewModel>().Select(row => row.Id).ToArray());
    }

    [Fact]
    public async Task ToggleSessionPin_ExtractsPinnedSessionIntoPinnedSection()
    {
        var sidebar = CreateSidebar(out var sessionService, out var workspaceService);
        sessionService.Sessions = [Summary("s1", "会话一"), Summary("s2", "会话二")];
        workspaceService.Workspaces =
        [
            new WorkspaceSummary("ws1", "工作区一", "/tmp/one", ["s1", "s2"], DateTimeOffset.Now)
        ];
        await sidebar.RefreshSessionsAsync(CancellationToken.None);
        await sidebar.RefreshWorkspacesAsync(CancellationToken.None);
        var row2 = sidebar.SessionRows.OfType<SessionItemViewModel>().Single(row => row.Id == "s2");

        sidebar.ToggleSessionPinCommand.Execute(row2);

        // 置顶分类出现在「工作区」分类之前：置顶头（非工作区行）+ 提取出的 s2；
        // 原工作区组不再包含 s2（提取语义，不重复出现）。
        var headers = sidebar.SessionRows.OfType<SessionGroupHeaderViewModel>().ToArray();
        Assert.Equal(["$pinned", "$workspaces", "ws1"], headers.Select(header => header.Key).ToArray());
        Assert.True(headers[0].IsCategory);
        Assert.False(headers[0].IsWorkspace);
        Assert.Equal(["s2", "s1"],
                     sidebar.SessionRows.OfType<SessionItemViewModel>().Select(row => row.Id).ToArray());

        sidebar.ToggleSessionPinCommand.Execute(row2);

        // 取消置顶：置顶分类头消失，s2 回到工作区组。
        Assert.Equal(["$workspaces", "ws1"],
                     sidebar.SessionRows.OfType<SessionGroupHeaderViewModel>()
                            .Select(header => header.Key).ToArray());
        Assert.Equal(["s1", "s2"],
                     sidebar.SessionRows.OfType<SessionItemViewModel>().Select(row => row.Id).ToArray());
    }

    [Fact]
    public async Task PinnedSessions_SortByUpdatedTimeDescendingRegardlessOfPinOrder()
    {
        var sidebar = CreateSidebar(out var sessionService, out _, out var pinService);
        var oldTime = DateTimeOffset.FromUnixTimeMilliseconds(1_000);
        sessionService.Sessions =
        [
            new SessionSummary("s1", "会话一", oldTime, false, SessionBlankState.Engaged),
            new SessionSummary("s2", "会话二", oldTime.AddMinutes(5), false, SessionBlankState.Engaged)
        ];
        await sidebar.RefreshSessionsAsync(CancellationToken.None);
        sidebar.SessionListModeIndex = 0;

        // 先置顶新的 s2、再置顶旧的 s1：注册表按最近置顶在前存储；
        // 若按置顶先后排序应得 [s1, s2]，按更新时间降序应为 [s2, s1]。
        sidebar.ToggleSessionPinCommand.Execute(sidebar.SessionRows.OfType<SessionItemViewModel>()
                                                       .Single(row => row.Id == "s2"));
        sidebar.ToggleSessionPinCommand.Execute(sidebar.SessionRows.OfType<SessionItemViewModel>()
                                                       .Single(row => row.Id == "s1"));

        // 置顶会话区按更新时间降序（新的在前），不按置顶先后排序。
        Assert.Equal(["s1", "s2"], pinService.PinnedSessionIds);
        Assert.Equal(["s2", "s1"],
                     sidebar.SessionRows.OfType<SessionItemViewModel>().Select(row => row.Id).ToArray());
    }

    [Fact]
    public async Task ToggleWorkspacePin_MovesWorkspaceIntoPinnedSection()
    {
        var sidebar = CreateSidebar(out var sessionService, out var workspaceService, out var pinService);
        sessionService.Sessions = [Summary("s1", "会话一"), Summary("s2", "会话二")];
        workspaceService.Workspaces =
        [
            new WorkspaceSummary("ws1", "工作区一", "/tmp/one", ["s1"], DateTimeOffset.Now),
            new WorkspaceSummary("ws2", "工作区二", "/tmp/two", ["s2"], DateTimeOffset.Now.AddMinutes(-1))
        ];
        await sidebar.RefreshSessionsAsync(CancellationToken.None);
        await sidebar.RefreshWorkspacesAsync(CancellationToken.None);

        var header1 = sidebar.SessionRows.OfType<SessionGroupHeaderViewModel>().Single(item => item.Key == "ws1");
        sidebar.ToggleWorkspacePinCommand.Execute(header1);

        // 置顶工作区提取到置顶分类：分类头在工作区分类之前，组内成员保留；
        // 原工作区分类不再出现该组。置顶头本身不是工作区行（无悬浮操作）。
        Assert.Equal(["ws1"], pinService.PinnedWorkspaceIds);
        var headers = sidebar.SessionRows.OfType<SessionGroupHeaderViewModel>().ToArray();
        Assert.Equal(["$pinned", "ws1", "$workspaces", "ws2"], headers.Select(header => header.Key).ToArray());
        Assert.True(headers[0].IsCategory);
        Assert.True(headers[1].Pinned);
        // 工作区行不随置顶换成图钉：IsCategory 专属分类头，保持文件夹图标。
        Assert.False(headers[1].IsCategory);
        Assert.True(headers[2].IsCategory);
        Assert.Equal("取消置顶", headers[1].PinActionText);
        Assert.Equal(["s1", "s2"],
                     sidebar.SessionRows.OfType<SessionItemViewModel>().Select(row => row.Id).ToArray());

        sidebar.ToggleWorkspacePinCommand.Execute(headers[1]);

        // 取消置顶：回到「工作区」分类原位置（组序为后端顺序）。
        Assert.Empty(pinService.PinnedWorkspaceIds);
        headers = sidebar.SessionRows.OfType<SessionGroupHeaderViewModel>().ToArray();
        Assert.Equal(["$workspaces", "ws1", "ws2"], headers.Select(header => header.Key).ToArray());
    }

    [Fact]
    public async Task UnpinnedWorkspaces_CollapseIntoWorkspacesCategory()
    {
        var sidebar = CreateSidebar(out var sessionService, out var workspaceService);
        sessionService.Sessions =
        [
            Summary("s1", "会话一"), Summary("s2", "会话二"), Summary("s3", "游离会话")
        ];
        workspaceService.Workspaces =
        [
            new WorkspaceSummary("ws1", "工作区一", "/tmp/one", ["s1"], DateTimeOffset.Now),
            new WorkspaceSummary("ws2", "工作区二", "/tmp/two", ["s2"], DateTimeOffset.Now)
        ];
        await sidebar.RefreshSessionsAsync(CancellationToken.None);
        await sidebar.RefreshWorkspacesAsync(CancellationToken.None);

        // 未置顶工作区统一收进「工作区」分类：分类头为纯文字分类行（非工作区行），
        // 组序为后端顺序；「未分组」不并入工作区分类、同为分类行。
        var headers = sidebar.SessionRows.OfType<SessionGroupHeaderViewModel>().ToArray();
        Assert.Equal(["$workspaces", "ws1", "ws2", "$ungrouped"], headers.Select(header => header.Key).ToArray());
        Assert.True(headers[0].IsCategory);
        Assert.False(headers[0].IsWorkspace);
        Assert.True(headers[3].IsCategory);
        Assert.Collection(sidebar.SessionRows,
                          row => Assert.Equal("$workspaces", Assert.IsType<SessionGroupHeaderViewModel>(row).Key),
                          row => Assert.Equal("ws1", Assert.IsType<SessionGroupHeaderViewModel>(row).Key),
                          row => Assert.Equal("s1", Assert.IsType<SessionItemViewModel>(row).Id),
                          row => Assert.Equal("ws2", Assert.IsType<SessionGroupHeaderViewModel>(row).Key),
                          row => Assert.Equal("s2", Assert.IsType<SessionItemViewModel>(row).Id),
                          row => Assert.Equal("$ungrouped", Assert.IsType<SessionGroupHeaderViewModel>(row).Key),
                          row => Assert.Equal("s3", Assert.IsType<SessionItemViewModel>(row).Id));

        // 折叠分类：组与成员一起隐藏，分类头与「未分组」不受影响；重新展开恢复。
        sidebar.ToggleGroupCommand.Execute(headers[0]);

        Assert.Equal(["$workspaces", "$ungrouped"],
                     sidebar.SessionRows.OfType<SessionGroupHeaderViewModel>().Select(header => header.Key).ToArray());
        Assert.Equal(["s3"], sidebar.SessionRows.OfType<SessionItemViewModel>().Select(row => row.Id).ToArray());

        sidebar.ToggleGroupCommand.Execute(headers[0]);

        Assert.Contains("ws1", sidebar.SessionRows.OfType<SessionGroupHeaderViewModel>().Select(header => header.Key));

        // 搜索过滤后无可见工作区组：分类头随空组一并隐藏。
        sidebar.SessionSearchText = "游离";

        Assert.Equal(["$ungrouped"],
                     sidebar.SessionRows.OfType<SessionGroupHeaderViewModel>().Select(header => header.Key).ToArray());
    }

    [Fact]
    public async Task PinnedSessionOfPinnedWorkspace_SitsParallelBelowWorkspaceGroup()
    {
        var sidebar = CreateSidebar(out var sessionService, out var workspaceService);
        sessionService.Sessions = [Summary("s1", "会话一"), Summary("s2", "会话二"), Summary("s3", "会话三")];
        workspaceService.Workspaces =
        [
            new WorkspaceSummary("ws1", "工作区一", "/tmp/one", ["s1", "s2"], DateTimeOffset.Now),
            new WorkspaceSummary("ws2", "工作区二", "/tmp/two", ["s3"], DateTimeOffset.Now)
        ];
        await sidebar.RefreshSessionsAsync(CancellationToken.None);
        await sidebar.RefreshWorkspacesAsync(CancellationToken.None);

        // 同时置顶工作区 ws1 与其成员 s2：分类内 s2 与 ws1 并列（不嵌套），
        // ws1 组内只余未置顶的 s1；ws2 收进「工作区」分类。
        sidebar.ToggleWorkspacePinCommand.Execute(sidebar.SessionRows.OfType<SessionGroupHeaderViewModel>()
                                                         .Single(item => item.Key == "ws1"));
        sidebar.ToggleSessionPinCommand.Execute(sidebar.SessionRows.OfType<SessionItemViewModel>()
                                                       .Single(row => row.Id == "s2"));

        var headers = sidebar.SessionRows.OfType<SessionGroupHeaderViewModel>().ToArray();
        Assert.Equal(["$pinned", "ws1", "$workspaces", "ws2"], headers.Select(header => header.Key).ToArray());
        // 分类内结构：置顶头 → ws1 组（组内只余未置顶的 s1）→ 并列会话区的 s2 →
        // 工作区分类头 → ws2 组。s2 不嵌套在 ws1 组内（与工作区并列）。
        Assert.Collection(sidebar.SessionRows,
                          row => Assert.Equal("$pinned", Assert.IsType<SessionGroupHeaderViewModel>(row).Key),
                          row => Assert.Equal("ws1", Assert.IsType<SessionGroupHeaderViewModel>(row).Key),
                          row => Assert.Equal("s1", Assert.IsType<SessionItemViewModel>(row).Id),
                          row => Assert.Equal("s2", Assert.IsType<SessionItemViewModel>(row).Id),
                          row => Assert.Equal("$workspaces", Assert.IsType<SessionGroupHeaderViewModel>(row).Key),
                          row => Assert.Equal("ws2", Assert.IsType<SessionGroupHeaderViewModel>(row).Key),
                          row => Assert.Equal("s3", Assert.IsType<SessionItemViewModel>(row).Id));
    }

    [Fact]
    public async Task ArchiveSession_HidesRowAndRequestsDraftPageWhenCurrent()
    {
        var newSessionRequests = new List<string?>();
        var sessionService     = new FakeSessionService();
        var workspaceService   = new FakeWorkspaceService();
        var pinService         = new FakeSidebarPinService();
        sessionService.Sessions = [Summary("s1", "会话一"), Summary("s2", "会话二")];
        var sidebar = new SidebarViewModel(sessionService, workspaceService, pinService,
                                           _ => { },
                                           id =>
                                           {
                                               newSessionRequests.Add(id);
                                               return Task.CompletedTask;
                                           },
                                           _ => { });
        await sidebar.RefreshSessionsAsync(CancellationToken.None);
        sidebar.SessionListModeIndex = 0;
        var row1 = sidebar.SessionRows.OfType<SessionItemViewModel>().Single(row => row.Id == "s1");
        sidebar.ApplySelectedSession(row1);
        sidebar.ToggleSessionPinCommand.Execute(row1);

        sidebar.ArchiveSessionCommand.Execute(row1);

        // 归档回流后：行移出列表表面且置顶互斥清除，但行实例保留在目录中待恢复；
        // 归档的是当前会话时请求进入新对话草稿页（root 编排入参为 null）。
        Assert.Contains("s1", workspaceService.ArchivedSessionIds);
        Assert.False(row1.Pinned);
        Assert.DoesNotContain(row1, sidebar.SessionRows);
        Assert.Contains(row1, sidebar.Sessions);
        Assert.Equal([null], newSessionRequests);
    }

    [Fact]
    public async Task ExternalArchiveOfCurrentSession_ReturnsToDraftPageWithoutSelectingOther()
    {
        var selectionRequests  = new List<SessionItemViewModel?>();
        var newSessionRequests = new List<string?>();
        var sessionService     = new FakeSessionService();
        var workspaceService   = new FakeWorkspaceService();
        sessionService.Sessions = [Summary("s1", "会话一"), Summary("s2", "会话二")];
        var sidebar = new SidebarViewModel(sessionService, workspaceService, new FakeSidebarPinService(),
                                           selectionRequests.Add,
                                           id =>
                                           {
                                               newSessionRequests.Add(id);
                                               return Task.CompletedTask;
                                           },
                                           _ => { });
        await sidebar.RefreshSessionsAsync(CancellationToken.None);
        var row1 = sidebar.SessionRows.OfType<SessionItemViewModel>().Single(row => row.Id == "s1");
        sidebar.ApplySelectedSession(row1);
        // 初始刷新的「无选中回退选中」已产生一次选中请求：清掉准备期记录再触发归档。
        selectionRequests.Clear();
        newSessionRequests.Clear();

        workspaceService.ApplyExternalArchived("s1");

        // 当前选中被外部归档（或重连基线带回归档态）：行移出列表表面，经 root 既有
        // 流程回新对话草稿页；不自动选中其他会话，归档行实例保留在目录中待恢复。
        Assert.DoesNotContain(row1, sidebar.SessionRows);
        Assert.Contains(row1, sidebar.Sessions);
        Assert.Equal([null], newSessionRequests);
        Assert.Empty(selectionRequests);

        // 会话刷新路径同样协调（重连基线也可能只触发会话列表刷新）。
        newSessionRequests.Clear();
        await sidebar.RefreshSessionsAsync(CancellationToken.None);
        Assert.Equal([null], newSessionRequests);
        Assert.Empty(selectionRequests);
    }

    [Fact]
    public async Task ExternalArchiveOfNonCurrentSession_DoesNotChangeNavigation()
    {
        var selectionRequests  = new List<SessionItemViewModel?>();
        var newSessionRequests = new List<string?>();
        var sessionService     = new FakeSessionService();
        var workspaceService   = new FakeWorkspaceService();
        sessionService.Sessions = [Summary("s1", "会话一"), Summary("s2", "会话二")];
        var sidebar = new SidebarViewModel(sessionService, workspaceService, new FakeSidebarPinService(),
                                           selectionRequests.Add,
                                           id =>
                                           {
                                               newSessionRequests.Add(id);
                                               return Task.CompletedTask;
                                           },
                                           _ => { });
        await sidebar.RefreshSessionsAsync(CancellationToken.None);
        var row1 = sidebar.SessionRows.OfType<SessionItemViewModel>().Single(row => row.Id == "s1");
        sidebar.ApplySelectedSession(row1);
        // 初始刷新的「无选中回退选中」已产生一次选中请求：清掉准备期记录再触发归档。
        selectionRequests.Clear();
        newSessionRequests.Clear();

        // 非当前会话被外部归档：当前选中不受影响，不触发任何导航。
        workspaceService.ApplyExternalArchived("s2");

        Assert.Contains(row1, sidebar.SessionRows);
        Assert.True(row1.IsCurrent);
        Assert.Empty(newSessionRequests);
        Assert.Empty(selectionRequests);
    }

    [Fact]
    public async Task ExternalArchiveOfCurrentSession_DoesNotStealIntentionalDraftPage()
    {
        var selectionRequests  = new List<SessionItemViewModel?>();
        var newSessionRequests = new List<string?>();
        var sessionService     = new FakeSessionService();
        var workspaceService   = new FakeWorkspaceService();
        sessionService.Sessions = [Summary("s1", "会话一"), Summary("s2", "会话二")];
        var sidebar = new SidebarViewModel(sessionService, workspaceService, new FakeSidebarPinService(),
                                           selectionRequests.Add,
                                           id =>
                                           {
                                               newSessionRequests.Add(id);
                                               return Task.CompletedTask;
                                           },
                                           _ => { });
        await sidebar.RefreshSessionsAsync(CancellationToken.None);
        var row1 = sidebar.SessionRows.OfType<SessionItemViewModel>().Single(row => row.Id == "s1");
        sidebar.ApplySelectedSession(row1);
        // 用户已主动停留新对话草稿页（root 推送守卫标记）。
        sidebar.SetDraftPageActive(true);
        // 初始刷新的「无选中回退选中」已产生一次选中请求：清掉准备期记录再触发归档。
        selectionRequests.Clear();
        newSessionRequests.Clear();

        workspaceService.ApplyExternalArchived("s1");

        // 归档回流不把草稿页抢回旧会话，也不重复请求进入草稿页。
        Assert.Empty(newSessionRequests);
        Assert.Empty(selectionRequests);
    }

    [Fact]
    public async Task LateSessionRenameSuccess_DoesNotDisturbNewPopupForOtherSession()
    {
        var sidebar = CreateSidebar(out var sessionService);
        sessionService.Sessions = [Summary("s1", "会话一"), Summary("s2", "会话二")];
        await sidebar.RefreshSessionsAsync(CancellationToken.None);
        var row1 = sidebar.Sessions.Single(session => session.Id == "s1");
        var row2 = sidebar.Sessions.Single(session => session.Id == "s2");
        var gate = new TaskCompletionSource<string>();
        sessionService.RenameGate = gate;

        // A 的确认在途时取消弹窗，再打开 B 的重命名弹窗并编辑。
        sidebar.OpenSessionRenameCommand.Execute(row1);
        sidebar.SessionRenameDraftText = "A 新标题";
        sidebar.ConfirmSessionRenameCommand.Execute(null);
        Assert.False(sidebar.CanConfirmSessionRename);
        sidebar.CancelSessionRenameCommand.Execute(null);
        sidebar.OpenSessionRenameCommand.Execute(row2);
        sidebar.SessionRenameDraftText = "B 新标题";

        // A 的迟到成功：只更新 A 的行标题；不关闭、不清空、不写入 B 的弹窗。
        gate.SetResult("A 新标题");
        await WaitUntilAsync(() => sidebar.CanConfirmSessionRename);

        Assert.Equal("A 新标题", row1.Title);
        Assert.True(sidebar.IsSessionRenameOpen);
        Assert.Equal("B 新标题", sidebar.SessionRenameDraftText);
        Assert.False(sidebar.HasSessionRenameError);

        // B 的弹窗仍可正常确认并关闭。
        sessionService.RenameGate = null;
        sidebar.ConfirmSessionRenameCommand.Execute(null);
        Assert.False(sidebar.IsSessionRenameOpen);
        Assert.Equal("B 新标题", row2.Title);
        Assert.Equal([("s1", "A 新标题"), ("s2", "B 新标题")], sessionService.RenamedSessions);
    }

    [Fact]
    public async Task LateSessionRenameFailure_DoesNotWriteErrorIntoNewPopup()
    {
        var sidebar = CreateSidebar(out var sessionService);
        sessionService.Sessions = [Summary("s1", "会话一"), Summary("s2", "会话二")];
        await sidebar.RefreshSessionsAsync(CancellationToken.None);
        var row1 = sidebar.Sessions.Single(session => session.Id == "s1");
        var row2 = sidebar.Sessions.Single(session => session.Id == "s2");
        var gate = new TaskCompletionSource<string>();
        sessionService.RenameGate = gate;

        sidebar.OpenSessionRenameCommand.Execute(row1);
        sidebar.SessionRenameDraftText = "A 新标题";
        sidebar.ConfirmSessionRenameCommand.Execute(null);
        sidebar.CancelSessionRenameCommand.Execute(null);
        sidebar.OpenSessionRenameCommand.Execute(row2);
        sidebar.SessionRenameDraftText = "B 新标题";

        // A 的迟到失败：错误属于 A 的那次弹窗，不写进 B 的弹窗。
        gate.SetException(new InvalidOperationException("改名失败"));
        await WaitUntilAsync(() => sidebar.CanConfirmSessionRename);

        Assert.Equal("会话一", row1.Title);
        Assert.True(sidebar.IsSessionRenameOpen);
        Assert.Equal("B 新标题", sidebar.SessionRenameDraftText);
        Assert.Null(sidebar.SessionRenameErrorText);
        Assert.False(sidebar.HasSessionRenameError);
    }

    [Fact]
    public async Task ReopenedSameSessionRename_OldResultDoesNotInterfere()
    {
        var sidebar = CreateSidebar(out var sessionService);
        sessionService.Sessions = [Summary("s1", "会话一")];
        await sidebar.RefreshSessionsAsync(CancellationToken.None);
        var row1 = sidebar.Sessions.Single(session => session.Id == "s1");
        var gate = new TaskCompletionSource<string>();
        sessionService.RenameGate = gate;

        sidebar.OpenSessionRenameCommand.Execute(row1);
        sidebar.SessionRenameDraftText = "A 新标题";
        sidebar.ConfirmSessionRenameCommand.Execute(null);
        sidebar.CancelSessionRenameCommand.Execute(null);
        // 关闭后重开同一会话并继续编辑：仅比较 sessionId 不足以识别新旧弹窗。
        sidebar.OpenSessionRenameCommand.Execute(row1);
        sidebar.SessionRenameDraftText = "A 更新标题";

        gate.SetResult("A 新标题");
        await WaitUntilAsync(() => sidebar.CanConfirmSessionRename);

        // 迟到结果只把权威标题落到行投影，不关闭重开的弹窗、不动草稿与错误状态。
        Assert.Equal("A 新标题", row1.Title);
        Assert.True(sidebar.IsSessionRenameOpen);
        Assert.Equal("A 更新标题", sidebar.SessionRenameDraftText);
        Assert.False(sidebar.HasSessionRenameError);

        // 重开的弹窗仍可正常确认并关闭。
        sessionService.RenameGate = null;
        sidebar.ConfirmSessionRenameCommand.Execute(null);
        Assert.False(sidebar.IsSessionRenameOpen);
        Assert.Equal("A 更新标题", row1.Title);
    }

    [Fact]
    public async Task WorkspacePinToggle_RaisesPinActionTextNotificationOnReusedRow()
    {
        var sidebar = CreateSidebar(out var sessionService, out var workspaceService);
        sessionService.Sessions = [Summary("s1", "会话一")];
        workspaceService.Workspaces =
        [
            new WorkspaceSummary("ws1", "工作区一", "/tmp/one", ["s1"], DateTimeOffset.Now)
        ];
        await sidebar.RefreshSessionsAsync(CancellationToken.None);
        await sidebar.RefreshWorkspacesAsync(CancellationToken.None);
        var header     = sidebar.SessionRows.OfType<SessionGroupHeaderViewModel>().Single(item => item.Key == "ws1");
        var properties = new List<string?>();
        header.PropertyChanged += (_, e) => properties.Add(e.PropertyName);

        sidebar.ToggleWorkspacePinCommand.Execute(header);

        // 置顶后行实例被投影复用（同一实例），文案属性收到更新通知。
        Assert.Contains(nameof(header.PinActionText), properties);
        Assert.Same(header,
                    sidebar.SessionRows.OfType<SessionGroupHeaderViewModel>().Single(item => item.Key == "ws1"));
        Assert.Equal("取消置顶", header.PinActionText);

        sidebar.ToggleWorkspacePinCommand.Execute(header);

        // 取消置顶同样通知文案变化。
        Assert.Equal(2, properties.Count(name => name == nameof(header.PinActionText)));
        Assert.Equal("置顶工作区", header.PinActionText);

        // 投影重建以同值重复 Update（刷新不改置顶态）：不重复通知文案。
        properties.Clear();
        await sidebar.RefreshWorkspacesAsync(CancellationToken.None);
        Assert.DoesNotContain(nameof(header.PinActionText), properties);
        Assert.Equal("置顶工作区", header.PinActionText);
    }

    [Fact]
    public async Task BranchSession_RenamesChildWithIncrementedTitleAndShowsRow()
    {
        var sidebar = CreateSidebar(out var sessionService, out _);
        sessionService.Sessions = [Summary("s1", "会话一")];
        await sidebar.RefreshSessionsAsync(CancellationToken.None);
        sidebar.SessionListModeIndex = 0;
        var sourceRow = sidebar.SessionRows.OfType<SessionItemViewModel>().Single(row => row.Id == "s1");

        sidebar.BranchSessionCommand.Execute(sourceRow);
        await WaitUntilAsync(() =>
                                 sidebar.SessionRows.OfType<SessionItemViewModel>().Any(row => row.Id == "s1-child"));

        // 分叉请求命中源会话；子会话无尾号按参考客户端语义追加 " (1)" 并上屏（不切换选中）。
        Assert.Equal(["s1"], sessionService.ForkSources);
        Assert.Equal([("s1-child", "会话一 (1)")], sessionService.RenamedSessions);
        Assert.Equal("会话一 (1)",
                     sidebar.SessionRows.OfType<SessionItemViewModel>()
                            .Single(row => row.Id == "s1-child").TitleText);
        Assert.Null(sidebar.SessionRows.OfType<SessionItemViewModel>()
                           .FirstOrDefault(row => row is { Id: "s1-child", IsCurrent: true }));

        // 对已带尾号的子会话再分叉：尾号递增而不是再次追加。
        var childRow = sidebar.SessionRows.OfType<SessionItemViewModel>().Single(row => row.Id == "s1-child");
        sidebar.BranchSessionCommand.Execute(childRow);
        await WaitUntilAsync(() =>
                                 sidebar.SessionRows.OfType<SessionItemViewModel>()
                                        .Any(row => row.Id == "s1-child-child"));

        Assert.Equal([("s1-child-child", "会话一 (2)")], sessionService.RenamedSessions.Skip(1));
    }

    [Fact]
    public async Task BranchSession_IncrementsFullWidthNumberAndSkipsUntitledSource()
    {
        var sidebar = CreateSidebar(out var sessionService, out _);
        sessionService.Sessions = [Summary("s1", "报告（3）"), Summary("s2", null)];
        await sidebar.RefreshSessionsAsync(CancellationToken.None);
        sidebar.SessionListModeIndex = 0;

        sidebar.BranchSessionCommand.Execute(
                                             sidebar.SessionRows.OfType<SessionItemViewModel>()
                                                    .Single(row => row.Id == "s1"));
        sidebar.BranchSessionCommand.Execute(
                                             sidebar.SessionRows.OfType<SessionItemViewModel>()
                                                    .Single(row => row.Id == "s2"));
        await WaitUntilAsync(() => sessionService.ForkSources.Count == 2);

        // 全角括号尾号同样递增；无标题源维持继承标题、不发改名请求。
        Assert.Equal([("s1-child", "报告（4）")], sessionService.RenamedSessions);
    }

    [Fact]
    public async Task OpenSessionRename_PrefillsDraftAndBlocksBlankConfirm()
    {
        var sidebar = CreateSidebar(out var sessionService);
        sessionService.Sessions = [Summary("s1", "会话一")];
        await sidebar.RefreshSessionsAsync(CancellationToken.None);
        var row = sidebar.Sessions.Single(session => session.Id == "s1");

        sidebar.OpenSessionRenameCommand.Execute(row);

        Assert.True(sidebar.IsSessionRenameOpen);
        Assert.Equal("会话一", sidebar.SessionRenameDraftText);
        // 对齐官方语义：未变更标题不阻止确认（确认当前自动标题即「钉住」），仅空白草稿阻止。
        Assert.True(sidebar.CanConfirmSessionRename);
        sidebar.SessionRenameDraftText = "   ";
        Assert.False(sidebar.CanConfirmSessionRename);
        Assert.False(sidebar.HasSessionRenameError);
    }

    [Fact]
    public async Task ReopenedSessionRename_RaisesCanConfirmNotificationWhenDraftUnchanged()
    {
        var sidebar = CreateSidebar(out var sessionService);
        sessionService.Sessions = [Summary("s1", "会话一")];
        await sidebar.RefreshSessionsAsync(CancellationToken.None);
        var row = sidebar.Sessions.Single(session => session.Id == "s1");

        // 先开关一轮：关闭路径把 CanConfirm 通知为 false，模拟常驻弹窗按钮停在禁用态。
        sidebar.OpenSessionRenameCommand.Execute(row);
        sidebar.CancelSessionRenameCommand.Execute(null);
        Assert.False(sidebar.CanConfirmSessionRename);

        // 重开同一会话：预填标题与残留草稿相同，草稿 setter 不通知——缺陷正是缺这次通知。
        var notified = new List<string?>();
        sidebar.PropertyChanged += (_, e) => notified.Add(e.PropertyName);
        sidebar.OpenSessionRenameCommand.Execute(row);

        Assert.True(sidebar.IsSessionRenameOpen);
        Assert.Equal("会话一", sidebar.SessionRenameDraftText);
        Assert.True(sidebar.CanConfirmSessionRename);
        Assert.Contains(nameof(sidebar.CanConfirmSessionRename), notified);
    }

    [Fact]
    public async Task ConfirmSessionRename_InvokesServiceAppliesTitleAndCloses()
    {
        var sidebar = CreateSidebar(out var sessionService);
        sessionService.Sessions = [Summary("s1", "会话一")];
        await sidebar.RefreshSessionsAsync(CancellationToken.None);
        var row = sidebar.Sessions.Single(session => session.Id == "s1");
        sidebar.OpenSessionRenameCommand.Execute(row);

        sidebar.SessionRenameDraftText = "  新标题  ";
        sidebar.ConfirmSessionRenameCommand.Execute(null);
        await WaitUntilAsync(() => !sidebar.IsSessionRenameOpen);

        Assert.Equal([("s1", "新标题")], sessionService.RenamedSessions);
        // 服务端接受后的标题就地落行投影，弹窗关闭且无错误残留。
        Assert.Equal("新标题", row.Title);
        Assert.False(sidebar.HasSessionRenameError);
    }

    [Fact]
    public async Task ConfirmSessionRename_ServiceErrorStaysOpenWithMessage()
    {
        var sidebar = CreateSidebar(out var sessionService);
        sessionService.Sessions = [Summary("s1", "会话一")];
        await sidebar.RefreshSessionsAsync(CancellationToken.None);
        var row = sidebar.Sessions.Single(session => session.Id == "s1");
        sidebar.OpenSessionRenameCommand.Execute(row);
        sidebar.SessionRenameDraftText = "另一个标题";
        sessionService.RenameFailure   = new InvalidOperationException("改名失败");

        sidebar.ConfirmSessionRenameCommand.Execute(null);
        await WaitUntilAsync(() => sidebar.HasSessionRenameError);

        // 失败不落标题、弹窗保持打开并呈现服务端错误；取消后关闭。
        Assert.True(sidebar.IsSessionRenameOpen);
        Assert.Equal("改名失败", sidebar.SessionRenameErrorText);
        Assert.Equal("会话一", row.Title);

        sessionService.RenameFailure = null;
        sidebar.CancelSessionRenameCommand.Execute(null);
        Assert.False(sidebar.IsSessionRenameOpen);
    }

    [Fact]
    public void ConfirmedBlankRow_HidesTrailingInfoAndActions()
    {
        var row = new SessionItemViewModel(new SessionSummary("s1", null,
                                                              DateTimeOffset.FromUnixTimeMilliseconds(1_000), false,
                                                              SessionBlankState.ConfirmedBlank));
        Assert.True(row.IsBlank);

        row.MarkEngaged();

        Assert.False(row.IsBlank);
    }

    private static SidebarViewModel CreateSidebar(out FakeSessionService sessionService)
    {
        return CreateSidebar(out sessionService, out _, out _);
    }

    private static SidebarViewModel CreateSidebar(
        out FakeSessionService sessionService, out FakeWorkspaceService workspaceService)
    {
        return CreateSidebar(out sessionService, out workspaceService, out _);
    }

    private static SidebarViewModel CreateSidebar(
        out FakeSessionService    sessionService,
        out FakeWorkspaceService  workspaceService,
        out FakeSidebarPinService pinService)
    {
        return CreateSidebar(out sessionService, out workspaceService, out pinService, null, null);
    }

    private static SidebarViewModel CreateSidebar(
        out FakeSessionService      sessionService,
        out FakeWorkspaceService    workspaceService,
        Func<string, string, Task>? renameWorkspace,
        Func<string, Task>?         deleteWorkspace)
    {
        return CreateSidebar(out sessionService, out workspaceService, out _, renameWorkspace, deleteWorkspace);
    }

    private static SidebarViewModel CreateSidebar(
        out FakeSessionService      sessionService,
        out FakeWorkspaceService    workspaceService,
        out FakeSidebarPinService   pinService,
        Func<string, string, Task>? renameWorkspace,
        Func<string, Task>?         deleteWorkspace)
    {
        sessionService   = new FakeSessionService();
        workspaceService = new FakeWorkspaceService();
        pinService       = new FakeSidebarPinService();
        return new SidebarViewModel(sessionService, workspaceService, pinService,
                                    _ => { }, _ => Task.CompletedTask, _ => { },
                                    null, renameWorkspace, deleteWorkspace);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition());
    }

    private static SessionSummary Summary(string id, string? title)
    {
        return new SessionSummary(id, title, DateTimeOffset.FromUnixTimeMilliseconds(1_000), false,
                                  SessionBlankState.Engaged);
    }

    /// <summary>最小会话服务桩：侧栏只消费目录读取与变更事件，其余成员不参与。</summary>
    private sealed class FakeSessionService : ISessionService
    {
        public IReadOnlyList<SessionSummary> Sessions { get; set; } = [];

        /// <summary>分叉请求记录：分叉同时把子会话并入目录并广播（模拟真实回流）。</summary>
        public List<string> ForkSources { get; } = [];

        /// <summary>改名请求记录（sessionId, title）。</summary>
        public List<(string SessionId, string Title)> RenamedSessions { get; } = [];

        /// <summary>注入后改名请求即抛出（验证重命名弹窗的错误呈现路径）。</summary>
        public Exception? RenameFailure { get; set; }

        /// <summary>
        ///     注入后改名请求挂起在门闩上（响应未返回，模拟网络在途）：由测试显式
        ///     SetResult/SetException 放行，控制迟到结果与新弹窗的顺序。
        /// </summary>
        public TaskCompletionSource<string>? RenameGate { get; set; }

        public event EventHandler? SessionsChanged;

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

        public Task<string> ForkSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            ForkSources.Add(sessionId);
            var source = Sessions.Single(session => session.Id == sessionId);
            var child  = source with { Id = $"{sessionId}-child", UpdatedAt = DateTimeOffset.Now };
            Sessions = [.. Sessions, child];
            RaiseChanged();
            return Task.FromResult(child.Id);
        }

        public Task<string> RenameSessionAsync(
            string sessionId, string title, CancellationToken cancellationToken = default)
        {
            if (RenameFailure is not null) throw RenameFailure;

            RenamedSessions.Add((sessionId, title));
            // 门闩挂起时服务端尚未采纳：不落服务端集合、不广播，模拟响应在途。
            if (RenameGate is not null) return RenameGate.Task;

            Sessions = Sessions.Select(session => session.Id == sessionId
                                           ? session with { Title = title }
                                           : session).ToArray();
            RaiseChanged();
            return Task.FromResult(title);
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

        private void RaiseChanged() => SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>最小工作区服务桩：静态集合，归档经 RPC 调用变更并广播（驱动投影刷新）。</summary>
    private sealed class FakeWorkspaceService : IWorkspaceService
    {
        public IReadOnlyList<WorkspaceSummary> Workspaces { get; set; } = [];

        public IReadOnlySet<string> ArchivedSessionIds { get; private set; } = new HashSet<string>();

        public event EventHandler? WorkspacesChanged;

        public Task<IReadOnlyList<WorkspaceSummary>> GetWorkspacesAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Workspaces);
        }

        public Task<WorkspaceSummary> RegisterWorkspaceAsync(string path, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<WorkspaceSummary> RenameWorkspaceAsync(string workspaceId,
                                                           string title, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task DeleteWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task ArchiveSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            ArchivedSessionIds = new HashSet<string>(ArchivedSessionIds) { sessionId };
            RaiseChanged();
            return Task.CompletedTask;
        }

        /// <summary>模拟外部归档回流（其他客户端归档或重连基线）：直接更新归档集合并广播。</summary>
        public void ApplyExternalArchived(params string[] sessionIds)
        {
            ArchivedSessionIds = new HashSet<string>(ArchivedSessionIds.Concat(sessionIds));
            RaiseChanged();
        }

        private void RaiseChanged() => WorkspacesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>最小本地置顶注册表桩：进程内集合（前插对齐真实服务存储序），不落盘。</summary>
    private sealed class FakeSidebarPinService : ISidebarPinService
    {
        private readonly List<string> _pinnedSessions   = [];
        private readonly List<string> _pinnedWorkspaces = [];

        public event EventHandler? PinsChanged;

        public IReadOnlyList<string> PinnedSessionIds   => _pinnedSessions.ToArray();
        public IReadOnlyList<string> PinnedWorkspaceIds => _pinnedWorkspaces.ToArray();

        public Task PinSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Toggle(_pinnedSessions, sessionId, true);

        public Task UnpinSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Toggle(_pinnedSessions, sessionId, false);

        public Task PinWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default) =>
            Toggle(_pinnedWorkspaces, workspaceId, true);

        public Task UnpinWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default) =>
            Toggle(_pinnedWorkspaces, workspaceId, false);

        private Task Toggle(List<string> source, string id, bool pin)
        {
            source.Remove(id);
            if (pin) source.Insert(0, id);

            PinsChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }
}
