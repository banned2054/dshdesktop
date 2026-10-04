using Avalonia.Media;
using DshDesktop.Core.Models;
using System.Text.Json;

namespace DshDesktop.Utils;

/// <summary>
///     工具调用折叠行文案（对齐官方 DSH 客户端规则）。协议不携带描述字段，
///     口语化标题与摘要由客户端本地推导：已知工具名映射中文标题，摘要按工具
///     变体从参数 JSON 取首个命中字段的首行；未知名回退通用标题并在摘要前
///     保留原始名。
/// </summary>
public static class ToolCallText
{
    // 行式条目图标（复刻官方 ui-primitives 16×16 线稿、线宽 1）：描边与填充拆成
    // 两个几何——混合图标（读取/编辑的实底框、思考的中心点）由 XAML 双 Path 叠加。
    private const string ApiStroke    = "M3,4 L7,8 L3,12 M9,12 L13,12";
    private const string BrowseStroke = "M4.9375,5.90295 L11.0625,5.90295 M4.9375,9.02991 L8.27841,9.02991";

    private const string BrowseFill =
        "M12.5,1.32617 C13.3039,1.32617 14,1.95171 14,2.77637 L14,13.2246 C13.9996,14.0489 13.3036,14.6738 12.5,14.6738 L3.5,14.6738 C2.69637,14.6738 2.00042,14.0489 2,13.2246 L2,2.77637 C2,1.95171 2.69613,1.32617 3.5,1.32617 L12.5,1.32617 Z M3.5,2.32617 C3.1993,2.32617 3,2.55186 3,2.77637 L3,13.2246 C3.00044,13.4489 3.19963,13.6738 3.5,13.6738 L12.5,13.6738 C12.8004,13.6738 12.9996,13.4489 13,13.2246 L13,2.77637 C13,2.55186 12.8007,2.32617 12.5,2.32617 L3.5,2.32617 Z";

    private const string EditStroke = "M7.7849,8.23878 L13.888,2.13574";

    private const string EditFill =
        "M8.85596,2.69971 L4.19971,2.69971 C3.37141,2.69971 2.69992,3.37146 2.69971,4.19971 L2.69971,11.8003 C2.69992,12.6285 3.37141,13.3003 4.19971,13.3003 L11.8003,13.3003 C12.6283,13.2999 13.3001,12.6283 13.3003,11.8003 L13.3003,7.89893 L14.3003,7.89893 L14.3003,11.8003 C14.3001,13.1806 13.1806,14.2999 11.8003,14.3003 L4.19971,14.3003 C2.81913,14.3003 1.69992,13.1808 1.69971,11.8003 L1.69971,4.19971 C1.69992,2.81918 2.81913,1.69971 4.19971,1.69971 L8.85596,1.69971 Z";

    private const string CodeStroke =
        "M6.27612,1.5 L4.52612,14.5 M11.4739,1.5 L9.72388,14.5 M2.39868,5.5 L14.0681,5.5 M1.93188,10.5 L13.6013,10.5";

    private const string SearchStroke =
        "M6.58727,11.8586 C9.55061,11.8586 11.9529,9.45637 11.9529,6.49304 C11.9529,3.5297 9.55061,1.12744 6.58727,1.12744 C3.62394,1.12744 1.22168,3.5297 1.22168,6.49304 C1.22168,9.45637 3.62394,11.8586 6.58727,11.8586 Z M10.2991,10.3933 L14.7783,14.8725";

    private const string ChecklistStroke =
        "M3.75,6.25 C4.7165,6.25 5.5,5.4665 5.5,4.5 C5.5,3.5335 4.7165,2.75 3.75,2.75 C2.7835,2.75 2,3.5335 2,4.5 C2,5.4665 2.7835,6.25 3.75,6.25 Z M7.5,4.5 L13.5,4.5 M3.75,13.25 C4.7165,13.25 5.5,12.4665 5.5,11.5 C5.5,10.5335 4.7165,9.75 3.75,9.75 C2.7835,9.75 2,10.5335 2,11.5 C2,12.4665 2.7835,13.25 3.75,13.25 Z M7.5,11.5 L13.5,11.5";

