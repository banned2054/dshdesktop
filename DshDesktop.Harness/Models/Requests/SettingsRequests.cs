using System.Text.Json;

namespace DshDesktop.Harness.Models.Requests;

/// <summary>settings/update 请求：patch 深合并进该命名空间的用户段；expectedRevision 缺省（null 序列化省略）表示无条件写。</summary>
public sealed record SettingsUpdateRequest(string Ns, JsonElement Patch, long? ExpectedRevision = null);

/// <summary>settings/replace 请求：整段替换该命名空间的用户段。</summary>
public sealed record SettingsReplaceRequest(string Ns, JsonElement Section, long? ExpectedRevision = null);

/// <summary>settings/mutate 请求：ops 按序解析于服务端现存储段。</summary>
public sealed record SettingsMutateRequest(
    string                           Ns,
    IReadOnlyList<SettingsOpRequest> Ops,
    long?                            ExpectedRevision = null);

/// <summary>settings/mutate 的单个路径操作；unset 不携带 value（WhenWritingNull 自动省略）。</summary>
public sealed record SettingsOpRequest(string Op, IReadOnlyList<string> Path, JsonElement? Value = null);
