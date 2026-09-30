using DshDesktop.Core.Models;
using DshDesktop.Core.Services;
using System.Collections.ObjectModel;

namespace DshDesktop.ViewModels;

/// <summary>
///     Composer 的「执行权限」下拉（会话工具行紧邻加号；新对话草稿页同款）：展示当前生效的
///     权限预设，并允许在发送下一轮前切换。三条权威边界：
///     ① 目录来自 permissionPresets/catalog，permission-presets/catalog-changed 广播触发重读；
///     ② 会话内当前权限以 permissions 投影为唯一权威——用户点选只是请求，确认由后端投影回流，
///     不做乐观更新，请求失败保持旧值；
///     ③ 与 ApprovalPanel 的单次裁决（allowed-once）完全独立：审批不改变这里的当前预设。
///     新对话草稿页（无会话）做本地预选：不发 RPC，经回调记入草稿（与模型预选同一模式），
///     首发送创建会话后由 root 经 /permission 应用，此后仍以投影回流为准。
///     danger-full-access 与 auto 的选择走风险确认门（对齐 WebUI RiskConfirmation：不勾选
///     确认无法启用），草稿页同样生效。
/// </summary>
public sealed class PermissionSelectorViewModel : ObservableObject, IDisposable
{
    private readonly Action<string>?          _onDraftPresetChanged;
    private readonly IPermissionPresetService _permissionPresetService;
    private readonly Action<Action>           _postToUi;
    private readonly Action<string?>          _reportError;

    private PermissionCatalog?         _catalog;
    private PermissionOptionViewModel? _pendingConfirmation;

    private long    _currentSeq;
    private string? _currentValue;
    private bool    _catalogFailed;
    private bool    _isAcknowledged;
    private bool    _isBackendConnected;
    private bool    _isCatalogLoaded;
    private bool    _isConfirmOpen;
    private bool    _isDraftTarget;
    private string? _draftPreset;
    private bool    _isMenuOpen;
    private bool    _isSwitching;

    public PermissionSelectorViewModel(IPermissionPresetService permissionPresetService,
                                       Action<string?>          reportError,
                                       Action<Action>?          postToUi             = null,
                                       Action<string>?          onDraftPresetChanged = null)
    {
        _permissionPresetService = permissionPresetService;
        _reportError             = reportError;
        _postToUi                = postToUi ?? (action => action());
        _onDraftPresetChanged    = onDraftPresetChanged;
        ToggleMenuCommand        = new RelayCommand(ToggleMenu);
        SelectOptionCommand      = new RelayCommand<PermissionOptionViewModel>(SelectOption);
        ConfirmSwitchCommand     = new RelayCommand(ConfirmSwitch);
        CancelSwitchCommand      = new RelayCommand(CancelSwitch);
        // 目录变化广播在连接线程到达：编组回界面线程重读；失败保持旧目录并等待下次触发。
        _permissionPresetService.CatalogChanged += OnCatalogChanged;
    }

    /// <summary>退订目录广播（root 释放时调用）。</summary>
    public void Dispose()
    {
        _permissionPresetService.CatalogChanged -= OnCatalogChanged;
    }

    public RelayCommand ToggleMenuCommand { get; }

    public RelayCommand<PermissionOptionViewModel> SelectOptionCommand { get; }

    public RelayCommand ConfirmSwitchCommand { get; }

    public RelayCommand CancelSwitchCommand { get; }

    /// <summary>目录选项（catalog 顺序）；目录重读时整体重建。</summary>
    public ObservableCollection<PermissionOptionViewModel> Options { get; } = [];

    /// <summary>切换请求的目标会话；root 在选中会话变化时推送。</summary>
    public string? SessionId { get; private set; }

    /// <summary>是否有选中会话：草稿页（无会话）时为假，可见性由 <see cref="IsVisible" /> 承担。</summary>
    public bool HasSession => SessionId is not null;

    /// <summary>permissions 投影给出的当前权威值；投影未到（无基线）或处于草稿页时为 null。</summary>
    public string? CurrentValue => _currentValue;

