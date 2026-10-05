using DshDesktop.Core.Models;
using DshDesktop.Utils;
using NUnit.Framework;
using NUnit.Framework.Legacy;

namespace DshDesktop.Tests;

public sealed class ToolCallTextTests
{
    [Test]
    public void KnownToolsMapToOfficialChineseTitles()
    {
        ClassicAssert.AreEqual("运行命令", ToolCallText.GetTitle("bash"));
        ClassicAssert.AreEqual("运行命令", ToolCallText.GetTitle("pwsh"));
        ClassicAssert.AreEqual("读取", ToolCallText.GetTitle("read"));
        ClassicAssert.AreEqual("读取图片", ToolCallText.GetTitle("read_image"));
        ClassicAssert.AreEqual("搜索", ToolCallText.GetTitle("grep"));
        ClassicAssert.AreEqual("写入", ToolCallText.GetTitle("write"));
        ClassicAssert.AreEqual("编辑", ToolCallText.GetTitle("edit"));
        ClassicAssert.AreEqual("交付文件", ToolCallText.GetTitle("present"));
        ClassicAssert.AreEqual("更新任务清单", ToolCallText.GetTitle("todo_write"));
        ClassicAssert.AreEqual("提问", ToolCallText.GetTitle("ask_user_question"));
        ClassicAssert.AreEqual("创建子智能体", ToolCallText.GetTitle("subagent"));
    }

    [Test]
    public void UnknownOrBlankNamesFallBackToGenericTitle()
    {
        ClassicAssert.AreEqual("工具调用", ToolCallText.GetTitle("fs.read"));
        ClassicAssert.AreEqual("工具调用", ToolCallText.GetTitle("shell.run"));
        ClassicAssert.AreEqual("工具调用", ToolCallText.GetTitle(""));
        ClassicAssert.AreEqual("工具调用", ToolCallText.GetTitle(null));
    }

    [Test]
    public void SummaryPrefersVariantFieldsAndTakesFirstLine()
    {
        ClassicAssert.AreEqual("说明在前",
                               ToolCallText.GetSummary("bash",
                                                       "{\"description\":\"说明在前\",\"command\":\"git status\\nlog\"}"));
        ClassicAssert.AreEqual("git status",
                               ToolCallText.GetSummary("bash", "{\"command\":\"git status\\nlog\"}"));
        ClassicAssert.AreEqual("a.md",
                               ToolCallText.GetSummary("read", "{\"path\":\"a.md\"}"));
        ClassicAssert.AreEqual("a.md",
                               ToolCallText.GetSummary("edit", "{\"file_path\":\"a.md\"}"));
        ClassicAssert.AreEqual("a.md",
                               ToolCallText.GetSummary("web_fetch", "{\"url\":\"a.md\"}"));
    }

    [Test]
    public void SearchSummaryJoinsQueryListWithComma()
    {
        ClassicAssert.AreEqual("a, b",
                               ToolCallText.GetSummary("grep", "{\"queries\":[\"a\\nx\",\"b\"]}"));
        ClassicAssert.AreEqual("x",
                               ToolCallText.GetSummary("web_search", "{\"query\":\"x\"}"));
        ClassicAssert.AreEqual("pattern",
                               ToolCallText.GetSummary("glob", "{\"pattern\":\"pattern\"}"));
    }

    [Test]
    public void PresentSummaryListsFilePathsCommaJoined()
    {
        ClassicAssert.AreEqual("a.cs, b.cs",
                               ToolCallText.GetSummary("present",
                                                       "{\"files\":[{\"path\":\"a.cs\"},{\"path\":\"b.cs\"}]}"));
        ClassicAssert.AreEqual("a.cs",
                               ToolCallText.GetSummary("present",
                                                       "{\"files\":[{\"path\":\"a.cs\",\"description\":\"忽略\"}]}"));
    }

    [Test]
    public void UnknownNameSummaryKeepsRawNamePrefix()
    {
        ClassicAssert.AreEqual("fs.read · n.md",
                               ToolCallText.GetSummary("fs.read", "{\"path\":\"n.md\"}"));
        // 空对象无字段可取，回退原文首行（对齐官方 deriveSummary 兜底口径）。
        ClassicAssert.AreEqual("fs.read · {}",
                               ToolCallText.GetSummary("fs.read", "{}"));
        ClassicAssert.AreEqual("fs.read · 直接文本",
                               ToolCallText.GetSummary("fs.read", "{\"text\":\"直接文本\"}"));
    }

    [Test]
    public void InvalidPrimitiveOrEmptyArgumentsFallBackSafely()
    {
        ClassicAssert.AreEqual(string.Empty, ToolCallText.GetSummary("bash", null));
        ClassicAssert.AreEqual(string.Empty, ToolCallText.GetSummary("bash", ""));
        // 未知名 + 无参数：摘要只剩原始名（官方此处显示 callId，本端回退为名）。
        ClassicAssert.AreEqual("fs.read", ToolCallText.GetSummary("fs.read", ""));
        ClassicAssert.AreEqual("cmd -v", ToolCallText.GetSummary("bash", "cmd -v"));
        // 无法解析时回退原文首行，长度交给 UI 省略号截断。
        ClassicAssert.AreEqual("{截断", ToolCallText.GetSummary("bash", "{截断"));
        ClassicAssert.AreEqual("42", ToolCallText.GetSummary("bash", "42"));
        // 未知名 + 非对象参数：原始名仍保留在摘要前。
        ClassicAssert.AreEqual("fs.read · 42", ToolCallText.GetSummary("fs.read", "42"));
    }

