namespace DshDesktop.Core.Models;

/// <summary>schema 声明的一个密钥槽位：Path 为值中的引用路径，Set 表示该引用已配置；密钥值本身不可读取。</summary>
public sealed record SettingsSecretInfo(IReadOnlyList<string> Path, bool Set);
