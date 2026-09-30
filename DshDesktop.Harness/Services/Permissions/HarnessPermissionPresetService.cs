using DshDesktop.Core.Models;
using DshDesktop.Core.Services;
using DshDesktop.Harness.Json;
using DshDesktop.Harness.Models.Requests;
using DshDesktop.Harness.Models.Responses;
using DshDesktop.Harness.Services.Connection;

namespace DshDesktop.Harness.Services.Permissions;

/// <summary>
///     基于真实 Harness 协议的权限预设服务：目录经 permissionPresets/catalog 查询，
///     目录变化由 $events 的 permission-presets/catalog-changed emit 帧驱动重读；切换经
///     commands/execute 提交「/permission &lt;preset&gt;」命令行（与 WebUI 同一后端能力）。
///     切换的生效确认由 permissions 投影回流，本服务不持有权威状态。
/// </summary>
public sealed class HarnessPermissionPresetService : IPermissionPresetService
{
    private readonly HarnessConnection _connection;

    public HarnessPermissionPresetService(HarnessConnection connection)
    {
        _connection                          =  connection;
        _connection.PermissionCatalogChanged += OnPermissionCatalogChanged;
    }

    /// <summary>目录广播（permission-presets/catalog-changed）；订阅方应重读目录。</summary>
    public event EventHandler? CatalogChanged;

    /// <summary>查询权限预设目录（只读，可安全重试）。</summary>
    public async Task<PermissionCatalog> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        var value = await _connection.InvokeEmptyAsync("permissionPresets/catalog",
                                                       HarnessJsonContext.Default.PermissionCatalogWire,
                                                       cancellationToken)
                                     .ConfigureAwait(false);
        return ToCatalog(value);
    }

    /// <summary>
    ///     切换会话权限预设。返回 false 表示宿主没有 /permission 命令；preset 非法时命令
    ///     handler 返回 kind:error（RPC 仍成功），表现为投影保持旧值，不在本层抛出。
    /// </summary>
    public async Task<bool> SwitchPresetAsync(string            sessionId, string preset,
                                              CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("会话 id 不能为空。", nameof(sessionId));

        if (string.IsNullOrWhiteSpace(preset)) throw new ArgumentException("预设不能为空。", nameof(preset));

        var value = await _connection.InvokeArgsAsync("commands/execute",
                                                      new CommandExecuteRequest(sessionId, $"/permission {preset}", []),
                                                      HarnessJsonContext.Default.CommandExecuteRequest,
                                                      HarnessJsonContext.Default.CommandExecuteValue,
                                                      cancellationToken)
                                     .ConfigureAwait(false);
        // 结果 undefined 表示命令名不存在（WebUI 的 matched=false 语义）。
        return value is not null;
    }

    /// <summary>目录线上形态到应用模型。</summary>
    internal static PermissionCatalog ToCatalog(PermissionCatalogWire value)
    {
        return new PermissionCatalog(value.Options
                                          .Select(option => new PermissionPresetOption(option.Value, option.Name,
                                                      option.Description))
                                          .ToArray(),
                                     value.DefaultPreset);
    }

    private void OnPermissionCatalogChanged(object? sender, EventArgs e)
    {
        try
        {
            CatalogChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            // 事件处理器异常不影响连接层。
        }
    }
}
