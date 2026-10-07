using DshDesktop.Core.Models;
using DshDesktop.Infrastructure.Services;
using NUnit.Framework;
using NUnit.Framework.Legacy;

namespace DshDesktop.Tests;

public sealed class SimulatedSessionServiceTests
{
    [Test]
    public async Task SessionsAreReturnedInMostRecentlyUpdatedOrder()
    {
        var service = new SimulatedSessionService();

        var sessions = await service.GetSessionsAsync();

        // 长会话翻页是模拟模式的功能演示会话，置为最新；其余按更新时间倒序
        //（交付文件演示会话最旧，居末位）。
        ClassicAssert.AreEqual(6, sessions.Count);
        ClassicAssert.AreEqual("session-history", sessions[0].Id);
        ClassicAssert.AreEqual("session-welcome", sessions[1].Id);
        ClassicAssert.AreEqual("session-native", sessions[2].Id);
        ClassicAssert.AreEqual("session-design", sessions[3].Id);
        ClassicAssert.AreEqual("session-todos", sessions[4].Id);
        ClassicAssert.AreEqual("session-deliverables", sessions[5].Id);
        ClassicAssert.IsTrue(sessions[0].UpdatedAt >= sessions[1].UpdatedAt);
        ClassicAssert.IsTrue(sessions[1].UpdatedAt >= sessions[2].UpdatedAt);
    }

    [Test]
    public async Task SendingMessagePersistsUserMessageAndSignalsChange()
    {
        var service     = new SimulatedSessionService();
        var changeCount = 0;
        service.SessionsChanged += (_, _) => changeCount++;

        await service.SendPromptAsync("session-native", "request-1", "请给我一个摘要");
        var messages = await service.GetMessagesAsync("session-native");

        ClassicAssert.AreEqual(MessageRole.User, messages[^1].Role);
        ClassicAssert.AreEqual("请给我一个摘要", messages[^1].Content);
        ClassicAssert.AreEqual(1, changeCount);
    }

    [Test]
    public async Task FollowSessionEmitsSnapshotThenPushedUpdates()
    {
        var       service      = new SimulatedSessionService();
        var       updates      = new List<SessionUpdate>();
        using var cancellation = new CancellationTokenSource();

        var followTask = Task.Run(async () =>
        {
            await foreach (var update in service.FollowSessionAsync("session-native", cancellation.Token))
            {
                updates.Add(update);
                // 快照 + usage/stats 两帧统计 + 流式三帧 + 消息 + 统计更新两帧。
                if (updates.Count >= 9) break;
            }
        });

        await WaitForAsync(() => updates.Count >= 1);
        service.PushAssistantReply("session-native", "模拟回复");
        await followTask;
        cancellation.Cancel();

        ClassicAssert.IsInstanceOf<SessionUpdate.Snapshot>(updates[0]);
        // 快照紧随统计整值基线（与真实后端快照投影同位）。
        ClassicAssert.IsInstanceOf<SessionUpdate.UsageUpdated>(updates[1]);
        ClassicAssert.IsInstanceOf<SessionUpdate.StatsUpdated>(updates[2]);
        Assert.That(updates.Any(update => update is SessionUpdate.MessageAppended
        {
            Message.Role: MessageRole.Assistant
        }), Is.True);
        Assert.That(updates.Any(update => update is SessionUpdate.StreamTextDelta), Is.True);
    }

    [Test]
    public async Task SnapshotCarriesAccountedUsageAndReplyAccumulates()
    {
        var       service      = new SimulatedSessionService();
        var       updates      = new List<SessionUpdate>();
        using var cancellation = new CancellationTokenSource();

        var followTask = Task.Run(async () =>
        {
            await foreach (var update in service.FollowSessionAsync("session-welcome", cancellation.Token))
                updates.Add(update);
        });

        await WaitForAsync(() => updates.OfType<SessionUpdate.UsageUpdated>().Any());
        var baseline = updates.OfType<SessionUpdate.UsageUpdated>().First().Usage;
        // 预置会话两条助手消息：至少两步计费，四桶非零。
        ClassicAssert.IsTrue(baseline.OutputTokens        > 0);
        ClassicAssert.IsTrue(baseline.CacheReadTokens     > 0);
        ClassicAssert.IsTrue(baseline.UncachedInputTokens > 0);

        service.PushAssistantReply("session-welcome", "一段会累计输出 token 的回复内容");
        await WaitForAsync(() => updates.OfType<SessionUpdate.UsageUpdated>().Count() >= 2);
        var afterReply = updates.OfType<SessionUpdate.UsageUpdated>().Last().Usage;
        ClassicAssert.IsTrue(afterReply.OutputTokens > baseline.OutputTokens);
        // 统计更新带单调 seq（快照与增量共用同一时间线）。
        ClassicAssert.IsTrue(updates.OfType<SessionUpdate.StatsUpdated>().Last().Seq >=
                             updates.OfType<SessionUpdate.StatsUpdated>().First().Seq);
        cancellation.Cancel();
        try
        {
            await followTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException)
        {
            // 取消收尾是预期路径。
        }

        // 计量持久在会话上：重新订阅的快照携带累计值。
        await using var reopen = service.FollowSessionAsync("session-welcome").GetAsyncEnumerator();
        await reopen.MoveNextAsync();
        await reopen.MoveNextAsync();
        ClassicAssert.AreEqual(afterReply, ((SessionUpdate.UsageUpdated)reopen.Current).Usage);
    }

