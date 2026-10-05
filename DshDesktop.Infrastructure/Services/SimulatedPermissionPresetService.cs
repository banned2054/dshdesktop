using DshDesktop.Core.Models;
using DshDesktop.Core.Services;

namespace DshDesktop.Infrastructure.Services;

/// <summary>
///     模拟权限预设服务：内存目录 + 切换记账，行为对齐真实服务的请求语义。切换只记账并
///     广播 PresetApplied（由组装层接到会话服务模拟 permissions 投影回流），权威状态仍由
///     会话更新通道承载——与真实后端一致，切换确认不经过本服务。
/// </summary>
public sealed class SimulatedPermissionPresetService : IPermissionPresetService
{
    /// <summary>模拟目录：与真实 base 组合相同的三预设（name 即裸键名，展示映射在客户端做）。</summary>
    private static readonly PermissionCatalog Catalog = new(
    [
        new PermissionPresetOption(PermissionPresetValues.ReadOnly, PermissionPresetValues.ReadOnly),
        new PermissionPresetOption(PermissionPresetValues.WorkspaceWrite, PermissionPresetValues.WorkspaceWrite),
        new PermissionPresetOption(PermissionPresetValues.FullAccess, PermissionPresetValues.FullAccess)
    ], PermissionPresetValues.WorkspaceWrite);

    private readonly List<(string SessionId, string Preset)> _switches = [];

    private readonly Lock _syncRoot = new();

    /// <summary>已下达的切换请求（按到达顺序）；供测试断言。</summary>
    public IReadOnlyList<(string SessionId, string Preset)> Switches
    {
        get
        {
            lock (_syncRoot)
            {
                return _switches.ToArray();
            }
        }
    }

    public event EventHandler? CatalogChanged;

    public Task<PermissionCatalog> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Catalog);
    }

    public Task<bool> SwitchPresetAsync(string sessionId, string preset, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("会话 id 不能为空。", nameof(sessionId));

        if (string.IsNullOrWhiteSpace(preset)) throw new ArgumentException("预设不能为空。", nameof(preset));

        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            _switches.Add((sessionId, preset));
        }

        PresetApplied?.Invoke(this, (sessionId, preset));
        return Task.FromResult(true);
    }

    /// <summary>模拟后端接受了切换（宿主有 /permission 命令）；组装层据此推送投影回流。</summary>
    public event EventHandler<(string SessionId, string Preset)>? PresetApplied;

    /// <summary>模拟后端权限预设目录变化（驱动客户端重读目录）。</summary>
    public void RaiseCatalogChanged()
    {
        CatalogChanged?.Invoke(this, EventArgs.Empty);
    }
}
