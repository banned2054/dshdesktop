namespace DshDesktop.Harness.Models.Requests;

/// <summary>workspace/create 请求：登记已存在的目录（幂等，重复登记返回既有工作区）。</summary>
public sealed record WorkspaceCreateRequest(string Path);

/// <summary>workspace/archiveSession 请求：把已知会话移出工作区分组表面（归档）。</summary>
public sealed record WorkspaceArchiveSessionRequest(string SessionId);
