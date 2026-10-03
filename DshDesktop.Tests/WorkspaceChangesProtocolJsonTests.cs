using DshDesktop.Core.Models;
using DshDesktop.Harness.Exceptions;
using DshDesktop.Harness.Json;
using DshDesktop.Harness.Models.Events;
using DshDesktop.Harness.Services.Changes;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using System.Text.Json;

namespace DshDesktop.Tests;

/// <summary>workspace/changes 域的载荷解析、应用模型映射与事件解析验证。</summary>
public sealed class WorkspaceChangesProtocolJsonTests
{
    [Test]
    public void SummaryWireParsesBackendPayload()
    {
        // 形状对照 ui-deliverables present-open.ts handleChangesSummary：
        // Host 只下发 turn/files/total/added/deleted，cwd 与 snapshot 保留在后端。
        var summary = HarnessWorkspaceChangesService.ToSummary(Json("""
                                                                    {"turn":3,"files":[{"path":"src/a.ts","display":"src/a.ts","added":24,"deleted":6},
                                                                     {"path":"assets/logo.bin","display":"assets/logo.bin","added":0,"deleted":0,"binary":true},
                                                                     {"path":"big.txt","display":"big.txt","added":0,"deleted":0,"oversized":true}],
                                                                     "total":3,"added":24,"deleted":6}
                                                                    """).Deserialize(HarnessJsonContext.Default
                                                                           .WorkspaceChangesSummaryWire)!);

        ClassicAssert.AreEqual(3, summary.Turn);
        ClassicAssert.AreEqual(3, summary.Total);
        ClassicAssert.AreEqual(24, summary.Added);
        ClassicAssert.AreEqual(6, summary.Deleted);
        ClassicAssert.AreEqual(3, summary.Files.Count);

        var text = summary.Files[0];
        ClassicAssert.AreEqual(("src/a.ts", "src/a.ts", 24L, 6L), (text.Path, text.Display, text.Added, text.Deleted));
        ClassicAssert.IsFalse(text.IsBinary);
        ClassicAssert.IsFalse(text.IsOversized);

        ClassicAssert.IsTrue(summary.Files[1].IsBinary);
        ClassicAssert.AreEqual(0, summary.Files[1].Added);
        ClassicAssert.IsTrue(summary.Files[2].IsOversized);
        ClassicAssert.IsFalse(summary.Files[2].IsBinary);
    }

    [Test]
    public void SummaryWireWithMissingFilesYieldsEmptyList()
    {
        // 上游保证 files 必有；缺失字段时按空集合处理，而不是以空引用异常冒出。
        var summary = HarnessWorkspaceChangesService.ToSummary(Json("""{"turn":3,"total":0,"added":0,"deleted":0}""")
                                                                  .Deserialize(HarnessJsonContext.Default
                                                                                  .WorkspaceChangesSummaryWire)!);

        ClassicAssert.AreEqual(3, summary.Turn);
        ClassicAssert.AreEqual(0, summary.Total);
        ClassicAssert.IsEmpty(summary.Files);
    }

    [Test]
    public void DiffWireParsesTextKind()
    {
        var diff = HarnessWorkspaceChangesService.ToDiff(Json("""
                                                              {"kind":"text","path":"src/a.ts","display":"src/a.ts","before":true,"after":true,
                                                               "hunks":[{"oldStart":12,"oldLines":3,"newStart":12,"newLines":4,
                                                                          "lines":[" ctx","- old","+ new1","+ new2"," ctx"]}],
                                                               "coarse":false}
                                                              """).Deserialize(HarnessJsonContext.Default
                                                                                  .WorkspaceFileDiffWire)!);

        ClassicAssert.AreEqual(WorkspaceDiffKind.Text, diff.Kind);
        ClassicAssert.IsTrue(diff.ExistedBefore);
        ClassicAssert.IsTrue(diff.ExistedAfter);
        ClassicAssert.IsFalse(diff.IsCoarse);
        Assert.That(diff.Hunks, Has.Count.EqualTo(1));
        var hunk = diff.Hunks.Single();
        ClassicAssert.AreEqual((12, 3, 12, 4), (hunk.OldStart, hunk.OldLines, hunk.NewStart, hunk.NewLines));
        ClassicAssert.AreEqual(new[] { " ctx", "- old", "+ new1", "+ new2", " ctx" }, hunk.Lines);
    }

