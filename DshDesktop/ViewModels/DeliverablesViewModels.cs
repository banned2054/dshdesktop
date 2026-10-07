using DshDesktop.Core.Models;
using DshDesktop.Core.Services;
using DshDesktop.Services.Conversations;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace DshDesktop.ViewModels;

/// <summary>One declared file from a durable deliverables/presented event.</summary>
public sealed class DeliveredFileViewModel : ObservableObject
{
    private DeliveredFileDeclaration _source;
    private string _statusText = string.Empty;
    private bool _hasError;
    private long? _added;
    private long? _deleted;
    private bool _isBinary;
    private bool _isOversized;
    private readonly Action<DeliveredFileViewModel> _open;

    internal DeliveredFileViewModel(DeliveredFileDeclaration source, long turn, Action<DeliveredFileViewModel> open)
    {
        _source = source;
        Turn    = turn;
        _open   = open;
        OpenCommand = new RelayCommand(() => _open(this));
    }

    /// <summary>声明的原始路径（相对工作区根），行左侧展示。</summary>
    public string Path => _source.Path;

    /// <summary>路径尾段文件名；单文件卡标题使用。</summary>
    public string Name
    {
        get
        {
            var path = Path;
            var separator = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
            return separator < 0 ? path : path[(separator + 1)..];
        }
    }

    public string? Description => _source.Description;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    /// <summary>交付声明所属轮次；面板数据关联（同会话同轮）使用。</summary>
    internal long Turn { get; }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

    /// <summary>轮次摘要匹配成功才有增删计数；声明文件不在快照里时右侧留空。</summary>
    public bool HasCountData => _added is not null;

    public bool HasLineCounts => HasCountData && !_isBinary && !_isOversized;

    /// <summary>匹配到的是二进制/超大文件时右侧显示形态标注而非行数。</summary>
    public bool HasCountText => HasCountData && !HasLineCounts;

    public string AddedText => $"+{_added}";

    public string DeletedText => $"−{_deleted}";

    public string CountText => _isBinary ? "二进制" : "过大";

    public ICommand OpenCommand { get; }

    internal long Seq => _source.Seq;

    internal int Index => _source.Index;

    internal void Update(DeliveredFileDeclaration source)
    {
        _source = source;
        OnPropertyChanged(nameof(Path));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(HasDescription));
    }

    internal void SetStatus(string text, bool isError)
    {
        StatusText = text;
        HasError = isError;
    }

    internal void SetCounts(long added, long deleted, bool isBinary, bool isOversized)
    {
        _added       = added;
        _deleted     = deleted;
        _isBinary    = isBinary;
        _isOversized = isOversized;
        OnPropertyChanged(nameof(HasCountData));
        OnPropertyChanged(nameof(HasLineCounts));
        OnPropertyChanged(nameof(HasCountText));
        OnPropertyChanged(nameof(AddedText));
        OnPropertyChanged(nameof(DeletedText));
        OnPropertyChanged(nameof(CountText));
    }
}

/// <summary>
///     The explicit files delivered in one turn. Declarations are durable; file contents are not archived.
///     Repeated declarations update a path in place and retain its first-seen display position.
///     展示与改动卡同一套形态：单文件整卡可点（「已编辑 文件名」+ 右侧「查看变更」），多文件
///     头部静态（「已编辑 N 个文件」+ 整轮增删合计）、超过 3 行折叠，仅文件行可点。增删计数来自
///     同轮 workspace/changes 摘要（组装器提供该轮宣告的 seq），摘要不可用时静默降级为无计数；
///     单击仍经既有解析链路打开右侧面板（完整差异 / 历史编辑片段 / 当前内容 / 不可用）。
/// </summary>
public sealed class DeliverablesCardViewModel : ConversationItemViewModel
{
    private const int CollapsedFileCount = 3;

    private readonly Dictionary<string, DeliveredFileViewModel> _filesByPath = new(StringComparer.Ordinal);
    private readonly string? _cwd;
    private readonly IWorkspaceChangesService? _changesService;
    private readonly Action<Action> _postToUi;
    private readonly Action<DeliveredFileViewModel>? _openDeliverable;
    private readonly Func<string, bool> _isSessionCurrent;
    private long? _changesSeq;
    private WorkspaceChangesSummary? _countsSummary;
    private bool _isExpanded;
    private bool _hasTotals;
    private long _added;
    private long _deleted;

