namespace DshDesktop.Core.Exceptions;

/// <summary>provider 拒写（settings/rejected）：命名空间非法、只读、schema 校验不过等；Message 可直接展示。</summary>
public sealed class SettingsRejectedException(string ns, string message) : Exception(message)
{
    public string Ns { get; } = ns;
}
