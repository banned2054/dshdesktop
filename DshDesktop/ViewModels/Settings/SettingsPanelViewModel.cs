using DshDesktop.Core.Exceptions;
using DshDesktop.Core.Models;
using DshDesktop.Core.Services;
using System.Text.Json;

namespace DshDesktop.ViewModels.Settings;

/// <summary>
///     设置面板壳：describe 快照持有者、分区导航、加载/错误态、即时写引擎（每 ns 串行队列、
///     同行排队写合并为最新值）、外部 DocumentUpdated 的防抖重投影与主题偏好回调。
///     通用行写失败（含冲突）一律全量重读重投影（无乐观更新）；模型/插件卡经
///     <see cref="ISettingsMutationRunner" /> 自行提交并处理冲突文案。
/// </summary>
public sealed class SettingsPanelViewModel : ObservableObject, ISettingsMutationRunner
{
    private const string ThemeNs = "ui-theme";

    /// <summary>外部改动防抖窗口：写入回流与真实外部更新合并为一次重投影。</summary>
    private const int ExternalRefreshDelayMilliseconds = 300;

    private readonly Action<string?>      _applyThemePreference;
    private readonly ICredentialsService? _credentialsService;
    private readonly ILlmCatalogService?  _llmCatalogService;
    private readonly Action<Action>       _postToUi;

    private readonly Dictionary<string, NamespaceWriteQueue> _queues = [];

    private readonly ISessionService? _sessionService;
    private readonly ISettingsService _settingsService;
    private readonly Lock             _stateSync = new();

    private SettingsSectionViewModel? _activeSection;

    private IReadOnlyDictionary<string, CredentialStatus> _credentialStatuses =
        new Dictionary<string, CredentialStatus>();

    private SettingsDescribeValue? _describe;

    private string  _errorText = string.Empty;
    private bool    _hasError;
    private bool    _isLoading = true;
    private bool    _isOpen;
    private bool    _isReadOnly;
    private string? _openDocumentError;

    private IReadOnlyList<LlmConfigurableProvider>? _providerDirectory;

    private CancellationTokenSource? _refreshDebounce;

    private Dictionary<string, long> _revisions = [];

    public SettingsPanelViewModel(ISettingsService    settingsService,      ICredentialsService? credentialsService,
                                  Action<string?>     applyThemePreference, Action<Action>?      postToUi = null,
                                  ISessionService?    sessionService    = null,
                                  ILlmCatalogService? llmCatalogService = null)
    {
        _settingsService      = settingsService;
        _credentialsService   = credentialsService;
        _sessionService       = sessionService;
        _llmCatalogService    = llmCatalogService;
        _applyThemePreference = applyThemePreference;
        _postToUi             = postToUi ?? (action => action());
        General               = new GeneralSettingsSectionViewModel();
        Models                = new ModelsSettingsSectionViewModel(this);
        Models.SetCatalogService(llmCatalogService);
        Plugins  = new PluginsSettingsSectionViewModel(this);
        Sections = [General, Models, Plugins];
        foreach (var row in General.Rows) row.WriteSink = WriteRowAsync;

        foreach (var section in Sections) section.SelectCommand = new RelayCommand(() => SelectSection(section));

        _activeSection              = General;
        General.IsSelected          = true;
        General.IsActive            = true;
        CloseCommand                = new RelayCommand(RequestClose);
        RetryCommand                = new AsyncRelayCommand(() => LoadAsync(CancellationToken.None));
        OpenSettingsDocumentCommand = new AsyncRelayCommand(OpenSettingsDocumentAsync);
    }

    public GeneralSettingsSectionViewModel General { get; }

    public ModelsSettingsSectionViewModel Models { get; }

    public PluginsSettingsSectionViewModel Plugins { get; }

    public IReadOnlyList<SettingsSectionViewModel> Sections { get; }

    public SettingsSectionViewModel ActiveSection
    {
        get => _activeSection ?? General;
        private set
        {
            if (!SetProperty(ref _activeSection, value)) return;

            foreach (var section in Sections)
            {
                section.IsSelected = ReferenceEquals(section, value);
                section.IsActive   = ReferenceEquals(section, value);
            }
        }
    }

    public RelayCommand CloseCommand { get; }

    public AsyncRelayCommand RetryCommand { get; }

