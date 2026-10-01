namespace DshDesktop.Core.Exceptions;

/// <summary>乐观锁冲突（settings/conflict）：读到的 revision 已过期。语义是重读重放，不是格式错误。</summary>
public sealed class SettingsConflictException(string ns, long expected, long actual)
    : Exception($"设置命名空间 {ns} 已被并发修改：期望 revision {expected}，实际 {actual}。")
{
    public string Ns { get; } = ns;

    public long Expected { get; } = expected;

    public long Actual { get; } = actual;
}