    [Test]
    public void DiffWireParsesBinaryAndOversizedKinds()
    {
        var binary =
            HarnessWorkspaceChangesService
               .ToDiff(Json("""{"kind":"binary","path":"assets/logo.bin","display":"assets/logo.bin"}""")
                          .Deserialize(HarnessJsonContext.Default.WorkspaceFileDiffWire)!);
        ClassicAssert.AreEqual(WorkspaceDiffKind.Binary, binary.Kind);
        ClassicAssert.IsFalse(binary.ExistedBefore);
        ClassicAssert.IsEmpty(binary.Hunks);

        var oversized =
            HarnessWorkspaceChangesService.ToDiff(Json("""{"kind":"oversized","path":"big.txt","display":"big.txt"}""")
                                                     .Deserialize(HarnessJsonContext.Default.WorkspaceFileDiffWire)!);
        ClassicAssert.AreEqual(WorkspaceDiffKind.Oversized, oversized.Kind);
        ClassicAssert.IsEmpty(oversized.Hunks);
    }

    [Test]
    public void DiffWireWithUnknownKindThrows()
    {
        var wire = Json("""{"kind":"future","path":"x","display":"x"}""")
           .Deserialize(HarnessJsonContext.Default.WorkspaceFileDiffWire)!;
        Assert.Throws<HarnessConnectionException>(() => HarnessWorkspaceChangesService.ToDiff(wire));
    }

    [Test]
    public void DiffWireWithHunkMissingLinesYieldsEmptyLines()
    {
        var diff = HarnessWorkspaceChangesService.ToDiff(Json("""
                                                              {"kind":"text","path":"src/a.ts","display":"src/a.ts",
                                                               "hunks":[{"oldStart":1,"oldLines":0,"newStart":1,"newLines":2}]}
                                                              """).Deserialize(HarnessJsonContext.Default
                                                                                  .WorkspaceFileDiffWire)!);

        Assert.That(diff.Hunks, Has.Count.EqualTo(1));
        var hunk = diff.Hunks.Single();
        ClassicAssert.AreEqual((1, 0, 1, 2), (hunk.OldStart, hunk.OldLines, hunk.NewStart, hunk.NewLines));
        ClassicAssert.IsEmpty(hunk.Lines);
    }

    [Test]
    public void WorkspaceChangesEventParsesTurnAndCarriesSeq()
    {
        var wireEvent =
            WireEventJson
               .ParseEvent(Json("""{"type":"workspace/changes","seq":41,"time":1727840000000,"data":{"turn":3}}"""))
            !;

        ClassicAssert.AreEqual(41, wireEvent.Seq);
        ClassicAssert.IsTrue(WireEventJson.TryGetWorkspaceChanges(wireEvent, out var turn));
        ClassicAssert.AreEqual(3, turn);
    }

    [Test]
    public void WorkspaceChangesEventWithMalformedDataYieldsNoTurn()
    {
        var noTurn =
            WireEventJson.ParseEvent(Json("""{"type":"workspace/changes","seq":42,"time":1727840000000,"data":{}}"""))
            !;
        ClassicAssert.IsFalse(WireEventJson.TryGetWorkspaceChanges(noTurn, out _));

        var other =
            WireEventJson.ParseEvent(Json("""{"type":"turn/end","seq":43,"time":1727840000000,"data":{"turn":3}}"""))
            !;
        ClassicAssert.IsFalse(WireEventJson.TryGetWorkspaceChanges(other, out _));
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
