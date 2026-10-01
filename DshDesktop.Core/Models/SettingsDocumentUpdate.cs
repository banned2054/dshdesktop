namespace DshDesktop.Core.Models;

/// <summary>一次设置文档更新：命名空间与新 revision（settings/document-updated 的核心模型形态）。</summary>
public sealed record SettingsDocumentUpdate(string Ns, long Revision);