    [Test]
    public void PresentToolUsesDeliveryStatusVerbs()
    {
        ClassicAssert.AreEqual("正在交付", ToolCallText.GetStatusText("present", ToolActivityStatus.Running));
        ClassicAssert.AreEqual("已交付", ToolCallText.GetStatusText("present", ToolActivityStatus.Succeeded));
        ClassicAssert.AreEqual("交付失败", ToolCallText.GetStatusText("present", ToolActivityStatus.Failed));
        ClassicAssert.AreEqual("运行中…", ToolCallText.GetStatusText("bash", ToolActivityStatus.Running));
        ClassicAssert.AreEqual("已完成", ToolCallText.GetStatusText("bash", ToolActivityStatus.Succeeded));
        ClassicAssert.AreEqual("失败", ToolCallText.GetStatusText("bash", ToolActivityStatus.Failed));
    }

    [Test]
    public void VariantsMapToDistinctRowIcons()
    {
        ClassicAssert.IsNotNull(ToolCallText.GetIconStroke("bash"));
        ClassicAssert.IsNotNull(ToolCallText.GetIconStroke("read"));
        ClassicAssert.IsNotNull(ToolCallText.GetIconFill("read"));
        ClassicAssert.IsNotNull(ToolCallText.GetIconFill("edit"));
        ClassicAssert.IsNotNull(ToolCallText.GetIconStroke("todo_write"));
        ClassicAssert.IsNotNull(ToolCallText.GetIconStroke("present"));
        // 纯描边图标没有填充部件。
        ClassicAssert.IsNull(ToolCallText.GetIconFill("bash"));
        ClassicAssert.IsNull(ToolCallText.GetIconFill("grep"));
        ClassicAssert.IsNull(ToolCallText.GetIconFill("run_code"));
        ClassicAssert.IsNull(ToolCallText.GetIconFill("todo_write"));
        ClassicAssert.IsNull(ToolCallText.GetIconFill("present"));
    }

    [Test]
    public void UnknownToolNamesFallBackToSparkleIcon()
    {
        ClassicAssert.IsNotNull(ToolCallText.GetIconStroke("fs.read"));
        ClassicAssert.IsNull(ToolCallText.GetIconFill("fs.read"));
        ClassicAssert.IsNull(ToolCallText.GetIconFill(null));
    }

    [Test]
    public void OfficialKeyedRowsOverrideVariantIcons()
    {
        // web_search 官方走 globe（web-row），grep 仍为放大镜：两者几何不同实例。
        ClassicAssert.AreNotSame(ToolCallText.GetIconStroke("web_search"), ToolCallText.GetIconStroke("grep"));
        // 问询行官方 IconQuestionOutlineRegular，不再落 Sparkle 兜底。
        ClassicAssert.AreNotSame(ToolCallText.GetIconStroke("ask_user_question"),
                                 ToolCallText.GetIconStroke("fs.read"));
        // details-row 家族拿到专属图标（agent/branch/clock）而非 Sparkle。
        ClassicAssert.AreNotSame(ToolCallText.GetIconStroke("subagent"), ToolCallText.GetIconStroke("fs.read"));
        ClassicAssert.AreNotSame(ToolCallText.GetIconStroke("workflow"), ToolCallText.GetIconStroke("fs.read"));
        ClassicAssert.AreNotSame(ToolCallText.GetIconStroke("schedule_create"), ToolCallText.GetIconStroke("fs.read"));
        // terminal_* 与 run_code 同用官方 IconCodeOutlineRegular（同一几何实例）。
        ClassicAssert.AreSame(ToolCallText.GetIconStroke("terminal_open"), ToolCallText.GetIconStroke("run_code"));
        // team_task_* 与 todo_write 同用官方 IconChecklistOutlineRegular（各自键位独立解析）。
        ClassicAssert.AreNotSame(ToolCallText.GetIconStroke("team_task_create"), ToolCallText.GetIconStroke("fs.read"));
        ClassicAssert.IsNull(ToolCallText.GetIconFill("team_task_create"));
        // session_*/lsp 与 grep 同用官方 IconSearchOutlineRegular。
        ClassicAssert.AreSame(ToolCallText.GetIconStroke("session_trace"), ToolCallText.GetIconStroke("grep"));
        // 混合图标（agent/cordis）带填充部件；users/trash 为纯描边。
        ClassicAssert.IsNotNull(ToolCallText.GetIconFill("subagent"));
        ClassicAssert.IsNotNull(ToolCallText.GetIconFill("cordis_inspect_list"));
        ClassicAssert.IsNull(ToolCallText.GetIconFill("list_agents"));
        ClassicAssert.IsNull(ToolCallText.GetIconFill("cordis_undefine"));
        // 官方 IconStopFillRegular 为实底方块：填充部件存在。
        ClassicAssert.IsNotNull(ToolCallText.GetIconFill("cordis_stop"));
        // 未知工具仍回退同一 Sparkle 实例。
        ClassicAssert.AreSame(ToolCallText.GetIconStroke("fs.read"), ToolCallText.GetIconStroke(null));
    }

