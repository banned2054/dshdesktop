using DshDesktop.Core.Models;
using DshDesktop.ViewModels;
using Xunit;

namespace DshDesktop.Tests;

public sealed class ToolActivityItemViewModelTests
{
    [Theory]
    [InlineData(ToolActivityStatus.Succeeded)]
    [InlineData(ToolActivityStatus.Failed)]
    public void SettlementNotifiesStatusBindingsWithoutErrorReason(ToolActivityStatus status)
    {
        var activity = new ToolActivity(1, "call-1", "fs.read", null, ToolActivityStatus.Running, null, null,
                                        DateTimeOffset.UtcNow);
        var card          = new ToolActivityItemViewModel(activity);
        var notifications = new List<string?>();
        card.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        card.Settle(activity.Settle(status, null, null, DateTimeOffset.UtcNow));

        Assert.Contains(nameof(card.IsSucceeded), notifications);
        Assert.Contains(nameof(card.HasError), notifications);
        Assert.Contains(nameof(card.IsRunning), notifications);
        Assert.Contains(nameof(card.IsFailed), notifications);
        Assert.Contains(nameof(card.HasDetails), notifications);
        Assert.Equal(status == ToolActivityStatus.Succeeded, card.IsSucceeded);
        Assert.Equal(status == ToolActivityStatus.Failed, card.HasError);
        Assert.Equal(status == ToolActivityStatus.Failed, card.HasDetails);
    }

    [Fact]
    public void SettlementNotifiesLateNameAndArgumentsBindings()
    {
        var activity = new ToolActivity(1, "call-1", "", null, ToolActivityStatus.Running,
                                        null, null, DateTimeOffset.UtcNow);
        var card          = new ToolActivityItemViewModel(activity);
        var notifications = new List<string?>();
        card.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        card.Settle(activity with { Name = "fs.read", ArgumentsJson = "{}" });

        Assert.Contains(nameof(card.Name), notifications);
        Assert.Contains(nameof(card.DisplayName), notifications);
        Assert.Contains(nameof(card.ArgumentsText), notifications);
        Assert.Contains(nameof(card.ArgumentsPreview), notifications);
        Assert.Equal("fs.read", card.DisplayName);
        Assert.Equal("{}", card.ArgumentsPreview);
    }
}
