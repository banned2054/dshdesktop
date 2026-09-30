using DshDesktop.Core.Models;

namespace DshDesktop.Core.Services;

/// <summary>
///     权限预设目录与会话权限切换（Composer「执行权限」下拉的后端通道）。
///     目录是 Host 级只读查询（permissionPresets/catalog）；切换是会话级命令——经
///     commands/execute 提交「/permission &lt;preset&gt;」命令行，与 WebUI 使用同一后端能力。
///     切换的生效确认由 permissions 投影回流承担：本契约只承载请求通道，
///     不是权威状态的来源。
/// </summary>
public interface IPermissionPresetService
{
    /// <summary>后端广播 permission-presets/catalog-changed 时触发；订阅方应重新拉取目录。</summary>
    event EventHandler? CatalogChanged;

    /// <summary>查询权限预设目录。只读，可安全重试。</summary>
    Task<PermissionCatalog> GetCatalogAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     为会话切换权限预设。返回 false 表示宿主未提供 /permission 命令；preset 非法等
    ///     业务失败不在此暴露（RPC 本身成功），表现为 permissions 投影保持旧值。
    ///     该命令会写入会话日志：结果不确定的失败不得自动换参重试。
    /// </summary>
    Task<bool> SwitchPresetAsync(string sessionId, string preset, CancellationToken cancellationToken = default);
}
