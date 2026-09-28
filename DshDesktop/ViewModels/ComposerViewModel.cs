using DshDesktop.Core.Models;
using DshDesktop.Core.Services;
using DshDesktop.Utils;
using System.Collections.ObjectModel;

namespace DshDesktop.ViewModels;

/// <summary>
///     底部输入区子视图模型：草稿编辑、发送/取消、模型选择与统计条。目标会话与后端连接状态是
///     外部推送的快照（由 MainWindowViewModel 在选中会话、运行状态与连接状态变化时同步），
///     本类不持有会话条目、不订阅后端事件；生效选型与统计整值由 root 转发的 follow 更新驱动，
///     发送/取消/选型失败经回调上报给窗口级错误显示。无选中会话时若 root 已置
///     <see cref="SetDraftTarget" />，本类处于新对话草稿页模式：输入文本仍记于 DraftMessage，
///     模型下拉仅做本地预选（回调交 root 记入草稿），发送由 root 的草稿编排命令承担。
/// </summary>
public sealed class ComposerViewModel : ObservableObject
{
    private readonly ISessionService _sessionService;

    // 错误仍由 MainWindow 级共享 ErrorText 呈现：null 表示清除当前错误。
    private readonly Action<string?> _reportError;

    // 发送被接受后的过渡信号（root 据此把会话标记为已开始，隐藏空白流程界面）。
    private readonly Action? _onPromptAccepted;

    // 新对话草稿页的本地预选模型（无 SessionId 时不发 RPC，root 记入草稿，创建后应用）。
    private readonly Action<ModelSelection>? _onDraftModelChanged;

    private ModelSelection?       _currentModel;
    private ModelCatalog?         _modelCatalog;
    private ModelOptionViewModel? _selectedModelOption;

    // 会话统计：整值更新带投影 seq 做乱序 gating；seq 归 root 转发，本类持有判定。
    private SessionStats? _stats;
    private long          _statsSeq;
    private SessionUsage? _usage;
    private long          _usageSeq;

    private string  _draftMessage = string.Empty;
    private bool    _isBackendConnected;
    private bool    _isCancelling;
    private bool    _isDraftTarget;
    private bool    _isModelMenuOpen;
    private bool    _isSelectingModel;
    private bool    _isSending;
    private bool    _isSessionRunning;
    private string? _sessionId;

    // 模型/推理等级弹出菜单的两级页签；IsOpen 由 Popup 双向绑定。
    private ModelMenuPageKind _modelMenuPage = ModelMenuPageKind.Root;

    public ComposerViewModel(ISessionService sessionService, Action<string?> reportError,
                             Action? onPromptAccepted = null,
                             Action<ModelSelection>? onDraftModelChanged = null)
    {
        _sessionService      = sessionService;
        _reportError         = reportError;
        _onPromptAccepted    = onPromptAccepted;
        _onDraftModelChanged = onDraftModelChanged;
        SendMessageCommand = new AsyncRelayCommand(SendMessageAsync, CanSendMessage);
        CancelCommand      = new AsyncRelayCommand(CancelGenerationAsync, CanCancelGeneration);
        EffortOptions      = ReasoningEffortLevels.All
                           .Select(value => new EffortOptionViewModel(value, EffortLabel(value),
                                                                      option => _ = SelectEffortOptionAsync(option)))
                           .ToArray();
        // 可用性只由 XAML 的 IsEnabled 绑定（IsModelPickerEnabled）承担：自研 RelayCommand
        // 不自动重算 CanExecute，命令谓词会在目录未加载时把按钮永久禁用。
        ToggleModelMenuCommand   = new RelayCommand(ToggleModelMenu);
        ShowModelMenuRootCommand = new RelayCommand(ShowModelMenuRoot);
        ShowModelsPageCommand    = new RelayCommand(ShowModelsMenuPage);
        ShowEffortsPageCommand   = new RelayCommand(ShowEffortsMenuPage);
    }

    public AsyncRelayCommand SendMessageCommand { get; }

    public AsyncRelayCommand CancelCommand { get; }

    public RelayCommand ToggleModelMenuCommand { get; }

    public RelayCommand ShowModelMenuRootCommand { get; }

