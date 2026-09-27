namespace DshDesktop.Core.Models;

/// <summary>
///     session/selectModel 的推理档位取值（reasoningEffort wire 字符串）。
///     顺序即界面菜单顺序；档位是否有效由后端按模型裁决，客户端以后端回声为准。
/// </summary>
public static class ReasoningEffortLevels
{
    public const string Off  = "off";
    public const string Low  = "low";
    public const string High = "high";
    public const string Max  = "max";

    public static IReadOnlyList<string> All { get; } = [Off, Low, High, Max];
}