    [Test]
    public async Task CreatingSessionStartsEmptyAndCanBeLoaded()
    {
        var service = new SimulatedSessionService();

        var created  = await service.CreateSessionAsync();
        var messages = await service.GetMessagesAsync(created.Id);

        ClassicAssert.AreEqual("新对话", created.Title);
        ClassicAssert.AreEqual(SessionBlankState.ConfirmedBlank, created.BlankState);
        ClassicAssert.IsEmpty(messages);
    }

    [Test]
    public async Task EngagedSessionStaysEngagedAcrossListRefresh()
    {
        var service = new SimulatedSessionService();
        var created = await service.CreateSessionAsync();

        // 过渡信号（发送被接受）进入台账后：迟到的空白摘要不能使会话退回空白。
        await service.SendPromptAsync(created.Id, "first-request", "第一条消息");
        service.MarkSessionEngaged(created.Id);
        var summary = (await service.GetSessionsAsync()).Single(item => item.Id == created.Id);

        ClassicAssert.AreEqual(SessionBlankState.Engaged, summary.BlankState);
    }

    [Test]
    public async Task AdoptingExistingSessionReusesItWithoutCreatingNew()
    {
        var service = new SimulatedSessionService();
        var first   = await service.CreateSessionAsync("ws-1");
        var second  = await service.CreateSessionAsync("ws-1", first.Id);

        // 收养语义：按身份复用同一会话，不新建、不伪造空白摘要。
        ClassicAssert.AreEqual(first.Id, second.Id);
        ClassicAssert.AreEqual(1, service.CreatedSessionCount);
    }

    [Test]
    public async Task SelectModelEchoesThroughFollowAndSnapshotCarriesCurrentModel()
    {
        var       service      = new SimulatedSessionService();
        var       updates      = new List<SessionUpdate>();
        using var cancellation = new CancellationTokenSource();

        var catalog = await service.GetModelCatalogAsync();
        ClassicAssert.IsNotNull(catalog.Default);
        ClassicAssert.AreEqual(2, catalog.Groups.Count);

        var followTask = Task.Run(async () =>
        {
            await foreach (var update in service.FollowSessionAsync("session-welcome", cancellation.Token))
                updates.Add(update);
        });

        await WaitForAsync(() => updates.Count >= 1);
        ClassicAssert.IsInstanceOf<SessionUpdate.Snapshot>(updates[0]);
        // 预置选型随快照携带（与真实后端的 modelSelection 投影同位）。
        ClassicAssert.AreEqual(new ModelSelection("sim", "sim-chat"),
                               ((SessionUpdate.Snapshot)updates[0]).CurrentModel);

        var selection = await service.SelectModelAsync("session-welcome", "sim-alt", "alt-chat");
        ClassicAssert.AreEqual(new ModelSelection("sim-alt", "alt-chat"), selection);
        await WaitForAsync(() => updates.Any(update => update is SessionUpdate.ModelSelected
        {
            Selection.Provider: "sim-alt", Selection.Model: "alt-chat"
        }));
        cancellation.Cancel();
        try
        {
            await followTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (OperationCanceledException)
        {
            // 取消收尾是预期路径。
        }

        // 选型持久在会话上：重新订阅的快照仍携带最新选型。
        await using var reopen = service.FollowSessionAsync("session-welcome").GetAsyncEnumerator();
        await reopen.MoveNextAsync();
        ClassicAssert.AreEqual(new ModelSelection("sim-alt", "alt-chat"),
                               ((SessionUpdate.Snapshot)reopen.Current).CurrentModel);
    }

    [Test]
    public async Task SelectingModelOnMissingSessionThrows()
    {
        var service = new SimulatedSessionService();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.SelectModelAsync("missing", "sim", "sim-chat"));
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 2000)
    {
        var timeout = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition() && DateTime.UtcNow < timeout) await Task.Delay(10);

        ClassicAssert.IsTrue(condition(), "预期的异步状态未在超时前出现。");
    }
}
