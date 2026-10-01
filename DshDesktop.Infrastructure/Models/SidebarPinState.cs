namespace DshDesktop.Infrastructure.Models;

/// <summary>置顶配置文件的落盘形态：置顶会话与置顶工作区两组 id（各自最近置顶在前）。</summary>
public sealed record SidebarPinState(IReadOnlyList<string> Sessions, IReadOnlyList<string> Workspaces);
