using DshDesktop.Core.Models;
using DshDesktop.Core.Services;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

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

    // 置顶分类（高于工作区一层）的固定哨兵值：可折叠行头，成员为置顶工作区组与置顶会话。
    private const string PinnedKey = "$pinned";

    // 「工作区」分类的固定哨兵值：未被置顶的工作区统一收进该分类（项目自有投影）。
    private const string WorkspacesKey = "$workspaces";

    private readonly HashSet<string> _collapsedGroups = [];

    // 工作区管理请求交 root 编排（真实/模拟服务），业务错误由本类呈现在对应弹窗内。
    private readonly Func<string, Task>? _deleteWorkspace;

    // 本地置顶注册表（项目自有方案，持久化到本项目配置文件，不消费后端置顶集合）：
    // 会话与工作区共用，集合变化经 PinsChanged 回流重建行投影。
    private readonly ISidebarPinService          _pinService;
    private readonly Action<Action>              _postToUi;
    private readonly Func<string, string, Task>? _renameWorkspace;
    private readonly Action<string?>             _reportError;
    private readonly Func<string?, Task>         _requestNewSession;

    // 用户点击行/新建入口时请求 root 切换选中或编排创建；错误上报到窗口级 ErrorText（null 表示清除）。
    private readonly Action<SessionItemViewModel?> _requestSelection;

    private readonly ISessionService   _sessionService;
    private readonly IWorkspaceService _workspaceService;

    /// <summary>完整会话目录（后端返回顺序，含未选中空白会话）；可由列表刷新整体重建。</summary>
    private IReadOnlyList<SessionSummary> _catalog = [];

    // root 最近推送的选中会话：用于 IsCurrent 标记、空白行可见性与刷新后的选中决策。
    private SessionItemViewModel? _currentSession;
    private string?               _deleteTargetKey;
    private string?               _deleteTargetTitle;

    private bool _isCreatingWorkspaceSession;
    private bool _isDeleteConfirmOpen;
    private bool _isDeletingWorkspace;

    // 用户主动停留在新对话草稿页：刷新触发的回退选中不得把草稿页抢回旧会话；
    // 手动点击行与选中会话被删除的回退不受此守卫影响。标记由 root 推送。
    private bool    _isDraftPageActive;
    private bool    _isGroupMenuOpen;
    private bool    _isMutatingSessionFlags;
    private bool    _isRenameOpen;
    private bool    _isRenamingSession;
    private bool    _isRenamingWorkspace;
    private bool    _isRequestingNewSession;
    private bool    _isSearchOpen;
    private bool    _isSessionRenameOpen;
    private int     _listRefreshPending;
    private string  _renameDraftText = string.Empty;
    private string? _renameServerError;
    private string? _renameTargetKey;
    private string? _renameTargetTitle;

    // 默认按工作区分组，对齐参考 Web 客户端的默认视图选项。
    private int    _sessionListModeIndex   = SessionListModeByWorkspace;
    private string _sessionRenameDraftText = string.Empty;

    // 弹窗代次：每次实际开合递增（取消、Esc、浅失焦经 TwoWay IsOpen 关闭同样经过）。
    // 在途确认以提交时捕获的代次判断归属，迟到的成功/失败不得关闭、清空或写入
    // 后来打开的其他弹窗；仅比较 sessionId 不足以识别关闭后重开的同一会话弹窗。
    private int     _sessionRenameGeneration;
    private string? _sessionRenameTargetId;

    private string _sessionSearchText = string.Empty;

    private IReadOnlyList<WorkspaceSummary> _workspaces = [];

    public SidebarViewModel(
        ISessionService               sessionService,
        IWorkspaceService             workspaceService,
        ISidebarPinService            sidebarPinService,
        Action<SessionItemViewModel?> requestSelection,
        Func<string?, Task>           requestNewSession,
        Action<string?>               reportError,
        Action<Action>?               postToUi        = null,
        Func<string, string, Task>?   renameWorkspace = null,
        Func<string, Task>?           deleteWorkspace = null)
    {
        _sessionService    = sessionService;
        _workspaceService  = workspaceService;
        _pinService        = sidebarPinService;
        _requestSelection  = requestSelection;
        _requestNewSession = requestNewSession;
        _reportError       = reportError;
        _postToUi          = postToUi ?? (action => action());
        _renameWorkspace   = renameWorkspace;
        _deleteWorkspace   = deleteWorkspace;
        NewSessionCommand  = new RelayCommand(() => _ = RequestNewSessionAsync(null));
        CreateWorkspaceSessionCommand = new RelayCommand<SessionGroupHeaderViewModel>(
             CreateWorkspaceSession, group => !_isCreatingWorkspaceSession && group is { IsWorkspace: true });
        SelectSessionCommand       = new RelayCommand<SessionItemViewModel>(requestSelection);
        ToggleGroupCommand         = new RelayCommand<SessionGroupHeaderViewModel>(ToggleGroup);
        OpenSearchCommand          = new RelayCommand(() => IsSearchOpen    = true);
        CloseSearchCommand         = new RelayCommand(() => IsSearchOpen    = false);
        ToggleGroupMenuCommand     = new RelayCommand(() => IsGroupMenuOpen = !IsGroupMenuOpen);
        SetGroupByWorkspaceCommand = new RelayCommand(() => SetSessionListMode(SessionListModeByWorkspace));
        SetGroupFlatCommand        = new RelayCommand(() => SetSessionListMode(SessionListModeFlat));
        OpenWorkspaceRenameCommand = new RelayCommand<SessionGroupHeaderViewModel>(
             OpenWorkspaceRename, group => group is { IsWorkspace: true });
        OpenWorkspaceDeleteCommand = new RelayCommand<SessionGroupHeaderViewModel>(
             OpenWorkspaceDelete, group => group is { IsWorkspace: true });
        ToggleWorkspacePinCommand = new RelayCommand<SessionGroupHeaderViewModel>(
             group => _ = ToggleWorkspacePinSafeAsync(group),
             group => !_isMutatingSessionFlags && group is { IsWorkspace: true });
        ToggleSessionPinCommand =
            new RelayCommand<SessionItemViewModel>(session => _ = ToggleSessionPinSafeAsync(session),
                                                   session => !_isMutatingSessionFlags && session is not null);
        ArchiveSessionCommand = new RelayCommand<SessionItemViewModel>(session => _ = ArchiveSessionSafeAsync(session),
                                                                       session => !_isMutatingSessionFlags &&
                                                                           session is not null);
        BranchSessionCommand = new RelayCommand<SessionItemViewModel>(session => _ = BranchSessionSafeAsync(session),
                                                                      session => !_isMutatingSessionFlags &&
                                                                          session is not null);
        OpenSessionRenameCommand =
            new RelayCommand<SessionItemViewModel>(OpenSessionRename, session => session is not null);
        ConfirmSessionRenameCommand   = new RelayCommand(() => _ = ConfirmSessionRenameAsync());
        CancelSessionRenameCommand    = new RelayCommand(CancelSessionRename);
        ConfirmWorkspaceRenameCommand = new RelayCommand(() => _ = ConfirmWorkspaceRenameAsync());
        CancelWorkspaceRenameCommand  = new RelayCommand(CancelWorkspaceRename);
        ConfirmWorkspaceDeleteCommand = new RelayCommand(() => _ = ConfirmWorkspaceDeleteAsync());
        CancelWorkspaceDeleteCommand  = new RelayCommand(CancelWorkspaceDelete);

        _sessionService.SessionsChanged     += OnSessionsChanged;
        _workspaceService.WorkspacesChanged += OnWorkspacesChanged;
        _pinService.PinsChanged             += OnPinsChanged;
    }

    public ObservableCollection<SessionItemViewModel> Sessions { get; } = [];

    /// <summary>会话列表的呈现行：会话行与分组标题行混排，按当前视图模式投影。</summary>
    public ObservableCollection<object> SessionRows { get; } = [];

    public RelayCommand NewSessionCommand { get; }

    public RelayCommand<SessionGroupHeaderViewModel> CreateWorkspaceSessionCommand { get; }

    public RelayCommand<SessionItemViewModel> SelectSessionCommand { get; }

    public RelayCommand<SessionGroupHeaderViewModel> ToggleGroupCommand { get; }

    public RelayCommand OpenSearchCommand { get; }

    public RelayCommand CloseSearchCommand { get; }

    public RelayCommand ToggleGroupMenuCommand { get; }

    public RelayCommand SetGroupByWorkspaceCommand { get; }

    public RelayCommand SetGroupFlatCommand { get; }

    public RelayCommand<SessionGroupHeaderViewModel> OpenWorkspaceRenameCommand { get; }

    public RelayCommand<SessionGroupHeaderViewModel> OpenWorkspaceDeleteCommand { get; }

    /// <summary>会话行悬浮置顶按钮与菜单「置顶/取消置顶」共用：切换本地置顶注册表。</summary>
    public RelayCommand<SessionItemViewModel> ToggleSessionPinCommand { get; }

    /// <summary>工作区行菜单「置顶/取消置顶」：置顶工作区进入侧栏置顶分类（本地置顶注册表）。</summary>
    public RelayCommand<SessionGroupHeaderViewModel> ToggleWorkspacePinCommand { get; }

    /// <summary>会话行悬浮归档按钮与菜单「归档会话」共用。</summary>
    public RelayCommand<SessionItemViewModel> ArchiveSessionCommand { get; }

    /// <summary>会话菜单「分叉会话」：以最近完成 turn 为界复制出新会话。</summary>
    public RelayCommand<SessionItemViewModel> BranchSessionCommand { get; }

    /// <summary>会话菜单「重命名」：以该行为对象打开重命名弹窗并预填当前标题。</summary>
    public RelayCommand<SessionItemViewModel> OpenSessionRenameCommand { get; }

    public RelayCommand ConfirmSessionRenameCommand { get; }

    public RelayCommand CancelSessionRenameCommand { get; }

    public RelayCommand ConfirmWorkspaceRenameCommand { get; }

    public RelayCommand CancelWorkspaceRenameCommand { get; }

    public RelayCommand ConfirmWorkspaceDeleteCommand { get; }

    public RelayCommand CancelWorkspaceDeleteCommand { get; }

    /// <summary>会话列表视图模式：0 单列表，1 按工作区。偏好持久化随阶段 4 桌面设置接入。</summary>
    public int SessionListModeIndex
    {
        get => _sessionListModeIndex;
        set
        {
            if (!SetProperty(ref _sessionListModeIndex, value)) return;
            OnPropertyChanged(nameof(IsGroupByWorkspace));
            OnPropertyChanged(nameof(IsGroupFlat));
            RebuildSessionRows();
        }
    }

    /// <summary>侧栏搜索行是否展开：展开时覆盖「会话」头部行，收起时清空过滤词。</summary>
    public bool IsSearchOpen
    {
        get => _isSearchOpen;
        private set
        {
            if (SetProperty(ref _isSearchOpen, value) && !value) SessionSearchText = string.Empty;
        }
    }

    /// <summary>会话搜索词：输入即过滤标题（忽略大小写），行投影随之重建。</summary>
    public string SessionSearchText
    {
        get => _sessionSearchText;
        set
        {
            if (SetProperty(ref _sessionSearchText, value ?? string.Empty)) RebuildSessionRows();
        }
    }

    /// <summary>分组方式弹层是否打开（滑块按钮弹出，浅失焦关闭）。</summary>
    public bool IsGroupMenuOpen
    {
        get => _isGroupMenuOpen;
        set => SetProperty(ref _isGroupMenuOpen, value);
    }

    /// <summary>重命名工作区弹窗是否打开（工作区菜单「重命名」项打开，浅失焦或取消关闭）。</summary>
    public bool IsRenameOpen
    {
        get => _isRenameOpen;
        private set => SetProperty(ref _isRenameOpen, value);
    }

    /// <summary>重命名输入草稿：弹窗打开时预填当前标题；trim 后为确认与校验依据。</summary>
    public string RenameDraftText
    {
        get => _renameDraftText;
        set
        {
            if (!SetProperty(ref _renameDraftText, value ?? string.Empty)) return;
            // 输入变化即重算本地校验；上一次确认失败的服务端错误随之让位。
            _renameServerError = null;
            NotifyRenameValidation();
        }
    }

    /// <summary>确认重命名是否可用：非空、与当前名不同、不与其他工作区重名且不在途。</summary>
    public bool CanConfirmRename =>
        !_isRenamingWorkspace                          &&
        _renameTargetKey is not null                   &&
        RenameTrimmedText.Length > 0                   &&
        RenameTrimmedText        != _renameTargetTitle &&
        !HasRenameConflict;

    /// <summary>重命名错误提示：优先呈现服务端错误，否则呈现本地重名冲突。</summary>
    public string? RenameErrorText =>
        _renameServerError ??
        (HasRenameConflict ? $"已存在名为“{RenameTrimmedText}”的工作区。" : null);

    public bool HasRenameError => RenameErrorText is not null;

    /// <summary>删除工作区确认弹窗是否打开（工作区菜单「删除工作区」项打开）。</summary>
    public bool IsDeleteConfirmOpen
    {
        get => _isDeleteConfirmOpen;
        private set => SetProperty(ref _isDeleteConfirmOpen, value);
    }

    /// <summary>删除确认描述：向用户说明只移出列表，目录与会话保留并回到「未分组」。</summary>
    public string DeleteConfirmText =>
        $"将把“{_deleteTargetTitle}”从工作区列表中移除。文件夹与会话记录会保留，其会话将显示在“未分组”下。";

    /// <summary>删除失败提示（确认弹窗内呈现，可重试或取消）。</summary>
    public string? DeleteErrorText { get; private set; }

    public bool HasDeleteError => DeleteErrorText is not null;

    /// <summary>确认删除是否可用：存在删除目标且不在途。</summary>
    public bool CanConfirmDelete => !_isDeletingWorkspace && _deleteTargetKey is not null;

    /// <summary>重命名草稿的 trim 结果：确认与重名比对都以它为准。</summary>
    private string RenameTrimmedText => RenameDraftText.Trim();

    /// <summary>其他工作区是否已占用目标名（与本地投影比对，忽略大小写）。</summary>
    private bool HasRenameConflict =>
        _renameTargetKey is not null &&
        _workspaces.Any(workspace => workspace.Id != _renameTargetKey &&
                                     string.Equals(workspace.Title, RenameTrimmedText,
                                                   StringComparison.OrdinalIgnoreCase));

    /// <summary>重命名会话弹窗是否打开（会话菜单「重命名」项打开，浅失焦或取消关闭）。</summary>
    public bool IsSessionRenameOpen
    {
        get => _isSessionRenameOpen;
        private set
        {
            if (!SetProperty(ref _isSessionRenameOpen, value)) return;

            // 开合即换代（取消、Esc、浅失焦经 TwoWay IsOpen 关闭都走这里）：
            // 在途确认以提交时捕获的代次判断归属。
            _sessionRenameGeneration++;
        }
    }

    /// <summary>会话重命名输入草稿：弹窗打开时预填当前标题；trim 后为确认与校验依据。</summary>
    public string SessionRenameDraftText
    {
        get => _sessionRenameDraftText;
        set
        {
            if (!SetProperty(ref _sessionRenameDraftText, value ?? string.Empty)) return;
            // 输入变化即重算本地校验；上一次确认失败的服务端错误随之让位。
            SessionRenameErrorText = null;
            NotifySessionRenameValidation();
        }
    }

    /// <summary>
    ///     确认重命名是否可用：存在目标、标题非空且不在途。对齐官方语义，与工作区
    ///     重命名不同，未变更的标题不阻止确认——确认当前自动标题正是「钉住」它的手势。
    /// </summary>
    public bool CanConfirmSessionRename =>
        !_isRenamingSession                &&
        _sessionRenameTargetId is not null &&
        SessionRenameTrimmedText.Length > 0;

    /// <summary>会话重命名错误提示：服务端校验与调用失败在弹窗内呈现（会话标题允许重名，无本地冲突检查）。</summary>
    public string? SessionRenameErrorText { get; private set; }

    public bool HasSessionRenameError => SessionRenameErrorText is not null;

    /// <summary>会话重命名草稿的 trim 结果：确认与未变更比对都以它为准。</summary>
    private string SessionRenameTrimmedText => SessionRenameDraftText.Trim();

    /// <summary>分组方式弹层勾选态：按工作区。随视图模式变化由 SessionListModeIndex 联动。</summary>
    public bool IsGroupByWorkspace => _sessionListModeIndex == SessionListModeByWorkspace;

    /// <summary>分组方式弹层勾选态：单列表。</summary>
    public bool IsGroupFlat => _sessionListModeIndex == SessionListModeFlat;

    public void Dispose()
    {
        _sessionService.SessionsChanged     -= OnSessionsChanged;
        _workspaceService.WorkspacesChanged -= OnWorkspacesChanged;
        _pinService.PinsChanged             -= OnPinsChanged;
    }

    /// <summary>重命名校验相关属性的统一通知点（输入、在途与错误状态变化都会经过）。</summary>
    private void NotifyRenameValidation()
    {
        OnPropertyChanged(nameof(CanConfirmRename));
        OnPropertyChanged(nameof(RenameErrorText));
        OnPropertyChanged(nameof(HasRenameError));
    }

    /// <summary>工作区菜单「重命名」：以该行工作区为对象打开重命名弹窗并预填标题。</summary>
    private void OpenWorkspaceRename(SessionGroupHeaderViewModel? group)
    {
        if (group is not { IsWorkspace: true }) return;

        _renameTargetKey   = group.Key;
        _renameTargetTitle = group.TitleText;
        _renameServerError = null;
        RenameDraftText    = group.TitleText;
        IsRenameOpen       = true;
    }

    /// <summary>确认重命名：本地校验通过后交 root 调服务；失败留在弹窗内呈现并可重试。</summary>
    private async Task ConfirmWorkspaceRenameAsync()
    {
        if (_renameWorkspace is null || !CanConfirmRename) return;

        var workspaceId = _renameTargetKey!;
        var title       = RenameTrimmedText;
        _isRenamingWorkspace = true;
        NotifyRenameValidation();
        try
        {
            await _renameWorkspace(workspaceId, title);
            // 成功：标题经工作区状态流回流重建行投影（组头不可变，不就地改标题）。
            _renameServerError = null;
            CloseRename();
        }
        catch (Exception exception)
        {
            _renameServerError = exception.Message;
            NotifyRenameValidation();
        }
        finally
        {
            _isRenamingWorkspace = false;
            NotifyRenameValidation();
        }
    }

    /// <summary>关闭并清理重命名弹窗状态（取消、确认成功与浅失焦共用）。</summary>
    private void CloseRename()
    {
        IsRenameOpen       = false;
        _renameTargetKey   = null;
        _renameTargetTitle = null;
        _renameServerError = null;
        NotifyRenameValidation();
    }

    private void CancelWorkspaceRename()
    {
        CloseRename();
    }

    /// <summary>工作区菜单「删除工作区」：以该行工作区为对象打开确认弹窗。</summary>
    private void OpenWorkspaceDelete(SessionGroupHeaderViewModel? group)
    {
        if (group is not { IsWorkspace: true }) return;

        _deleteTargetKey   = group.Key;
        _deleteTargetTitle = group.TitleText;
        DeleteErrorText    = null;
        OnPropertyChanged(nameof(DeleteConfirmText));
        OnPropertyChanged(nameof(DeleteErrorText));
        OnPropertyChanged(nameof(HasDeleteError));
        OnPropertyChanged(nameof(CanConfirmDelete));
        IsDeleteConfirmOpen = true;
    }

    /// <summary>确认删除：交 root 调服务（只删注册）；失败留在弹窗内呈现并可重试。</summary>
    private async Task ConfirmWorkspaceDeleteAsync()
    {
        if (_deleteWorkspace is null || !CanConfirmDelete) return;

        var workspaceId = _deleteTargetKey!;
        _isDeletingWorkspace = true;
        OnPropertyChanged(nameof(CanConfirmDelete));
        try
        {
            await _deleteWorkspace(workspaceId);
            // 成功：移除经工作区状态流回流；成员会话按记账语义由刷新投影落「未分组」。
            DeleteErrorText = null;
            CloseDelete();
        }
        catch (Exception exception)
        {
            DeleteErrorText = exception.Message;
            OnPropertyChanged(nameof(DeleteErrorText));
            OnPropertyChanged(nameof(HasDeleteError));
        }
        finally
        {
            _isDeletingWorkspace = false;
            OnPropertyChanged(nameof(CanConfirmDelete));
        }
    }

    /// <summary>关闭并清理删除确认弹窗状态（取消、确认成功与浅失焦共用）。</summary>
    private void CloseDelete()
    {
        IsDeleteConfirmOpen = false;
        _deleteTargetKey    = null;
        _deleteTargetTitle  = null;
        DeleteErrorText     = null;
        OnPropertyChanged(nameof(DeleteConfirmText));
        OnPropertyChanged(nameof(DeleteErrorText));
        OnPropertyChanged(nameof(HasDeleteError));
        OnPropertyChanged(nameof(CanConfirmDelete));
    }

    private void CancelWorkspaceDelete()
    {
        CloseDelete();
    }

    /// <summary>会话重命名校验相关属性的统一通知点（输入、在途与错误状态变化都会经过）。</summary>
    private void NotifySessionRenameValidation()
    {
        OnPropertyChanged(nameof(CanConfirmSessionRename));
        OnPropertyChanged(nameof(SessionRenameErrorText));
        OnPropertyChanged(nameof(HasSessionRenameError));
    }

    /// <summary>会话菜单「重命名」：以该行为对象打开重命名弹窗并预填当前标题。</summary>
    private void OpenSessionRename(SessionItemViewModel? session)
    {
        if (session is null) return;

        _sessionRenameTargetId = session.Id;
        SessionRenameErrorText = null;
        SessionRenameDraftText = session.Title ?? string.Empty;
        IsSessionRenameOpen    = true;

        // 预填与残留草稿相同时草稿 setter 不通知，开窗即补齐校验通知（CanConfirm 与错误文本），
        // 否则常驻弹窗的确认按钮停留在关闭时通知的禁用态。
        NotifySessionRenameValidation();
    }

    /// <summary>确认重命名：本地校验通过后直调 session/rename；失败留在弹窗内呈现并可重试。</summary>
    private async Task ConfirmSessionRenameAsync()
    {
        if (!CanConfirmSessionRename) return;

        var sessionId  = _sessionRenameTargetId!;
        var title      = SessionRenameTrimmedText;
        var generation = _sessionRenameGeneration;
        _isRenamingSession = true;
        NotifySessionRenameValidation();
        try
        {
            // 服务端接受后的规范化标题就地落投影（权威值）；列表刷新携带同值回流，幂等对齐。
            var accepted = await _sessionService.RenameSessionAsync(sessionId, title);
            Sessions.FirstOrDefault(session => session.Id == sessionId)?.ApplyRenamedTitle(accepted);
            if (generation != _sessionRenameGeneration)
                // 迟到结果：弹窗已取消/失焦关闭，或已重开（可能是别的会话）——
                // 只更新原会话标题，不关闭、不清空、不写入当前弹窗。
                return;

            SessionRenameErrorText = null;
            CloseSessionRename();
        }
        catch (Exception exception)
        {
            // 归属已变时不写入：错误只属于发起确认的那个弹窗。
            if (generation != _sessionRenameGeneration) return;

            SessionRenameErrorText = exception.Message;
            NotifySessionRenameValidation();
        }
        finally
        {
            // 在途标记按请求而非弹窗代次复位：弹窗换代后也要复位，否则新弹窗的确认
            // 按钮会被永久禁用；弹窗内状态（关闭、错误文本）的写入已按代次检查归属。
            _isRenamingSession = false;
            NotifySessionRenameValidation();
        }
    }

    /// <summary>关闭并清理会话重命名弹窗状态（取消、确认成功与浅失焦共用）。</summary>
    private void CloseSessionRename()
    {
        IsSessionRenameOpen    = false;
        _sessionRenameTargetId = null;
        SessionRenameErrorText = null;
        NotifySessionRenameValidation();
    }

    private void CancelSessionRename()
    {
        CloseSessionRename();
    }

    /// <summary>分组方式弹层选项：切换视图模式并收起弹层（对齐参考客户端菜单选中即关闭）。</summary>
    private void SetSessionListMode(int modeIndex)
    {
        SessionListModeIndex = modeIndex;
        IsGroupMenuOpen      = false;
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
        if (previous is not null                &&
            !ReferenceEquals(previous, session) &&
            previous.BlankState == SessionBlankState.ConfirmedBlank)
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
        var visible = _catalog
                     .Where(summary => summary.BlankState != SessionBlankState.ConfirmedBlank ||
                                       summary.Id         == selectedSessionId)
                     .ToArray();

        // 就地更新既有条目：重建 ObservableCollection 会替换选中实例，
        // 触发重新订阅并让新快照清掉流式气泡，生成中的内容会闪动。
        var existingById = Sessions.ToDictionary(session => session.Id);
        var visibleIds   = visible.Select(summary => summary.Id).ToHashSet();
        for (var index = Sessions.Count - 1; index >= 0; index--)
            if (!visibleIds.Contains(Sessions[index].Id))
                Sessions.RemoveAt(index);

        var insertIndex = 0;
        foreach (var summary in visible)
        {
            if (existingById.TryGetValue(summary.Id, out var item))
            {
                item.UpdateSummary(summary);
                if (!ReferenceEquals(Sessions[insertIndex], item))
                    Sessions.Move(Sessions.IndexOf(item), insertIndex);
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
        {
            // 选中实例刷新后仍在列表中（同一实例）：不触发重订阅；唯一例外是它已被
            // 归档（其他客户端归档当前会话，或重连基线带回归档态）——协调回草稿页。
            await CoordinateArchivedCurrentPageAsync();
            return;
        }

        // 无选中时的回退选中只服务于初始化默认选中：用户主动停留在新对话草稿页时，
        // 创建会话等触发的列表刷新不得把草稿页抢回旧会话（手动点击行不走本分支）。
        if (_currentSession is null && _isDraftPageActive) return;

        // 选中已不存在（或尚无选中）：请求 root 采用回退选择。回退按完整目录匹配，
        // 避免选中空白会话在可见投影中缺席时被误判为消失；归档会话已移出列表表面，不作回退候选。
        var archived = _workspaceService.ArchivedSessionIds;
        var selection = Sessions.FirstOrDefault(session => session.Id == selectedSessionId) ?? _catalog
           .Where(summary => summary.BlankState != SessionBlankState.ConfirmedBlank && !archived.Contains(summary.Id))
           .Select(summary => Sessions.FirstOrDefault(session => session.Id == summary.Id))
           .FirstOrDefault(session => session is not null);
        _requestSelection(selection);
    }

    /// <summary>重读工作区投影并重建分组行投影（初始化与 WorkspacesChanged 共用）。</summary>
    public async Task RefreshWorkspacesAsync(CancellationToken cancellationToken)
    {
        _workspaces = await _workspaceService.GetWorkspacesAsync(cancellationToken);
        RebuildSessionRows();
        // 外部归档事件与重连基线经 WorkspacesChanged 回流，会话目录不变时不会触发
        // 会话刷新：归档当前会话的页面协调在工作区刷新路径同样执行。
        await CoordinateArchivedCurrentPageAsync();
    }

    /// <summary>
    ///     归档集合回流后的当前页面协调：当前选中已被归档（其他客户端归档当前会话，
    ///     或重连基线包含其归档态）时，对齐本窗口归档当前会话的行为，经 root 既有流程
    ///     （请求新对话）回到新对话草稿页。用户已主动停留草稿页时不抢导航；非当前会话
    ///     归档不进入本路径，不影响导航，也不自动选中其他会话。归档行实例保留在目录中
    ///     待恢复，后端记录不变。
    /// </summary>
    private async Task CoordinateArchivedCurrentPageAsync()
    {
        if (_currentSession is null) return;
        if (!_workspaceService.ArchivedSessionIds.Contains(_currentSession.Id)) return;
        if (_isDraftPageActive) return;

        await RequestNewSessionSafeAsync(null);
    }

    /// <summary>
    ///     按当前视图模式把可见会话投影为呈现行。分类是高于工作区一层的自有投影：
    ///     置顶分类在上（置顶工作区组在上、置顶会话在下，同类按更新时间降序），
    ///     其后为「工作区」分类，未置顶工作区组统一收进其中（组序为后端顺序）；
    ///     三个分类头（置顶/工作区/未分组）均为纯文字+右侧箭头的可折叠行，无图标
    ///     与高亮。置顶会话从原分组提取
    ///     （不再出现在原位置），置顶工作区的成员仍挂在组内，其中被置顶的会话以并列
    ///     形式进入置顶会话区（不嵌套）。单列表没有工作区行表示：置顶会话按更新时间
    ///     降序浮在最前，置顶工作区不参与投影。
    ///     搜索词非空时按标题过滤（忽略大小写）：只影响呈现投影，不改目录与选中语义；
    ///     过滤时空组（含工作区组）整体隐藏，避免残留无成员的组头；分类下无可见组时
    ///     分类头一并隐藏。
    ///     归档会话默认移出列表表面（参考客户端默认筛选），行实例保留在目录中待恢复。
    /// </summary>
    private void RebuildSessionRows()
    {
        SyncSessionFlags();
        var archived = _workspaceService.ArchivedSessionIds;
        var rows     = new List<object>();
        var headers  = SessionRows.OfType<SessionGroupHeaderViewModel>().ToDictionary(header => header.Key);
        var query    = _sessionSearchText.Trim();
        var matches = Sessions.Where(session => !archived.Contains(session.Id) &&
                                                (query.Length == 0 ||
                                                 session.TitleText
                                                        .Contains(query, StringComparison.OrdinalIgnoreCase)))
                              .ToArray();

        var pinnedIds = _pinService.PinnedSessionIds.ToHashSet();
        var pinnedSessions = matches.Where(session => pinnedIds.Contains(session.Id))
                                    .OrderByDescending(session => session.UpdatedAt)
                                    .ToArray();
        if (_sessionListModeIndex == SessionListModeFlat)
        {
            rows.AddRange(pinnedSessions);
            rows.AddRange(matches.Where(session => !pinnedIds.Contains(session.Id)));
            ApplySessionRows(rows);
            return;
        }

        // 分组模式：先投影置顶分类（有可见内容才显示分类头），再投影「工作区」分类与「未分组」。
        var filtering          = query.Length > 0;
        var pinnedWorkspaceIds = _pinService.PinnedWorkspaceIds;
        var pinnedWorkspaces = _workspaces.Where(workspace => pinnedWorkspaceIds.Contains(workspace.Id))
                                          .OrderByDescending(workspace => workspace.UpdatedAt)
                                          .ToArray();
        var accounted = new HashSet<string>();
        foreach (var session in pinnedSessions) accounted.Add(session.Id);

        if (pinnedSessions.Length > 0 || pinnedWorkspaces.Length > 0)
        {
            var pinnedRows = new List<object>();
            foreach (var workspace in pinnedWorkspaces)
            {
                var memberIds = workspace.SessionIds.ToHashSet();
                AppendGroup(pinnedRows, headers, workspace.Id, workspace.Title,
                            matches.Where(session => memberIds.Contains(session.Id) &&
                                                     !pinnedIds.Contains(session.Id)),
                            accounted, true, true, filtering);
            }

            pinnedRows.AddRange(pinnedSessions);
            if (pinnedRows.Count > 0)
            {
                var expanded = !_collapsedGroups.Contains(PinnedKey);
                if (headers.TryGetValue(PinnedKey, out var pinnedHeader))
                    pinnedHeader.Update("置顶", pinnedRows.Count, expanded, false);
                else
                    pinnedHeader = new SessionGroupHeaderViewModel(PinnedKey, "置顶", pinnedRows.Count, expanded,
                                                                   ToggleGroupCommand, false, isCategory : true);
                rows.Add(pinnedHeader);
                if (expanded) rows.AddRange(pinnedRows);
            }
        }

        // 工作区分类：未被置顶的工作区统一收进「工作区」分类（组序为后端顺序，
        // 成员按更新时间降序，参考客户端 orderBy=updated）；置顶工作区已提取到
        // 置顶分类，不重复出现。分类头与置顶分类同为纯文字行：分类下无可见组时
        // （搜索过滤空组）一并隐藏。不被任何工作区记账的会话（含新建空白会话）
        // 落入分类外的「未分组」，仅在有成员时显示。
        var workspaceRows = new List<object>();
        foreach (var workspace in _workspaces)
        {
            if (pinnedWorkspaceIds.Contains(workspace.Id)) continue;

            var memberIds = workspace.SessionIds.ToHashSet();
            AppendGroup(workspaceRows, headers, workspace.Id, workspace.Title,
                        matches.Where(session => memberIds.Contains(session.Id) &&
                                                 !pinnedIds.Contains(session.Id)),
                        accounted, true, false, filtering);
        }

        if (workspaceRows.Count > 0)
        {
            var expanded = !_collapsedGroups.Contains(WorkspacesKey);
            if (headers.TryGetValue(WorkspacesKey, out var workspacesHeader))
                workspacesHeader.Update("工作区", workspaceRows.Count, expanded, false);
            else
                workspacesHeader = new SessionGroupHeaderViewModel(WorkspacesKey, "工作区", workspaceRows.Count,
                                                                   expanded, ToggleGroupCommand, false,
                                                                   isCategory : true);
            rows.Add(workspacesHeader);
            if (expanded) rows.AddRange(workspaceRows);
        }

        // 「未分组」同为分类行（纯文字+右侧箭头，不并入工作区分类），仅在有成员时显示。
        AppendGroup(rows, headers, UngroupedKey, "未分组", matches.Where(session => !accounted.Contains(session.Id)),
                    accounted,
                    false, false, true, true);
        ApplySessionRows(rows);
    }

    /// <summary>保留未变行与控件；刷新不 Reset，排序只移动、筛选只增删实际变化的行。</summary>
    private void ApplySessionRows(List<object> rows)
    {
        var retained = rows.ToHashSet();
        for (var index = SessionRows.Count - 1; index >= 0; index--)
            if (!retained.Contains(SessionRows[index]))
                SessionRows.RemoveAt(index);

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (index < SessionRows.Count && ReferenceEquals(SessionRows[index], row)) continue;

            var previousIndex = SessionRows.IndexOf(row);
            if (previousIndex >= 0) SessionRows.Move(previousIndex, index);
            else SessionRows.Insert(index, row);
        }
    }

    /// <summary>
    ///     把本地置顶注册表的会话集合同步到行实例（行标记的数据源）；每次投影重建前调用，
    ///     列表刷新与本地置顶变更共用同一入口。置顶与归档互斥：归档会话行本身已隐藏，
    ///     标记仍按互斥语义落。
    /// </summary>
    private void SyncSessionFlags()
    {
        var pinnedIds = _pinService.PinnedSessionIds.ToHashSet();
        var archived  = _workspaceService.ArchivedSessionIds;
        foreach (var session in Sessions)
            session.Pinned = pinnedIds.Contains(session.Id) && !archived.Contains(session.Id);
    }

    private void AppendGroup(
        List<object> rows,        Dictionary<string, SessionGroupHeaderViewModel> headers,
        string       key,         string title, IEnumerable<SessionItemViewModel> members, HashSet<string> accounted,
        bool         isWorkspace, bool isPinned, bool skipWhenEmpty, bool isCategory = false)
    {
        var memberList = members.ToList();
        if (memberList.Count == 0 && skipWhenEmpty) return;

        foreach (var member in memberList) accounted.Add(member.Id);

        var expanded = !_collapsedGroups.Contains(key);
        // 分类头不参与选中高亮，IsCurrent 恒为 false。
        var current = !isCategory && memberList.Any(member => member.IsCurrent);
        // isPinned 只驱动菜单置顶标记；isCategory 标记分类头——「未分组」恒为分类，
        // 置顶/工作区分类头在 RebuildSessionRows 显式创建。
        if (!headers.TryGetValue(key, out var header))
            headers[key] = header = new SessionGroupHeaderViewModel(key, title, memberList.Count, expanded,
                                                                    ToggleGroupCommand, isWorkspace, current,
                                                                    isCategory);
        header.Update(title, memberList.Count, expanded, current, isPinned);
        rows.Add(header);
        if (!expanded) return;

        rows.AddRange(memberList);
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

    /// <summary>本地置顶注册表集合变化：置顶/取消置顶已生效，直接重建行投影。</summary>
    private void OnPinsChanged(object? sender, EventArgs e)
    {
        _postToUi(RebuildSessionRows);
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

    /// <summary>
    ///     切换会话置顶（本地置顶注册表，持久化到本项目配置文件）：集合变化经
    ///     PinsChanged 回流重建行投影；持久化失败上报窗口级错误，集合不变。
    /// </summary>
    private async Task ToggleSessionPinSafeAsync(SessionItemViewModel? session)
    {
        if (session is null || _isMutatingSessionFlags) return;

        _isMutatingSessionFlags = true;
        RaiseSessionMutationCanExecuteChanged();
        try
        {
            if (session.Pinned) await _pinService.UnpinSessionAsync(session.Id);
            else await _pinService.PinSessionAsync(session.Id);
        }
        catch (Exception exception)
        {
            _reportError(exception.Message);
        }
        finally
        {
            _isMutatingSessionFlags = false;
            RaiseSessionMutationCanExecuteChanged();
        }
    }

    /// <summary>
    ///     切换工作区置顶（本地置顶注册表，工作区行菜单入口）：置顶工作区提取到侧栏
    ///     置顶分类（工作区在上、会话在下），分类内其会话仍挂在组内；失败语义同会话置顶。
    /// </summary>
    private async Task ToggleWorkspacePinSafeAsync(SessionGroupHeaderViewModel? group)
    {
        if (group is null || !group.IsWorkspace || _isMutatingSessionFlags) return;

        _isMutatingSessionFlags = true;
        RaiseSessionMutationCanExecuteChanged();
        try
        {
            if (group.Pinned) await _pinService.UnpinWorkspaceAsync(group.Key);
            else await _pinService.PinWorkspaceAsync(group.Key);
        }
        catch (Exception exception)
        {
            _reportError(exception.Message);
        }
        finally
        {
            _isMutatingSessionFlags = false;
            RaiseSessionMutationCanExecuteChanged();
        }
    }

    /// <summary>
    ///     归档会话（workspace/archiveSession，移出列表表面，记录保留）。归档回流（本地
    ///     RPC 落投影与外部/基线事件共用）经 <see cref="CoordinateArchivedCurrentPageAsync" />
    ///     统一协调当前页面：归档的是当前会话时对齐参考客户端回到新对话草稿页。
    /// </summary>
    private async Task ArchiveSessionSafeAsync(SessionItemViewModel? session)
    {
        if (session is null || _isMutatingSessionFlags) return;

        _isMutatingSessionFlags = true;
        RaiseSessionMutationCanExecuteChanged();
        try
        {
            await _workspaceService.ArchiveSessionAsync(session.Id);
        }
        catch (Exception exception)
        {
            _reportError(exception.Message);
        }
        finally
        {
            _isMutatingSessionFlags = false;
            RaiseSessionMutationCanExecuteChanged();
        }
    }

    /// <summary>
    ///     分叉会话（session/fork，菜单「分叉会话」）：以最近完成 turn 为界复制出独立新会话，
    ///     子会话经列表刷新上屏、不切换选中（对齐参考客户端）。源会话有标题时按参考客户端
    ///     语义对子会话做尾部序号递增改名；改名失败不影响已创建的分支，只上报错误。
    /// </summary>
    private async Task BranchSessionSafeAsync(SessionItemViewModel? session)
    {
        if (session is null || _isMutatingSessionFlags) return;

        _isMutatingSessionFlags = true;
        RaiseSessionMutationCanExecuteChanged();
        try
        {
            var childId = await _sessionService.ForkSessionAsync(session.Id);
            if (!string.IsNullOrWhiteSpace(session.Title))
                await _sessionService.RenameSessionAsync(childId, IncreaseForkTitle(session.Title));
        }
        catch (Exception exception)
        {
            _reportError(exception.Message);
        }
        finally
        {
            _isMutatingSessionFlags = false;
            RaiseSessionMutationCanExecuteChanged();
        }
    }

    /// <summary>分叉子会话标题：尾部 (N)/（N）序号递增，无序号追加 " (1)"（对齐参考客户端）。</summary>
    private static string IncreaseForkTitle(string title)
    {
        var ascii = Regex.Match(title, @"^(.*?)\((\d+)\)$");
        if (ascii.Success && long.TryParse(ascii.Groups[2].Value, out var asciiNumber))
            return $"{ascii.Groups[1].Value}({asciiNumber + 1})";

        var fullWidth = Regex.Match(title, @"^(.*?)（(\d+)）$");
        if (fullWidth.Success && long.TryParse(fullWidth.Groups[2].Value, out var fullWidthNumber))
            return $"{fullWidth.Groups[1].Value}（{fullWidthNumber + 1}）";

        return $"{title} (1)";
    }

    private void RaiseSessionMutationCanExecuteChanged()
    {
        ToggleWorkspacePinCommand.RaiseCanExecuteChanged();
        ToggleSessionPinCommand.RaiseCanExecuteChanged();
        ArchiveSessionCommand.RaiseCanExecuteChanged();
        BranchSessionCommand.RaiseCanExecuteChanged();
    }
}
