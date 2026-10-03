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
        Assert.That(notifications, Does.Contain(nameof(card.ArgumentsText)));
        Assert.That(notifications, Does.Contain(nameof(card.ArgumentsPreview)));
        ClassicAssert.AreEqual("fs.read", card.DisplayName);
        ClassicAssert.AreEqual("{}", card.ArgumentsPreview);
    }
}
