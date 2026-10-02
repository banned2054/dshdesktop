using DshDesktop.Core.Models;
using System.Text.Json;

namespace DshDesktop.ViewModels.Settings;

/// <summary>通用设置分区：外观/展示行为/权限/开关等即时写行。</summary>
public sealed class GeneralSettingsSectionViewModel : SettingsSectionViewModel
{
    public GeneralSettingsSectionViewModel() : base("general", "通用设置")
    {
        AppearanceRow = new SettingsSegmentRowViewModel("ui-theme", "preference", "外观", "选择应用的界面主题");
        TranscriptViewRow = new SettingsChoiceRowViewModel("ui-chat", "transcriptView", "工作步骤展示", "会话中每轮工作的详细程度",
        [
            ("compact", "简洁"), ("standard", "标准"), ("detailed", "详细"), ("verbose", "完全展开")
        ], ResolveTranscriptLabel);
        PerformanceUsageRow = new SettingsChoiceRowViewModel("ui-chat", "performanceUsage", "性能与用量",
                                                             "性能与 token 用量的展示粒度",
                                                             [("compact", "简洁"), ("detailed", "详细")],
                                                             ResolveChoiceLabel);
        BusyEnterRow = new SettingsChoiceRowViewModel("ui-conversation", "busyEnter", "繁忙时的发送行为",
                                                      "Agent 运行中按 Enter 的行为", [("queue", "排队发送"), ("steer", "插话发送")],
                                                      ResolveChoiceLabel);
        // 选中含 "full" 的预设（full-access/fullAccess）须经风险确认卡二次确认。
        PermissionRow = new SettingsChoiceRowViewModel("permission", "defaultPreset", "权限", "选择新会话的默认权限模式", [],
                                                       ResolvePermissionLabel,
                                                       value =>
                                                           value.Contains("full", StringComparison.OrdinalIgnoreCase));
        CodeWorkViewRow = new SettingsToggleRowViewModel("ui-settings", "enabled", "显示代码工作视图", "开启后，显示轨迹与本轮代码差异", true);
        SessionLogRow = new SettingsToggleRowViewModel("session-log-deepseek", "enabled", "在使用官方模型 API 时上传会话日志",
                                                       "帮助改进 DeepSeek 模型", true);
        Rows =
        [
            AppearanceRow, TranscriptViewRow, PerformanceUsageRow, BusyEnterRow, PermissionRow, CodeWorkViewRow,
            SessionLogRow
        ];
    }

    public SettingsSegmentRowViewModel AppearanceRow { get; }

    public SettingsChoiceRowViewModel TranscriptViewRow { get; }

    public SettingsChoiceRowViewModel PerformanceUsageRow { get; }

    public SettingsChoiceRowViewModel BusyEnterRow { get; }

    public SettingsChoiceRowViewModel PermissionRow { get; }

    public SettingsToggleRowViewModel CodeWorkViewRow { get; }

    public SettingsToggleRowViewModel SessionLogRow { get; }

    public IReadOnlyList<SettingsRowViewModel> Rows { get; }

    /// <summary>按 describe 快照投影：ns 缺失隐藏对应行；权限行候选取自 schema.choices。</summary>
    internal void Project(IReadOnlyDictionary<string, SettingsNamespaceView> namespaces)
    {
        ProjectRow(AppearanceRow, "ui-theme", namespaces);
        ProjectRow(TranscriptViewRow, "ui-chat", namespaces);
        ProjectRow(PerformanceUsageRow, "ui-chat", namespaces);
        ProjectRow(BusyEnterRow, "ui-conversation", namespaces);
        ProjectPermissionRow(namespaces);
        ProjectRow(CodeWorkViewRow, "ui-settings", namespaces);
        ProjectRow(SessionLogRow, "session-log-deepseek", namespaces);

        // 错误随重投影清除；末行分隔线按可见行序计算（隐藏行不参与）。
        SettingsRowViewModel? last = null;
        foreach (var row in Rows)
        {
            row.ClearError();
            if (row.IsVisible) last = row;
        }

        foreach (var row in Rows) row.IsLast = ReferenceEquals(row, last);
    }

    private static void ProjectRow(
        SettingsRowViewModel row, string ns, IReadOnlyDictionary<string, SettingsNamespaceView> namespaces)
    {
        if (namespaces.TryGetValue(ns, out var view))
        {
            row.IsVisible = true;
            row.ApplyView(view);
        }
        else
        {
            row.IsVisible = false;
        }
    }

    private void ProjectPermissionRow(IReadOnlyDictionary<string, SettingsNamespaceView> namespaces)
    {
        if (!namespaces.TryGetValue("permission", out var view) || ReadSchemaChoices(view.Schema) is not { } choices)
        {
            PermissionRow.IsVisible = false;
            return;
        }

        var options = choices.Select(choice => (choice, ResolvePermissionLabel(choice))).ToList();

        PermissionRow.SetOptions(options);
        PermissionRow.IsVisible = true;
        PermissionRow.ApplyView(view);
    }

    /// <summary>schema 根部的 choices 数组（字符串项）；缺失或为空返回 null（该行隐藏）。</summary>
    private static IReadOnlyList<string>? ReadSchemaChoices(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object           ||
            !schema.TryGetProperty("choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array)
            return null;

        var values = choices.EnumerateArray()
                            .Where(item => item.ValueKind == JsonValueKind.String)
                            .Select(item => item.GetString() ?? string.Empty)
                            .Where(value => value.Length > 0)
                            .ToList();
        return values.Count > 0 ? values : null;
    }

    /// <summary>遗留值 normal/expanded 展示为「详细」，但不主动改写存储。</summary>
    private static string ResolveTranscriptLabel(string value)
    {
        return value switch
        {
            "compact"              => "简洁",
            "standard"             => "标准",
            "detailed"             => "详细",
            "verbose"              => "完全展开",
            "normal" or "expanded" => "详细",
            _                      => value
        };
    }

    private static string ResolveChoiceLabel(string value)
    {
        return value switch
        {
            "compact"  => "简洁",
            "detailed" => "详细",
            "queue"    => "排队发送",
            "steer"    => "插话发送",
            _          => value
        };
    }

    private static string ResolvePermissionLabel(string value)
    {
        return value switch
        {
            "default"                     => "默认",
            "read-only"                   => "仅可查看",
            "workspace-write"             => "工作区内修改",
            "full-access" or "fullAccess" => "完全权限",
            _                             => value
        };
    }
}