    public DeliverablesCardViewModel(
        string sessionId,
        string? cwd,
        DeliverablesPresentedAnnouncement announcement,
        IWorkspaceChangesService? changesService,
        Action<Action> postToUi,
        long? changesSeq,
        Action<DeliveredFileViewModel>? openDeliverable,
        Func<string, bool> isSessionCurrent)
        : base(announcement.Seq)
    {
        SessionId         = sessionId;
        Turn              = announcement.Turn;
        _cwd              = cwd;
        _changesService   = changesService;
        _postToUi         = postToUi;
        _openDeliverable  = openDeliverable;
        _isSessionCurrent = isSessionCurrent;
        ToggleCommand     = new RelayCommand(() => IsExpanded = !IsExpanded);
        SingleOpenCommand = new RelayCommand(() => OpenFile(SingleFile));
        Apply(announcement);
        if (changesSeq is { } seq)
        {
            _changesSeq = seq;
            _ = LoadCountsAsync(seq);
        }
    }

    public string SessionId { get; }

    public long Turn { get; }

    public ObservableCollection<DeliveredFileViewModel> Files { get; } = [];

    public ICommand ToggleCommand { get; }

    /// <summary>单文件形态下整卡与「查看变更」按钮共用的打开命令。</summary>
    public ICommand SingleOpenCommand { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        private set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(VisibleFiles));
                OnPropertyChanged(nameof(ToggleText));
            }
        }
    }

    /// <summary>声明数超过折叠阈值时显示「再显示 / 收起」开关（与改动卡同一阈值）。</summary>
    public bool HasToggle => Files.Count > CollapsedFileCount;

    public IReadOnlyList<DeliveredFileViewModel> VisibleFiles => IsExpanded || !HasToggle
        ? Files.ToArray()
        : Files.Take(CollapsedFileCount).ToArray();

    public string ToggleText => IsExpanded ? "收起文件" : $"再显示 {Files.Count - CollapsedFileCount} 个文件";

    public bool IsSingleFile => Files.Count == 1;

    public DeliveredFileViewModel? SingleFile => Files.Count == 1 ? Files[0] : null;

    /// <summary>单文件卡标题：对齐改动卡「已编辑 文件名」（多文件容器才是「已编辑 N 个文件」）。</summary>
    public string SingleTitleText => SingleFile is { } file ? $"已编辑 {file.Name}" : TitleText;

    public bool SingleHasLineCounts => SingleFile is { } file && file.HasLineCounts;

    public bool SingleHasCountText => SingleFile is { } file && file.HasCountText;

    public string SingleStatusText => SingleFile?.StatusText ?? string.Empty;

    public bool SingleHasError => SingleFile?.HasError == true;

    public string TitleText => $"已编辑 {Files.Count} 个文件";

    /// <summary>同轮摘要读取成功后头部显示整轮增删行数合计。</summary>
    public bool HasTotals
    {
        get => _hasTotals;
        private set => SetProperty(ref _hasTotals, value);
    }

    public string HeaderAddedText => $"+{_added}";

    public string HeaderDeletedText => $"−{_deleted}";

    /// <summary>Apply another durable declaration without duplicating already-seen event records.</summary>
    internal void Apply(DeliverablesPresentedAnnouncement announcement)
    {
        if (announcement.Turn != Turn) return;

        foreach (var declaration in announcement.Files)
        {
            if (_filesByPath.TryGetValue(declaration.Path, out var existing))
            {
                if (declaration.Seq < existing.Seq ||
                    declaration.Seq == existing.Seq && declaration.Index <= existing.Index)
                    continue;

                existing.Update(declaration);
                UpdateAvailability(existing);
                continue;
            }

            var file = new DeliveredFileViewModel(declaration, Turn, OpenFile);
            _filesByPath.Add(declaration.Path, file);
            Files.Add(file);
            UpdateAvailability(file);
            if (_countsSummary is not null) MatchCounts(file, _countsSummary);
        }

        NotifyShapeChanged();
    }

    /// <summary>Replace the card projection after a late history/replay declaration is merged by seq.</summary>
    internal void ReplaceFiles(IReadOnlyList<DeliveredFileDeclaration> declarations)
    {
        var ordered = new List<DeliveredFileViewModel>(declarations.Count);
        var nextByPath = new Dictionary<string, DeliveredFileViewModel>(StringComparer.Ordinal);
        foreach (var declaration in declarations)
        {
            if (!_filesByPath.TryGetValue(declaration.Path, out var file))
                file = new DeliveredFileViewModel(declaration, Turn, OpenFile);
            else
                file.Update(declaration);

            UpdateAvailability(file);
            if (_countsSummary is not null) MatchCounts(file, _countsSummary);
            nextByPath.Add(declaration.Path, file);
            ordered.Add(file);
        }

        _filesByPath.Clear();
        foreach (var pair in nextByPath) _filesByPath.Add(pair.Key, pair.Value);
        Files.Clear();
        foreach (var file in ordered) Files.Add(file);
        NotifyShapeChanged();
    }

    /// <summary>同轮 changes 宣告晚于卡片到达（组装器补接）时更新计数来源并重取。</summary>
    internal void SetChangesSource(long seq)
    {
        if (_changesSeq == seq) return;
        _changesSeq = seq;
        _ = LoadCountsAsync(seq);
    }

    private async Task LoadCountsAsync(long seq)
    {
        if (_changesService is null) return;

        try
        {
            var summary = await _changesService.GetSummaryAsync(SessionId, seq).ConfigureAwait(false);
            if (summary is null || _changesSeq != seq) return;

            _postToUi(() =>
            {
                if (_changesSeq == seq) ApplyCounts(summary);
            });
        }
        catch (Exception)
        {
            // 计数是补充展示：摘要不可用（本地缓存释放、Host 404、网络失败）时卡片
            // 保留声明列表，只是不显示增删行数；不打断打开面板等既有交互。
        }
    }

    private void ApplyCounts(WorkspaceChangesSummary summary)
    {
        _countsSummary = summary;
        foreach (var file in Files) MatchCounts(file, summary);
        _added    = summary.Added;
        _deleted  = summary.Deleted;
        HasTotals = summary.Files.Count > 0;
        OnPropertyChanged(nameof(HeaderAddedText));
        OnPropertyChanged(nameof(HeaderDeletedText));
        OnPropertyChanged(nameof(SingleHasLineCounts));
        OnPropertyChanged(nameof(SingleHasCountText));
    }

    /// <summary>摘要文件按解析后的绝对路径匹配声明；解析失败的路径跳过，不猜测。</summary>
    private void MatchCounts(DeliveredFileViewModel file, WorkspaceChangesSummary summary)
    {
        var declared = WorkspacePathResolver.TryResolve(file.Path, _cwd, out _);
        if (declared is null) return;

        foreach (var candidate in summary.Files)
        {
            var resolved = WorkspacePathResolver.TryResolve(candidate.Path, _cwd, out _);
            if (resolved is null || !WorkspacePathResolver.SamePath(resolved, declared)) continue;

            file.SetCounts(candidate.Added, candidate.Deleted, candidate.IsBinary, candidate.IsOversized);
            return;
        }
    }

    private void OpenFile(DeliveredFileViewModel? file)
    {
        if (file is null) return;
        if (!_isSessionCurrent(SessionId))
        {
            file.SetStatus("会话已切换，请重新选择此会话后再打开。", true);
            return;
        }

        if (_openDeliverable is null)
        {
            file.SetStatus("应用内查看面板不可用。", true);
            return;
        }

        _openDeliverable(file);
    }

    private void UpdateAvailability(DeliveredFileViewModel file)
    {
        var absolutePath = WorkspacePathResolver.TryResolve(file.Path, _cwd, out var error);
        if (absolutePath is null)
        {
            file.SetStatus(error, true);
            return;
        }

        // 正常文件不再显示行内提示（行本身可点即查看）；缺失文件保留红色说明。
        var exists = File.Exists(absolutePath);
        file.SetStatus(exists
            ? string.Empty
            : "文件缺失或当前不可访问；交付声明仍保留在历史记录中。", !exists);
    }

    /// <summary>文件集合形状变化后刷新全部派生绑定（单/多文件形态可互转）。</summary>
    private void NotifyShapeChanged()
    {
        OnPropertyChanged(nameof(IsSingleFile));
        OnPropertyChanged(nameof(SingleFile));
        OnPropertyChanged(nameof(SingleTitleText));
        OnPropertyChanged(nameof(SingleHasLineCounts));
        OnPropertyChanged(nameof(SingleHasCountText));
        OnPropertyChanged(nameof(SingleStatusText));
        OnPropertyChanged(nameof(SingleHasError));
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(HasToggle));
        OnPropertyChanged(nameof(ToggleText));
        OnPropertyChanged(nameof(VisibleFiles));
    }
}
