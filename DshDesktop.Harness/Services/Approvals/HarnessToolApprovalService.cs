using DshDesktop.Core.Models;
using DshDesktop.Core.Services;
using DshDesktop.Harness.Models.Events;
using DshDesktop.Harness.Services.Connection;

namespace DshDesktop.Harness.Services.Approvals;

/// <summary>
///     基于 $events 瀑布的工具审批服务：approval/request 进入待决列表等待用户裁决，
///     cancel 帧与连接代重置清空对应条目。裁决经 $events/result 回执（kind=result）。
/// </summary>
public sealed class HarnessToolApprovalService : IToolApprovalService
{
    private readonly HarnessConnection _connection;
    private readonly HashSet<string>   _responding = [];
    private readonly Lock              _sync       = new();

    private List<PendingApproval> _pending = [];

    public HarnessToolApprovalService(HarnessConnection connection)
    {
        _connection                      =  connection;
        _connection.ApprovalRequested    += OnApprovalRequested;
        _connection.WaterfallCancelled   += OnWaterfallCancelled;
        _connection.EventGenerationReset += OnGenerationReset;
    }

    public event EventHandler? ApprovalsChanged;

    public IReadOnlyList<PendingApproval> Pending
    {
        get
        {
            lock (_sync)
            {
                return _pending.ToArray();
            }
        }
    }

    public async Task RespondAsync(string eventId, bool allowed, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_pending.All(approval => approval.EventId != eventId) ||
              !_responding.Add(eventId))
                return;
        }

        try
        {
            await _connection.ReplyWaterfallResultAsync(eventId, allowed, cancellationToken)
                             .ConfigureAwait(false);

            // 只有后端接受回执后才移除。传输失败时保留请求，允许用户重试，
            // 避免审批因瞬时断线而静默丢失。
            Remove(eventId);
        }
        finally
        {
            lock (_sync)
            {
                _responding.Remove(eventId);
            }
        }
    }

    private void OnApprovalRequested(object? sender, RemoteEventFrame.Waterfall waterfall)
    {
        if (RemoteEventJson.TryGetApprovalRequest(waterfall) is not { } request)
        {
            // 载荷不可呈现（缺 toolName）：无法构成有意义的裁决界面，按拒绝回执避免悬挂。
            _ = RejectUnpresentableAsync(waterfall.EventId);
            return;
        }

        lock (_sync)
        {
            if (_pending.Any(approval => approval.EventId == waterfall.EventId)) return;

            _pending =
            [
                .. _pending,
                new PendingApproval(waterfall.EventId, waterfall.AgentId, request.ToolName,
                                    request.CallId, request.Reason, DateTimeOffset.Now)
            ];
        }

        RaiseApprovalsChanged();
    }

    private void OnWaterfallCancelled(object? sender, string eventId)
    {
        Remove(eventId);
    }

    private void OnGenerationReset(object? sender, EventArgs e)
    {
        lock (_sync)
        {
            _pending = [];
            _responding.Clear();
        }

        RaiseApprovalsChanged();
    }

    private void Remove(string eventId)
    {
        lock (_sync)
        {
            if (_pending.All(approval => approval.EventId != eventId)) return;

            _pending = _pending.Where(approval => approval.EventId != eventId).ToList();
        }

        RaiseApprovalsChanged();
    }

    private void RaiseApprovalsChanged()
    {
        try
        {
            ApprovalsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            // 事件处理器异常不影响订阅管理。
        }
    }

    private async Task RejectUnpresentableAsync(string eventId)
    {
        try
        {
            await _connection.ReplyWaterfallResultAsync(eventId, false, CancellationToken.None)
                             .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 无法呈现的请求没有可供用户操作的条目；连接恢复后由后端超时兜底。
        }
    }
}
