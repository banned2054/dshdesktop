using DshDesktop.Core.Models;
using DshDesktop.Core.Services;
using DshDesktop.Services.Conversations;
using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Input;

namespace DshDesktop.ViewModels;

/// <summary>改动卡片中的一行文件；点击后在会话右侧打开该文件的对比。</summary>
public sealed class WorkspaceChangedFileViewModel : ObservableObject
{
    private bool _isSelected;

    public WorkspaceChangedFileViewModel(WorkspaceChangedFileInfo source, int index)
    {
        Source = source;
        Index  = index;
    }

    public long AnnouncementSeq { get; init; }

    public string SessionId { get; init; } = string.Empty;

    public WorkspaceChangedFileInfo Source { get; }

    public int Index { get; }

    public string Display => Source.Display;

    public string AddedText => $"+{Source.Added}";

    public string DeletedText => $"−{Source.Deleted}";

    /// <summary>非二进制且未超限时右侧显示增删行数，否则显示形态标注（二进制/过大）。</summary>
    public bool HasLineCounts => !Source.IsBinary && !Source.IsOversized;

    public string CountText => Source.IsBinary ? "二进制" : "过大";

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>
///     一轮工作区文件快照差异卡片：单文件渲染为一张可点卡片（标题「已编辑 文件名」，
///     右侧「查看变更」），多文件渲染为「已编辑 N 个文件」容器——头部静态、超过
///     3 行折叠，仅文件行可点击。摘要和对比由本端缓存提供。
/// </summary>
public sealed class WorkspaceChangesCardViewModel : ConversationItemViewModel
{
    private const int CollapsedFileCount = 3;

    private readonly IWorkspaceChangesService? _changesService;
    private readonly Action<Action>            _postToUi;
    private readonly Action<WorkspaceChangedFileViewModel> _openDiff;

    private bool _isLoading = true;
    private bool _isUnavailable;
    private bool _hasError;
    private bool _isExpanded;
    private bool _hasTotals;
    private long _added;
    private long _deleted;
    private string _titleText   = "正在读取文件改动";
    private string _summaryText = string.Empty;

    public WorkspaceChangesCardViewModel(
        string sessionId,
        WorkspaceChangesAnnouncement announcement,
        IWorkspaceChangesService? changesService,
        Action<WorkspaceChangedFileViewModel> openDiff,
        Action<Action> postToUi)
        : base(announcement.Seq)
    {
        SessionId       = sessionId;
        Turn            = announcement.Turn;
        _changesService = changesService;
        _openDiff       = openDiff;
        _postToUi       = postToUi;
        SelectFileCommand = new RelayCommand<WorkspaceChangedFileViewModel>(SelectFile);
        ToggleCommand     = new RelayCommand(() => IsExpanded = !IsExpanded);
        _ = LoadAsync();
    }

    public string SessionId { get; }

    public long Turn { get; }

    public ObservableCollection<WorkspaceChangedFileViewModel> Files { get; } = [];

    public ICommand SelectFileCommand { get; }

    public ICommand ToggleCommand { get; }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public bool IsUnavailable
    {
        get => _isUnavailable;
        private set => SetProperty(ref _isUnavailable, value);
    }

    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

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

    /// <summary>文件数超过折叠阈值时显示「再显示 / 收起」开关。</summary>
    public bool HasToggle => Files.Count > CollapsedFileCount;

    public IReadOnlyList<WorkspaceChangedFileViewModel> VisibleFiles => IsExpanded || !HasToggle
        ? Files.ToArray()
        : Files.Take(CollapsedFileCount).ToArray();

    public string ToggleText => IsExpanded ? "收起文件" : $"再显示 {Files.Count - CollapsedFileCount} 个文件";

    public bool HasFiles => Files.Count > 0;

    public bool IsSingleFile => Files.Count == 1;

    public WorkspaceChangedFileViewModel? SingleFile => Files.Count == 1 ? Files[0] : null;

    /// <summary>单文件卡标题：对齐官方「已编辑 文件名」（多文件容器才是「已编辑 N 个文件」）。</summary>
    public string SingleTitleText => SingleFile is { } file
        ? $"已编辑 {WorkspaceDiffPanelViewModel.DisplayName(file.Display)}"
        : TitleText;

