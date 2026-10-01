using System.Text.Json;

namespace DshDesktop.Core.Models;

/// <summary>
///     一个设置命名空间的脱敏视图。Value 为脱敏后的生效解析值；Base/User 为可选分层，
///     User 段中字段的存在即用户覆盖标记。Secrets 标记值中的密钥引用路径及其配置状态。
/// </summary>
public sealed record SettingsNamespaceView(
    string                             Ns,
    bool                               AutoGenerate,
    JsonElement                        Schema,
    JsonElement                        Value,
    string                             Applies,
    long                               Revision,
    JsonElement?                       Base    = null,
    JsonElement?                       User    = null,
    IReadOnlyList<SettingsSecretInfo>? Secrets = null);