    [Test]
    public void TodoSummaryShowsCompletedRatioAndActiveItem()
    {
        // 官方 todo-row：头段「{done}/{total} 已完成」，首个进行中条目以「 · 」追加。
        ClassicAssert.AreEqual("1/3 已完成 · 写组件",
                               ToolCallText.GetSummary("todo_write",
                                                       """{"todos":[{"content":"a","status":"completed"},{"content":"写组件","status":"in_progress"},{"content":"b","status":"pending"}]}"""));
        // 全部完成只剩头段。
        ClassicAssert.AreEqual("2/2 已完成",
                               ToolCallText.GetSummary("todo_write",
                                                       """{"todos":[{"content":"a","status":"completed"},{"content":"b","status":"completed"}]}"""));
        // 空清单是合法清空调用，头段保留「0/0 已完成」。
        ClassicAssert.AreEqual("0/0 已完成", ToolCallText.GetSummary("todo_write", "{\"todos\":[]}"));
        // status 缺失按待处理计（与事件解析同一回退）。
        ClassicAssert.AreEqual("0/1 已完成", ToolCallText.GetSummary("todo_write", "{\"todos\":[{\"content\":\"a\"}]}"));
        // 进行中条目内容为空白时不作为活动项展示（官方 activeContent 非空白才有效）。
        ClassicAssert.AreEqual("0/1 已完成",
                               ToolCallText.GetSummary("todo_write",
                                                       """{"todos":[{"content":"  ","status":"in_progress"}]}"""));
        // 参数无 todos 数组：保持原始 JSON 首行兜底（此前口径，不因 todo 分支收窄）。
        ClassicAssert.AreEqual("{\"other\":1}", ToolCallText.GetSummary("todo_write", "{\"other\":1}"));
    }

    [Test]
    public void TodoParseTodosReadsArgumentList()
    {
        var todos = ToolCallText.ParseTodos(
                                            """{"todos":[{"content":"a","status":"completed"},{"content":"b","status":"nope"},{"content":"  "}]}""");
        ClassicAssert.IsNotNull(todos);
        ClassicAssert.AreEqual(3, todos!.Count);
        ClassicAssert.AreEqual(SessionTodoStatus.Completed, todos[0].Status);
        ClassicAssert.AreEqual(SessionTodoStatus.Pending, todos[1].Status);
        ClassicAssert.AreEqual(SessionTodoStatus.Pending, todos[2].Status);
        // 无 todos 数组、非法 JSON 与空参数都返回 null（调用方按无清单处理）。
        ClassicAssert.IsNull(ToolCallText.ParseTodos("{}"));
        ClassicAssert.IsNull(ToolCallText.ParseTodos("{\"todos\":5}"));
        ClassicAssert.IsNull(ToolCallText.ParseTodos("{截断"));
        ClassicAssert.IsNull(ToolCallText.ParseTodos(null));
    }

    [Test]
    public void TodoDiffSummaryMatchesOfficialSegments()
    {
        SessionTodoItem Item(string content, SessionTodoStatus status)
        {
            return new SessionTodoItem(content, status);
        }

        // 官方 todoDiffModel：按 content 匹配，状态变化或位置移动都计「更新」，
        // 零计数段省略；段序固定为新增、更新、移除。
        var baseline = new[]
        {
            Item("a", SessionTodoStatus.Pending), Item("b", SessionTodoStatus.InProgress),
            Item("c", SessionTodoStatus.Completed)
        };
        var current = new[]
        {
            Item("a", SessionTodoStatus.Completed), Item("b", SessionTodoStatus.Pending),
            Item("d", SessionTodoStatus.Pending)
        };
        ClassicAssert.AreEqual("新增 1 · 更新 2 · 移除 1",
                               ToolCallText.TodoDiffSummary(baseline, current));

        // 仅顺序移动也算更新（状态不变）。
        var reordered = new[]
        {
            Item("b", SessionTodoStatus.InProgress), Item("a", SessionTodoStatus.Pending),
            Item("c", SessionTodoStatus.Completed)
        };
        ClassicAssert.AreEqual("更新 2", ToolCallText.TodoDiffSummary(baseline, reordered));

        // 全零为「清单没有变化」；尚无基线（首次记录）返回 null。
        ClassicAssert.AreEqual("清单没有变化", ToolCallText.TodoDiffSummary(baseline, baseline));
        ClassicAssert.IsNull(ToolCallText.TodoDiffSummary(null, current));
        // 空基线（上次写入为空清单）：当前条目全部算新增。
        ClassicAssert.AreEqual("新增 3", ToolCallText.TodoDiffSummary([], current));
    }
}
