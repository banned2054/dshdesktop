using DshDesktop.Core.Models;
using System.Text.Json;

namespace DshDesktop.Core.Services;

/// <summary>
///     dsh 偏好设置的读写（settings RPC 域）。外观、语言、字号等由壳层渲染承担的偏好
///     不在其中，由桌面本地持久化负责。所有写操作返回写后的最新脱敏视图，状态以返回值为准。
/// </summary>
public interface ISettingsService
{
    /// <summary>
    ///     设置文档被更新；载荷为命名空间与新 revision。事件由后端 settings/document-updated
    ///     回流驱动，本端写入是否触发取决于后端是否向写入方回发（尚未在真实后端验证），消费方不应假定写入后必然收到。
    /// </summary>
    event EventHandler<SettingsDocumentUpdate>? DocumentUpdated;

    /// <summary>全量读取设置文档：命名空间视图、schema、脱敏生效值与 revision。</summary>
    Task<SettingsDescribeValue> DescribeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     把 patch 深合并进命名空间的用户段；patch 须为 JSON 对象。
    ///     expectedRevision 为 null 时无条件写，否则做乐观锁；冲突抛 <see cref="Exceptions.SettingsConflictException" />。
    /// </summary>
    Task<SettingsNamespaceView> UpdateAsync(string            ns, JsonElement patch, long? expectedRevision = null,
                                            CancellationToken cancellationToken = default);

    /// <summary>整段替换命名空间的用户段；expectedRevision 语义同 <see cref="UpdateAsync" />。</summary>
    Task<SettingsNamespaceView> ReplaceAsync(string            ns, JsonElement section, long? expectedRevision = null,
                                             CancellationToken cancellationToken = default);

    /// <summary>按路径编辑命名空间的用户段；ops 依序解析于服务端现存储段。</summary>
    Task<SettingsNamespaceView> MutateAsync(string            ns, IReadOnlyList<SettingsMutationOp> ops,
                                            long?             expectedRevision  = null,
                                            CancellationToken cancellationToken = default);

    /// <summary>物化 patch 文档并调用系统编辑器打开。</summary>
    Task OpenSettingsDocumentAsync(CancellationToken cancellationToken = default);
}
