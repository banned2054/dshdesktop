using DshDesktop.Core.Models;
using DshDesktop.ViewModels;
using NUnit.Framework;
using NUnit.Framework.Legacy;

namespace DshDesktop.Tests;

/// <summary>
///     助手消息角色标签已移除（思考行与工具行并列），状态行改为仅在
///     流式或中断时出现：验证 HasStatusHint 随 IsStreaming/IsInterrupted 的通知链。
/// </summary>
public sealed class MessageItemViewModelTests
{
    [Test]
    public void StreamingPlaceholderShowsStatusHintUntilStopped()
    {
        var message       = MessageItemViewModel.CreateStreaming();
        var notifications = new List<string?>();
        message.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        ClassicAssert.IsTrue(message.HasStatusHint);

        message.StopStreaming();

        ClassicAssert.IsFalse(message.HasStatusHint);
        Assert.That(notifications, Does.Contain(nameof(message.HasStatusHint)));
    }

    [Test]
    public void InterruptedTurnRestoresStatusHint()
    {
        var message = MessageItemViewModel.CreateStreaming();
        message.StopStreaming();

        var notifications = new List<string?>();
        message.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        message.MarkInterrupted();

        ClassicAssert.IsTrue(message.IsInterrupted);
        ClassicAssert.IsTrue(message.HasStatusHint);
        Assert.That(notifications, Does.Contain(nameof(message.HasStatusHint)));
    }

    [Test]
    public void SettledMessageHasNoStatusHint()
    {
        var message = new MessageItemViewModel(new ConversationMessage(
            1, "m-1", MessageRole.Assistant, "正文", DateTimeOffset.UtcNow));

        ClassicAssert.IsFalse(message.HasStatusHint);
        ClassicAssert.IsFalse(message.IsStreaming);
        ClassicAssert.IsFalse(message.IsInterrupted);
    }
}