    private const string DeliverStroke =
        "M6.15479,4.91687 L9.84543,4.91687 M11.8798,9.55347 L11.8798,2.71525 C11.8798,2.37416 11.564,2.09766 11.1744,2.09766 L4.82577,2.09766 C4.43618,2.09766 4.12036,2.37416 4.12036,2.71525 L4.12036,9.55347 M2.28735,13.8022 L2.28735,8.84792 C2.28735,8.77514 2.36262,8.72673 2.42884,8.75693 L13.2936,13.7112 C13.3914,13.7558 13.3596,13.9022 13.2521,13.9022 L2.38735,13.9022 C2.33213,13.9022 2.28735,13.8575 2.28735,13.8022 Z M7.46929,10.979 L13.5783,8.7416 C13.6435,8.7177 13.7126,8.76601 13.7126,8.83551 L13.7125,13.8022 C13.7125,13.8574 13.6678,13.9022 13.6125,13.9022 L7.99999,13.9022 M6.15479,7.2395 L9.05644,7.2395";

    private const string SparkleStroke =
        "M5.875,3 C5.875,6.33333 7.54167,8 10.875,8 C7.54167,8 5.875,9.66667 5.875,13 C5.875,9.66667 4.20833,8 0.875,8 C4.20833,8 5.875,6.33333 5.875,3 Z M12.375,1.55823 C12.375,3.39156 13.2917,4.30823 15.125,4.30823 C13.2917,4.30823 12.375,5.22489 12.375,7.05823 C12.375,5.22489 11.4583,4.30823 9.625,4.30823 C11.4583,4.30823 12.375,3.39156 12.375,1.55823 Z M12.375,10.4418 C12.375,11.7751 13.0417,12.4418 14.375,12.4418 C13.0417,12.4418 12.375,13.1084 12.375,14.4418 C12.375,13.1084 11.7083,12.4418 10.375,12.4418 C11.7083,12.4418 12.375,11.7751 12.375,10.4418 Z";

    private const string ThinkStroke =
        "M10.2854,5.71481 C12.9673,8.39663 14.1182,11.5938 12.8562,12.8559 C11.5942,14.1179 8.39706,12.9669 5.71518,10.2851 C3.03333,7.60323 1.88236,4.40608 3.14441,3.14403 C4.40644,1.882 7.6036,3.03297 10.2854,5.71481 Z M10.2854,10.2851 C7.6036,12.9669 4.40644,14.1179 3.14441,12.8559 C1.88236,11.5938 3.03333,8.39663 5.71518,5.71481 C8.39706,3.03297 11.5942,1.882 12.8562,3.14403 C14.1182,4.40608 12.9673,7.60323 10.2854,10.2851 Z";

    private const string ThinkFill =
        "M8.86291,8.0002 C8.86291,8.47549 8.47762,8.86087 8.00224,8.86087 C7.52694,8.86087 7.1416,8.47549 7.1416,8.0002 C7.1416,7.52485 7.52694,7.13953 8.00224,7.13953 C8.47762,7.13953 8.86291,7.52485 8.86291,8.0002 Z";

