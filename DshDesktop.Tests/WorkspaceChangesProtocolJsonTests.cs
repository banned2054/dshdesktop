using DshDesktop.Core.Models;
using DshDesktop.Harness.Exceptions;
using DshDesktop.Harness.Json;
using DshDesktop.Harness.Models.Events;
using DshDesktop.Harness.Services.Changes;
using System.Text.Json;
using Xunit;

namespace DshDesktop.Tests;

/// <summary>workspace/changes 域的载荷解析、应用模型映射与事件解析验证。</summary>
public sealed class WorkspaceChangesProtocolJsonTests
{
    [Fact]
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

        Assert.Equal(3, summary.Turn);
        Assert.Equal(3, summary.Total);
        Assert.Equal(24, summary.Added);
        Assert.Equal(6, summary.Deleted);
        Assert.Equal(3, summary.Files.Count);

        var text = summary.Files[0];
        Assert.Equal(("src/a.ts", "src/a.ts", 24L, 6L), (text.Path, text.Display, text.Added, text.Deleted));
        Assert.False(text.IsBinary);
        Assert.False(text.IsOversized);

        Assert.True(summary.Files[1].IsBinary);
        Assert.Equal(0, summary.Files[1].Added);
        Assert.True(summary.Files[2].IsOversized);
        Assert.False(summary.Files[2].IsBinary);
    }

    [Fact]
    public void SummaryWireWithMissingFilesYieldsEmptyList()
    {
        // 上游保证 files 必有；缺失字段时按空集合处理，而不是以空引用异常冒出。
        var summary = HarnessWorkspaceChangesService.ToSummary(Json("""{"turn":3,"total":0,"added":0,"deleted":0}""")
                                                                  .Deserialize(HarnessJsonContext.Default
                                                                                  .WorkspaceChangesSummaryWire)!);

        Assert.Equal(3, summary.Turn);
        Assert.Equal(0, summary.Total);
        Assert.Empty(summary.Files);
    }

    [Fact]
    public void DiffWireParsesTextKind()
    {
        var diff = HarnessWorkspaceChangesService.ToDiff(Json("""
                                                              {"kind":"text","path":"src/a.ts","display":"src/a.ts","before":true,"after":true,
                                                               "hunks":[{"oldStart":12,"oldLines":3,"newStart":12,"newLines":4,
                                                                          "lines":[" ctx","- old","+ new1","+ new2"," ctx"]}],
                                                               "coarse":false}
                                                              """).Deserialize(HarnessJsonContext.Default
                                                                                  .WorkspaceFileDiffWire)!);

        Assert.Equal(WorkspaceDiffKind.Text, diff.Kind);
        Assert.True(diff.ExistedBefore);
        Assert.True(diff.ExistedAfter);
        Assert.False(diff.IsCoarse);
        var hunk = Assert.Single(diff.Hunks);
        Assert.Equal((12, 3, 12, 4), (hunk.OldStart, hunk.OldLines, hunk.NewStart, hunk.NewLines));
        Assert.Equal([" ctx", "- old", "+ new1", "+ new2", " ctx"], hunk.Lines);
    }

    [Fact]
    public void DiffWireParsesBinaryAndOversizedKinds()
    {
        var binary =
            HarnessWorkspaceChangesService
               .ToDiff(Json("""{"kind":"binary","path":"assets/logo.bin","display":"assets/logo.bin"}""")
                          .Deserialize(HarnessJsonContext.Default.WorkspaceFileDiffWire)!);
        Assert.Equal(WorkspaceDiffKind.Binary, binary.Kind);
        Assert.False(binary.ExistedBefore);
        Assert.Empty(binary.Hunks);

        var oversized =
            HarnessWorkspaceChangesService.ToDiff(Json("""{"kind":"oversized","path":"big.txt","display":"big.txt"}""")
                                                     .Deserialize(HarnessJsonContext.Default.WorkspaceFileDiffWire)!);
        Assert.Equal(WorkspaceDiffKind.Oversized, oversized.Kind);
        Assert.Empty(oversized.Hunks);
    }

    [Fact]
    public void DiffWireWithUnknownKindThrows()
    {
        var wire = Json("""{"kind":"future","path":"x","display":"x"}""")
           .Deserialize(HarnessJsonContext.Default.WorkspaceFileDiffWire)!;
        Assert.Throws<HarnessConnectionException>(() => HarnessWorkspaceChangesService.ToDiff(wire));
    }

    [Fact]
    public void DiffWireWithHunkMissingLinesYieldsEmptyLines()
    {
        var diff = HarnessWorkspaceChangesService.ToDiff(Json("""
                                                              {"kind":"text","path":"src/a.ts","display":"src/a.ts",
                                                               "hunks":[{"oldStart":1,"oldLines":0,"newStart":1,"newLines":2}]}
                                                              """).Deserialize(HarnessJsonContext.Default
                                                                                  .WorkspaceFileDiffWire)!);

        var hunk = Assert.Single(diff.Hunks);
        Assert.Equal((1, 0, 1, 2), (hunk.OldStart, hunk.OldLines, hunk.NewStart, hunk.NewLines));
        Assert.Empty(hunk.Lines);
    }

    [Fact]
    public void WorkspaceChangesEventParsesTurnAndCarriesSeq()
    {
        var wireEvent =
            WireEventJson
               .ParseEvent(Json("""{"type":"workspace/changes","seq":41,"time":1727840000000,"data":{"turn":3}}"""))
            !;

        Assert.Equal(41, wireEvent.Seq);
        Assert.True(WireEventJson.TryGetWorkspaceChanges(wireEvent, out var turn));
        Assert.Equal(3, turn);
    }

    [Fact]
    public void WorkspaceChangesEventWithMalformedDataYieldsNoTurn()
    {
        var noTurn =
            WireEventJson.ParseEvent(Json("""{"type":"workspace/changes","seq":42,"time":1727840000000,"data":{}}"""))
            !;
        Assert.False(WireEventJson.TryGetWorkspaceChanges(noTurn, out _));

        var other =
            WireEventJson.ParseEvent(Json("""{"type":"turn/end","seq":43,"time":1727840000000,"data":{"turn":3}}"""))
            !;
        Assert.False(WireEventJson.TryGetWorkspaceChanges(other, out _));
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