    public RelayCommand ShowModelsPageCommand { get; }

    public RelayCommand ShowEffortsPageCommand { get; }

    /// <summary>模型下拉可选项：目录扁平投影；当前选型不在目录中时补一项占位。</summary>
    public ObservableCollection<ModelOptionViewModel> ModelOptions { get; } = [];

    /// <summary>模型二级菜单的提供方分组：与 ModelOptions 共享同一批选项实例。</summary>
    public ObservableCollection<ModelGroupMenuViewModel> ModelGroups { get; } = [];

    /// <summary>推理等级菜单项（off/low/high/max）：勾选态随后端回声刷新。</summary>
    public IReadOnlyList<EffortOptionViewModel> EffortOptions { get; }

    public string DraftMessage
    {
        get => _draftMessage;
        set
        {
            if (SetProperty(ref _draftMessage, value)) SendMessageCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsSending
    {
        get => _isSending;
        private set
        {
            if (SetProperty(ref _isSending, value)) SendMessageCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>当前发送/取消的目标会话；未选中会话时为 null。</summary>
    public string? SessionId
    {
        get => _sessionId;
        private set
        {
            if (SetProperty(ref _sessionId, value))
            {
                SendMessageCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(IsModelPickerEnabled));
            }
        }
    }

    public bool IsSessionRunning
    {
        get => _isSessionRunning;
        private set
        {
            if (SetProperty(ref _isSessionRunning, value)) CancelCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsBackendConnected
    {
        get => _isBackendConnected;
        private set
        {
            if (SetProperty(ref _isBackendConnected, value))
            {
                SendMessageCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(IsModelPickerEnabled));
            }
        }
    }

    /// <summary>当前会话生效的模型选型；由 root 转发的 follow 快照与 model/selection 回声更新。</summary>
    public ModelSelection? CurrentModel
    {
        get => _currentModel;
        private set
        {
            if (SetProperty(ref _currentModel, value)) SyncSelectedModelOption();
        }
    }

    /// <summary>下拉选中项；用户改动即发起选型。生效值仍以后端回声为准，失败时回退显示。</summary>
    public ModelOptionViewModel? SelectedModelOption
    {
        get => _selectedModelOption;
        set
        {
            if (SetProperty(ref _selectedModelOption, value) && value is not null)
                _ = SelectModelInternalAsync(value, EffectiveModel?.ReasoningEffort);
        }
    }

    /// <summary>模型/推理等级弹出菜单是否展开；点击外部或 Esc 的轻量关闭会经双向绑定写回。</summary>
    public bool IsModelMenuOpen
    {
        get => _isModelMenuOpen;
        set
        {
            // 每次展开都回到主菜单两行，不残留上次的二级页。
            if (SetProperty(ref _isModelMenuOpen, value) && value) ModelMenuPage = ModelMenuPageKind.Root;
        }
    }

    /// <summary>主菜单页（「模型」「推理等级」两行导航）是否可见。</summary>
    public bool IsModelMenuRootPage => _modelMenuPage == ModelMenuPageKind.Root;

    public bool IsModelsMenuPage => _modelMenuPage == ModelMenuPageKind.Models;

    public bool IsEffortsMenuPage => _modelMenuPage == ModelMenuPageKind.Efforts;

    /// <summary>下拉是否可用：目录已加载、后端已连接，且有选中会话或处于新对话草稿页。</summary>
    public bool IsModelPickerEnabled => ModelOptions.Count > 0 && IsBackendConnected
                                     && (SessionId is not null || _isDraftTarget);

    /// <summary>底栏按钮文案：生效模型名 + 当前推理等级；无生效选型时显示「模型」。</summary>
    public string ModelPickerLabel
    {
        get
        {
            if (EffectiveModel is not { } effective) return "模型";

            var name = ResolveModelDisplayName(effective);
            return effective.ReasoningEffort is { } effort ? $"{name} · {EffortLabel(effort)}" : name;
        }
    }

    /// <summary>主菜单「模型」行的当前值：生效模型名；无生效选型时「默认」。</summary>
    public string CurrentModelNameText =>
        EffectiveModel is { } model ? ResolveModelDisplayName(model) : "默认";

    /// <summary>主菜单「推理等级」行的当前值；后端未下发档位时「默认」。</summary>
    public string CurrentEffortText =>
        EffectiveModel is { } selection && selection.ReasoningEffort is { } effort
            ? EffortLabel(effort)
            : "默认";

    /// <summary>当前会话累计 token 计量（whole-log 投影；无数据时为 null）。</summary>
    public SessionUsage? Usage
    {
        get => _usage;
        private set
        {
            if (SetProperty(ref _usage, value))
            {
                OnPropertyChanged(nameof(UsageValueText));
                OnPropertyChanged(nameof(CacheHitValueText));
                OnPropertyChanged(nameof(UsageDetailText));
                OnPropertyChanged(nameof(HasStatsData));
            }
        }
    }

    /// <summary>当前会话累计时间/步数统计（whole-log 投影；无数据时为 null）。</summary>
    public SessionStats? Stats
    {
        get => _stats;
        private set
        {
            if (SetProperty(ref _stats, value))
            {
                OnPropertyChanged(nameof(SpeedValueText));
                OnPropertyChanged(nameof(StatsDetailText));
                OnPropertyChanged(nameof(HasStatsData));
            }
        }
    }

    /// <summary>统计栏 Token 用量文案：总量 = 计费输入（未命中 + 缓存读 + 缓存写）+ 输出。</summary>
    public string UsageValueText
    {
        get
        {
            if (Usage is not { } usage) return "Token 用量 —";

            var total = usage.UncachedInputTokens + usage.CacheReadTokens + usage.CacheWriteTokens
                      + usage.OutputTokens;
            return total > 0 ? $"Token 用量 {TokenFormat.Compact(total)}" : "Token 用量 —";
        }
    }

    /// <summary>统计栏缓存命中率文案：缓存读 / 计费输入；无计费输入时显示 —。</summary>
    public string CacheHitValueText
    {
        get
        {
            if (Usage is not { } usage) return "缓存命中 —";

            var billed = usage.UncachedInputTokens + usage.CacheReadTokens + usage.CacheWriteTokens;
            return TokenFormat.CacheHitPercent(usage.CacheReadTokens, billed) is { } percent
                ? $"缓存命中 {percent}%"
                : "缓存命中 —";
        }
    }

    /// <summary>统计栏生成速度文案：解码 token / 解码时长；无解码数据时显示 —。</summary>
    public string SpeedValueText =>
        Stats is { DecodeMs: > 0 } stats
            ? $"生成速度 {TokenFormat.TokensPerSecond(stats.DecodeTokens / (stats.DecodeMs / 1000))}"
            : "生成速度 —";

    /// <summary>usage 明细悬停：四个桶的精确计数。</summary>
    public string? UsageDetailText =>
        Usage is { } usage
            ? $"未命中输入 {usage.UncachedInputTokens} · 缓存读 {usage.CacheReadTokens}"
            + $" · 缓存写 {usage.CacheWriteTokens} · 输出 {usage.OutputTokens}"
            : null;

    /// <summary>统计明细悬停：轮次、步数与累计耗时。</summary>
    public string? StatsDetailText =>
        Stats is { } stats
            ? $"{stats.Turns} 轮 · {stats.Steps} 步 · 模型耗时 {stats.LlmMs / 1000:0.#}s"
            + $" · 工具耗时 {stats.ToolMs                                 / 1000:0.#}s"
            : null;

    /// <summary>
    ///     统计条是否显示：对齐 WebUI StatsPills 的空会话口径——出现过至少一步生成
    ///     或有任何计费 token 才显示。不能只判 Usage/Stats 非 null：冷会话的 follow
    ///     快照会携带全 0 的投影 wire 视图，占位「—」不该在空对话露出。
    /// </summary>
    public bool HasStatsData => Stats is { Steps: > 0 } || (Usage is { } usage &&
                                                            usage.UncachedInputTokens + usage.CacheReadTokens +
                                                            usage.CacheWriteTokens    + usage.OutputTokens > 0);

    /// <summary>展示用生效选型：会话未选过型时回退目录默认。</summary>
    private ModelSelection? EffectiveModel => _currentModel ?? _modelCatalog?.Default;

    /// <summary>
    ///     选中会话变化时整体替换上下文；isRunning 取新会话当前的运行状态，draft 装载该会话
    ///     的草稿（按会话记账由 root 提供）。会话身份变化时重置会话级选型并让下拉回退目录
    ///     默认——必须在新会话 follow 启动前调用，否则清空动作会把随后（可能同步）到达的
    ///     新会话快照选型抹掉。Usage/Stats 与其 seq 同属会话级状态一并清零，让新会话的首批
    ///     整值（seq 从头计）可被正常接受。
    /// </summary>
    public void SetSession(string? sessionId, bool isRunning, string draft)
    {
        var sessionChanged = sessionId != SessionId;
        SessionId          = sessionId;
        IsSessionRunning   = isRunning;
        DraftMessage       = draft;
        if (!sessionChanged) return;

        IsModelMenuOpen = false;
        CurrentModel = null;
        _usageSeq    = 0;
        _statsSeq    = 0;
        Usage        = null;
        Stats        = null;
        SyncSelectedModelOption();
    }

    public void SetSessionRunning(bool isRunning)
    {
        IsSessionRunning = isRunning;
    }

    /// <summary>
    ///     新对话草稿页目标开关：无 SessionId 时仍允许模型下拉做本地预选（不发 RPC，
    ///     选型经回调交 root 记入草稿，会话创建后再应用）；离开草稿页恢复会话语义。
    /// </summary>
    public void SetDraftTarget(bool isDraftTarget)
    {
        if (SetProperty(ref _isDraftTarget, isDraftTarget)) OnPropertyChanged(nameof(IsModelPickerEnabled));
    }

    public void SetBackendConnected(bool connected)
    {
        IsBackendConnected = connected;
    }

    /// <summary>接收 root 转发的生效选型：follow 快照投影或 model/selection 回声（后端权威）。</summary>
    public void ApplyCurrentModel(ModelSelection? selection)
    {
        CurrentModel = selection;
    }

    /// <summary>草稿页本地预选：立即驱动显示与菜单勾选，并经回调交 root 记入草稿（不发 RPC）。</summary>
    private void ApplyDraftModel(ModelSelection selection)
    {
        CurrentModel = selection;
        _onDraftModelChanged?.Invoke(selection);
    }

    /// <summary>接收 root 转发的 usage 整值更新：乱序到达的旧 seq（重连竞态）直接忽略。</summary>
    public void ApplyUsage(long seq, SessionUsage usage)
    {
        if (seq < _usageSeq) return;

        _usageSeq = seq;
        Usage     = usage;
    }

    /// <summary>接收 root 转发的 stats 整值更新：乱序到达的旧 seq（重连竞态）直接忽略。</summary>
    public void ApplyStats(long seq, SessionStats stats)
    {
        if (seq < _statsSeq) return;

        _statsSeq = seq;
        Stats     = stats;
    }

    /// <summary>菜单里点选模型：关菜单并把当前推理档位一并提交（档位是否保留由后端回声裁决）。</summary>
    private Task SelectModelOptionAsync(ModelOptionViewModel option)
    {
        IsModelMenuOpen = false;
        return SelectModelInternalAsync(option, EffectiveModel?.ReasoningEffort);
    }

    /// <summary>菜单里点选推理等级：关菜单并对当前生效选型发起带档位的选型请求。</summary>
    private async Task SelectEffortOptionAsync(EffortOptionViewModel option)
    {
        IsModelMenuOpen = false;
        if (_isDraftTarget && SessionId is null)
        {
            // 草稿页本地预选档位：不发 RPC；当前无生效选型（如目录默认）时以目录默认为基底。
            if (EffectiveModel is not { } baseModel) return;

            ApplyDraftModel(baseModel with { ReasoningEffort = option.Value });
            return;
        }

        if (SessionId is null || _isSelectingModel || EffectiveModel is not { } effective) return;

        // 与当前生效档位相同：重复提交没有意义（回声权威，界面不会先于回声变化）。
        if (string.Equals(effective.ReasoningEffort, option.Value, StringComparison.OrdinalIgnoreCase)) return;

        _isSelectingModel = true;
        try
        {
            // 生效值以 follow 流的 model/selection 回声为准（模拟实现同路径）。
            await _sessionService.SelectModelAsync(SessionId, effective.Provider, effective.Model, option.Value);
        }
        catch (Exception exception)
        {
            _reportError(exception.Message);
        }
        finally
        {
            _isSelectingModel = false;
        }
    }

    private async Task SelectModelInternalAsync(ModelOptionViewModel option, string? reasoningEffort)
    {
        // 与当前生效选型（含档位）相同、无会话或已有选型在途：回退显示，不重复请求。
        // 失败回退读取当前生效选型而非请求时的值：在途请求跨会话完成时不会污染新会话显示。
        if (_isDraftTarget && SessionId is null)
        {
            // 草稿页本地预选模型：不发 RPC，选型记入草稿，会话创建后由 root 应用。
            ApplyDraftModel(new ModelSelection(option.Provider, option.Model, reasoningEffort));
            return;
        }

        if (SessionId is null || _isSelectingModel
                              || (EffectiveModel is { } effective && option.Matches(effective)
                                  && effective.ReasoningEffort == reasoningEffort))
        {
            SyncSelectedModelOption();
            return;
        }

        _isSelectingModel = true;
        try
        {
            // 生效值以 follow 流的 model/selection 回声为准（模拟实现同路径）。
            await _sessionService.SelectModelAsync(SessionId, option.Provider, option.Model, reasoningEffort);
        }
        catch (Exception exception)
        {
            _reportError(exception.Message);
            SyncSelectedModelOption();
        }
        finally
        {
            _isSelectingModel = false;
        }
    }

    /// <summary>把菜单勾选与显示文案对齐到生效选型；目录不含该选型时先补占位项。</summary>
    private void SyncSelectedModelOption()
    {
        var effective = EffectiveModel;
        if (effective is null)
        {
            _selectedModelOption = null;
            OnPropertyChanged(nameof(SelectedModelOption));
            RefreshModelMenuState();
            return;
        }

        var match = ModelOptions.FirstOrDefault(option => option.Matches(effective));
        if (match is null)
        {
            match = new ModelOptionViewModel(effective.Provider, effective.Provider,
                                             effective.Model, effective.Model,
                                             option => _ = SelectModelOptionAsync(option));
            ModelOptions.Insert(0, match);
            // 目录外的后端选型在分组菜单中单独成组，保持可见可选。
            ModelGroups.Insert(0, new ModelGroupMenuViewModel(effective.Provider, [match]));
        }

        _selectedModelOption = match;
        OnPropertyChanged(nameof(SelectedModelOption));
        RefreshModelMenuState();
    }

    /// <summary>菜单勾选态与底栏/主菜单文案统一对齐生效选型（模型组与选项共享实例）。</summary>
    private void RefreshModelMenuState()
    {
        var effective = EffectiveModel;
        foreach (var option in ModelOptions)
            option.IsSelected = effective is not null && option.Matches(effective);

        foreach (var effort in EffortOptions)
            effort.IsSelected = effective?.ReasoningEffort is { } value
                                && string.Equals(value, effort.Value, StringComparison.OrdinalIgnoreCase);

        OnPropertyChanged(nameof(ModelPickerLabel));
        OnPropertyChanged(nameof(CurrentModelNameText));
        OnPropertyChanged(nameof(CurrentEffortText));
    }

    /// <summary>目录变化时重建菜单选项与提供方分组（当前生效选型保持可选）。</summary>
    private void RebuildModelOptions()
    {
        ModelOptions.Clear();
        ModelGroups.Clear();
        if (_modelCatalog is { } catalog)
            foreach (var group in catalog.Groups)
            {
                var options = group.Models.Select(model => new ModelOptionViewModel(
                                                        group.Id, group.Name, model.Id, model.Name,
                                                        option => _ = SelectModelOptionAsync(option)))
                                       .ToArray();
                foreach (var option in options) ModelOptions.Add(option);

                ModelGroups.Add(new ModelGroupMenuViewModel(group.Name, options));
            }

        OnPropertyChanged(nameof(IsModelPickerEnabled));
        SyncSelectedModelOption();
    }

    /// <summary>拉取模型目录并重建下拉选项；异常抛给调用方决定上报与重试语义。</summary>
    public async Task RefreshModelCatalogAsync(CancellationToken cancellationToken = default)
    {
        _modelCatalog = await _sessionService.GetModelCatalogAsync(cancellationToken);
        RebuildModelOptions();
    }

    /// <summary>重连等 fire-and-forget 场景的目录刷新：失败经错误回调上报。</summary>
    public async Task RefreshModelCatalogSafeAsync()
    {
        try
        {
            await RefreshModelCatalogAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            _reportError($"模型目录加载失败：{exception.Message}");
        }
    }

    private void ToggleModelMenu()
    {
        if (!IsModelPickerEnabled) return;

        IsModelMenuOpen = !IsModelMenuOpen;
    }

    private void ShowModelMenuRoot()
    {
        ModelMenuPage = ModelMenuPageKind.Root;
    }

    private void ShowModelsMenuPage()
    {
        ModelMenuPage = ModelMenuPageKind.Models;
    }

    private void ShowEffortsMenuPage()
    {
        ModelMenuPage = ModelMenuPageKind.Efforts;
    }

    private ModelMenuPageKind ModelMenuPage
    {
        get => _modelMenuPage;
        set
        {
            if (SetProperty(ref _modelMenuPage, value))
            {
                OnPropertyChanged(nameof(IsModelMenuRootPage));
                OnPropertyChanged(nameof(IsModelsMenuPage));
                OnPropertyChanged(nameof(IsEffortsMenuPage));
            }
        }
    }

    /// <summary>弹出菜单的两级页签。</summary>
    private enum ModelMenuPageKind
    {
        Root,
        Models,
        Efforts
    }

    /// <summary>档位 wire 值到展示名；后端新档位先原样展示，不猜语义。</summary>
    private static string EffortLabel(string value)
    {
        return value switch
        {
            ReasoningEffortLevels.Off  => "Off",
            ReasoningEffortLevels.Low  => "Low",
            ReasoningEffortLevels.High => "High",
            ReasoningEffortLevels.Max  => "Max",
            _                          => value
        };
    }

    /// <summary>生效选型的模型展示名：目录内的取目录名，目录外（占位）回退模型 id。</summary>
    private string ResolveModelDisplayName(ModelSelection selection)
    {
        return ModelOptions.FirstOrDefault(option => option.Matches(selection))?.ModelName ?? selection.Model;
    }

    private async Task SendMessageAsync()
    {
        if (SessionId is null || string.IsNullOrWhiteSpace(DraftMessage)) return;

        var draftAtSend = DraftMessage;
        var content     = draftAtSend.Trim();
        var requestId   = Guid.NewGuid().ToString();
        IsSending = true;
        _reportError(null);
        try
        {
            await _sessionService.SendPromptAsync(SessionId, requestId, content);
            // 对齐参考实现：发送被接受即本地清除空白（不等后端帧回流），会话在列表
            // 过滤与空白流程界面中立即按"已开始"处理；被拒绝的发送保持空白资格。
            _onPromptAccepted?.Invoke();
            // 请求完成时草稿若已被改动（发送期间继续输入），不清除新输入的内容。
            if (string.Equals(DraftMessage, draftAtSend, StringComparison.Ordinal)) DraftMessage = string.Empty;
        }
        catch (Exception exception)
        {
            _reportError(exception.Message);
        }
        finally
        {
            IsSending = false;
        }
    }

    private async Task CancelGenerationAsync()
    {
        if (SessionId is null) return;

        _isCancelling = true;
        CancelCommand.RaiseCanExecuteChanged();
        try
        {
            await _sessionService.CancelAsync(SessionId);
        }
        catch (Exception exception)
        {
            _reportError(exception.Message);
        }
        finally
        {
            _isCancelling = false;
            CancelCommand.RaiseCanExecuteChanged();
        }
    }

    private bool CanSendMessage()
    {
        return SessionId is not null
            && !IsSending
            && !string.IsNullOrWhiteSpace(DraftMessage)
            && IsBackendConnected;
    }

    private bool CanCancelGeneration()
    {
        return IsSessionRunning && !_isCancelling;
    }
}
