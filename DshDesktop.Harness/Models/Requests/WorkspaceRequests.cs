namespace DshDesktop.Harness.Models.Requests;

/// <summary>workspace/create 请求：登记已存在的目录（幂等，重复登记返回既有工作区）。</summary>
public sealed record WorkspaceCreateRequest(string Path);

/// <summary>workspace/archiveSession 请求：把已知会话移出工作区分组表面（归档）。</summary>
public sealed record WorkspaceArchiveSessionRequest(string SessionId);

/// <summary>workspace/rename 请求：按 id 重命名工作区显示名（服务端 trim 并查重）。</summary>
public sealed record WorkspaceRenameRequest(string WorkspaceId, string Title);

/// <summary>workspace/delete 请求：从注册表移除工作区（目录与会话保留）。</summary>
public sealed record WorkspaceDeleteRequest(string WorkspaceId);
