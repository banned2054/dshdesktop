namespace DshDesktop.Core.Services;

/// <summary>
///     侧栏本地置顶注册表：会话与工作区的置顶是本项目自有方案，不消费后端的
///     workspace 置顶集合。集合在进程内即时生效并持久化到本项目的配置文件；
///     多个客户端实例并发变更时以磁盘最新状态为合并基准，互不覆盖。
///     置顶分类的投影（工作区在上、会话在下）由消费方按各自的权威时间重建。
/// </summary>
public interface ISidebarPinService
{
    /// <summary>置顶集合发生变化（置顶或取消置顶成功）时触发。</summary>
    event EventHandler? PinsChanged;

    /// <summary>置顶会话 id 集合（最近置顶在前的存储序）。</summary>
    IReadOnlyList<string> PinnedSessionIds { get; }

    /// <summary>置顶工作区 id 集合（最近置顶在前的存储序）。</summary>
    IReadOnlyList<string> PinnedWorkspaceIds { get; }

    /// <summary>置顶会话：集合前插并落盘；磁盘写入失败时集合不变并抛出。</summary>
    Task PinSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>取消置顶会话：集合移除并落盘；失败语义同 <see cref="PinSessionAsync" />。</summary>
    Task UnpinSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>置顶工作区：集合前插并落盘；失败语义同 <see cref="PinSessionAsync" />。</summary>
    Task PinWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default);

    /// <summary>取消置顶工作区：集合移除并落盘；失败语义同 <see cref="PinSessionAsync" />。</summary>
    Task UnpinWorkspaceAsync(string workspaceId, CancellationToken cancellationToken = default);
}
