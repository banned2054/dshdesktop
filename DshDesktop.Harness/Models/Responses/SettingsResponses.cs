using System.Text.Json;

namespace DshDesktop.Harness.Models.Responses;

/// <summary>settings/describe 返回值。hasDocument 上游恒为 true，本端不消费。</summary>
public sealed record SettingsDescribeValueWire(bool                         Writable,
                                               bool                         HasDocument,
                                               IReadOnlyList<SettingsNamespaceViewWire> Namespaces);

/// <summary>设置命名空间视图（线上形态）：schema 为序列化的 schemastery 包络，value 为脱敏后的生效值。</summary>
public sealed record SettingsNamespaceViewWire(
    string                             Ns,
    bool                               AutoGenerate,
    JsonElement                        Schema,
    JsonElement                        Value,
    string                             Applies,
    long                               Revision,
    JsonElement?                       Base    = null,
    JsonElement?                       User    = null,
    IReadOnlyList<SettingsSecretWire>? Secrets = null);

/// <summary>schema 声明的密钥槽位；set 表示该引用已配置，值本身不可读取。</summary>
public sealed record SettingsSecretWire(IReadOnlyList<string> Path, bool Set);

/// <summary>settings/openSettingsDocument 返回值（上游恒为 {opened:true}）。</summary>
public sealed record SettingsOpenDocumentValue(bool Opened);
