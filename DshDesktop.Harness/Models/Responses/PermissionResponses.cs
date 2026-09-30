namespace DshDesktop.Harness.Models.Responses;

/// <summary>commands/execute 返回值（CommandExecution）。结果为 undefined 表示宿主没有该命令（matched=false）。</summary>
public sealed record CommandExecuteValue(string? Kind = null, string? Text = null);

/// <summary>permissionPresets/catalog 的选项行（线上形态）。</summary>
public sealed record PermissionPresetOptionWire(string Value, string Name, string? Description = null);

/// <summary>
///     permissionPresets/catalog 返回值。defaultOptions（可作为新会话默认值的配置表，
///     不含 auto）当前无消费方，反序列化时忽略。
/// </summary>
public sealed record PermissionCatalogWire(IReadOnlyList<PermissionPresetOptionWire> Options, string DefaultPreset);
