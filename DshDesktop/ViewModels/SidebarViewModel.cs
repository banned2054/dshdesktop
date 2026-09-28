using DshDesktop.Core.Models;
using DshDesktop.Core.Services;
using System.Collections.ObjectModel;

namespace DshDesktop.ViewModels;

/// <summary>
///     左侧会话列表子视图模型：会话条目、工作区分组投影、视图模式与新建入口。当前选中
///     会话由 MainWindowViewModel 持有（同时驱动时间线、Composer 与审批）：本类在选中变化时
///     接收推送维护 IsCurrent 高亮；用户点击行与新建会话经回调请求 root 编排。
///     完整会话目录（含未选中空白行）保存在 <see cref="_catalog" />，侧栏可见行是其派生投影：
///     已确认空白且未选中的会话隐藏，未知与已开始的会话保留。空白状态不复述权威——
///     目录随时可由 session/list 重建，不是第二套会话数据库。
/// </summary>
public sealed class SidebarViewModel : ObservableObject, IDisposable
{
    // 会话列表展示：单列表或按工作区分组（对齐参考客户端的视图选项）。
    private const int SessionListModeFlat        = 0;
    private const int SessionListModeByWorkspace = 1;

    private const string UngroupedKey = "$ungrouped";

    private readonly ISessionService   _sessionService;
    private readonly IWorkspaceService _workspaceService;

    // 用户点击行/新建入口时请求 root 切换选中或编排创建；错误上报到窗口级 ErrorText（null 表示清除）。
    private readonly Action<SessionItemViewModel?> _requestSelection;
    private readonly Func<string?, Task>           _requestNewSession;
    private readonly Action<string?>               _reportError;
    private readonly Action<Action>                _postToUi;

    /// <summary>完整会话目录（后端返回顺序，含未选中空白会话）；可由列表刷新整体重建。</summary>
    private IReadOnlyList<SessionSummary> _catalog = [];

    private readonly HashSet<string> _collapsedGroups = [];

    private bool _isRequestingNewSession;
    private bool _isCreatingWorkspaceSession;

    // root 最近推送的选中会话：用于 IsCurrent 标记、空白行可见性与刷新后的选中决策。
    private SessionItemViewModel? _currentSession;

    // 用户主动停留在新对话草稿页：刷新触发的回退选中不得把草稿页抢回旧会话；
    // 手动点击行与选中会话被删除的回退不受此守卫影响。标记由 root 推送。
    private bool _isDraftPageActive;

    private int _listRefreshPending;

    // 默认按工作区分组，对齐参考 Web 客户端的默认视图选项。
    private int _sessionListModeIndex = SessionListModeByWorkspace;

    private IReadOnlyList<WorkspaceSummary> _workspaces = [];

    public SidebarViewModel(
        ISessionService               sessionService,
        IWorkspaceService             workspaceService,
        Action<SessionItemViewModel?> requestSelection,
        Func<string?, Task>           requestNewSession,
        Action<string?>               reportError,
        Action<Action>?               postToUi = null)
    {
        _sessionService                     =  sessionService;
        _workspaceService                   =  workspaceService;
        _requestSelection                   =  requestSelection;
        _requestNewSession                  =  requestNewSession;
        _reportError                        =  reportError;
        _postToUi                           =  postToUi ?? (action => action());
        NewSessionCommand                   =  new RelayCommand(() => _ = RequestNewSessionAsync(null));
        CreateWorkspaceSessionCommand       =  new RelayCommand<SessionGroupHeaderViewModel>(
            CreateWorkspaceSession, group => !_isCreatingWorkspaceSession && group is { IsWorkspace: true });
        SelectSessionCommand                =  new RelayCommand<SessionItemViewModel>(requestSelection);
        ToggleGroupCommand                  =  new RelayCommand<SessionGroupHeaderViewModel>(ToggleGroup);
        _sessionService.SessionsChanged     += OnSessionsChanged;
        _workspaceService.WorkspacesChanged += OnWorkspacesChanged;
    }

    public ObservableCollection<SessionItemViewModel> Sessions { get; } = [];

    /// <summary>会话列表的呈现行：会话行与分组标题行混排，按当前视图模式投影。</summary>
    public ObservableCollection<object> SessionRows { get; } = [];

    public RelayCommand NewSessionCommand { get; }

    public RelayCommand<SessionGroupHeaderViewModel> CreateWorkspaceSessionCommand { get; }

    public RelayCommand<SessionItemViewModel> SelectSessionCommand { get; }

    public RelayCommand<SessionGroupHeaderViewModel> ToggleGroupCommand { get; }

    /// <summary>会话列表视图模式：0 单列表，1 按工作区。偏好持久化随阶段 4 桌面设置接入。</summary>
    public int SessionListModeIndex
    {
        get => _sessionListModeIndex;
        set
        {
            if (SetProperty(ref _sessionListModeIndex, value)) RebuildSessionRows();
        }
    }

