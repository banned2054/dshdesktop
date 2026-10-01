namespace DshDesktop.Harness.Models.Requests;

/// <summary>credentials/set 请求：值写入后不可读取回。</summary>
public sealed record CredentialsSetRequest(string Ref, string Value);

/// <summary>credentials/unset 请求：移除凭据引用。</summary>
public sealed record CredentialsUnsetRequest(string Ref);
