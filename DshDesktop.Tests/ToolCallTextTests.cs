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
        ClassicAssert.AreEqual("运行命令",   ToolCallText.GetTitle("bash"));
        ClassicAssert.AreEqual("运行命令",   ToolCallText.GetTitle("pwsh"));
        ClassicAssert.AreEqual("读取",       ToolCallText.GetTitle("read"));
        ClassicAssert.AreEqual("读取图片",   ToolCallText.GetTitle("read_image"));
        ClassicAssert.AreEqual("搜索",       ToolCallText.GetTitle("grep"));
        ClassicAssert.AreEqual("写入",       ToolCallText.GetTitle("write"));
        ClassicAssert.AreEqual("编辑",       ToolCallText.GetTitle("edit"));
        ClassicAssert.AreEqual("交付文件",   ToolCallText.GetTitle("present"));
        ClassicAssert.AreEqual("更新任务清单", ToolCallText.GetTitle("todo_write"));
        ClassicAssert.AreEqual("提问",       ToolCallText.GetTitle("ask_user_question"));
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
            ToolCallText.GetSummary("bash", "{\"description\":\"说明在前\",\"command\":\"git status\\nlog\"}"));
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
            ToolCallText.GetSummary("present", "{\"files\":[{\"path\":\"a.cs\"},{\"path\":\"b.cs\"}]}"));
        ClassicAssert.AreEqual("a.cs",
            ToolCallText.GetSummary("present", "{\"files\":[{\"path\":\"a.cs\",\"description\":\"忽略\"}]}"));
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
        ClassicAssert.AreEqual("已交付",   ToolCallText.GetStatusText("present", ToolActivityStatus.Succeeded));
        ClassicAssert.AreEqual("交付失败", ToolCallText.GetStatusText("present", ToolActivityStatus.Failed));
        ClassicAssert.AreEqual("运行中…",  ToolCallText.GetStatusText("bash", ToolActivityStatus.Running));
        ClassicAssert.AreEqual("已完成",   ToolCallText.GetStatusText("bash", ToolActivityStatus.Succeeded));
        ClassicAssert.AreEqual("失败",     ToolCallText.GetStatusText("bash", ToolActivityStatus.Failed));
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
}