    /// <summary>
    ///     接收 root 推送的当前选中会话（每次实际切换时调用）：维护 IsCurrent 行高亮并重建
    ///     行投影——工作区头的 IsCurrent（是否包含当前会话）随选中变化。已确认空白的会话只在
    ///     选中期间可见：失去选中时就地移除——空闲后端不再有事件触发刷新兜底，否则草稿会以
    ///     "新对话"常驻列表（与 <see cref="RefreshSessionsAsync" /> 的可见性规则同一语义）。
    /// </summary>
    public void ApplySelectedSession(SessionItemViewModel? session)
    {
        var previous = _currentSession;
        _currentSession?.IsCurrent = false;
        _currentSession            = session;
        session?.IsCurrent         = true;
        if (previous is not null && !ReferenceEquals(previous, session)
                               && previous.BlankState == SessionBlankState.ConfirmedBlank)
            Sessions.Remove(previous);

        RebuildSessionRows();
    }

    /// <summary>root 推送「用户主动停留在新对话草稿页」标记：守卫刷新触发的回退选中。</summary>
    public void SetDraftPageActive(bool active)
    {
        _isDraftPageActive = active;
    }

    /// <summary>本端过渡信号（发送被接受等）：立即提升行状态并重建投影，不等列表刷新。</summary>
    public void NotifySessionEngaged(string sessionId)
    {
        var row = Sessions.FirstOrDefault(session => session.Id == sessionId);
        if (row is null || row.BlankState == SessionBlankState.Engaged) return;

        row.MarkEngaged();
        RebuildSessionRows();
    }

    /// <summary>
    ///     root 首发送被后端接受后加入可见行（已开始状态插入头部）；已存在同 id 行时
    ///     原位更新并返回（迟到的列表刷新/重复回调不重建实例）。返回行实例供选中。
    /// </summary>
    public SessionItemViewModel AddSessionRow(SessionSummary summary)
    {
        var row = Sessions.FirstOrDefault(session => session.Id == summary.Id);
        if (row is null)
        {
            row = new SessionItemViewModel(summary);
            Sessions.Insert(0, row);
        }
        else
        {
            row.UpdateSummary(summary);
        }

        RebuildSessionRows();
        return row;
    }

    /// <summary>
    ///     重读完整会话目录并投影可见行（已确认空白且未选中的会话隐藏，未知与已开始保留）。
    ///     就地更新既有条目：选中实例仍在可见集合中时不打扰 root——Running 变化经
    ///     SessionItemViewModel.PropertyChanged 由 root 既有订阅同步；选中已不在可见集合
    ///     （被删除，或尚无选中）时，经 requestSelection 请求 root 采用回退选择（按 id
    ///     匹配目录或首项，可能为 null）。
    /// </summary>
    public async Task RefreshSessionsAsync(CancellationToken cancellationToken)
    {
        _catalog = await _sessionService.GetSessionsAsync(cancellationToken);
        var selectedSessionId = _currentSession?.Id;
        var visible = _catalog.Where(summary => summary.BlankState != SessionBlankState.ConfirmedBlank
                                             || summary.Id == selectedSessionId)
                              .ToArray();

        // 就地更新既有条目：重建 ObservableCollection 会替换选中实例，
        // 触发重新订阅并让新快照清掉流式气泡，生成中的内容会闪动。
        var existingById = Sessions.ToDictionary(session => session.Id);
        for (var index = Sessions.Count - 1; index >= 0; index--)
            if (visible.All(summary => summary.Id != Sessions[index].Id))
                Sessions.RemoveAt(index);

        var insertIndex = 0;
        foreach (var summary in visible)
        {
            if (existingById.TryGetValue(summary.Id, out var item))
            {
                item.UpdateSummary(summary);
                var currentIndex = Sessions.IndexOf(item);
                if (currentIndex != insertIndex) Sessions.Move(currentIndex, insertIndex);
            }
            else
            {
                Sessions.Insert(insertIndex, new SessionItemViewModel(summary));
            }

            insertIndex++;
        }

        RebuildSessionRows();

        // retained 判定必须基于刷新后的 Sessions：existingById 是刷新前快照，
        // 后端已删除当前会话时它仍会命中，而实例其实已被上面的移除循环剔除。
        if (_currentSession is not null && Sessions.Contains(_currentSession))
            // 选中实例刷新后仍在列表中（同一实例）：不触发重订阅，也无需 root 协调。
            return;

        // 无选中时的回退选中只服务于初始化默认选中：用户主动停留在新对话草稿页时，
        // 创建会话等触发的列表刷新不得把草稿页抢回旧会话（手动点击行不走本分支）。
        if (_currentSession is null && _isDraftPageActive) return;

        // 选中已不存在（或尚无选中）：请求 root 采用回退选择。回退按完整目录匹配，
        // 避免选中空白会话在可见投影中缺席时被误判为消失。
        var selection = Sessions.FirstOrDefault(session => session.Id == selectedSessionId) ??
                        _catalog.Where(summary => summary.BlankState != SessionBlankState.ConfirmedBlank)
                                .Select(summary => Sessions.FirstOrDefault(session => session.Id == summary.Id))
                                .FirstOrDefault(session => session is not null);
        _requestSelection(selection);
    }

