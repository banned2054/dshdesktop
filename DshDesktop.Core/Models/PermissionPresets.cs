namespace DshDesktop.Core.Models;

/// <summary>
///     权限预设的 wire 取值常量。内置三键由 base 组合配置；auto 仅在后端存在实验性
///     Auto review 集成时出现在 catalog，custom 是「当前组合不匹配任何预设」的派生态，
///     后端永远不会把它作为可选项下发。
/// </summary>
public static class PermissionPresetValues
{
    public const string ReadOnly       = "read-only";
    public const string WorkspaceWrite = "workspace-write";
    public const string FullAccess     = "danger-full-access";
    public const string AutoReview     = "auto";
    public const string Custom         = "custom";

    /// <summary>切换到该预设需要显式风险确认（对齐 WebUI PermissionSelect 的确认门，含当前值比较之外的任意来源）。</summary>
    public static bool RequiresConfirmation(string value)
    {
        return value is FullAccess or AutoReview;
    }
}

/// <summary>权限预设目录项（permissionPresets/catalog 的 options 行）。Name 是宿主提供的展示名。</summary>
public sealed record PermissionPresetOption(string Value, string Name, string? Description = null);

/// <summary>
///     权限预设目录：Options 是当前会话可切换的预设集合；DefaultPreset 是后端配置的
///     新会话默认值（仅参考，本端不据此设置当前权限）。
/// </summary>
public sealed record PermissionCatalog(IReadOnlyList<PermissionPresetOption> Options, string DefaultPreset);
