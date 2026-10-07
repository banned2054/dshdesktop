using DshDesktop.Core.Models;
using DshDesktop.Harness.Models.Events;
using DshDesktop.Harness.Services.Sessions;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using System.Text.Json;

namespace DshDesktop.Tests;

/// <summary>deliverables/presented 载荷校验、历史/实时映射与同轮合并语义验证。</summary>
public sealed class TurnDeliverablesMappingTests
{
    [Test]
    public void PresentedEventParsesValidFilesAndKeepsRawIndex()
    {
        // 形状对照官方 isPresentedData/isPresentedFile：path 非空白、description
        // 缺省或字符串；空白 path 与 null description 的条目跳过但占住原始下标。
        var wireEvent =
            WireEventJson.ParseEvent(Json("""
                                          {"type":"deliverables/presented","seq":748,"time":1727840748000,
                                           "data":{"turn":1,"callId":"call-1",
                                                   "files":[{"path":"docs/report.md","description":"周报"},
                                                            {"path":"   "},
                                                            {"path":"out/data.csv","description":null},
                                                            {"path":"out/chart.png"}]}}
                                          """))!;

        ClassicAssert.IsTrue(WireEventJson.TryGetPresentedFiles(wireEvent, out var turn, out var files));
        ClassicAssert.AreEqual(1, turn);
        ClassicAssert.AreEqual(2, files.Count);
        ClassicAssert.AreEqual((748L, 0, "docs/report.md", "周报"),
                               (files[0].Seq, files[0].Index, files[0].Path, files[0].Description));
        ClassicAssert.AreEqual(748L, files[1].Seq);
        ClassicAssert.AreEqual(3, files[1].Index);
        ClassicAssert.AreEqual("out/chart.png", files[1].Path);
        Assert.That(files[1].Description, Is.Null);
    }

    [Test]
    public void PresentedEventWithInvalidPayloadYieldsNothing()
    {
        AssertNoFiles("""{"type":"deliverables/presented","seq":1,"time":0,"data":{}}""");
        AssertNoFiles("""{"type":"deliverables/presented","seq":2,"time":0,"data":{"turn":0,"callId":"c","files":[]}}""");
        AssertNoFiles("""{"type":"deliverables/presented","seq":3,"time":0,"data":{"turn":1.5,"callId":"c","files":[]}}""");
        AssertNoFiles("""{"type":"deliverables/presented","seq":30,"time":0,"data":{"turn":9007199254740992,"callId":"c","files":[{"path":"a"}]}}""");
        AssertNoFiles("""{"type":"deliverables/presented","seq":4,"time":0,"data":{"turn":1,"files":[{"path":"a"}]}}""");
        AssertNoFiles("""{"type":"deliverables/presented","seq":5,"time":0,"data":{"turn":1,"callId":"","files":[]}}""");
        AssertNoFiles("""{"type":"deliverables/presented","seq":6,"time":0,"data":{"turn":1,"callId":"c","files":{}}}""");
        AssertNoFiles("""{"type":"deliverables/presented","seq":7,"time":0,"data":{"turn":1,"callId":"c"}}""");
        // 全部文件无效与坏载荷同义：官方视为不改变状态。
        AssertNoFiles("""{"type":"deliverables/presented","seq":8,"time":0,"data":{"turn":1,"callId":"c","files":[{"path":" "}]}}""");
    }

    [Test]
    public void PresentedEventAcceptsSafeIntegralNumberNotation()
    {
        var wireEvent = WireEventJson.ParseEvent(Json("""
            {"type":"deliverables/presented","seq":31,"time":0,
             "data":{"turn":1.0,"callId":"c","files":[{"path":"a"}]}}
            """))!;

        ClassicAssert.IsTrue(WireEventJson.TryGetPresentedFiles(wireEvent, out var turn, out var files));
        ClassicAssert.AreEqual(1, turn);
        ClassicAssert.AreEqual("a", files.Single().Path);
    }

    [Test]
    public void EventFrameMapsToDeliverablesPresentedUpdate()
    {
        var updates = ParseFollowEventFrames("""
                                             [
                                               {"type": "deliverables/presented", "seq": 11, "time": 1700000011000,
                                                "data": {"turn": 2, "callId": "call-a",
                                                         "files": [{"path": "report.md", "description": "报告"}]}}
                                             ]
                                             """);

        Assert.That(updates.Single(), Is.TypeOf<SessionUpdate.DeliverablesPresented>());
        var presented = ((SessionUpdate.DeliverablesPresented)updates.Single()).Announcement;
        ClassicAssert.AreEqual((2L, 11L), (presented.Turn, presented.Seq));
        ClassicAssert.AreEqual("report.md", presented.Files.Single().Path);
    }

    [Test]
    public void InvalidPresentedEventFrameYieldsNoUpdate()
    {
        // 坏载荷不能让会话加载失败：映射层静默忽略该事件。
        var updates = ParseFollowEventFrames("""
                                             [
                                               {"type": "deliverables/presented", "seq": 12, "time": 1700000012000,
                                                "data": {"turn": 1, "callId": "call-b", "files": "nope"}}
                                             ]
                                             """);

        Assert.That(updates, Is.Empty);
    }