    /// <summary>是否有选中会话：草稿页（无会话）时为假，可见性由 <see cref="IsVisible" /> 承担。</summary>
    public bool IsVisible => HasSession || _isDraftTarget;

    /// <summary>
    ///     当前生效值的展示来源：会话内是投影权威值；草稿页是本地预选（未预选时回退目录
    ///     默认——即新会话将被后端播种的预设）。两者皆无时为 null（显示「权限…」）。
    /// </summary>
    private string? EffectiveValue =>
        HasSession ? _currentValue : _isDraftTarget ? _draftPreset ?? _catalog?.DefaultPreset : null;

    /// <summary>目录是否已加载且非空（加载失败或空目录即不可用态）。</summary>
    private bool IsCatalogAvailable => _catalog is { Options.Count: > 0 };

    /// <summary>
    ///     选择器可用：后端已连接、目录已加载、确认弹层未打开；会话内还要求投影已给出
    ///     基线且无在途切换，草稿页只要处于预选目标态即可（本地预选不发请求）。
    ///     加载中/不可用/会话内无投影基线一律禁用，不显示猜测的默认值。
    /// </summary>
    public bool IsSelectorEnabled => !_isConfirmOpen && _isBackendConnected && _isCatalogLoaded && IsCatalogAvailable &&
                                     (HasSession ? _currentValue is not null && !_isSwitching : _isDraftTarget);

    /// <summary>预设下拉展开态（Popup 双向绑定）。</summary>
    public bool IsMenuOpen
    {
        get => _isMenuOpen;
        set => SetProperty(ref _isMenuOpen, value);
    }

    /// <summary>风险确认弹层展开态（Popup 双向绑定）。</summary>
    public bool IsConfirmOpen
    {
        get => _isConfirmOpen;
        set => SetProperty(ref _isConfirmOpen, value);
    }

    /// <summary>风险确认勾选；对齐 WebUI RiskConfirmation——不勾选无法点击启用。</summary>
    public bool IsAcknowledged
    {
        get => _isAcknowledged;
        set => SetProperty(ref _isAcknowledged, value);
    }

    /// <summary>切换请求在途：选择器暂时禁用，请求收束即恢复（不永久卡死）；确认由投影回流。</summary>
    public bool IsSwitching
    {
        get => _isSwitching;
        private set
        {
            if (SetProperty(ref _isSwitching, value)) OnPropertyChanged(nameof(IsSelectorEnabled));
        }
    }

    /// <summary>确认弹层文案：full access 与 auto 各自对齐 WebUI permission.access 字典（zh）。</summary>
    public string ConfirmTitle =>
        IsPendingAutoReview ? "确认启用 Auto review（实验）？" : "确认启用完全权限？";

    public string ConfirmDescription =>
        IsPendingAutoReview
            ? "Auto review 不使用沙箱。每次原生工具调用和 PTC 内层调用前，都会由与当前 agent 相同的模型进行审查；审查拒绝的调用由你批准或拒绝。此功能仍属实验性，可能误放行或误拒绝，并会消耗额外 token。"
            : "启用完全权限后，智能体将减少确认步骤，并且可以直接执行更多操作，包括敏感操作、文件修改或外部命令。仅建议在你信任当前任务时使用。";

    public string ConfirmAcknowledgeText =>
        IsPendingAutoReview ? "我已了解这些风险，并愿意继续" : "我已了解风险，并愿意继续";

    public string ConfirmCancelText => "取消";

    public string ConfirmEnableText =>
        IsPendingAutoReview ? "启用 Auto review" : "启用完全权限";

    private bool IsPendingAutoReview => _pendingConfirmation?.Value == PermissionPresetValues.AutoReview;

    /// <summary>底栏按钮文案：生效值优先（目录内取展示名），未知预设回退派生展示；加载中/不可用有明确提示。</summary>
    public string PermissionPickerLabel
    {
        get
        {
            // 加载过但目录为空、或加载失败：明确的不可用态；否则是加载中。
            if (!_isCatalogLoaded || !IsCatalogAvailable) return _isCatalogLoaded || _catalogFailed ? "权限不可用" : "权限…";
            if (EffectiveValue is { } value)
                return Options.FirstOrDefault(option => option.Value == value)?.DisplayName
                    ?? PermissionOptionViewModel.DisplayPresetName(value);
            return "权限…";
        }
    }

