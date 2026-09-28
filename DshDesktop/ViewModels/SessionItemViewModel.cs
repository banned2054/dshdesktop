using DshDesktop.Core.Models;

namespace DshDesktop.ViewModels;

public sealed class SessionItemViewModel(SessionSummary summary) : ObservableObject
{
    private bool             _isCurrent;
    private bool             _running     = summary.Running;
    private SessionBlankState _blankState = summary.BlankState;
    private string?          _title       = summary.Title;
    private string           _updatedText = FormatUpdatedText(summary.UpdatedAt);

    public string Id { get; } = summary.Id;

    /// <summary>空白三态（确认空白/未知/已开始）；随摘要刷新与过渡信号更新。</summary>
    public SessionBlankState BlankState
    {
        get => _blankState;
        private set => SetProperty(ref _blankState, value);
    }

    public string? Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string TitleText => string.IsNullOrWhiteSpace(Title) ? "新对话" : Title;

    /// <summary>该会话是否为当前打开的会话；由主视图在选中变化时维护，驱动行高亮。</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        internal set => SetProperty(ref _isCurrent, value);
    }

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
        // 列表摘要可能尚未携带标题投影；不用空值覆盖本地已采纳的标题。
        if (!string.IsNullOrWhiteSpace(summary.Title)) Title = summary.Title;

        BlankState  = summary.BlankState;
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

    private static string FormatUpdatedText(DateTimeOffset updatedAt)
    {
        return updatedAt.Date == DateTimeOffset.Now.Date
            ? updatedAt.ToLocalTime().ToString("HH:mm")
            : updatedAt.ToLocalTime().ToString("MM-dd");
    }
}
