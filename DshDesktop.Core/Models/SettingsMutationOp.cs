using System.Text.Json;

namespace DshDesktop.Core.Models;

/// <summary>settings/mutate 的单个路径操作；unset 不携带值。</summary>
public sealed record SettingsMutationOp(string Op, IReadOnlyList<string> Path, JsonElement? Value)
{
    public const string SetOp   = "set";
    public const string UnsetOp = "unset";

    /// <summary>set 操作：把 value 写到 path（缺失的中间层级按对象补齐）。</summary>
    public static SettingsMutationOp Set(IReadOnlyList<string> path, JsonElement value)
    {
        return new SettingsMutationOp(SetOp, path, value);
    }

    /// <summary>unset 操作：移除 path。</summary>
    public static SettingsMutationOp Unset(IReadOnlyList<string> path)
    {
        return new SettingsMutationOp(UnsetOp, path, null);
    }
}
