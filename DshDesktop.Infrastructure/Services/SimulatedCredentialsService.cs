using DshDesktop.Core.Models;
using DshDesktop.Core.Services;

namespace DshDesktop.Infrastructure.Services;

/// <summary>
///     模拟凭据服务：内存保存引用与值的映射，语义对齐 credentials 域
///     （值不可读取回、describe 批量上限与语法校验整包拒、set 值非空）。
/// </summary>
public sealed class SimulatedCredentialsService : ICredentialsService
{
    private const int MaxDescribeBatch = 64;

    private readonly Lock                       _syncRoot = new();
    private readonly Dictionary<string, string> _values   = new(StringComparer.Ordinal);

    public event EventHandler? ReferenceUpdated;

    public Task<IReadOnlyDictionary<string, CredentialStatus>> DescribeAsync(
        IReadOnlyList<string> references, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (references.Count > MaxDescribeBatch)
            throw new ArgumentException($"refs 一次最多 {MaxDescribeBatch} 个。", nameof(references));

        lock (_syncRoot)
        {
            var snapshot = new Dictionary<string, CredentialStatus>(StringComparer.Ordinal);
            foreach (var reference in references)
            {
                ValidateReference(reference);
                snapshot[reference] = _values.TryGetValue(reference, out _)
                    ? new CredentialStatus(true, "simulated", true)
                    : new CredentialStatus(false, null, true);
            }

            IReadOnlyDictionary<string, CredentialStatus> result = snapshot;
            return Task.FromResult(result);
        }
    }

    public Task SetAsync(string reference, string value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateReference(reference);
        if (value.Length == 0)
            throw new ArgumentException("凭据值不能为空。", nameof(value));

        lock (_syncRoot)
        {
            _values[reference] = value;
        }

        ReferenceUpdated?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task UnsetAsync(string reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateReference(reference);

        lock (_syncRoot)
        {
            _values.Remove(reference);
        }

        ReferenceUpdated?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private static void ValidateReference(string reference)
    {
        if (!IsValidReference(reference))
            throw new ArgumentException($"凭据引用语法非法：{reference}", nameof(reference));
    }

    /// <summary>引用语法 ^[A-Za-z_][A-Za-z0-9_]*$，与后端 credentialRef 一致。</summary>
    private static bool IsValidReference(string reference)
    {
        if (reference.Length == 0)
            return false;
        if (!char.IsAsciiLetter(reference[0]) && reference[0] != '_')
            return false;
        for (var index = 1; index < reference.Length; index++)
        {
            var character = reference[index];
            if (!char.IsAsciiLetterOrDigit(character) && character != '_')
                return false;
        }

        return true;
    }
}