    /// <summary>打开配置文件：经后端物化 patch 文档并调起系统编辑器；失败在按钮旁内联呈现。</summary>
    public AsyncRelayCommand OpenSettingsDocumentCommand { get; }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (!SetProperty(ref _isLoading, value)) return;
            OnPropertyChanged(nameof(IsReady));
            OnPropertyChanged(nameof(ShowReadOnlyBanner));
        }
    }

    public bool HasError
    {
        get => _hasError;
        private set
        {
            if (!SetProperty(ref _hasError, value)) return;
            OnPropertyChanged(nameof(IsReady));
            OnPropertyChanged(nameof(ShowReadOnlyBanner));
        }
    }

    public bool IsReady => !IsLoading && !HasError;

    public string ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    /// <summary>后端报告设置只读：顶部黄条提示并禁用全部写控件。</summary>
    public bool IsReadOnly
    {
        get => _isReadOnly;
        private set
        {
            if (SetProperty(ref _isReadOnly, value)) OnPropertyChanged(nameof(ShowReadOnlyBanner));
        }
    }

    public bool ShowReadOnlyBanner => IsReadOnly && IsReady;

    public string? OpenDocumentError
    {
        get => _openDocumentError;
        private set
        {
            if (SetProperty(ref _openDocumentError, value)) OnPropertyChanged(nameof(HasOpenDocumentError));
        }
    }

    public bool HasOpenDocumentError => !string.IsNullOrEmpty(OpenDocumentError);

    /// <summary>关闭请求（面板 UI / Esc / 遮罩点击触发）；由宿主把 IsSettingsOpen 置回 false。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>打开面板：每次都重新 DescribeAsync、全量投影、凭据批量查询与应用主题。</summary>
    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        _isOpen = true;
        CancelDebounce();
        await LoadAsync(cancellationToken);
    }

    /// <summary>面板关闭通知：停止挂起的防抖刷新；在途写自然完成并接受结果（忽略 UI 更新）。</summary>
    internal void NotifyClosed()
    {
        _isOpen = false;
        CancelDebounce();
    }

    /// <summary>
    ///     DocumentUpdated 转发（由宿主订阅服务事件后编组调用）：面板打开时防抖 300ms 后全量重投影
    ///     （保草稿：通用行重投影、模型/插件卡只更新基线 Revision 与覆盖标记）。
    /// </summary>
    public void HandleDocumentUpdated(SettingsDocumentUpdate update)
    {
        if (!_isOpen) return;

        CancelDebounce();
        var cancellation = new CancellationTokenSource();
        _refreshDebounce = cancellation;
        _                = DelayedExternalRefreshAsync(cancellation.Token);
    }

    /// <summary>凭据引用更新转发（由宿主编组调用）：重查面板内全部引用并刷新圆点。</summary>
    public Task HandleReferenceUpdatedAsync()
    {
        return RefreshCredentialStatusesAsync();
    }

    /// <summary>导航切换分区：丢弃模型/插件卡草稿（从当前 describe 快照重投影）。</summary>
    public void SelectSection(SettingsSectionViewModel section)
    {
        if (ReferenceEquals(ActiveSection, section)) return;

        ActiveSection = section;
        ProjectFromSnapshot(false);
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        IsLoading = true;
        HasError  = false;
        try
        {
            var describe = await _settingsService.DescribeAsync(cancellationToken);
            // 提供方目录先行（失败降级 null，行投影走设置文档兜底）；随后整体编组到界面线程：
            // 投影、只读降级与主题回调同批落地，避免半新半旧。
            _providerDirectory = await TryLoadProviderDirectoryAsync(cancellationToken);
            _postToUi(() =>
            {
                AcceptDescribe(describe);
                IsReadOnly = !describe.Writable;
                ProjectFromSnapshot(false);
                ApplyThemeFromSnapshot();
                IsLoading = false;
            });
            await RefreshCredentialStatusesAsync(cancellationToken);
            await RefreshModelCatalogAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _postToUi(() => IsLoading = false);
        }
        catch (Exception exception)
        {
            _postToUi(() =>
            {
                ErrorText = $"设置加载失败：{exception.Message}";
                HasError  = true;
                IsLoading = false;
            });
        }
    }

    private async Task DelayedExternalRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ExternalRefreshDelayMilliseconds, cancellationToken);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (!_isOpen) return;

        _postToUi(() => _ = RefreshExternalAsync());
    }

    private async Task RefreshExternalAsync()
    {
        if (!_isOpen) return;

        try
        {
            var describe = await _settingsService.DescribeAsync();
            AcceptDescribe(describe);
            _postToUi(() => ProjectFromSnapshot(true));
            _ = RefreshCredentialStatusesAsync();
        }
        catch (Exception)
        {
            // 外部刷新失败：保留当前投影，不把面板切到错误态。
        }
    }

    private async Task OpenSettingsDocumentAsync()
    {
        OpenDocumentError = null;
        try
        {
            await _settingsService.OpenSettingsDocumentAsync();
        }
        catch (Exception)
        {
            OpenDocumentError = "打开配置文件失败";
        }
    }

    private void RequestClose()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CancelDebounce()
    {
        _refreshDebounce?.Cancel();
        _refreshDebounce?.Dispose();
        _refreshDebounce = null;
    }

    private void AcceptDescribe(SettingsDescribeValue describe)
    {
        lock (_stateSync)
        {
            _describe  = describe;
            _revisions = describe.Namespaces.ToDictionary(view => view.Ns, view => view.Revision);
        }
    }

    /// <summary>接受一次写回视图：更新 revision 与快照中该 ns 的条目（供取消/切换分区的重投影）。</summary>
    private void AcceptNamespaceView(SettingsNamespaceView view)
    {
        lock (_stateSync)
        {
            _revisions[view.Ns] = view.Revision;
            if (_describe is not null)
                _describe = _describe with
                {
                    Namespaces = _describe.Namespaces.Select(candidate => candidate.Ns == view.Ns ? view : candidate)
                                          .ToArray()
                };
        }
    }

    private void ProjectFromSnapshot(bool preserveCardDrafts)
    {
        SettingsDescribeValue? describe;
        lock (_stateSync)
        {
            describe = _describe;
        }

        if (describe is null) return;

        var map     = describe.Namespaces.ToDictionary(view => view.Ns);
        var canEdit = !IsReadOnly;

        foreach (var row in General.Rows) row.CanEdit = canEdit;

        General.Project(map);
        Models.SetCanEdit(canEdit);
        Plugins.SetCanEdit(canEdit);
        if (preserveCardDrafts)
        {
            Models.Rebase(map);
            Plugins.Rebase(map);
        }
        else
        {
            Models.Project(map, _providerDirectory);
            Plugins.Project(map, _credentialStatuses);
        }
    }

    private async Task RefreshCredentialStatusesAsync(CancellationToken cancellationToken = default)
    {
        if (_credentialsService is null) return;

        var references = CollectCredentialReferences();
        if (references.Count == 0)
        {
            _credentialStatuses = new Dictionary<string, CredentialStatus>();
            return;
        }

        try
        {
            var statuses = await _credentialsService.DescribeAsync(references, cancellationToken);
            _credentialStatuses = statuses;
            _postToUi(() => Plugins.UpdateCredentialStatuses(statuses));
        }
        catch (Exception)
        {
            // 凭据状态查询失败：保持当前圆点状态。
        }
    }

    /// <summary>拉取可配置提供方目录（添加下拉与行显示名来源）；失败降级 null，不把面板切到错误态。</summary>
    private async Task<IReadOnlyList<LlmConfigurableProvider>?> TryLoadProviderDirectoryAsync(
        CancellationToken cancellationToken)
    {
        if (_llmCatalogService is null) return null;

        try
        {
            return await _llmCatalogService.GetConfigurableProvidersAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    ///     拉取模型目录（与 composer 模型菜单同源）并投影到模型分区（账户行可见性 + 只读目录）；
    ///     无会话服务、加载被取消或失败时区块保持/置为空，不影响面板错误态。
    /// </summary>
    private async Task RefreshModelCatalogAsync(CancellationToken cancellationToken)
    {
        if (_sessionService is null) return;

        try
        {
            var catalog = await _sessionService.GetModelCatalogAsync(cancellationToken);
            _postToUi(() => Models.SetCatalog(catalog));
        }
        catch (OperationCanceledException)
        {
            // 目录加载被取消（面板关闭/重试覆盖）：保持现状。
        }
        catch (Exception)
        {
            // 目录查询失败：区块置空（视为无数据），不把面板切到错误态。
            _postToUi(() => Models.SetCatalog(null));
        }
    }

    private List<string> CollectCredentialReferences()
    {
        SettingsDescribeValue? describe;
        lock (_stateSync)
        {
            describe = _describe;
        }

        var references = new List<string>();
        if (describe is not null)
            references.AddRange(from view in describe.Namespaces
                                where view.Ns == "web-search-deepseek"
                                select SettingsValues.GetString(view.Value, ["apiKeyEnv"])
                                into reference
                                select reference is { Length: > 0 } ? reference : "DEEPSEEK_API_KEY");

        return references.Distinct().ToList();
    }

    private void ApplyThemeFromSnapshot()
    {
        SettingsDescribeValue? describe;
        lock (_stateSync)
        {
            describe = _describe;
        }

        var view = describe?.Namespaces.FirstOrDefault(candidate => candidate.Ns == ThemeNs);
        if (view is not null) ApplyThemeFromView(view);
    }

    private void ApplyThemeFromView(SettingsNamespaceView view)
    {
        // light/dark 原样传递，其余（含 system 与缺省）交给回调按跟随系统处理。
        _applyThemePreference(SettingsValues.GetString(view.Value, ["preference"]));
    }

    #region 即时写引擎

    private Task WriteRowAsync(SettingsRowViewModel row, JsonElement value)
    {
        if (!_queues.TryGetValue(row.Ns, out var queue))
        {
            queue           = new NamespaceWriteQueue();
            _queues[row.Ns] = queue;
        }

        queue.Enqueue(new PendingRowWrite(this, row, value));
        return Task.CompletedTask;
    }

    private async Task RecoverRowWriteFailureAsync(SettingsRowViewModel row, Exception exception)
    {
        var message = exception switch
        {
            SettingsConflictException          => "这些设置已被其他地方改动，已恢复为最新值。",
            SettingsRejectedException rejected => rejected.Message,
            _                                  => "无法保存设置"
        };
        try
        {
            var describe = await _settingsService.DescribeAsync();
            AcceptDescribe(describe);
            _postToUi(() =>
            {
                ProjectFromSnapshot(true);
                row.ErrorText = message;
            });
        }
        catch (Exception)
        {
            _postToUi(() => row.ErrorText = message);
        }
    }

    Task<SettingsNamespaceView> ISettingsMutationRunner.MutateAsync(
        string ns, IReadOnlyList<SettingsMutationOp> ops, long expectedRevision)
    {
        return RunCardMutationAsync(ns, ops, expectedRevision);
    }

    private async Task<SettingsNamespaceView> RunCardMutationAsync(
        string ns, IReadOnlyList<SettingsMutationOp> ops, long expectedRevision)
    {
        var view = await _settingsService.MutateAsync(ns, ops, expectedRevision);
        AcceptNamespaceView(view);
        return view;
    }

    async Task ISettingsMutationRunner.SetCredentialAsync(string reference, string value)
    {
        if (_credentialsService is null) return;

        await _credentialsService.SetAsync(reference, value);
    }

    /// <summary>单个排队的即时写：执行时读取该 ns 当前 revision 作为乐观锁。</summary>
    private sealed class PendingRowWrite(SettingsPanelViewModel panel, SettingsRowViewModel row, JsonElement value)
    {
        public SettingsRowViewModel Row { get; } = row;

        public async Task ExecuteAsync()
        {
            var   ns = Row.Ns;
            long? revision;
            lock (panel._stateSync)
            {
                revision = panel._revisions.TryGetValue(ns, out var current) ? current : null;
            }

            try
            {
                var view = await panel._settingsService.MutateAsync(ns, [SettingsMutationOp.Set(Row.Path, value)],
                                                                    revision);
                panel.AcceptNamespaceView(view);
                panel._postToUi(() =>
                {
                    Row.ClearError();
                    Row.ApplyView(view);
                    if (ns == ThemeNs) panel.ApplyThemeFromView(view);
                });
            }
            catch (Exception exception)
            {
                await panel.RecoverRowWriteFailureAsync(Row, exception);
            }
        }
    }

    /// <summary>
    ///     每 ns 一个串行队列：前一个写完成后才发出下一个；排队期间同一行的新写替换旧写
    ///     （合并为最新值），正在执行的写不被打断。
    /// </summary>
    private sealed class NamespaceWriteQueue
    {
        private readonly List<PendingRowWrite> _pending = [];
        private readonly Lock                  _sync    = new();
        private          bool                  _isPumping;

        public void Enqueue(PendingRowWrite write)
        {
            lock (_sync)
            {
                var index = _pending.FindIndex(candidate => ReferenceEquals(candidate.Row, write.Row));
                if (index >= 0) _pending[index] = write;
                else _pending.Add(write);

                if (_isPumping) return;

                _isPumping = true;
            }

            _ = PumpAsync();
        }

        private async Task PumpAsync()
        {
            while (true)
            {
                PendingRowWrite write;
                lock (_sync)
                {
                    if (_pending.Count == 0)
                    {
                        _isPumping = false;
                        return;
                    }

                    write = _pending[0];
                    _pending.RemoveAt(0);
                }

                await write.ExecuteAsync();
            }
        }
    }

    #endregion
}
