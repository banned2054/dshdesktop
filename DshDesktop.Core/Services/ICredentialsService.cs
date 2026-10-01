using DshDesktop.Core.Models;

namespace DshDesktop.Core.Services;

/// <summary>
///     凭据引用的写入与状态查询（credentials RPC 域）。密钥值单向流入：
///     写入后任何读路径都拿不到值，只能查询 configured/source/writable。
/// </summary>
public interface ICredentialsService
{
    /// <summary>凭据引用被更新。事件由后端 credentials/reference-updated 回流驱动，本端写入/移除是否触发取决于后端是否向写入方回发（尚未在真实后端验证），消费方不应假定写入后必然收到。</summary>
    event EventHandler? ReferenceUpdated;

    /// <summary>批量查询引用的解析状态；一次最多 64 个，超限或语法错整包拒。</summary>
    Task<IReadOnlyDictionary<string, CredentialStatus>> DescribeAsync(IReadOnlyList<string> references,
                                                                      CancellationToken cancellationToken = default);

    /// <summary>写入凭据值（不可读取回）；引用语法 ^[A-Za-z_][A-Za-z0-9_]*$，值非空。</summary>
    Task SetAsync(string reference, string value, CancellationToken cancellationToken = default);

    /// <summary>移除凭据引用。</summary>
    Task UnsetAsync(string reference, CancellationToken cancellationToken = default);
}
