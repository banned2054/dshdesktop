using DshDesktop.Core.Models;
using DshDesktop.ViewModels;
using NUnit.Framework;
using NUnit.Framework.Legacy;

namespace DshDesktop.Tests;

public sealed class ToolActivityItemViewModelTests
{
    [Test]
    [TestCase(ToolActivityStatus.Succeeded)]
    [TestCase(ToolActivityStatus.Failed)]
    public void SettlementNotifiesStatusBindingsWithoutErrorReason(ToolActivityStatus status)
    {
        var activity = new ToolActivity(1, "call-1", "fs.read", null, ToolActivityStatus.Running, null, null,
                                        DateTimeOffset.UtcNow);
        var card          = new ToolActivityItemViewModel(activity);
        var notifications = new List<string?>();
        card.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        card.Settle(activity.Settle(status, null, null, DateTimeOffset.UtcNow));

        Assert.That(notifications, Does.Contain(nameof(card.IsSucceeded)));
        Assert.That(notifications, Does.Contain(nameof(card.HasError)));
        Assert.That(notifications, Does.Contain(nameof(card.IsRunning)));
        Assert.That(notifications, Does.Contain(nameof(card.IsFailed)));
        Assert.That(notifications, Does.Contain(nameof(card.HasDetails)));
        ClassicAssert.AreEqual(status == ToolActivityStatus.Succeeded, card.IsSucceeded);
        ClassicAssert.AreEqual(status == ToolActivityStatus.Failed, card.HasError);
        ClassicAssert.AreEqual(status == ToolActivityStatus.Failed, card.HasDetails);
    }

    [Test]
    public void SettlementNotifiesLateNameAndArgumentsBindings()
    {
        var activity = new ToolActivity(1, "call-1", "", null, ToolActivityStatus.Running,
                                        null, null, DateTimeOffset.UtcNow);
        var card          = new ToolActivityItemViewModel(activity);
        var notifications = new List<string?>();
        card.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        card.Settle(activity with { Name = "fs.read", ArgumentsJson = "{}" });

        Assert.That(notifications, Does.Contain(nameof(card.Name)));
        Assert.That(notifications, Does.Contain(nameof(card.DisplayName)));
        Assert.That(notifications, Does.Contain(nameof(card.Title)));
        Assert.That(notifications, Does.Contain(nameof(card.ArgumentsText)));
        Assert.That(notifications, Does.Contain(nameof(card.ArgumentsPreview)));
        ClassicAssert.AreEqual("fs.read", card.DisplayName);
        ClassicAssert.AreEqual("{}", card.ArgumentsPreview);
    }

    [Test]
    public void CollapseRowDerivesTitleSummaryAndPresentStatus()
    {
        var card = new ToolActivityItemViewModel(
                                                 new ToolActivity(1, "call-1", "present",
                                                                  "{\"files\":[{\"path\":\"a.cs\"},{\"path\":\"b.cs\"}]}",
                                                                  ToolActivityStatus.Running, null, null,
                                                                  DateTimeOffset.UtcNow));

        ClassicAssert.AreEqual("交付文件", card.Title);
        ClassicAssert.AreEqual("a.cs, b.cs", card.SummaryText);
        ClassicAssert.AreEqual("正在交付", card.StatusText);

        card.Settle(new ToolActivity(1, "call-1", "present", "{\"files\":[{\"path\":\"a.cs\"},{\"path\":\"b.cs\"}]}",
                                     ToolActivityStatus.Succeeded, "已交付 2 个文件", null,
                                     DateTimeOffset.UtcNow));

        ClassicAssert.AreEqual("已交付", card.StatusText);
        ClassicAssert.AreEqual("a.cs, b.cs", card.SummaryText);
    }

    [Test]
    public void FailedCardSummarizesFirstErrorLine()
    {
        var card = new ToolActivityItemViewModel(
                                                 new ToolActivity(1, "call-1", "bash", "{\"command\":\"git status\"}",
                                                                  ToolActivityStatus.Failed, null, "命令以非零状态退出\nexit 1",
                                                                  DateTimeOffset.UtcNow));

        ClassicAssert.IsTrue(card.IsFailed);
        ClassicAssert.AreEqual("命令以非零状态退出", card.SummaryText);
    }

    [Test]
    public void LateArgumentsUpdateTitleAndSummary()
    {
        var card = new ToolActivityItemViewModel(
                                                 new ToolActivity(1, "call-1", "", null, ToolActivityStatus.Running,
                                                                  null, null, DateTimeOffset.UtcNow));
        var notifications = new List<string?>();
        card.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        ClassicAssert.AreEqual("工具调用", card.Title);

        card.Settle(new ToolActivity(1, "call-1", "bash", "{\"command\":\"ls\"}",
                                     ToolActivityStatus.Running, null, null, DateTimeOffset.UtcNow));

        ClassicAssert.AreEqual("运行命令", card.Title);
        ClassicAssert.AreEqual("ls", card.SummaryText);
        Assert.That(notifications, Does.Contain(nameof(card.Title)));
        Assert.That(notifications, Does.Contain(nameof(card.SummaryText)));
    }

    [Test]
    public void TodoDiffSummaryAppendsToCollapseRowSummary()
    {
        // todo_write 折叠行：头段「{done}/{total} 已完成」后拼 diff 段（官方 summarySuffix 位）。
        var card = new ToolActivityItemViewModel(
                                                 new ToolActivity(1, "call-1", "todo_write",
                                                                  """{"todos":[{"content":"a","status":"completed"},{"content":"b","status":"pending"}]}""",
                                                                  ToolActivityStatus.Succeeded, null, null,
                                                                  DateTimeOffset.UtcNow),
                                                 "新增 1 · 移除 1");

        ClassicAssert.AreEqual("1/2 已完成 · 新增 1 · 移除 1", card.SummaryText);

        // 落定回填同参参数不改变 diff 段（diff 在发起时定格）。
        card.Settle(new ToolActivity(1, "call-1", "todo_write",
                                     """{"todos":[{"content":"a","status":"completed"},{"content":"b","status":"pending"}]}""",
                                     ToolActivityStatus.Succeeded, "清单已更新", null, DateTimeOffset.UtcNow));

        ClassicAssert.AreEqual("1/2 已完成 · 新增 1 · 移除 1", card.SummaryText);
    }

    [Test]
    public void FailedTodoCardKeepsErrorLineOverDiffSummary()
    {
        // 失败口径与通用工具一致：错误首行优先，diff 段不显示。
        var card = new ToolActivityItemViewModel(
                                                 new ToolActivity(1, "call-1", "todo_write", "{\"todos\":[]}",
                                                                  ToolActivityStatus.Failed, null, "清单校验失败\nzod",
                                                                  DateTimeOffset.UtcNow),
                                                 "新增 1");

        ClassicAssert.AreEqual("清单校验失败", card.SummaryText);
    }
}