    /// <summary>按钮悬停提示；无生效值时只说明用途，不猜当前值。</summary>
    public string PickerToolTip =>
        EffectiveValue is null ? "执行权限预设" : $"访问模式，当前：{PermissionPickerLabel}";

    /// <summary>
    ///     选中会话变化时整体替换上下文。投影基线与 seq 属会话级状态一并清零：新会话的
    ///     首个投影（seq 从头计）可被正常接受；切换请求的目标会话随之后续请求更新。
    /// </summary>
    public void SetSession(string? sessionId)
    {
        if (SessionId == sessionId) return;

        SessionId     = sessionId;
        _currentValue = null;
        _currentSeq   = 0;
        _draftPreset  = null;
        IsMenuOpen    = false;
        CancelSwitch();
        OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(IsVisible));
        RefreshPermissionState();
    }

    /// <summary>
    ///     草稿页目标开关：进入草稿页时清除上一会话的投影状态（无会话可订阅，预选显示由
    ///     root 随后经 <see cref="ApplyDraftPreset" /> 推送）；离开时清除本地预选，改由
    ///     会话投影驱动。可见性与可用性随目标态刷新。
    /// </summary>
    public void SetDraftTarget(bool isDraftTarget)
    {
        if (_isDraftTarget == isDraftTarget) return;

        _isDraftTarget = isDraftTarget;
        if (isDraftTarget)
        {
            _currentValue = null;
            _currentSeq   = 0;
            IsMenuOpen    = false;
        }
        else
        {
            _draftPreset = null;
        }

        OnPropertyChanged(nameof(IsVisible));
        RefreshPermissionState();
    }

    /// <summary>
    ///     草稿页权限预选显示（root 在进入草稿页时推送既有草稿的预选；null=未预选，显示
    ///     目录默认）。仅改本地显示，不发 RPC；点选新预选经 <see cref="SelectOption" /> 的
    ///     草稿分支承担。
    /// </summary>
    public void ApplyDraftPreset(string? preset)
    {
        if (_draftPreset == preset) return;

        _draftPreset = preset;
        RefreshSelectionMarks();
        RefreshPermissionState();
    }

    public void SetBackendConnected(bool connected)
    {
        if (_isBackendConnected == connected) return;

        _isBackendConnected = connected;
        OnPropertyChanged(nameof(IsSelectorEnabled));
    }

    /// <summary>接收 root 转发的 permissions 投影整值：唯一权威更新入口；乱序旧 seq（重连竞态）忽略。</summary>
    public void ApplyPermission(long seq, string currentValue)
    {
        if (string.IsNullOrEmpty(currentValue) || seq < _currentSeq) return;

        _currentSeq   = seq;
        _currentValue = currentValue;
        RefreshSelectionMarks();
        RefreshPermissionState();
    }

    /// <summary>初始化/重连/catalog 广播后的目录重读：失败保持不可用态，不影响正常聊天。</summary>
    public async Task ReloadCatalogSafeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await ReloadCatalogAsync(cancellationToken);
            _catalogFailed = false;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // 目录失败只影响选择器（保持禁用/不可用文案），不影响会话聊天；
            // 下次连接恢复或 catalog 广播会再次触发重试。
            _catalogFailed = true;
            RefreshPermissionState();
        }
    }

    /// <summary>拉取目录并重建选项；异常抛给调用方（测试与初始化路径可感知失败）。</summary>
    public async Task ReloadCatalogAsync(CancellationToken cancellationToken = default)
    {
        _catalog         = await _permissionPresetService.GetCatalogAsync(cancellationToken);
        _isCatalogLoaded = true;
        RebuildOptions();
    }

    private void OnCatalogChanged(object? sender, EventArgs e)
    {
        _postToUi(() => _ = ReloadCatalogSafeAsync());
    }

    /// <summary>目录重读只重建选项；当前权限仍由投影决定，不因重读改写。</summary>
    private void RebuildOptions()
    {
        Options.Clear();
        if (_catalog is { } catalog)
            foreach (var option in catalog.Options)
                Options.Add(new PermissionOptionViewModel(option.Value, option.Name, option.Description, SelectOption));

        RefreshSelectionMarks();
        OnPropertyChanged(nameof(IsSelectorEnabled));
    }

    /// <summary>
    ///     下拉点选：先关菜单。目标与当前生效值相同直接返回（不重复请求，已当前值不触发
    ///     确认）；full access 与 auto 走风险确认门，确认后提交；其余直接提交。会话内提交
    ///     发切换请求，草稿页提交改本地预选。
    /// </summary>
    private void SelectOption(PermissionOptionViewModel? option)
    {
        IsMenuOpen = false;
        if (option is null || !IsSelectorEnabled) return;

        if (option.Value == EffectiveValue) return;

        if (PermissionPresetValues.RequiresConfirmation(option.Value))
        {
            _pendingConfirmation = option;
            IsAcknowledged       = false;
            OnPropertyChanged(nameof(ConfirmTitle));
            OnPropertyChanged(nameof(ConfirmDescription));
            OnPropertyChanged(nameof(ConfirmAcknowledgeText));
            OnPropertyChanged(nameof(ConfirmEnableText));
            IsConfirmOpen = true;
            return;
        }

        CommitSelection(option);
    }

    /// <summary>确认弹层「启用」：勾选确认后才提交选择。</summary>
    private void ConfirmSwitch()
    {
        if (_pendingConfirmation is not { } option || !_isAcknowledged) return;

        IsConfirmOpen        = false;
        _pendingConfirmation = null;
        CommitSelection(option);
    }

    /// <summary>
    ///     提交一次选择：会话内发起切换请求（投影回流确认，失败保持旧值）；草稿页改本地
    ///     预选并经回调记入草稿（不发 RPC，首发送创建会话后由 root 应用）。
    /// </summary>
    private void CommitSelection(PermissionOptionViewModel option)
    {
        if (HasSession)
        {
            _ = SubmitSwitchAsync(option);
            return;
        }

        ApplyDraftPreset(option.Value);
        _onDraftPresetChanged?.Invoke(option.Value);
    }

    /// <summary>取消确认弹层：不发送任何请求。</summary>
    private void CancelSwitch()
    {
        _pendingConfirmation = null;
        IsAcknowledged       = false;
        IsConfirmOpen        = false;
        OnPropertyChanged(nameof(IsSelectorEnabled));
    }

    /// <summary>
    ///     提交切换请求（commands/execute 的 /permission 命令行）。投影是权威状态：本方法
    ///     不改写当前值——成功由 permissions 投影回流确认；RPC 失败或宿主无 /permission
    ///     命令时报错并保持旧值。在途标记防止连续冲突请求，请求收束即恢复。
    /// </summary>
    private async Task SubmitSwitchAsync(PermissionOptionViewModel option)
    {
        if (SessionId is null || IsSwitching) return;

        IsSwitching = true;
        _reportError(null);
        try
        {
            var matched = await _permissionPresetService.SwitchPresetAsync(SessionId, option.Value);
            if (!matched) _reportError("后端未提供权限切换命令（/permission）。");
        }
        catch (Exception exception)
        {
            _reportError($"权限切换失败：{exception.Message}");
        }
        finally
        {
            IsSwitching = false;
        }
    }

    private void ToggleMenu()
    {
        if (!IsSelectorEnabled) return;

        IsMenuOpen = !IsMenuOpen;
    }

    /// <summary>下拉勾选与按钮文案统一对齐生效值（会话内为投影权威值，草稿页为预选）。</summary>
    private void RefreshSelectionMarks()
    {
        foreach (var option in Options)
            option.IsSelected = option.Value == EffectiveValue;

        OnPropertyChanged(nameof(PermissionPickerLabel));
        OnPropertyChanged(nameof(PickerToolTip));
    }

    private void RefreshPermissionState()
    {
        OnPropertyChanged(nameof(IsSelectorEnabled));
        OnPropertyChanged(nameof(PermissionPickerLabel));
        OnPropertyChanged(nameof(PickerToolTip));
    }
}
