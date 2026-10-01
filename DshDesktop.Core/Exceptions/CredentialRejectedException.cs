namespace DshDesktop.Core.Exceptions;

/// <summary>凭据写失败（credential/rejected）：如只读源遮蔽该引用；Message 须原样展示给用户。</summary>
public sealed class CredentialRejectedException(string reference, string message) : Exception(message)
{
    public string Reference { get; } = reference;
}