    public bool SingleHasLineCounts => SingleFile is { } file && file.HasLineCounts;

    /// <summary>摘要读取成功后头部显示整轮增删行数合计。</summary>
    public bool HasTotals
    {
        get => _hasTotals;
        private set => SetProperty(ref _hasTotals, value);
    }

    public string HeaderAddedText => $"+{_added}";

    public string HeaderDeletedText => $"−{_deleted}";

    public string TitleText
    {
        get => _titleText;
        private set => SetProperty(ref _titleText, value);
    }

    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    private async Task LoadAsync()
    {
        if (_changesService is null)
        {
            ApplyUnavailable();
            return;
        }

        try
        {
            var summary = await _changesService.GetSummaryAsync(SessionId, Seq).ConfigureAwait(false);
            if (summary is null)
            {
                _postToUi(ApplyUnavailable);
                return;
            }

            _postToUi(() => ApplySummary(summary));
        }
        catch (Exception exception)
        {
            _postToUi(() =>
            {
                IsLoading   = false;
                HasError    = true;
                TitleText   = "无法读取文件改动";
                SummaryText = exception.Message;
            });
        }
    }

    private void ApplyUnavailable()
    {
        IsLoading     = false;
        IsUnavailable = true;
        TitleText     = "文件改动已不可用";
        SummaryText   = "本地缓存已释放；改动摘要只在本次应用运行期间保留。";
    }

    private void ApplySummary(WorkspaceChangesSummary summary)
    {
        foreach (var file in summary.Files)
            Files.Add(new WorkspaceChangedFileViewModel(file, Files.Count)
            {
                AnnouncementSeq = Seq,
                SessionId       = SessionId
            });

        _added   = summary.Added;
        _deleted = summary.Deleted;
        // 默认按官方行为折叠：只有少量文件直接展开，其余先收起留待用户展开。
        _isExpanded = summary.Files.Count <= CollapsedFileCount;
        _titleText  = summary.Files.Count == 1 ? "已编辑 1 个文件" : $"已编辑 {summary.Files.Count} 个文件";
        _summaryText = $"+{summary.Added} −{summary.Deleted}";
        IsLoading    = false;
        HasTotals    = true;
        OnPropertyChanged(nameof(IsExpanded));
        OnPropertyChanged(nameof(HasToggle));
        OnPropertyChanged(nameof(VisibleFiles));
        OnPropertyChanged(nameof(ToggleText));
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(IsSingleFile));
        OnPropertyChanged(nameof(SingleFile));
        OnPropertyChanged(nameof(SingleTitleText));
        OnPropertyChanged(nameof(SingleHasLineCounts));
        // 增删合计是字段上的计算属性：绑定在摘要到达前求值为 +0/−0，必须显式刷新。
        OnPropertyChanged(nameof(HeaderAddedText));
        OnPropertyChanged(nameof(HeaderDeletedText));
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(SummaryText));
    }

    private void SelectFile(WorkspaceChangedFileViewModel? file)
    {
        if (file is null) return;

        foreach (var item in Files)
            item.IsSelected = ReferenceEquals(item, file);

        _openDiff(file);
    }
}

/// <summary>会话窗口右侧的单文件对比面板；空态、非文本形态和加载失败独立呈现。</summary>
public sealed class WorkspaceDiffPanelViewModel : ObservableObject
{
    private readonly IWorkspaceChangesService? _changesService;
    private readonly IWorkspaceFileOpener?     _fileOpener;
    private readonly Action<Action>            _postToUi;
    private CancellationTokenSource? _loadCancellation;
    private int _requestId;

    private bool _isOpen;
    private bool _isLoading;
    private bool _hasError;
    private bool _hasDiff;
    private bool _hasNotice;
    private bool _hasFragments;
    private bool _hasContent;
    private bool _canOpenExternally;
    private WorkspaceChangedFileViewModel? _selectedFile;
    private string _titleText  = string.Empty;
    private string _sourceText = string.Empty;
    private string _noticeText = string.Empty;
    private string _errorText  = string.Empty;
    private string _contentText = string.Empty;
    private string? _absolutePath;
    private bool _fileExists;
    private Banned.CodeDiff.Services.DiffFile? _diffFile;

