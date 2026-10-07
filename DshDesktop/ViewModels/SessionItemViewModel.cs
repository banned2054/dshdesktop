using DshDesktop.Core.Models;

namespace DshDesktop.ViewModels;

public sealed class SessionItemViewModel(SessionSummary summary) : ObservableObject
{
    private SessionBlankState _blankState = summary.BlankState;

    private bool           _isCurrent;
    private bool           _pinned;
    private bool           _running     = summary.Running;
    private string?        _title       = summary.Title;
    private DateTimeOffset _updatedAt   = summary.UpdatedAt;
    private string         _updatedText = FormatUpdatedText(summary.UpdatedAt);

    public string Id { get; } = summary.Id;

    /// <summary>后端目录中该会话所属工作目录，供本地轮次快照使用。</summary>
    public string? Cwd { get; private set; } = summary.Cwd;

    /// <summary>会话最近更新时间（摘要投影）；置顶分类内同类行按它降序。</summary>
    public DateTimeOffset UpdatedAt
    {
        get => _updatedAt;
        private set => SetProperty(ref _updatedAt, value);
    }

    /// <summary>空白三态（确认空白/未知/已开始）；随摘要刷新与过渡信号更新。</summary>
    public SessionBlankState BlankState
    {
        get => _blankState;
        private set
        {
            if (SetProperty(ref _blankState, value)) OnPropertyChanged(nameof(IsBlank));
        }
    }

    public string? Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string TitleText => string.IsNullOrWhiteSpace(Title) ? "新对话" : Title;

    /// <summary>已确认空白的会话行不显示相对时间与悬浮操作（对齐参考客户端 blank 行）。</summary>
    public bool IsBlank => BlankState == SessionBlankState.ConfirmedBlank;

    /// <summary>该会话是否为当前打开的会话；由主视图在选中变化时维护，驱动行高亮。</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        internal set => SetProperty(ref _isCurrent, value);
    }

    /// <summary>
    ///     是否置顶（注册表级集合投影，由侧栏在重建行投影时同步）：驱动置顶标记、
    ///     悬浮置顶按钮形态与置顶分类排序（置顶分类内按更新时间降序，不按置顶先后）。
    /// </summary>
    public bool Pinned
    {
        get => _pinned;
        internal set
        {
            if (SetProperty(ref _pinned, value)) OnPropertyChanged(nameof(PinActionText));
        }
    }

    /// <summary>置顶动作的菜单/按钮文案：未置顶「置顶会话」，已置顶「取消置顶」。</summary>
    public string PinActionText => Pinned ? "取消置顶" : "置顶会话";

    public string UpdatedText
    {
        get => _updatedText;
        private set => SetProperty(ref _updatedText, value);
    }

    public bool Running
    {
        get => _running;
        private set => SetProperty(ref _running, value);
    }

    public string RunningText => Running ? "生成中" : string.Empty;

    public void UpdateSummary(SessionSummary summary)
    {
        AdoptCwd(summary.Cwd);
        // 列表摘要可能尚未携带标题投影；不用空值覆盖本地已采纳的标题。
        if (!string.IsNullOrWhiteSpace(summary.Title)) Title = summary.Title;

        BlankState  = summary.BlankState;
        UpdatedAt   = summary.UpdatedAt;
        UpdatedText = FormatUpdatedText(summary.UpdatedAt);
        Running     = summary.Running;
    }

    /// <summary>本端过渡信号（发送被接受等）立即把会话提升为已开始；不可逆。</summary>
    public void MarkEngaged()
    {
        BlankState = SessionBlankState.Engaged;
    }

    /// <summary>仅在没有本地标题时采用快照/事件提供的标题。</summary>
    public void AdoptTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(title)) Title = title;
    }

    /// <summary>follow header 与目录摘要均来自该会话；缺失字段不清空已知目录。</summary>
    public void AdoptCwd(string? cwd)
    {
        if (!string.IsNullOrWhiteSpace(cwd)) Cwd = cwd;
    }

    /// <summary>
    ///     重命名确认后就地采用服务端规范化标题（权威值，本地先落投影）；
    ///     后续列表刷新携带同值回流，幂等对齐。
    /// </summary>
    internal void ApplyRenamedTitle(string title)
    {
        if (!string.IsNullOrWhiteSpace(title)) Title = title;
    }

    private static string FormatUpdatedText(DateTimeOffset updatedAt)
    {
        return updatedAt.Date == DateTimeOffset.Now.Date
            ? updatedAt.ToLocalTime().ToString("HH:mm")
            : updatedAt.ToLocalTime().ToString("MM-dd");
    }
}
