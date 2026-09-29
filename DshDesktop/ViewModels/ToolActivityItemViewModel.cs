using DshDesktop.Core.Models;

namespace DshDesktop.ViewModels;

/// <summary>
///     工具调用卡片：名称、参数与结果的状态展示。折叠态显示单行状态，
///     展开态显示参数与结果文本；运行中卡片在结果事件到达后原地落定。
/// </summary>
public sealed class ToolActivityItemViewModel : ConversationItemViewModel
{
    /// <summary>结果文本的截断上限；完整文本不再重复保存，避免长会话双份大字符串。</summary>
    private const int ResultPreviewLimit = 2000;

    private string? _errorReason;
    private bool    _isExpanded;
    private string? _resultText;

    private ToolActivityStatus _status;

    public ToolActivityItemViewModel(ToolActivity activity) : base(activity.Seq)
    {
        CallId               = activity.CallId;
        Name                 = activity.Name;
        ArgumentsText        = activity.ArgumentsJson;
        _status              = activity.Status;
        _resultText          = activity.ResultText;
        _errorReason         = activity.ErrorReason;
        CreatedAtText        = activity.CreatedAt.ToLocalTime().ToString("HH:mm");
        ToggleDetailsCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public string CallId { get; }

    public string Name { get; private set; }

    public string? ArgumentsText { get; private set; }

    public string CreatedAtText { get; }

    public ToolActivityStatus Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(IsFailed));
            }
        }
    }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "工具调用" : Name;

    public string StatusText => Status switch
    {
        ToolActivityStatus.Running   => "运行中…",
        ToolActivityStatus.Succeeded => "已完成",
        ToolActivityStatus.Failed    => "失败",
        _                            => string.Empty
    };

    public bool IsRunning => Status == ToolActivityStatus.Running;

    public bool IsFailed => Status == ToolActivityStatus.Failed;

    public bool IsSucceeded => Status == ToolActivityStatus.Succeeded;

    public string? ResultText
    {
        get => _resultText;
        private set => SetProperty(ref _resultText, value);
    }

    public string? ErrorReason
    {
        get => _errorReason;
        private set
        {
            if (SetProperty(ref _errorReason, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorReason) || IsFailed;

    /// <summary>结果文本的截断展示，仅在展开时渲染。</summary>
    public string ResultPreview => Truncate(ResultText);

    public string ArgumentsPreview => Truncate(ArgumentsText);

    public bool HasDetails =>
        !string.IsNullOrWhiteSpace(ArgumentsText) ||
        !string.IsNullOrWhiteSpace(ResultText)    ||
        HasError;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value)) OnPropertyChanged(nameof(DetailsToggleText));
        }
    }

    public RelayCommand ToggleDetailsCommand { get; }

    public string DetailsToggleText => IsExpanded ? "收起" : "详情";

    /// <summary>结果事件到达：保持发起位置与参数，落定状态与结果。</summary>
    public void Settle(ToolActivity settled)
    {
        if (!string.IsNullOrWhiteSpace(settled.Name))
        {
            Name = settled.Name;
            OnPropertyChanged(nameof(DisplayName));
        }

        if (!string.IsNullOrWhiteSpace(settled.ArgumentsJson)) ArgumentsText = settled.ArgumentsJson;

        Status      = settled.Status;
        ResultText  = settled.ResultText;
        ErrorReason = settled.ErrorReason;
        OnPropertyChanged(nameof(ResultPreview));
        OnPropertyChanged(nameof(ArgumentsPreview));
        OnPropertyChanged(nameof(HasDetails));
    }

    private static string Truncate(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= ResultPreviewLimit) return text ?? string.Empty;

        return $"{text[..ResultPreviewLimit]}…（已截断）";
    }
}