    /// <summary>重读工作区投影并重建分组行投影（初始化与 WorkspacesChanged 共用）。</summary>
    public async Task RefreshWorkspacesAsync(CancellationToken cancellationToken)
    {
        _workspaces = await _workspaceService.GetWorkspacesAsync(cancellationToken);
        RebuildSessionRows();
    }

    /// <summary>按当前视图模式把可见会话投影为呈现行；分组模式对齐参考客户端投影语义。</summary>
    private void RebuildSessionRows()
    {
        SessionRows.Clear();
        if (_sessionListModeIndex == SessionListModeFlat)
        {
            foreach (var session in Sessions) SessionRows.Add(session);

            return;
        }

        // 按工作区分组：组序为后端顺序，成员按更新时间降序（参考客户端 orderBy=updated）；
        // 不被任何工作区记账的会话（含新建空白会话）落入「未分组」，仅在有成员时显示。
        var accounted = new HashSet<string>();
        foreach (var workspace in _workspaces)
            AppendGroup(workspace.Id, workspace.Title,
                        Sessions.Where(session => workspace.SessionIds.Contains(session.Id)),
                        accounted, isWorkspace: true);

        AppendGroup(UngroupedKey, "未分组",
                    Sessions.Where(session => !accounted.Contains(session.Id)),
                    accounted, isWorkspace: false);
    }

    private void AppendGroup(
        string key, string title, IEnumerable<SessionItemViewModel> members, HashSet<string> accounted,
        bool isWorkspace)
    {
        var memberList = members.ToList();
        if (key == UngroupedKey && memberList.Count == 0) return;

        foreach (var member in memberList) accounted.Add(member.Id);

        var expanded = !_collapsedGroups.Contains(key);
        SessionRows.Add(new SessionGroupHeaderViewModel(key, title, memberList.Count, expanded, ToggleGroupCommand,
                                                        isWorkspace, memberList.Any(member => member.IsCurrent)));
        if (expanded)
            foreach (var member in memberList)
                SessionRows.Add(member);
    }

    private void ToggleGroup(SessionGroupHeaderViewModel? header)
    {
        if (header is null) return;

        if (!_collapsedGroups.Remove(header.Key)) _collapsedGroups.Add(header.Key);

        RebuildSessionRows();
    }

    private void OnWorkspacesChanged(object? sender, EventArgs e)
    {
        // 后台线程事件：回到界面线程重读投影（工作区变更频率低，不做合并）。
        _postToUi(() => _ = RefreshWorkspacesSafeAsync());
    }

    private async Task RefreshWorkspacesSafeAsync()
    {
        try
        {
            await RefreshWorkspacesAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            _reportError(exception.Message);
        }
    }

    private void OnSessionsChanged(object? sender, EventArgs e)
    {
        // 后台线程事件：合并 400ms 内的重复通知，再回到界面线程刷新列表。
        if (Interlocked.Exchange(ref _listRefreshPending, 1) == 1) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(400);
                Interlocked.Exchange(ref _listRefreshPending, 0);
                _postToUi(() => _ = RefreshSessionsSafeAsync());
            }
            catch (Exception)
            {
                Interlocked.Exchange(ref _listRefreshPending, 0);
            }
        });
    }

    /// <summary>SessionsChanged 合并刷新的 Safe 入口：失败经错误回调上报窗口级错误。</summary>
    private async Task RefreshSessionsSafeAsync()
    {
        try
        {
            await RefreshSessionsAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            _reportError(exception.Message);
        }
    }

    /// <summary>工作区组头「+」：请求 root 进入新对话草稿页并预选该工作区（本地防连点）。</summary>
    private void CreateWorkspaceSession(SessionGroupHeaderViewModel? group)
    {
        if (group is null || !group.IsWorkspace || _isCreatingWorkspaceSession) return;

        _isCreatingWorkspaceSession = true;
        CreateWorkspaceSessionCommand.RaiseCanExecuteChanged();
        _ = RequestWorkspaceSessionSafeAsync(group.Key);
    }

    private async Task RequestWorkspaceSessionSafeAsync(string workspaceId)
    {
        try
        {
            await _requestNewSession(workspaceId);
        }
        catch (Exception exception)
        {
            _reportError(exception.Message);
        }
        finally
        {
            _isCreatingWorkspaceSession = false;
            CreateWorkspaceSessionCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task RequestNewSessionSafeAsync(string? workspaceId)
    {
        try
        {
            await _requestNewSession(workspaceId);
        }
        catch (Exception exception)
        {
            _reportError(exception.Message);
        }
    }

    private async Task RequestNewSessionAsync(string? workspaceId)
    {
        if (_isRequestingNewSession) return;

        _isRequestingNewSession = true;
        try
        {
            await RequestNewSessionSafeAsync(workspaceId);
        }
        finally
        {
            _isRequestingNewSession = false;
        }
    }

    public void Dispose()
    {
        _sessionService.SessionsChanged     -= OnSessionsChanged;
        _workspaceService.WorkspacesChanged -= OnWorkspacesChanged;
    }
}