    /// <summary>已知工具名 → 中文标题（对齐官方 tool.title.* 词典与插件词典）。</summary>
    private static readonly Dictionary<string, string> Titles = new()
    {
        ["bash"]                   = "运行命令",
        ["pwsh"]                   = "运行命令",
        ["read"]                   = "读取",
        ["read_image"]             = "读取图片",
        ["web_fetch"]              = "读取",
        ["web_search"]             = "搜索",
        ["grep"]                   = "搜索",
        ["glob"]                   = "搜索",
        ["write"]                  = "写入",
        ["edit"]                   = "编辑",
        ["run_code"]               = "代码",
        ["present"]                = "交付文件",
        ["todo_write"]             = "更新任务清单",
        ["ask_user_question"]      = "提问",
        ["subagent"]               = "创建子智能体",
        ["list_subagent_models"]   = "查看可用模型",
        ["list_agents"]            = "查看子智能体",
        ["send_message"]           = "发送消息",
        ["interrupt_agent"]        = "中断智能体",
        ["wait_agent"]             = "等待子智能体",
        ["spawn_teammate"]         = "创建队友",
        ["team_task_create"]       = "创建团队任务",
        ["team_task_get"]          = "读取团队任务",
        ["team_task_update"]       = "更新团队任务",
        ["team_task_list"]         = "查看团队任务",
        ["create_goal"]            = "创建目标",
        ["get_goal"]               = "查看目标",
        ["update_goal"]            = "更新目标",
        ["schedule_create"]        = "创建定时任务",
        ["schedule_list"]          = "查看定时任务",
        ["schedule_update"]        = "修改定时任务",
        ["schedule_delete"]        = "删除定时任务",
        ["job_list"]               = "查看后台任务",
        ["job_output"]             = "读取任务输出",
        ["job_kill"]               = "取消后台任务",
        ["terminal_open"]          = "创建终端",
        ["terminal_read"]          = "读取终端",
        ["terminal_list"]          = "查看终端",
        ["terminal_signal"]        = "发送终端信号",
        ["terminal_close"]         = "关闭终端",
        ["lsp"]                    = "查询代码符号",
        ["workflow"]               = "运行工作流",
        ["ralph"]                  = "运行循环工作流",
        ["session_event_read"]     = "读取事件",
        ["session_event_search"]   = "搜索事件",
        ["session_event_trace"]    = "追踪事件",
        ["session_search"]         = "搜索会话",
        ["session_trace"]          = "追踪会话",
        ["cordis_package_inspect"] = "查询 Cordis 环境",
        ["cordis_runtime_inspect"] = "查询 Cordis 环境",
        ["cordis_inspect_list"]    = "检查提供方",
        ["cordis_inspect_query"]   = "查询运行时",
        ["cordis_inspect_self"]    = "检查动态插件",
        ["cordis_run"]             = "运行 Cordis 插件",
        ["cordis_stop"]            = "停止 Cordis 插件",
        ["cordis_undefine"]        = "移除 Cordis 插件"
    };

    /// <summary>工具名 → 摘要变体（对齐官方 TOOL_VARIANTS；缺席即 others 变体）。</summary>
    private static readonly Dictionary<string, string> Variants = new()
    {
        ["bash"]                   = "bash",
        ["pwsh"]                   = "bash",
        ["read"]                   = "read",
        ["read_image"]             = "read",
        ["web_fetch"]              = "read",
        ["cordis_package_inspect"] = "read",
        ["cordis_runtime_inspect"] = "read",
        ["web_search"]             = "search",
        ["grep"]                   = "search",
        ["glob"]                   = "search",
        ["write"]                  = "write",
        ["edit"]                   = "edit",
        ["run_code"]               = "code",
        ["present"]                = "present",
        ["todo_write"]             = "todo"
    };

    /// <summary>变体 → 摘要字段优先级（对齐官方 SUMMARY_KEYS）。</summary>
    private static readonly Dictionary<string, string[]> SummaryKeys = new()
    {
        ["bash"]   = ["description", "command"],
        ["read"]   = ["path", "file_path", "url"],
        ["search"] = ["query", "pattern", "url"],
        ["write"]  = ["path", "file_path"],
        ["edit"]   = ["path", "file_path"],
        ["code"]   = ["description"]
    };

    /// <summary>变体 → 行图标（stroke 必有，fill 仅实底部件非空）。</summary>
    private static readonly Dictionary<string, (StreamGeometry Stroke, StreamGeometry? Fill)> IconParts = new()
    {
        ["bash"]    = (Parse(ApiStroke), null),
        ["read"]    = (Parse(BrowseStroke), Parse(BrowseFill)),
        ["search"]  = (Parse(SearchStroke), null),
        ["write"]   = (Parse(EditStroke), Parse(EditFill)),
        ["edit"]    = (Parse(EditStroke), Parse(EditFill)),
        ["code"]    = (Parse(CodeStroke), null),
        ["present"] = (Parse(DeliverStroke), null),
        ["todo"]    = (Parse(ChecklistStroke), null),
        ["others"]  = (Parse(SparkleStroke), null)
    };

    /// <summary>思考行图标（ReasoningRow 的 IconThinkOutlineRegular）。</summary>
    public static readonly StreamGeometry ThinkingIconStroke = Parse(ThinkStroke);

