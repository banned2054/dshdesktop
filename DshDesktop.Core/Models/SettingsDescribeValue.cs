namespace DshDesktop.Core.Models;

/// <summary>settings/describe 的全量快照。上游 patch 文档恒存在（hasDocument 恒 true），本端不携带该常量。</summary>
public sealed record SettingsDescribeValue(bool Writable, IReadOnlyList<SettingsNamespaceView> Namespaces);