    public WorkspaceDiffPanelViewModel(
        IWorkspaceChangesService? changesService,
        IWorkspaceFileOpener?     fileOpener,
        Action<Action>            postToUi)
    {
        _changesService = changesService;
        _fileOpener     = fileOpener;
        _postToUi       = postToUi;
        CloseCommand           = new RelayCommand(Close);
        OpenInDefaultAppCommand = new RelayCommand(OpenInDefaultApp);
    }

    public ICommand CloseCommand { get; }

    public ICommand OpenInDefaultAppCommand { get; }

    public bool IsOpen
    {
        get => _isOpen;
        private set => SetProperty(ref _isOpen, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

    public bool HasDiff
    {
        get => _hasDiff;
        private set => SetProperty(ref _hasDiff, value);
    }

    public bool HasNotice
    {
        get => _hasNotice;
        private set => SetProperty(ref _hasNotice, value);
    }

    public string TitleText
    {
        get => _titleText;
        private set => SetProperty(ref _titleText, value);
    }

    /// <summary>数据来源标注：本轮文件差异 / 历史编辑片段 / 当前文件内容 / 不可用。</summary>
    public string SourceText
    {
        get => _sourceText;
        private set => SetProperty(ref _sourceText, value);
    }

    public string NoticeText
    {
        get => _noticeText;
        private set => SetProperty(ref _noticeText, value);
    }

    public string ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    /// <summary>历史编辑片段块（按事件顺序）；仅交付文件视图使用。</summary>
    public ObservableCollection<DeliverableFragmentItemViewModel> Fragments { get; } = [];

    public bool HasFragments
    {
        get => _hasFragments;
        private set => SetProperty(ref _hasFragments, value);
    }

    /// <summary>当前文件内容（只读文本）；仅交付文件视图使用。</summary>
    public string ContentText
    {
        get => _contentText;
        private set => SetProperty(ref _contentText, value);
    }

    public bool HasContent
    {
        get => _hasContent;
        private set => SetProperty(ref _hasContent, value);
    }

    /// <summary>文件在磁盘上存在时提供「用默认程序打开」的附加操作。</summary>
    public bool CanOpenExternally
    {
        get => _canOpenExternally;
        private set => SetProperty(ref _canOpenExternally, value);
    }

    public Banned.CodeDiff.Services.DiffFile? DiffFile
    {
        get => _diffFile;
        private set => SetProperty(ref _diffFile, value);
    }

    public async Task OpenAsync(WorkspaceChangedFileViewModel file)
    {
        var requestId = Interlocked.Increment(ref _requestId);
        _loadCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;

        _postToUi(() =>
        {
            if (_requestId != requestId) return;

            ResetDisplay();
            _selectedFile = file;
            IsOpen        = true;
            IsLoading     = true;
            TitleText     = file.Display;
        });

        var sessionId = file.SessionId;
        if (string.IsNullOrEmpty(sessionId) || _changesService is null)
        {
            _postToUi(() =>
            {
                if (_requestId != requestId) return;

                IsLoading  = false;
                HasNotice  = true;
                NoticeText = "当前没有可用的会话或改动服务。";
            });
            return;
        }

        try
        {
            var diff = await _changesService.GetDiffAsync(sessionId, file.AnnouncementSeq, file.Index, cancellation.Token)
                                          .ConfigureAwait(false);
            if (requestId != _requestId || cancellation.IsCancellationRequested) return;

            if (diff is null)
            {
                _postToUi(() =>
                {
                    if (_requestId != requestId) return;

                    IsLoading  = false;
                    HasNotice  = true;
                    NoticeText = "本地对比缓存已释放，无法读取本轮内容。";
                });
                return;
            }

            _postToUi(() =>
            {
                if (_requestId != requestId) return;

                ApplyDiff(diff);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _postToUi(() =>
            {
                if (_requestId != requestId) return;

                IsLoading  = false;
                HasError   = true;
                ErrorText  = exception.Message;
            });
        }
    }

    public void Close()
    {
        Interlocked.Increment(ref _requestId);
        _loadCancellation?.Cancel();
        ResetDisplay();
        _selectedFile = null;
        IsOpen        = false;
        IsLoading     = false;
    }

    /// <summary>清空全部显示状态；开面板与关闭共用，避免两条打开路径的状态残留。</summary>
    private void ResetDisplay()
    {
        HasError          = false;
        HasDiff           = false;
        HasNotice         = false;
        HasFragments      = false;
        HasContent        = false;
        CanOpenExternally = false;
        Fragments.Clear();
        ContentText  = string.Empty;
        ErrorText    = string.Empty;
        NoticeText   = string.Empty;
        SourceText   = string.Empty;
        DiffFile     = null;
        _absolutePath = null;
        _fileExists   = false;
    }

    /// <summary>
    ///     打开交付文件的应用内视图：显示当前磁盘文本，文件缺失时回退到已核实的历史编辑片段；
    ///     requestId 保证快速连点或切换时迟到的解析不覆盖当前显示。
    /// </summary>
    public async Task OpenDeliverableAsync(DeliverableViewRequest request)
    {
        var requestId = Interlocked.Increment(ref _requestId);
        _loadCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;

        _postToUi(() =>
        {
            if (_requestId != requestId) return;

            ResetDisplay();
            _selectedFile = null;
            IsOpen        = true;
            IsLoading     = true;
            TitleText     = DisplayName(request.DeclaredPath);
        });

        DeliverableView view;
        try
        {
            view = await Task.Run(() => DeliverableViewResolver.ResolveAsync(
                request.SessionId, request.Turn, request.DeclaredPath, request.Cwd,
                request.Entries, _changesService, cancellation.Token), cancellation.Token)
                                  .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            _postToUi(() =>
            {
                if (_requestId != requestId) return;

                IsLoading = false;
                HasError  = true;
                ErrorText = exception.Message;
            });
            return;
        }

        if (requestId != _requestId || cancellation.IsCancellationRequested) return;

        _postToUi(() =>
        {
            if (_requestId != requestId) return;

            ApplyDeliverableView(view);
        });
    }

    private void ApplyDeliverableView(DeliverableView view)
    {
        IsLoading         = false;
        _absolutePath     = view.AbsolutePath;
        _fileExists       = view.FileExists;
        CanOpenExternally = view.FileExists && _fileOpener is not null;
        SourceText        = view.SourceText;

        switch (view.Kind)
        {
            case DeliverableViewKind.FullDiff when view.Diff is not null :
                ApplyDiff(view.Diff);
                break;

            case DeliverableViewKind.Fragments :
                var display = DisplayName(view.AbsolutePath);
                foreach (var fragment in view.Fragments)
                    Fragments.Add(new DeliverableFragmentItemViewModel(
                        fragment.Seq, fragment.Kind, fragment.OldText, fragment.NewText, fragment.Note, display));
                HasFragments = Fragments.Count > 0;
                HasNotice    = true;
                NoticeText   = view.NoticeText;
                break;

            case DeliverableViewKind.CurrentText when view.CurrentText is not null :
                HasContent  = true;
                ContentText = view.CurrentText;
                HasNotice   = true;
                NoticeText  = view.NoticeText;
                break;

            default :
                HasNotice  = true;
                NoticeText = view.NoticeText;
                break;
        }
    }

    private void OpenInDefaultApp()
    {
        if (_absolutePath is null || !_fileExists || !File.Exists(_absolutePath))
        {
            HasError  = true;
            ErrorText = "文件当前不存在或不可访问，无法在默认程序中打开。";
            return;
        }

        if (_fileOpener is null)
        {
            HasError  = true;
            ErrorText = "系统文件打开服务不可用。";
            return;
        }

        try
        {
            _fileOpener.Open(_absolutePath);
            HasError = false;
            ErrorText = string.Empty;
        }
        catch (Exception exception)
        {
            HasError  = true;
            ErrorText = $"无法打开文件：{exception.Message}";
        }
    }

    /// <summary>路径尾段文件名；卡片标题与面板标题共用。</summary>
    internal static string DisplayName(string path)
    {
        var separator = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return separator < 0 ? path : path[(separator + 1)..];
    }

    private void ApplyDiff(WorkspaceFileDiff diff)
    {
        IsLoading = false;
        TitleText = diff.Display;
        switch (diff.Kind)
        {
            case WorkspaceDiffKind.Binary:
                HasNotice  = true;
                NoticeText = "二进制文件不显示内容对比。";
                break;

            case WorkspaceDiffKind.Oversized:
                HasNotice  = true;
                NoticeText = "文件超过快照大小上限，不显示内容对比。";
                break;

            default:
                var status = (diff.ExistedBefore, diff.ExistedAfter) switch
                {
                    (false, true) => "新增文件",
                    (true, false) => "删除文件",
                    _ => "修改文件"
                };
                NoticeText = diff.IsCoarse ? $"{status}；行级对比超过计算上限，已按变化区段替换。" : status;
                HasNotice  = true;
                if (diff.Hunks.Count == 0)
                {
                    NoticeText += " 内容没有差异。";
                    break;
                }

                HasDiff  = true;
                DiffFile = CreateDiffFile(diff);
                break;
        }
    }

    private static Banned.CodeDiff.Services.DiffFile CreateDiffFile(WorkspaceFileDiff diff)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"diff --git a/{diff.Display} b/{diff.Display}");
        builder.AppendLine(diff.ExistedBefore ? $"--- a/{diff.Display}" : "--- /dev/null");
        builder.AppendLine(diff.ExistedAfter ? $"+++ b/{diff.Display}" : "+++ /dev/null");
        foreach (var hunk in diff.Hunks)
        {
            builder.AppendLine($"@@ -{hunk.OldStart},{hunk.OldLines} +{hunk.NewStart},{hunk.NewLines} @@");
            foreach (var line in hunk.Lines)
                builder.AppendLine(line);
        }

        return new(diff.Display, string.Empty, diff.Display, string.Empty, [builder.ToString()]);
    }
}

/// <summary>交付文件应用内查看请求：卡片声明与解析数据所需的会话上下文。</summary>
/// <param name="SessionId">交付声明所属会话。</param>
/// <param name="Turn">交付声明所属轮次；片段只取同一轮的成功编辑。</param>
/// <param name="DeclaredPath">事件声明的原始路径。</param>
/// <param name="Cwd">会话工作目录，用于解析相对路径。</param>
/// <param name="Entries">当前已加载的会话条目快照（解析在后台线程进行）。</param>
public sealed record DeliverableViewRequest(
    string SessionId, long Turn, string DeclaredPath, string? Cwd, IReadOnlyList<ConversationEntry> Entries);

/// <summary>
///     历史编辑片段面板中的一块：一次成功调用内的一段已核实替换。差异由
///     CodeDiff 按片段内行号渲染；头部标注工具类型、事件 seq 与语义限制。
/// </summary>
public sealed class DeliverableFragmentItemViewModel
{
    public DeliverableFragmentItemViewModel(
        long seq, string kind, string? oldText, string newText, string? note, string displayPath)
    {
        var label = kind == "edit" ? "编辑" : kind == "write" ? "写入" : kind;
        HeaderText = note is { Length: > 0 } noteText
            ? $"{label} · 事件 seq {seq} · {noteText}"
            : $"{label} · 事件 seq {seq}";
        DiffFile = CreateFragmentDiffFile(oldText ?? string.Empty, newText, displayPath);
    }

    public string HeaderText { get; }

    public Banned.CodeDiff.Services.DiffFile DiffFile { get; }

    /// <summary>
    ///     片段差异文件：把片段前后文本交给 CodeDiff 渲染，diff 文本由片段自身的
    ///     行级对比生成，@@ 行号是片段内行号（原文件位置未记录，不伪造）。两侧内容
    ///     同时提供时 CodeDiff 用真实文本渲染，支持词级高亮。
    /// </summary>
    private static Banned.CodeDiff.Services.DiffFile CreateFragmentDiffFile(
        string oldText, string newText, string displayPath)
    {
        var workBudget = 1_000_000L;
        var hunks = LocalWorkspaceChangesService.CreateHunks(oldText, newText, ref workBudget, out _);
        var builder = new StringBuilder();
        builder.AppendLine($"diff --git a/{displayPath} b/{displayPath}");
        builder.AppendLine($"--- a/{displayPath}");
        builder.AppendLine($"+++ b/{displayPath}");
        foreach (var hunk in hunks)
        {
            builder.AppendLine($"@@ -{hunk.OldStart},{hunk.OldLines} +{hunk.NewStart},{hunk.NewLines} @@");
            foreach (var line in hunk.Lines)
                builder.AppendLine(line);
        }

        return new(displayPath, oldText, displayPath, newText, [builder.ToString()]);
    }
}
