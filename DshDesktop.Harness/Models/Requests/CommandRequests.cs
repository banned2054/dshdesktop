namespace DshDesktop.Harness.Models.Requests;

/// <summary>
///     commands/execute 请求：向目标会话提交一条斜杠命令行（如「/permission auto」）。
///     wire 上 args 是扁平命名参数表（agentId/line/submittedAttachments，无 request 包装），
///     由 RpcEnvelope.BuildArgsRequest 承载。
/// </summary>
public sealed record CommandExecuteRequest(string AgentId, string Line, string[]? SubmittedAttachments = null);