    public static readonly StreamGeometry ThinkingIconFill = Parse(ThinkFill);

    /// <summary>折叠行图标描边部件；未知工具回退通用四角星。</summary>
    public static StreamGeometry GetIconStroke(string? name)
    {
        return IconParts.GetValueOrDefault(VariantKey(name)).Stroke;
    }

    /// <summary>折叠行图标填充部件；纯描边图标为 null。</summary>
    public static StreamGeometry? GetIconFill(string? name)
    {
        return IconParts.GetValueOrDefault(VariantKey(name)).Fill;
    }

    private static string VariantKey(string? name)
    {
        return name != null && Variants.TryGetValue(name, out var variant) ? variant : "others";
    }

    private static StreamGeometry Parse(string data)
    {
        return StreamGeometry.Parse(data);
    }

    /// <summary>折叠行标题；未知或空白名回退通用「工具调用」。</summary>
    public static string GetTitle(string? name)
    {
        return name != null && Titles.TryGetValue(name, out var title) ? title : "工具调用";
    }

    /// <summary>
    ///     折叠行摘要：已知工具按变体取参数字段，未知名在摘要前保留原始名
    ///     （对齐官方 generic 行「名称 · 摘要」的口径）。
    /// </summary>
    public static string GetSummary(string? name, string? argumentsJson)
    {
        var summary = DeriveSummary(name, argumentsJson);
        if (string.IsNullOrWhiteSpace(name) || Titles.ContainsKey(name)) return summary;

        return string.IsNullOrEmpty(summary) ? name : $"{name} · {summary}";
    }

    /// <summary>状态词：present 工具对齐官方交付文案，其余用通用词。</summary>
    public static string GetStatusText(string? name, ToolActivityStatus status)
    {
        var delivering = name == "present";
        return status switch
        {
            ToolActivityStatus.Running   => delivering ? "正在交付" : "运行中…",
            ToolActivityStatus.Succeeded => delivering ? "已交付" : "已完成",
            ToolActivityStatus.Failed    => delivering ? "交付失败" : "失败",
            _                            => string.Empty
        };
    }

    /// <summary>取首行（对齐官方 firstLine）：摘要与失败摘要都只展示一行。</summary>
    public static string FirstLine(string text)
    {
        var newline = text.IndexOf('\n');
        return newline < 0 ? text : text[..newline];
    }

    private static string DeriveSummary(string? name, string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return string.Empty;

        using var document = TryParse(argumentsJson);
        if (document == null) return FirstLine(argumentsJson);

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return FirstLine(argumentsJson);

        if (name == "present")
        {
            var files = ExtractPresentFiles(root);
            if (files.Length > 0) return string.Join(", ", files);
        }

        var variant = name != null && Variants.TryGetValue(name, out var mapped) ? mapped : "others";

        if (variant == "search"
         && root.TryGetProperty("queries", out var queries)
         && queries.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var query in queries.EnumerateArray())
                if (query.ValueKind == JsonValueKind.String
                 && query.GetString() is { Length: > 0 } text)
                    list.Add(FirstLine(text));

            if (list.Count > 0) return string.Join(", ", list);
        }

        foreach (var key in SummaryKeys.GetValueOrDefault(variant, []))
            if (root.TryGetProperty(key, out var value)
             && value.ValueKind == JsonValueKind.String
             && value.GetString() is { Length: > 0 } picked)
                return FirstLine(picked);

        foreach (var property in root.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.String
             && property.Value.GetString() is { Length: > 0 } fallback)
                return FirstLine(fallback);

        return FirstLine(argumentsJson);
    }

    private static string[] ExtractPresentFiles(JsonElement args)
    {
        if (!args.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return [];

        var paths = new List<string>();
        foreach (var file in files.EnumerateArray())
            if (file.ValueKind == JsonValueKind.Object
             && file.TryGetProperty("path", out var path)
             && path.ValueKind == JsonValueKind.String
             && path.GetString() is { Length: > 0 } text)
                paths.Add(FirstLine(text));

        return [.. paths];
    }

    private static JsonDocument? TryParse(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