    [Test]
    public void FollowSnapshotCarriesAnnouncementEntriesAndReplaysPresentedUpdates()
    {
        var frame = FollowFrameJson.Parse(JsonDocument.Parse("""
                                                             {"type": "snapshot",
                                                              "header": {"version": 4, "id": "session-1", "createdAt": 1700000000000, "isSeeded": false},
                                                              "cursor": 2,
                                                              "records": [
                                                                {"type": "event", "event": {"type": "deliverables/presented", "seq": 748, "time": 1700000748000,
                                                                 "data": {"turn": 1, "callId": "call-1", "files": [{"path": "a.md"}, {"path": "b.md", "description": "说明"}]}}}],
                                                              "hasMore": false}
                                                             """).RootElement.Clone())
                    ?? throw new AssertionException("The minimal snapshot fixture should parse.");
        Assert.That(frame, Is.TypeOf<FollowFrame.Snapshot>());
        var snapshotFrame = (FollowFrame.Snapshot)frame;

        var updates = HarnessSessionService.MapFollowFrame(snapshotFrame).ToList();

        // 快照整窗替换携带时间线条目；重放更新紧随其后。
        ClassicAssert.AreEqual(2, updates.Count);
        var snapshot     = (SessionUpdate.Snapshot)updates[0];
        var announcement = snapshot.Entries.OfType<DeliverablesPresentedAnnouncement>().Single();
        ClassicAssert.AreEqual((748L, 1L), (announcement.Seq, announcement.Turn));
        ClassicAssert.AreEqual(2, announcement.Files.Count);
        Assert.That(updates[1], Is.TypeOf<SessionUpdate.DeliverablesPresented>());
        var replayed = ((SessionUpdate.DeliverablesPresented)updates[1]).Announcement;
        ClassicAssert.AreEqual((1L, 748L), (replayed.Turn, replayed.Seq));
    }

    [Test]
    public void MapEntriesKeepsDeliverablesSeparatePerTurnAndFromWorkspaceChanges()
    {
        var records = new[]
        {
            WireEventJson.ParseEvent(Json("""{"type":"deliverables/presented","seq":10,"time":1700000010000,"data":{"turn":1,"callId":"c1","files":[{"path":"one.md"}]}}"""))!,
            WireEventJson.ParseEvent(Json("""{"type":"workspace/changes","seq":11,"time":1700000011000,"data":{"turn":1}}"""))!,
            WireEventJson.ParseEvent(Json("""{"type":"deliverables/presented","seq":12,"time":1700000012000,"data":{"turn":2,"callId":"c2","files":[{"path":"two.md"}]}}"""))!
        };

        var entries = HarnessSessionService.MapEntries(records);

        var presented = entries.OfType<DeliverablesPresentedAnnouncement>().ToList();
        ClassicAssert.AreEqual(2, presented.Count);
        ClassicAssert.AreEqual((10L, 1L, 1), (presented[0].Seq, presented[0].Turn, presented[0].Files.Count));
        ClassicAssert.AreEqual("one.md", presented[0].Files[0].Path);
        ClassicAssert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1700000010000), presented[0].CreatedAt);
        ClassicAssert.AreEqual((12L, 2L, 1), (presented[1].Seq, presented[1].Turn, presented[1].Files.Count));
        // workspace/changes 链路不受影响：两类条目并存。
        ClassicAssert.AreEqual(1, entries.OfType<WorkspaceChangesAnnouncement>().Count());
    }

    [Test]
    public void ForClosingKeepsLatestDeclarationPerPathInFirstSeenOrder()
    {
        IReadOnlyList<DeliveredFileDeclaration> declarations =
        [
            new("a.md", null, 10, 0),
            new("b.md", null, 11, 0),
            new("a.md", "最终版", 12, 0)
        ];

        var closing = TurnDeliverables.ForClosing(declarations, 20);

        ClassicAssert.AreEqual(2, closing.Count);
        ClassicAssert.AreEqual(("a.md", "最终版", 12L), (closing[0].Path, closing[0].Description, closing[0].Seq));
        ClassicAssert.AreEqual(("b.md", 11L), (closing[1].Path, closing[1].Seq));
    }

    [Test]
    public void ForClosingExcludesDeclarationsAtOrAfterClosingSeq()
    {
        IReadOnlyList<DeliveredFileDeclaration> declarations =
        [
            new("kept.md", null, 5, 0),
            new("excluded.md", null, 9, 0)
        ];

        // 官方为严格小于：seq 等于收束消息 seq 的声明不计入。
        var closing = TurnDeliverables.ForClosing(declarations, 9);

        ClassicAssert.AreEqual(1, closing.Count);
        ClassicAssert.AreEqual("kept.md", closing.Single().Path);
    }

    /// <summary>按事件 JSON 数组构造 EventFrame 序列并映射为会话更新。</summary>
    private static List<SessionUpdate> ParseFollowEventFrames(string json)
    {
        using var document = JsonDocument.Parse(json);
        var frames = document.RootElement.EnumerateArray()
                             .Select(FollowFrame (element) =>
                                         new FollowFrame.EventFrame(WireEventJson.ParseEvent(element.Clone())!))
                             .ToArray();
        return frames.SelectMany(HarnessSessionService.MapFollowFrame).ToList();
    }

    private static void AssertNoFiles(string json)
    {
        var wireEvent = WireEventJson.ParseEvent(Json(json))!;
        ClassicAssert.IsFalse(WireEventJson.TryGetPresentedFiles(wireEvent, out _, out _));
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
