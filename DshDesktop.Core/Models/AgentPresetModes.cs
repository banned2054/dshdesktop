namespace DshDesktop.Core.Models;

/// <summary>
///     dsh 内置 Agent 预设（界面上的"模式"）的 wire id。新会话经 session/create 的
///     agentPreset 字段在创建时绑定；会话开始（首条消息被接受）后由后端锁定，
///     不可再更改。顺序即界面菜单顺序。用户自定义预设经 agentPresets/list 下发，
///     接入后并入菜单，不在此列。
/// </summary>
public static class AgentPresetModes
{
    public const string Standard = "standard";
    public const string Ptc      = "ptc";
    public const string Minimal  = "minimal";
    public const string Cordis   = "cordis";

    /// <summary>dsh 内置默认（agent-preset-registry 出厂 defaultId）。</summary>
    public const string Default = Standard;

    public static IReadOnlyList<string> All { get; } = [Standard, Ptc, Minimal, Cordis];
}
