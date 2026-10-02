using DshDesktop.Core.Models;
using DshDesktop.Core.Services;
using DshDesktop.Harness.Exceptions;
using DshDesktop.Services.Conversations;
using DshDesktop.ViewModels.Settings;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;

namespace DshDesktop.ViewModels;

public sealed class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    /// <summary>整页都被过滤条目（注入上下文）时的连续翻页上限，避免一次触发连环拉取。</summary>
    private const int EmptyPageFollowUpLimit = 4;

    private readonly IBackendHostService _backendHostService;

    // 导航与创建编排状态。_drafts 是按会话记的草稿簿（空串键为新对话草稿页的输入文本，
    // UI 线程写入、可重建的客户端状态）；_navigationGeneration 随每次选中切换递增，
    // 首发编排据此丢弃迟到结果，防止过期结果抢回界面或搬走草稿。
    private readonly Dictionary<string, string> _drafts = [];

    // follow 代际门闩：随 BeginFollow 递增；旧订阅循环在锁内校验代际后才应用更新，
    // 防止被抢占的旧循环把上一会话的迟到更新写进新会话的状态。
    private readonly Lock           _followGate = new();
    private readonly Action<Action> _postToUi;

    private readonly IPermissionPresetService _permissionPresetService;
    private readonly ISessionService          _sessionService;
    private readonly ISettingsService         _settingsService;
    private readonly ICredentialsService      _credentialsService;
    private readonly ILlmCatalogService?      _llmCatalogService;
    private readonly IToolApprovalService     _toolApprovalService;
    private readonly IWorkspaceService        _workspaceService;

    // 时间线组装状态：快照、增量与翻页共用同一套分组规则。
    private TimelineAssembly _assembly;
    private string?          _draftAgentPreset = AgentPresetModes.Default;
    private ModelSelection?  _draftModelSelection;

    // 新对话草稿的本地权限预选（与模型预选同一模式）：选择器回调记账并递增草稿版本，
    // 首发送创建会话后、发送首条消息前经 /permission 应用（会话创建参数不携带权限）。
    private string? _draftPermissionPreset;

    // 待复用会话记账：AttachCompleted 区分「会话已创建但工作区关联未完成」（重试须先恢复
    // 关联）与「关联已完成、仅后续选型或发送失败」（重试直接复用，不再创建）。AgentPreset
    // 记录创建时的绑定模式：改选后失配失效（preset 已在创建时固定），重试按新选择创建。
    private (string SessionId, string? WorkspaceId, bool WithoutWorkspace, string? AgentPreset,
        bool AttachCompleted)? _draftPendingSession;

    private bool _draftWithoutWorkspace;

    // 新对话草稿的版本（身份）：文本、工作区去向、预选模式或预选模型任一被用户改动时
    // 递增；导航离开/返回只是同一份草稿的缓存与装载，不递增。首发送快照捕获版本，成功后
    // 版本仍一致才整份消费——以此区分「同一份草稿只是切走又回来」与「已形成的新草稿」，
    // 不依赖文本字符串相等。
    private int _draftVersion;

    // SelectedSession 切换时程序化装载草稿文本的同步守卫：装载属于同一份草稿的恢复，
    // 不让 DraftMessage 的 PropertyChanged 误增草稿版本。
    private bool _isRestoringComposerDraft;

    // 新对话草稿（进程内，独立于已有会话；窗口关闭即丢弃，不写 Harness 存储与日志）：
    // 预选工作区（null=未选择）、是否显式选择不使用工作区、预选模型/档位、预选模式
    // （内置 wire id，初始为 dsh 默认）。创建已成功但发送未完成时的待复用会话连同其
    // 创建目标一起记账（防重复创建）：目标或模式被改选后自动失配失效（cwd 与 preset
    // 已在创建时固定），重试按新选择创建；_draftWorkspaceTitleFallback 缓存预选工作区
    // 标题，工作区投影尚未回流时下拉仍有可读文案。
    private string? _draftWorkspaceId;
    private string? _draftWorkspaceTitleFallback;
    private string  _errorText = string.Empty;

    private CancellationTokenSource? _followCancellation;

    private int  _followEpoch;
    private bool _hasMoreHistory;

    // 历史窗口状态：快照游标（throughSeq）、窗口首条事件 seq（beforeSeq）与是否还有更早历史。
    private long _historyThroughSeq;
    private bool _isDraftSendInFlight;
    private bool _isInitialized;
    private bool _isLoadingOlder;

    // 工作区/模式下拉展开态（Popup 双向绑定）；首次发送编排的在途标记（连点合并）。
    private bool _isWorkspaceMenuOpen;
    private bool _isPresetMenuOpen;

    // 添加工作区登记的在途标记：侧栏与草稿页下拉两处入口共用，选完文件夹后合并连点。
    private int _registeringWorkspace;

    private int _navigationGeneration;

    private SessionItemViewModel? _selectedSession;

    private string?               _streamingAttemptId;
    private MessageItemViewModel? _streamingMessage;

    // 已加载窗口的全量条目（按 seq 升序）；翻页折叠开关变化时据此整体重建时间线。
    private List<ConversationEntry> _timelineEntries = [];

    private long _windowStartSeq = 1;

    private bool _isSettingsOpen;

    /// <summary>
    ///     保持旧测试与宿主构造调用的兼容性。未提供审批服务时，界面没有审批来源，
    ///     但纯会话测试不应因此必须组装基础设施实现；未提供置顶服务时界面同样没有
    ///     置顶来源（空实现，集合恒空且不持久化）；未提供设置服务时没有设置文档入口。
    /// </summary>
    public MainWindowViewModel(
        ISessionService     sessionService,
        IBackendHostService backendHostService,
        IWorkspaceService   workspaceService,
        bool                isSimulatedMode = true,
        Action<Action>?     postToUi        = null)
        : this(sessionService, backendHostService, workspaceService, EmptyToolApprovalService.Instance,
               isSimulatedMode, postToUi)
    {
    }

    public MainWindowViewModel(
        ISessionService           sessionService,
        IBackendHostService       backendHostService,
        IWorkspaceService         workspaceService,
        IToolApprovalService      toolApprovalService,
        bool                      isSimulatedMode         = true,
        Action<Action>?           postToUi                = null,
        IPermissionPresetService? permissionPresetService = null,
        ISidebarPinService?       sidebarPinService       = null,
        ISettingsService?         settingsService         = null,
        ICredentialsService?      credentialService       = null,
        Action<string?>?          applyThemePreference    = null,
        ILlmCatalogService?       llmCatalogService       = null)
    {
        _sessionService          = sessionService;
        _backendHostService      = backendHostService;
        _toolApprovalService     = toolApprovalService;
        _workspaceService        = workspaceService;
        _permissionPresetService = permissionPresetService ?? EmptyPermissionPresetService.Instance;
        _settingsService         = settingsService         ?? EmptySettingsService.Instance;
        _credentialsService      = credentialService       ?? EmptyCredentialsService.Instance;
        _llmCatalogService       = llmCatalogService;
        IsSimulationMode         = isSimulatedMode;
        _postToUi                = postToUi ?? (action => action());
        // 草稿/发送/取消与模型选择已迁入 Composer；失败仍走窗口级 ErrorText（null 表示清除）。
        // 发送被接受时 root 立即把会话标记为已开始（不等后端帧回流）。草稿页的本地预选
        // 模型经回调记入草稿（不发 RPC），首发送创建会话后再应用。
        Composer = new ComposerViewModel(sessionService, text => ErrorText = text ?? string.Empty, HandlePromptAccepted,
                                         OnDraftModelChanged);
        // 执行权限选择器：目录与切换经独立服务，投影权威值由 root 转发（ApplySessionUpdate），
        // 会话与连接上下文随选中变化推送；草稿页做本地预选（回调记入草稿，首发送后应用）。
        // 审批（ApprovalPanel）与本选择器互不干涉。
        PermissionSelector = new PermissionSelectorViewModel(_permissionPresetService,
                                                             text => ErrorText = text ?? string.Empty, _postToUi,
                                                             OnDraftPermissionChanged);
        // 会话列表已迁入 Sidebar：选中切换仍由 root 编排（follow、Composer 与审批随 active
        // session 联动），Sidebar 只在用户操作或选中缺失/消失时经回调请求切换；
        // 新建入口统一交给 root 的编排流程（目标解析、复用与防重都在 root）。
        Sidebar = new SidebarViewModel(sessionService, workspaceService,
                                       sidebarPinService ?? EmptySidebarPinService.Instance,
                                       session => SelectedSession                = session,
                                       RequestNewSessionAsync, text => ErrorText = text ?? string.Empty, _postToUi,
                                       RenameWorkspaceAsync, DeleteWorkspaceAsync);
        LoadOlderCommand = new AsyncRelayCommand(LoadOlderAsync, CanLoadOlder);
        SelectWorkspaceCommand =
            new RelayCommand<WorkspaceOptionViewModel>(SelectDraftWorkspace);
        SelectPresetCommand =
            new RelayCommand<AgentPresetOptionViewModel>(SelectDraftPreset);
        AgentPresetOptions = AgentPresetOptionViewModel.CreateBuiltIns(SelectPresetCommand);
        RefreshPresetSelectionMarks();
        SendDraftCommand           = new AsyncRelayCommand(SendDraftAsync, () => CanSendDraft);
        ToggleWorkspaceMenuCommand = new RelayCommand(ToggleWorkspaceMenu);
        TogglePresetMenuCommand    = new RelayCommand(TogglePresetMenu);
        ApproveApprovalCommand =
            new RelayCommand<PendingApprovalViewModel>(approval => _ = RespondApprovalAsync(approval, true));
        RejectApprovalCommand =
            new RelayCommand<PendingApprovalViewModel>(approval => _ = RespondApprovalAsync(approval, false));
        OpenSettingsCommand = new AsyncRelayCommand(OpenSettingsAsync);
        // 设置面板子视图模型：服务事件由 root 订阅并经 _postToUi 编组转发（见 OnSettings*），
        // 面板开合由 root 的 IsSettingsOpen 承担，关闭请求由面板回调 root。
        // 会话服务供模型分区展示账户路由可见性（与 composer 模型菜单同源），
        // 目录服务供提供方行列表与添加卡（llm/listConfigurableProviders + llm/discoverModels）。
        Settings = new SettingsPanelViewModel(_settingsService, _credentialsService,
                                              applyThemePreference ?? (_ => { }), _postToUi, _sessionService,
                                              _llmCatalogService);
        Settings.CloseRequested               += OnSettingsCloseRequested;
        _backendHostService.StatusChanged     += OnBackendStatusChanged;
        _toolApprovalService.ApprovalsChanged += OnApprovalsChanged;
        _workspaceService.WorkspacesChanged   += OnWorkspacesChanged;
        _settingsService.DocumentUpdated      += OnSettingsDocumentUpdated;
        _credentialsService.ReferenceUpdated  += OnCredentialsReferenceUpdated;
        // 草稿页发送可用性随输入文本变化；Composer 由本类持有，同生命周期无需退订。
        Composer.PropertyChanged += OnComposerPropertyChanged;
        _assembly                =  CreateAssembly();
        Composer.SetBackendConnected(IsBackendConnected);
        // 初始即处于新对话草稿页（无选中会话）：构造期直接进入草稿模式，模型菜单的
        // 可用性不依赖「先选中过会话再切回」。无历史会话（SelectedSession 一直
        // null→null 不触发 setter）时目录加载完成后菜单即可用；重复进入草稿页的
        // 初始化（SetDraftTarget 等）均可安全重入。
        Composer.SetDraftTarget(true);
        PermissionSelector.SetDraftTarget(true);
    }

    public ObservableCollection<ConversationItemViewModel> ConversationItems { get; } = [];

    /// <summary>当前选中会话的待决审批（审批横幅）；随审批增删与会话切换重建。</summary>
    public ObservableCollection<PendingApprovalViewModel> SessionPendingApprovals { get; } = [];

    /// <summary>底部输入区子视图模型：草稿、发送/取消与模型选择；会话/后端上下文由本类在状态变化时推送。</summary>
    public ComposerViewModel Composer { get; }

    /// <summary>
    ///     执行权限选择子视图模型：当前会话权限预设的展示与切换。目录经 IPermissionPresetService
    ///     （catalog-changed 广播自行重读），当前权限以 permissions 投影为唯一权威，
    ///     由本类经 <see cref="ApplySessionUpdate" /> 转发。
    /// </summary>
    public PermissionSelectorViewModel PermissionSelector { get; }

    /// <summary>左侧会话列表子视图模型：条目、分组投影与新建；选中会话由本类持有并推送给它维护高亮。</summary>
    public SidebarViewModel Sidebar { get; }

    public AsyncRelayCommand LoadOlderCommand { get; }

    /// <summary>新对话草稿页选择工作区：仅改本地草稿预选，不调用 session/create。</summary>
    public RelayCommand<WorkspaceOptionViewModel> SelectWorkspaceCommand { get; }

    /// <summary>新对话草稿页选择模式：仅改本地草稿预选，不调用 session/create。</summary>
    public RelayCommand<AgentPresetOptionViewModel> SelectPresetCommand { get; }

    /// <summary>新对话草稿页的发送命令：按预选创建会话（含预选模型与模式），后端接受首条消息才进入普通会话。</summary>
    public AsyncRelayCommand SendDraftCommand { get; }

    /// <summary>工作区下拉按钮的展开/收起切换。</summary>
    public RelayCommand ToggleWorkspaceMenuCommand { get; }

    /// <summary>模式下拉按钮的展开/收起切换。</summary>
    public RelayCommand TogglePresetMenuCommand { get; }

    public RelayCommand<PendingApprovalViewModel> ApproveApprovalCommand { get; }

    public RelayCommand<PendingApprovalViewModel> RejectApprovalCommand { get; }

    /// <summary>打开应用内设置面板（侧栏齿轮入口）；面板内仍保留「打开配置文件」系统编辑器入口。</summary>
    public AsyncRelayCommand OpenSettingsCommand { get; }

    /// <summary>设置面板子视图模型：分区投影、即时写引擎与外部改动刷新。</summary>
    public SettingsPanelViewModel Settings { get; }

    /// <summary>设置面板是否打开（全窗口覆盖层的可见性）。</summary>
    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        private set => SetProperty(ref _isSettingsOpen, value);
    }

    /// <summary>新对话草稿页的工作区选项（含显式「不使用工作区」项）；随工作区投影刷新。</summary>
    public ObservableCollection<WorkspaceOptionViewModel> WorkspaceOptions { get; } = [];

    /// <summary>新对话草稿页的模式选项：dsh 内置四模式，固定集合；勾选态随草稿预选对齐。</summary>
    public IReadOnlyList<AgentPresetOptionViewModel> AgentPresetOptions { get; }

    /// <summary>模式下拉展开态（Popup 双向绑定）。</summary>
    public bool IsPresetMenuOpen
    {
        get => _isPresetMenuOpen;
        set => SetProperty(ref _isPresetMenuOpen, value);
    }

    /// <summary>模式下拉按钮文案：当前草稿预选模式的显示名。</summary>
    public string PresetPickerLabel =>
        AgentPresetOptions.FirstOrDefault(option => option.Id == _draftAgentPreset)?.Name
     ?? _draftAgentPreset ?? "选择模式";

    /// <summary>
    ///     下拉滚动区条目：真实工作区（与 <see cref="WorkspaceOptions" /> 同源同序，不含兼容项）。
    ///     兼容项固定渲染在面板底行，不进滚动列表。
    /// </summary>
    public ObservableCollection<WorkspaceOptionViewModel> WorkspaceMenuOptions { get; } = [];

    private List<WorkspaceOptionViewModel> _allWorkspaceMenuOptions = [];

    private string _workspaceSearchText = string.Empty;

    /// <summary>工作区下拉搜索词：输入即过滤滚动区（纯文本包含、忽略大小写），关闭面板时清空。</summary>
    public string WorkspaceSearchText
    {
        get => _workspaceSearchText;
        set
        {
            if (SetProperty(ref _workspaceSearchText, value)) RefillWorkspaceMenuOptions();
        }
    }

    private WorkspaceOptionViewModel? _withoutWorkspaceOption;

    /// <summary>下拉固定底行的「不使用工作区」兼容项；随工作区投影与集合一起重建。</summary>
    public WorkspaceOptionViewModel? WithoutWorkspaceOption
    {
        get => _withoutWorkspaceOption;
        private set => SetProperty(ref _withoutWorkspaceOption, value);
    }

    /// <summary>选中会话是否有待决审批（控制悬浮面板审批横幅区域）。</summary>
    public bool HasSessionPendingApprovals => SessionPendingApprovals.Count > 0;

    public SessionItemViewModel? SelectedSession
    {
        get => _selectedSession;
        set
        {
            var previous = _selectedSession;
            if (!SetProperty(ref _selectedSession, value)) return;
            // 用户导航递增代际：创建/连接流程的迟到结果据此让位，不抢回界面。
            Interlocked.Increment(ref _navigationGeneration);

            if (previous is not null) previous.PropertyChanged -= OnSelectedSessionPropertyChanged;

            if (value is not null) value.PropertyChanged += OnSelectedSessionPropertyChanged;

            // 先保存切出会话的草稿再装载新会话草稿（null 键为新对话草稿页）。
            SaveDraft(previous?.Id);

            // 先重置上一会话的会话级状态再订阅：新会话的当前选型由其快照携带
            // （模拟实现的快照可能同步到达，先启动订阅再清空会把快照值抹掉）。
            // SetSession 在会话身份变化时清除上一会话选型与统计并让下拉回退目录默认。
            // Sidebar 据此维护 IsCurrent 行高亮并重建分组投影（工作区头随选中变化）。
            // 切入无会话状态即进入新对话草稿页：Composer 切换到草稿目标（本地预选模型）。
            // 用户选中了某个会话：草稿页在途标记随之解除（回退选中守卫恢复常规行为）。
            Sidebar.ApplySelectedSession(value);
            if (value is not null) Sidebar.SetDraftPageActive(false);
            Composer.SetDraftTarget(value is null);
            // 权限选择器随选中目标切换：会话内以投影基线回流为准（快照或 control 帧提供）；
            // 进入草稿页时恢复该草稿的权限预选显示（与模型预选同一恢复路径）。
            PermissionSelector.SetDraftTarget(value is null);
            PermissionSelector.SetSession(value?.Id);
            if (value is null) PermissionSelector.ApplyDraftPreset(_draftPermissionPreset);
            // 装载草稿/会话文本属于既有内容的恢复（切走又回来是同一份草稿），不是用户新
            // 意图：guard 让版本不因导航往返递增，首发送的迟到结果仍能正确识别草稿身份。
            _isRestoringComposerDraft = true;
            try
            {
                Composer.SetSession(value?.Id, value?.Running ?? false, LoadDraft(value?.Id));
            }
            finally
            {
                _isRestoringComposerDraft = false;
            }

            if (value is null) Composer.ApplyCurrentModel(_draftModelSelection);
            _ = FollowSelectedSessionAsync(value);
            RebuildSessionPendingApprovals();
            RefreshConversationPhase();
            OnPropertyChanged(nameof(IsSessionRunning));
            LoadOlderCommand.RaiseCanExecuteChanged();
        }
    }

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public bool IsSimulationMode { get; }

    public string SimulationNotice => "模拟模式 · 未连接真实 Harness";

    public bool IsSessionRunning => SelectedSession?.Running ?? false;

    public bool HasMoreHistory
    {
        get => _hasMoreHistory;
        private set
        {
            if (SetProperty(ref _hasMoreHistory, value)) LoadOlderCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsLoadingOlder
    {
        get => _isLoadingOlder;
        private set
        {
            if (!SetProperty(ref _isLoadingOlder, value)) return;
            LoadOlderCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(LoadOlderText));
        }
    }

    /// <summary>「加载更早」按钮文案；加载中切换为进行时提示（对齐参考客户端的按钮分页）。</summary>
    public string LoadOlderText => IsLoadingOlder ? "加载中…" : "加载更早";

    public string BackendStatusText
    {
        get
        {
            return _backendHostService.Status switch
            {
                BackendStatus.Starting  => "后端启动中…",
                BackendStatus.Connected => IsSimulationMode ? "模拟后端已连接" : "Harness 后端已连接",
                BackendStatus.Error     => "后端错误",
                _                       => "后端未连接"
            };
        }
    }

    public string? BackendErrorDetail => _backendHostService.LastError;

    public bool HasBackendError => !string.IsNullOrWhiteSpace(BackendErrorDetail);

    public bool IsBackendConnected => _backendHostService.Status == BackendStatus.Connected;

    public bool IsBackendDisconnected => !IsBackendConnected;

    /// <summary>正在进行首发送编排（创建会话/应用模型/发送首条消息）；界面据此禁用发送并提示。</summary>
    public bool IsStartingConversation => _connectingCount > 0;

    /// <summary>
    ///     新对话草稿页是否可见：无选中会话时。页面只有一份进程内草稿（输入文本、预选
    ///     工作区与模型），不创建 Harness 会话、不显示会话 ID、不在侧栏产生空白行；
    ///     首发送被后端接受后才进入普通会话。未知状态与已开始的会话不显示本页。
    /// </summary>
    public bool ShowNewConversationPage => SelectedSession is null;

    /// <summary>工作区下拉展开态（Popup 双向绑定）。</summary>
    public bool IsWorkspaceMenuOpen
    {
        get => _isWorkspaceMenuOpen;
        set
        {
            if (!SetProperty(ref _isWorkspaceMenuOpen, value)) return;

            // 关闭面板即清空搜索并恢复完整列表：重开时不残留上次的过滤词。
            if (!value) WorkspaceSearchText = string.Empty;
        }
    }

    /// <summary>工作区下拉按钮文案：未选择时是明确的选择状态提示。</summary>
    public string WorkspacePickerLabel
    {
        get
        {
            if (_draftWithoutWorkspace) return "不使用工作区";
            if (_draftWorkspaceId is { } id)
                return WorkspaceOptions.FirstOrDefault(option => option.Id == id)?.TitleText
                    ?? _draftWorkspaceTitleFallback ?? id;
            return "选择工作区";
        }
    }

    /// <summary>草稿首发送是否可用：有文本、已选定工作区去向（含显式不使用）、已连接且不在编排中。</summary>
    public bool CanSendDraft => !IsStartingConversation                           &&
                                IsBackendConnected                                &&
                                !string.IsNullOrWhiteSpace(Composer.DraftMessage) &&
                                (_draftWithoutWorkspace || _draftWorkspaceId is not null);

    public async ValueTask DisposeAsync()
    {
        var cancellation = Interlocked.Exchange(ref _followCancellation, null);
        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }

        if (SelectedSession is not null) SelectedSession.PropertyChanged -= OnSelectedSessionPropertyChanged;

        Sidebar.Dispose();
        PermissionSelector.Dispose();
        _backendHostService.StatusChanged     -= OnBackendStatusChanged;
        _toolApprovalService.ApprovalsChanged -= OnApprovalsChanged;
        _workspaceService.WorkspacesChanged   -= OnWorkspacesChanged;
        _settingsService.DocumentUpdated      -= OnSettingsDocumentUpdated;
        _credentialsService.ReferenceUpdated  -= OnCredentialsReferenceUpdated;
        Settings.CloseRequested               -= OnSettingsCloseRequested;
    }

    /// <summary>把会话提升为已开始（发送被接受/观察到内容或运行的过渡信号），并刷新阶段界面。</summary>
    private void MarkCurrentSessionEngaged()
    {
        var sessionId = SelectedSession?.Id;
        if (sessionId is null) return;

        _sessionService.MarkSessionEngaged(sessionId);
        Sidebar.NotifySessionEngaged(sessionId);
        RefreshConversationPhase();
    }

    /// <summary>发送被接受（Composer 回调）：会话立即按"已开始"处理，空白选择器随隐藏。</summary>
    private void HandlePromptAccepted()
    {
        MarkCurrentSessionEngaged();
    }

    /// <summary>新对话草稿页状态变化后统一刷新派生属性。</summary>
    private void RefreshConversationPhase()
    {
        OnPropertyChanged(nameof(IsStartingConversation));
        OnPropertyChanged(nameof(ShowNewConversationPage));
        OnPropertyChanged(nameof(WorkspacePickerLabel));
        OnPropertyChanged(nameof(PresetPickerLabel));
        OnPropertyChanged(nameof(CanSendDraft));
        SendDraftCommand.RaiseCanExecuteChanged();
    }

    /// <summary>按会话保存草稿（空草稿清除记账）；仅 UI 线程调用。空串键为新对话草稿页。</summary>
    private void SaveDraft(string? sessionId)
    {
        var key = sessionId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(Composer.DraftMessage)) _drafts.Remove(key);
        else _drafts[key] = Composer.DraftMessage;
    }

    /// <summary>按会话读取草稿；无记账返回空串。</summary>
    private string LoadDraft(string? sessionId)
    {
        return _drafts.TryGetValue(sessionId ?? string.Empty, out var draft) ? draft : string.Empty;
    }

    /// <summary>呈现组合阶段发现的问题（例如真实后端配置缺失回退模拟）。</summary>
    public void ShowStartupNotice(string text)
    {
        ErrorText = text;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized) return;

        _isInitialized = true;
        try
        {
            // 启动即停留在新对话草稿页（构造期已进入草稿模式）：先立回退选中守卫再启动
            // 后端与刷新列表，初始化与后台事件触发的刷新都不会把草稿页抢成某个旧会话；
            // 用户选中会话时守卫随之解除（SelectedSession setter）。
            Sidebar.SetDraftPageActive(true);
            await _backendHostService.StartAsync(cancellationToken);
            // 工作区订阅先于会话列表启动：基线未到达时先按空投影分组，
            // WorkspacesChanged 事件到达后再重组（对齐参考客户端的 pending 表现）。
            await Sidebar.RefreshWorkspacesAsync(cancellationToken);
            await Sidebar.RefreshSessionsAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _isInitialized = false;
        }
        catch (Exception exception)
        {
            _isInitialized = false;
            ErrorText      = exception.Message;
        }

        // 模型目录独立加载：失败不阻塞会话列表，下拉保持禁用并提示原因。
        if (IsBackendConnected)
            try
            {
                await Composer.RefreshModelCatalogAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _isInitialized = false;
            }
            catch (Exception exception)
            {
                ErrorText = $"模型目录加载失败：{exception.Message}";
            }

        // 权限预设目录独立加载：失败只让权限选择器保持不可用态，不报窗口级错误、不影响聊天。
        if (IsBackendConnected) await PermissionSelector.ReloadCatalogSafeAsync(cancellationToken);

        await RefreshWorkspaceOptionsAsync();
    }

    /// <summary>草稿页输入文本变化：用户输入形成新的草稿版本（装载恢复除外），并刷新首发送命令可用性。</summary>
    private void OnComposerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ComposerViewModel.DraftMessage)) return;

        if (SelectedSession is null && !_isRestoringComposerDraft)
            Interlocked.Increment(ref _draftVersion);

        SendDraftCommand.RaiseCanExecuteChanged();
    }

    /// <summary>审批列表变化可能在任一线程到达：回到界面线程重建选中会话的待决投影。</summary>
    private void OnApprovalsChanged(object? sender, EventArgs e)
    {
        _postToUi(RebuildSessionPendingApprovals);
    }

    /// <summary>把待决审批投影为当前选中会话的横幅条目。</summary>
    private void RebuildSessionPendingApprovals()
    {
        SessionPendingApprovals.Clear();
        if (SelectedSession is not null)
            foreach (var approval in _toolApprovalService.Pending)
                if (approval.SessionId == SelectedSession.Id)
                    SessionPendingApprovals.Add(new PendingApprovalViewModel(approval,
                                                                             ApproveApprovalCommand,
                                                                             RejectApprovalCommand));

        OnPropertyChanged(nameof(HasSessionPendingApprovals));
    }

    /// <summary>回复审批；裁决结果由服务的 ApprovalsChanged 回流（移除条目）。</summary>
    private async Task RespondApprovalAsync(PendingApprovalViewModel? approval, bool allowed)
    {
        if (approval is null) return;

        try
        {
            await _toolApprovalService.RespondAsync(approval.EventId, allowed);
        }
        catch (Exception exception)
        {
            ErrorText = exception.Message;
        }
    }

    private async Task FollowSelectedSessionAsync(SessionItemViewModel? session)
    {
        var cancellation = BeginFollow();
        int epoch;
        lock (_followGate)
        {
            epoch = _followEpoch;
        }

        if (session is null)
        {
            ConversationItems.Clear();
            ResetStreamingMessage();
            _timelineEntries = [];
            _assembly        = CreateAssembly();
            ResetHistoryWindow();
            return;
        }

        try
        {
            await foreach (var update in _sessionService.FollowSessionAsync(session.Id, cancellation.Token))
                lock (_followGate)
                {
                    if (cancellation.IsCancellationRequested ||
                        epoch != _followEpoch                ||
                        !ReferenceEquals(SelectedSession, session))
                        return;

                    ApplySessionUpdate(update);
                }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ErrorText = exception.Message;
        }
        finally
        {
            CompleteFollow(cancellation);
        }
    }

    private void ApplySessionUpdate(SessionUpdate update)
    {
        switch (update)
        {
            case SessionUpdate.Snapshot snapshot :
                ResetStreamingMessage();
                _timelineEntries = [.. snapshot.Entries];
                // 真实 Host 会在快照尾部为开放中的轮合成 interrupted 边界（seq 即 cursor，
                // 持久日志中不存在）：丢弃它，让该轮保持开放，由后续真实 turn/end 收束；
                // 否则中途 attach/重连会把生成中的轮提前折旧，且真实边界到达后会二次折叠。
                if (_timelineEntries.Count > 0 && _timelineEntries[^1] is TurnBoundary { Reason: "interrupted" })
                    _timelineEntries.RemoveAt(_timelineEntries.Count - 1);

                RebuildTimeline();

                _historyThroughSeq = snapshot.Cursor;
                _windowStartSeq    = snapshot.WindowStartSeq;
                HasMoreHistory     = snapshot.HasMore;
                SelectedSession?.AdoptTitle(snapshot.Title);
                Composer.ApplyCurrentModel(snapshot.CurrentModel);
                // 快照携带已加载内容：作为"已参与对话"的过渡信号（回到界面线程执行）。
                if (snapshot.Entries.Count > 0) _postToUi(MarkCurrentSessionEngaged);
                break;

            case SessionUpdate.MessageAppended appended :
                if (appended.Message.Role == MessageRole.Assistant) RemoveStreamingMessage();

                _timelineEntries.Add(appended.Message);
                _assembly.Add(appended.Message);
                break;

            case SessionUpdate.TurnEnded ended :
                var boundary = new TurnBoundary(ended.Seq, ended.Turn, DateTimeOffset.Now, ended.Reason);
                _timelineEntries.Add(boundary);
                _assembly.Add(boundary);
                break;

            case SessionUpdate.ToolCallStarted started :
                // 流式气泡之后、提交消息之前的工具调用：直接按事件顺序追加。
                _timelineEntries.Add(started.Activity);
                _assembly.Add(started.Activity);
                break;

            case SessionUpdate.ToolCallSettled settled :
                ApplyToolSettled(settled.Activity);
                break;

            case SessionUpdate.TitleChanged title :
                SelectedSession?.AdoptTitle(title.Title);
                break;

            case SessionUpdate.ModelSelected selected :
                Composer.ApplyCurrentModel(selected.Selection);
                break;

            // 统计整值更新转发 Composer；乱序 seq 的 gating 由其持有（会话级状态）。
            case SessionUpdate.UsageUpdated usage :
                Composer.ApplyUsage(usage.Seq, usage.Usage);
                break;

            case SessionUpdate.StatsUpdated statsUpdate :
                Composer.ApplyStats(statsUpdate.Seq, statsUpdate.Stats);
                break;

            // 权限投影整值更新转发权限选择器（唯一权威来源；gating 同样在选择器内）。
            case SessionUpdate.PermissionsUpdated permissions :
                PermissionSelector.ApplyPermission(permissions.Seq, permissions.CurrentValue);
                break;

            case SessionUpdate.StreamStarted started :
                _streamingAttemptId = started.AttemptId;
                if (_streamingMessage is null)
                {
                    _streamingMessage = MessageItemViewModel.CreateStreaming();
                    ConversationItems.Add(_streamingMessage);
                }

                break;

            case SessionUpdate.StreamTextDelta delta when delta.AttemptId == _streamingAttemptId :
                _streamingMessage?.AppendText(delta.Text);
                break;

            case SessionUpdate.StreamEnded ended :
                // 用户取消通常以 committed 结算：interrupted 助手消息事件会到达并替换气泡。
                // abandoned 是无法落盘的错误路径，不会有正式消息，需就地标注避免气泡悬挂。
                if (ended.Outcome == StreamOutcomeKind.Abandoned)
                    _streamingMessage?.MarkInterrupted();
                else
                    _streamingMessage?.StopStreaming();

                break;
        }
    }

    private void ApplyToolSettled(ToolActivity settled)
    {
        if (_assembly.SettleTool(settled)) return;

        // 窗口起点落在调用中间（或恢复期repair合成）：没有发起事件也展示结果卡片。
        _timelineEntries.Add(settled);
        _assembly.Add(settled);
    }

    /// <summary>
    ///     翻页：页内条目前插进全量条目后整体重建时间线。参考客户端在历史未读全时
    ///     不折叠过程组（historyIncomplete），因此折叠状态随 HasMoreHistory 变化，
    ///     逐页拼接无法维护，统一以全量条目重建。
    /// </summary>
    private async Task LoadOlderAsync()
    {
        if (SelectedSession is null || IsLoadingOlder || !HasMoreHistory) return;

        IsLoadingOlder = true;
        ErrorText      = string.Empty;
        try
        {
            var followUps = 0;
            while (HasMoreHistory && followUps <= EmptyPageFollowUpLimit)
            {
                var page = await _sessionService.LoadOlderAsync(SelectedSession.Id, _historyThroughSeq,
                                                                _windowStartSeq);
                _windowStartSeq = page.WindowStartSeq;
                _timelineEntries.InsertRange(0, page.Entries);
                HasMoreHistory = page.HasMore;
                RebuildTimeline();

                if (page.Entries.Count > 0) break;

                // 整页都是被过滤的注入上下文：继续翻下一页直到出现可见条目。
                followUps++;
            }
        }
        catch (Exception exception)
        {
            ErrorText = exception.Message;
        }
        finally
        {
            IsLoadingOlder = false;
        }
    }

    /// <summary>打开应用内设置面板：每次打开都重新 describe 并全量投影（含主题应用）。</summary>
    private async Task OpenSettingsAsync()
    {
        IsSettingsOpen = true;
        await Settings.OpenAsync();
    }

    /// <summary>面板关闭请求（Esc/遮罩/关闭按钮）：收起覆盖层并停用面板的防抖刷新。</summary>
    private void OnSettingsCloseRequested(object? sender, EventArgs e)
    {
        IsSettingsOpen = false;
        Settings.NotifyClosed();
    }

    /// <summary>设置文档外部改动回流：root 订阅服务事件，编组转发给面板（面板不自行订阅）。</summary>
    private void OnSettingsDocumentUpdated(object? sender, SettingsDocumentUpdate e)
    {
        _postToUi(() => Settings.HandleDocumentUpdated(e));
    }

    /// <summary>凭据引用更新回流：编组转发给面板重查凭据状态。</summary>
    private void OnCredentialsReferenceUpdated(object? sender, EventArgs e)
    {
        _postToUi(() => _ = Settings.HandleReferenceUpdatedAsync());
    }

    /// <summary>从全量条目重建时间线；折叠资格逐轮判定（窗口内完整覆盖的轮次折叠）。</summary>
    private void RebuildTimeline()
    {
        ConversationItems.Clear();
        _assembly = CreateAssembly();
        foreach (var entry in _timelineEntries) _assembly.Add(entry);

        // 重建会丢掉流式气泡；生成中重新挂回尾部，等待正式消息事件替换。
        if (_streamingMessage is not null) ConversationItems.Add(_streamingMessage);
    }

    private TimelineAssembly CreateAssembly()
    {
        return new TimelineAssembly(ConversationItems);
    }

    private bool CanLoadOlder()
    {
        return SelectedSession is not null && HasMoreHistory && !IsLoadingOlder && IsBackendConnected;
    }

    private void ResetHistoryWindow()
    {
        _historyThroughSeq = 0;
        _windowStartSeq    = 1;
        HasMoreHistory     = false;
    }

    private CancellationTokenSource BeginFollow()
    {
        var cancellation = new CancellationTokenSource();
        var previous     = Interlocked.Exchange(ref _followCancellation, cancellation);
        if (previous is not null)
        {
            previous.Cancel();
            previous.Dispose();
        }

        // 代际门闩：新订阅开代。旧循环的「校验 + 应用」在同一把锁内原子进行，
        // 代际不符即退出——否则旧会话的迟到更新（如 stats 整值）会在新会话基线
        // 之后落盘，把新会话的统计串台成旧值且不再被修正。
        lock (_followGate)
        {
            _followEpoch++;
        }

        return cancellation;
    }

    private void CompleteFollow(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(Interlocked.CompareExchange(ref _followCancellation, null, cancellation),
                            cancellation))
            cancellation.Dispose();
    }

    private void RemoveStreamingMessage()
    {
        if (_streamingMessage is not null) ConversationItems.Remove(_streamingMessage);

        ResetStreamingMessage();
    }

    private void ResetStreamingMessage()
    {
        _streamingMessage   = null;
        _streamingAttemptId = null;
    }

    private void OnSelectedSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SessionItemViewModel.Running) :
                _postToUi(() =>
                {
                    OnPropertyChanged(nameof(IsSessionRunning));
                    Composer.SetSessionRunning(SelectedSession?.Running ?? false);
                    // 观察到运行中：作为"已参与对话"的过渡信号（对齐参考实现 status 运行降空白）。
                    if (SelectedSession?.Running == true) MarkCurrentSessionEngaged();
                });
                break;
            case nameof(SessionItemViewModel.BlankState) :
                _postToUi(RefreshConversationPhase);
                break;
        }
    }

    private void OnBackendStatusChanged(object? sender, EventArgs e)
    {
        _postToUi(() =>
        {
            OnPropertyChanged(nameof(BackendStatusText));
            OnPropertyChanged(nameof(BackendErrorDetail));
            OnPropertyChanged(nameof(HasBackendError));
            OnPropertyChanged(nameof(IsBackendConnected));
            OnPropertyChanged(nameof(IsBackendDisconnected));
            Composer.SetBackendConnected(IsBackendConnected);
            PermissionSelector.SetBackendConnected(IsBackendConnected);
            SendDraftCommand.RaiseCanExecuteChanged();
            LoadOlderCommand.RaiseCanExecuteChanged();
            if (IsBackendConnected)
            {
                // 重连后代目录可能变化，重新拉取（只读，可安全重试）。
                _ = Composer.RefreshModelCatalogSafeAsync();
                _ = PermissionSelector.ReloadCatalogSafeAsync();
            }
        });
    }


    private sealed class EmptyToolApprovalService : IToolApprovalService
    {
        public static EmptyToolApprovalService Instance { get; } = new();

        public event EventHandler? ApprovalsChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<PendingApproval> Pending => [];

        public Task RespondAsync(string eventId, bool allowed, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    /// <summary>未提供置顶注册表时的空实现：集合恒空，置顶请求无效果（旧构造调用的兼容路径）。</summary>
    private sealed class EmptySidebarPinService : ISidebarPinService
    {
        public static EmptySidebarPinService Instance { get; } = new();

        public event EventHandler? PinsChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<string> PinnedSessionIds { get; } = [];

        public IReadOnlyList<string> PinnedWorkspaceIds { get; } = [];

        public Task PinSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task UnpinSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task PinWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task UnpinWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    /// <summary>未提供权限预设服务时的空实现：目录为空（选择器保持不可用态），切换请求无效果。</summary>
    private sealed class EmptyPermissionPresetService : IPermissionPresetService
    {
        public static EmptyPermissionPresetService Instance { get; } = new();

        private static readonly PermissionCatalog Catalog = new([], PermissionPresetValues.WorkspaceWrite);

        public event EventHandler? CatalogChanged
        {
            add { }
            remove { }
        }

        public Task<PermissionCatalog> GetCatalogAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Catalog);
        }

        public Task<bool> SwitchPresetAsync(string            sessionId, string preset,
                                            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(false);
        }
    }

    /// <summary>无设置来源时的降级实现：只读/写入一律拒绝，避免测试构造被迫组装基础设施实现。</summary>
    private sealed class EmptySettingsService : ISettingsService
    {
        public static EmptySettingsService Instance { get; } = new();

        public event EventHandler<SettingsDocumentUpdate>? DocumentUpdated
        {
            add { }
            remove { }
        }

        public Task<SettingsDescribeValue> DescribeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotSupportedException("当前上下文没有设置服务。");
        }

        public Task<SettingsNamespaceView> UpdateAsync(string ns, JsonElement patch, long? expectedRevision = null,
                                                       CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotSupportedException("当前上下文没有设置服务。");
        }

        public Task<SettingsNamespaceView> ReplaceAsync(
            string            ns, JsonElement section, long? expectedRevision = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotSupportedException("当前上下文没有设置服务。");
        }

        public Task<SettingsNamespaceView> MutateAsync(
            string            ns, IReadOnlyList<SettingsMutationOp> ops, long? expectedRevision = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotSupportedException("当前上下文没有设置服务。");
        }

        public Task OpenSettingsDocumentAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotSupportedException("当前上下文没有设置服务。");
        }
    }

    /// <summary>未提供凭据服务时的空实现：状态查询恒为空（密码圆点隐藏），写入无效果。</summary>
    private sealed class EmptyCredentialsService : ICredentialsService
    {
        public static EmptyCredentialsService Instance { get; } = new();

        public event EventHandler? ReferenceUpdated
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyDictionary<string, CredentialStatus>> DescribeAsync(
            IReadOnlyList<string> references, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyDictionary<string, CredentialStatus> empty = new Dictionary<string, CredentialStatus>();
            return Task.FromResult(empty);
        }

        public Task SetAsync(string reference, string value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task UnsetAsync(string reference, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    #region 新对话草稿页与首发送编排

    private int _connectingCount;

    /// <summary>
    ///     新建入口的统一编排（顶部新建与工作区组头「+」共用）：进入新对话草稿页。
    ///     workspaceId 非空（组头「+」）时把预选工作区改为对应工作区；为 null（顶部新建）
    ///     时恢复同一份进程内草稿（输入文本、预选工作区与模型）。本入口不调用
    ///     session/create、不收养空白会话、不绑定工作区——全部推迟到首发送。
    /// </summary>
    private Task RequestNewSessionAsync(string? workspaceId)
    {
        if (workspaceId is not null)
            SetDraftWorkspace(workspaceId, ResolveWorkspaceTitle(workspaceId), false);

        EnterNewConversationPage();
        return Task.CompletedTask;
    }

    /// <summary>
    ///     进入新对话草稿页：保存当前会话草稿并装载草稿页（SelectedSession setter 编排）。
    ///     主动草稿页在途标记随后由回退选中守卫消费，防止并发列表刷新把页面抢回旧会话。
    /// </summary>
    private void EnterNewConversationPage()
    {
        Sidebar.SetDraftPageActive(true);
        SelectedSession     = null;
        IsWorkspaceMenuOpen = false;
        IsPresetMenuOpen    = false;
        RefreshConversationPhase();
        RefreshWorkspaceSelectionMarks();
    }

    /// <summary>草稿页下拉点选工作区（含「不使用工作区」兼容项）：只改本地草稿预选。</summary>
    private void SelectDraftWorkspace(WorkspaceOptionViewModel? option)
    {
        if (option is null) return;

        SetDraftWorkspace(option.Id, option.TitleText, option.IsWithoutWorkspace);
        IsWorkspaceMenuOpen = false;
        RefreshWorkspaceSelectionMarks();
    }

    private void ToggleWorkspaceMenu()
    {
        IsWorkspaceMenuOpen = !IsWorkspaceMenuOpen;
    }

    /// <summary>草稿页下拉点选模式：只改本地草稿预选；会话开始后模式由后端锁定。</summary>
    private void SelectDraftPreset(AgentPresetOptionViewModel? option)
    {
        if (option is null) return;

        SetDraftPreset(option.Id);
        IsPresetMenuOpen = false;
        RefreshPresetSelectionMarks();
    }

    private void TogglePresetMenu()
    {
        IsPresetMenuOpen = !IsPresetMenuOpen;
    }

    /// <summary>更新草稿的预选模式（内置 wire id）。模式是草稿内容的一部分：改选即形成新的草稿版本。</summary>
    private void SetDraftPreset(string presetId)
    {
        if (presetId == _draftAgentPreset) return;

        _draftAgentPreset = presetId;
        Interlocked.Increment(ref _draftVersion);
        OnPropertyChanged(nameof(PresetPickerLabel));
    }

    /// <summary>按下拉勾选态对齐草稿预选模式。</summary>
    private void RefreshPresetSelectionMarks()
    {
        foreach (var option in AgentPresetOptions)
            option.IsSelected = option.Id == _draftAgentPreset;
    }

    /// <summary>
    ///     更新草稿的预选工作区去向。待复用的已创建会话连同目标一起记账：目标改选后
    ///     自动失配失效（其 cwd 已在创建时固定），重试按新选择创建，不会把消息送进
    ///     旧工作区会话。
    /// </summary>
    private void SetDraftWorkspace(string? workspaceId, string? title, bool withoutWorkspace)
    {
        if (workspaceId == _draftWorkspaceId && withoutWorkspace == _draftWithoutWorkspace) return;

        _draftWorkspaceId            = workspaceId;
        _draftWorkspaceTitleFallback = title;
        _draftWithoutWorkspace       = withoutWorkspace;
        // 工作区去向是草稿内容的一部分：改选即形成新的草稿版本。
        Interlocked.Increment(ref _draftVersion);
        OnPropertyChanged(nameof(WorkspacePickerLabel));
        OnPropertyChanged(nameof(CanSendDraft));
        SendDraftCommand.RaiseCanExecuteChanged();
    }

    /// <summary>预选工作区标题：优先取已加载的下拉选项，未加载时回退「工作区」占位。</summary>
    private string? ResolveWorkspaceTitle(string workspaceId)
    {
        return WorkspaceOptions.FirstOrDefault(option => option.Id == workspaceId)?.TitleText ?? "工作区";
    }

    /// <summary>按下拉勾选态对齐草稿预选（含「不使用工作区」项）。</summary>
    private void RefreshWorkspaceSelectionMarks()
    {
        foreach (var option in WorkspaceOptions)
            option.IsSelected = _draftWithoutWorkspace
                ? option.IsWithoutWorkspace
                : option.Id is not null && option.Id == _draftWorkspaceId;
    }

    /// <summary>
    ///     首发送编排：按预选创建会话（显式「不使用工作区」时不归属），必要时应用预选
    ///     模型/档位，再发送首条消息。后端接受首条消息后才进入普通会话并展示侧栏行。
    ///     发送发起时对文本、目标与草稿版本做快照，全程按快照推进；await 期间用户改选
    ///     目标、改写文本或改选模型都会递增草稿版本——旧发送只消费自己的快照：迟到的
    ///     会话不记为待复用、不抢回界面、不清空新草稿。创建已成功而后续失败时记录待复用
    ///     会话（连同目标），重试跳过创建；其中工作区关联未完成的（attach 失败）重试先用
    ///     同一 sessionId + 目标 workspaceId 走 session/create 收养路径恢复关联，恢复成功
    ///     才继续选型与发送。发送一次一条，不自动换新 requestId 重发（结果不明确的重试
    ///     交给用户决定）。
    /// </summary>
    private async Task SendDraftAsync()
    {
        if (_isDraftSendInFlight || SelectedSession is not null) return;

        var textAtSend            = Composer.DraftMessage;
        var content               = textAtSend.Trim();
        var generationAtStart     = Volatile.Read(ref _navigationGeneration);
        var versionAtSend         = Volatile.Read(ref _draftVersion);
        var withoutWorkspace      = _draftWithoutWorkspace;
        var workspaceId           = withoutWorkspace ? null : _draftWorkspaceId;
        var presetAtSend          = _draftAgentPreset;
        var preselectedModel      = _draftModelSelection;
        var preselectedPermission = _draftPermissionPreset;
        _isDraftSendInFlight = true;
        BeginConnecting();
        try
        {
            ErrorText = string.Empty;
            string sessionId;
            if (_draftPendingSession is { } pending          &&
                pending.WithoutWorkspace == withoutWorkspace &&
                pending.WorkspaceId      == workspaceId      &&
                pending.AgentPreset      == presetAtSend)
            {
                sessionId = pending.SessionId;
                if (!pending.AttachCompleted)
                {
                    // 会话已创建但工作区关联未完成：用同一个 sessionId + 目标 workspaceId
                    // 再走一次 session/create 收养（协议中唯一的关联写入接口），携带与创建
                    // 时一致的模式（收养按 cwd/preset 校验冲突），恢复成功后才继续选型与
                    // 发送；再次失败由下方 catch 保留可恢复记账。
                    await _sessionService.CreateSessionAsync(workspaceId, sessionId, presetAtSend);
                    _draftPendingSession = (sessionId, workspaceId, withoutWorkspace, presetAtSend, true);
                }
            }
            else
            {
                if (workspaceId is not null)
                {
                    var workspaces = await _workspaceService.GetWorkspacesAsync();
                    if (workspaces.All(workspace => workspace.Id != workspaceId))
                        throw new InvalidOperationException($"工作区不可用或已移除：{workspaceId}");
                }

                sessionId = (await _sessionService.CreateSessionAsync(workspaceId, null, presetAtSend)).Id;
                // 草稿目标在创建期间被改选时，这个迟到会话不记为待复用（其 cwd 已固定），
                // 重试按新目标创建；确认空白会话按既有规则在目录中隐藏。
                if (IsDraftTarget(workspaceId, withoutWorkspace))
                    _draftPendingSession = (sessionId, workspaceId, withoutWorkspace, presetAtSend, true);
            }

            if (preselectedModel != null)
                await _sessionService.SelectModelAsync(sessionId, preselectedModel.Provider, preselectedModel.Model,
                                                       preselectedModel.ReasoningEffort);

            if (preselectedPermission is { } preselectedPreset)
                // 草稿页预选的权限预设：会话创建参数不携带权限，在首条消息前经 /permission 应用，
                // 让首轮即按预选执行；重复应用同值无副作用（待复用重试路径安全）。宿主无该命令
                // （matched=false）时不阻塞发送——会话实际生效值由投影回流展示。
                await _permissionPresetService.SwitchPresetAsync(sessionId, preselectedPreset);

            await _sessionService.SendPromptAsync(sessionId, Guid.NewGuid().ToString(), content);

            // 后端已接受首条消息：核对发送上下文（未导航、草稿版本未变）并收束快照。
            var sameContext = Volatile.Read(ref _navigationGeneration) == generationAtStart &&
                              Volatile.Read(ref _draftVersion)         == versionAtSend;
            ConsumeNewConversationDraft(versionAtSend);
            _sessionService.MarkSessionEngaged(sessionId);
            var row = Sidebar.AddSessionRow(new SessionSummary(sessionId, null, DateTimeOffset.Now, false,
                                                               SessionBlankState.Engaged));
            // 新会话的工作区记账可能晚于行插入到达：重读工作区投影，让行落入正确分组；
            // 刷新失败不阻塞发送完成，后续 WorkspacesChanged 仍会触发重组。
            try
            {
                await Sidebar.RefreshWorkspacesAsync(CancellationToken.None);
            }
            catch
            {
                // 工作区投影只读刷新；权威记账仍以后端状态流为准。
            }

            // 用户仍停留在这条发送的上下文（未导航、未改写草稿）：进入普通会话；
            // 否则只让行照常出现，不抢回界面。
            if (sameContext) SelectedSession = row;
        }
        catch (HarnessRpcException exception) when (exception.Code == "session/workspace-attach-failed" &&
                                                    exception.FindDetailString("sessionId") is { } attachedId)
        {
            // 会话本体已创建、工作区关联失败：按快照目标记账待复用（关联未完成，重试先
            // 恢复关联；改选目标或模式后失配失效），如实报告失败阶段，不重复创建。
            _draftPendingSession = (attachedId, workspaceId, withoutWorkspace, presetAtSend, false);
            ErrorText            = "会话已创建，但工作区关联失败；重试将恢复关联并复用该会话，或改选工作区。";
        }
        catch (Exception exception)
        {
            ErrorText = exception.Message;
        }
        finally
        {
            _isDraftSendInFlight = false;
            EndConnecting();
        }
    }

    /// <summary>草稿当前目标是否仍等于某次发送的快照（用户未改选工作区去向）。</summary>
    private bool IsDraftTarget(string? sentWorkspaceId, bool sentWithoutWorkspace)
    {
        return _draftWithoutWorkspace == sentWithoutWorkspace && _draftWorkspaceId == sentWorkspaceId;
    }

    /// <summary>草稿页本地预选模型/档位（Composer 回调，无 RPC）：记入草稿并形成新的草稿版本，会话创建后应用。</summary>
    private void OnDraftModelChanged(ModelSelection selection)
    {
        _draftModelSelection = selection;
        Interlocked.Increment(ref _draftVersion);
    }

    /// <summary>草稿页本地预选权限预设（权限选择器回调，无 RPC）：记入草稿并形成新的草稿版本；会话创建后、首条消息前经 /permission 应用。</summary>
    private void OnDraftPermissionChanged(string preset)
    {
        _draftPermissionPreset = preset;
        Interlocked.Increment(ref _draftVersion);
    }

    /// <summary>
    ///     首发送被接受后收束旧发送的快照。待复用记账无条件作废：会话已进入正常流程，
    ///     不再适用失败重试语义。草稿自快照以来未变（版本一致）时整份消费：移除草稿
    ///     缓存，当前仍显示草稿页则同步清空输入框（版本未变时显示的文本必然属于本草稿，
    ///     无需字符串比较），并重置工作区与模型预选；版本已变（文本、工作区或模型任一
    ///     被改动）说明用户已形成新草稿，文字与相关选择整份保留，也不触碰当前会话的
    ///     输入框——其文本可能恰好与快照相同，但属于该会话自己的草稿。
    /// </summary>
    private void ConsumeNewConversationDraft(int versionAtSend)
    {
        _draftPendingSession = null;

        if (Volatile.Read(ref _draftVersion) == versionAtSend)
        {
            _drafts.Remove(string.Empty);
            if (SelectedSession is null) Composer.DraftMessage = string.Empty;

            _draftModelSelection   = null;
            _draftPermissionPreset = null;

            _draftAgentPreset            = AgentPresetModes.Default;
            _draftWorkspaceId            = null;
            _draftWorkspaceTitleFallback = null;
            _draftWithoutWorkspace       = false;
        }

        RefreshConversationPhase();
        RefreshWorkspaceSelectionMarks();
        RefreshPresetSelectionMarks();
        // 仍停留草稿页（未导航进新会话）时，预选清空后选择器回退目录默认显示。
        if (SelectedSession is null) PermissionSelector.ApplyDraftPreset(_draftPermissionPreset);
    }

    /// <summary>首发送编排期间合并连点（按钮禁用之外的第二道防线）。</summary>
    private void BeginConnecting()
    {
        if (Interlocked.Increment(ref _connectingCount) == 1) RefreshConversationPhase();
    }

    private void EndConnecting()
    {
        if (Interlocked.Decrement(ref _connectingCount) == 0) RefreshConversationPhase();
    }

    /// <summary>
    ///     重建新对话草稿页的工作区下拉选项（含显式「不使用工作区」兼容项），并按当前
    ///     草稿预选恢复勾选。预选工作区已从后端移除时保留最近标题，发送前会再校验可用性。
    /// </summary>
    private async Task RefreshWorkspaceOptionsAsync()
    {
        IReadOnlyList<WorkspaceSummary> workspaces;
        try
        {
            workspaces = await _workspaceService.GetWorkspacesAsync();
        }
        catch (Exception)
        {
            // 选项刷新失败保持现状；投影失败已在工作区服务内降级处理。
            return;
        }

        if (_draftWorkspaceId is { } selected && !_draftWithoutWorkspace)
            _draftWorkspaceTitleFallback =
                workspaces.FirstOrDefault(workspace => workspace.Id == selected)?.Title;

        WorkspaceOptions.Clear();
        _allWorkspaceMenuOptions = workspaces
                                  .Select(workspace =>
                                              WorkspaceOptionViewModel.CreateWorkspace(workspace,
                                                  SelectWorkspaceCommand))
                                  .ToList();
        foreach (var option in _allWorkspaceMenuOptions) WorkspaceOptions.Add(option);

        var withoutWorkspace = WorkspaceOptionViewModel.CreateWithoutWorkspace(SelectWorkspaceCommand);
        WorkspaceOptions.Add(withoutWorkspace);
        WithoutWorkspaceOption = withoutWorkspace;
        RefillWorkspaceMenuOptions();
        RefreshWorkspaceSelectionMarks();
        RefreshConversationPhase();
    }

    /// <summary>按当前搜索词重建下拉滚动区条目：纯文本包含、忽略大小写；空词即全量。</summary>
    private void RefillWorkspaceMenuOptions()
    {
        var keyword = _workspaceSearchText.Trim();
        WorkspaceMenuOptions.Clear();
        foreach (var option in _allWorkspaceMenuOptions)
            if (keyword.Length == 0 ||
                option.TitleText.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                WorkspaceMenuOptions.Add(option);
    }

    private void OnWorkspacesChanged(object? sender, EventArgs e)
    {
        _postToUi(() => _ = RefreshWorkspaceOptionsAsync());
    }

    /// <summary>
    ///     登记选中的文件夹为工作区（侧栏与草稿页下拉两个添加入口共用，路径由视图的
    ///     文件夹对话框取得）。真实与模拟服务都会经工作区状态流回流投影，下拉选项与
    ///     侧栏分组随之刷新；失败呈现到窗口级错误条，成功不额外动作。
    /// </summary>
    public async Task RegisterWorkspaceAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (Interlocked.Exchange(ref _registeringWorkspace, 1) == 1) return;

        try
        {
            await _workspaceService.RegisterWorkspaceAsync(path.Trim());
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ReportWorkspaceError(exception);
        }
        finally
        {
            Interlocked.Exchange(ref _registeringWorkspace, 0);
        }
    }

    internal void ReportWorkspaceError(Exception exception)
    {
        ErrorText = $"添加工作区失败：{exception.Message}";
    }

    /// <summary>
    ///     侧栏工作区菜单的重命名请求（回调注入 Sidebar，业务错误由 Sidebar 呈现在
    ///     重命名弹窗内，不经窗口级错误条）；成功后投影经工作区状态流回流刷新。
    /// </summary>
    private Task RenameWorkspaceAsync(string workspaceId, string title) =>
        _workspaceService.RenameWorkspaceAsync(workspaceId, title);

    /// <summary>侧栏工作区菜单的删除请求（语义同上：只删注册，会话由后端记账回到「未分组」）。</summary>
    private Task DeleteWorkspaceAsync(string workspaceId) => _workspaceService.DeleteWorkspaceAsync(workspaceId);

    #endregion
}
