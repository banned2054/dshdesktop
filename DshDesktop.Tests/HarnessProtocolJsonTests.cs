using DshDesktop.Core.Models;
using DshDesktop.Harness.Json;
using DshDesktop.Harness.Models.Events;
using DshDesktop.Harness.Models.Requests;
using DshDesktop.Harness.Models.Responses;
using DshDesktop.Harness.Models.Rpc;
using DshDesktop.Harness.Services.Connection;
using DshDesktop.Harness.Services.Sessions;
using DshDesktop.Harness.Services.Workspaces;
using System.Text.Json;
using Xunit;

namespace DshDesktop.Tests;

/// <summary>协议信封与线上帧解析的行为验证（真实样本形态）。</summary>
public sealed class HarnessProtocolJsonTests
{
    [Fact]
    public void RequestEnvelopeCarriesCamelCaseWireShape()
    {
        var body = RpcEnvelope.BuildRequest("rpc-1", "session/prompt",
                                            new SessionPromptRequest("request-1", "session-1", "queue",
                                                                     [new PromptTextPart("你好")], "Asia/Shanghai"),
                                            HarnessJsonContext.Default.SessionPromptRequest);

        using var document = JsonDocument.Parse(body);
        var       root     = document.RootElement;
        Assert.Equal("client-request", root.GetProperty("type").GetString());
        Assert.Equal("rpc-1", root.GetProperty("rpcId").GetString());
        Assert.Equal("session/prompt", root.GetProperty("method").GetString());
        var request = root.GetProperty("payload")
                          .GetProperty("args")
                          .GetProperty("request");
        Assert.Equal("request-1", request.GetProperty("requestId").GetString());
        Assert.Equal("queue", request.GetProperty("mode").GetString());
        Assert.Equal("Asia/Shanghai", request.GetProperty("clientTimeZone").GetString());
        var part = request.GetProperty("content")[0];
        Assert.Equal("text", part.GetProperty("type").GetString());
        Assert.Equal("你好", part.GetProperty("text").GetString());
    }

    [Fact]
    public void SessionAddressSerializesAsKindSession()
    {
        var element =
            JsonSerializer.SerializeToElement(new SessionAddress("session-9"),
                                              HarnessJsonContext.Default.SessionAddress);
        Assert.Equal("session", element.GetProperty("kind").GetString());
        Assert.Equal("session-9", element.GetProperty("sessionId").GetString());
    }

    [Fact]
    public void ResponseEnvelopeParsesSuccessAndFailure()
    {
        var success =
            RpcEnvelope.ParseResponse("""{"type":"server-response","rpcId":"rpc-1","result":{"ok":true,"value":{"accepted":true}}}""");
        Assert.Equal("rpc-1", success.RpcId);
        Assert.True(success.Ok);
        Assert.NotNull(success.Value);
        Assert.True(success.Value.Value.GetProperty("accepted").GetBoolean());

        var failure =
            RpcEnvelope.ParseResponse("""{"type":"server-response","rpcId":"rpc-2","result":{"ok":false,"error":{"code":"session/not-found","message":"会话不存在"}}}""");
        Assert.False(failure.Ok);
        Assert.Equal("session/not-found", failure.ErrorCode);
        Assert.Equal("会话不存在", failure.ErrorMessage);
    }

    [Fact]
    public void FollowSnapshotFrameParsesHeaderRecordsAndTitle()
    {
        var frame = FollowFrameJson.Parse(JsonDocument.Parse("""
                                                             {
                                                               "type": "snapshot",
                                                               "header": {"version": 3, "id": "session-1", "createdAt": 1700000000000, "isSeeded": false},
                                                               "cursor": 4,
                                                               "records": [
                                                                 {"type": "event", "event": {"type": "user/message", "seq": 1, "time": 1700000001000,
                                                                   "data": {"id": "m1", "role": "user", "content": [{"type": "text", "text": "问题"}], "source": {}}}},
                                                                 {"type": "event", "event": {"type": "assistant/message", "seq": 2, "time": 1700000002000,
                                                                   "data": {"turn": 1, "step": 0, "interrupted": true,
                                                                     "message": {"id": "m2", "role": "assistant", "content": [{"type": "reasoning", "text": "先想一想"}, {"type": "text", "text": "回答"}], "source": {}},
                                                                     "stream": []}}}
                                                               ],
                                                               "hasMore": false,
                                                               "projections": {"asOfSeq": 4, "values": {"title": "示例标题"}}
                                                             }
                                                             """).RootElement.Clone());

        var snapshot = Assert.IsType<FollowFrame.Snapshot>(frame);
        Assert.Equal(4, snapshot.Cursor);
        Assert.Equal("示例标题", snapshot.Title);
        Assert.False(snapshot.HasMore);
        Assert.Equal(2, snapshot.Records.Count);

        var userMessage = WireEventJson.TryGetMessage(snapshot.Records[0]);
        Assert.NotNull(userMessage);
        Assert.Equal("问题", WireEventJson.ExtractText(userMessage));

        var assistantMessage = WireEventJson.TryGetMessage(snapshot.Records[1]);
        Assert.NotNull(assistantMessage);
        Assert.Equal("回答", WireEventJson.ExtractText(assistantMessage));
        Assert.Equal("先想一想", WireEventJson.ExtractReasoning(assistantMessage));
        Assert.True(WireEventJson.IsInterrupted(snapshot.Records[1]));
    }

    [Fact]
    public void SnapshotMappingHidesInjectedUserContextMessages()
    {
        var records = ParseRecords("""
                                   [
                                     {"type": "user/message", "seq": 1, "time": 1700000001000,
                                      "data": {"id": "m1", "role": "user", "content": [{"type": "text", "text": "你好"}],
                                               "source": {"kind": "user", "rpcId": "request-1"}}},
                                     {"type": "user/message", "seq": 2, "time": 1700000002000,
                                      "data": {"id": "m2", "role": "user", "content": [{"type": "text", "text": "Current runtime context."}],
                                               "source": {"kind": "plugin", "plugin": "@deepseek-ai/dsh-system-prompt", "form": "snapshot", "sections": []}}},
                                     {"type": "user/message", "seq": 3, "time": 1700000003000,
                                      "data": {"id": "m3", "role": "user", "content": [{"type": "text", "text": "<system-reminder>技能目录</system-reminder>"}],
                                               "source": {"kind": "skill-catalog", "form": "catalog"}}},
                                     {"type": "assistant/message", "seq": 4, "time": 1700000004000,
                                      "data": {"turn": 1, "step": 0,
                                               "message": {"id": "m4", "role": "assistant", "content": [{"type": "text", "text": "回复"}], "source": {"kind": "model"}}}}
                                   ]
                                   """);

        var messages = HarnessSessionService.MapMessages(records);

        // 真实用户输入与助手回复保留；runtime-context 快照与技能目录等注入消息不作为用户气泡显示。
        Assert.Equal(2, messages.Count);
        Assert.Equal("你好", messages[0].Content);
        Assert.Equal(MessageRole.User, messages[0].Role);
        Assert.Equal("回复", messages[1].Content);
        Assert.Equal(MessageRole.Assistant, messages[1].Role);
    }

    [Fact]
    public void SessionPageRequestSerializesBackwardCursorWireShape()
    {
        var element =
            JsonSerializer.SerializeToElement(new SessionPageRequest(new SessionAddress("session-1"), 42, 9, 50),
                                              HarnessJsonContext.Default.SessionPageRequest);

        Assert.Equal("session", element.GetProperty("address").GetProperty("kind").GetString());
        Assert.Equal("session-1", element.GetProperty("address").GetProperty("sessionId").GetString());
        Assert.Equal(42, element.GetProperty("throughSeq").GetInt64());
        Assert.Equal(9, element.GetProperty("beforeSeq").GetInt64());
        Assert.Equal(50, element.GetProperty("maxMessages").GetInt32());
    }

    [Fact]
    public void PageValueRecordsParseAndFoldIntoEntries()
    {
        var page = JsonSerializer.Deserialize("""
                                              {
                                                "records": [
                                                  {"type": "event", "event": {"type": "tool/call", "seq": 7, "time": 1700000007000,
                                                    "data": {"turn": 1, "step": 2, "callId": "call-1", "name": "fs.read", "arguments": "{\"path\":\"a.md\"}"}}},
                                                  {"type": "event", "event": {"type": "assistant/message", "seq": 8, "time": 1700000008000,
                                                    "data": {"turn": 1, "step": 3,
                                                             "message": {"id": "m8", "role": "assistant", "content": [{"type": "text", "text": "调用完成"}], "source": {"kind": "model"}}}}},
                                                  {"type": "event", "event": {"type": "tool/result", "seq": 9, "time": 1700000009000,
                                                    "data": {"turn": 1, "step": 3,
                                                             "message": {"id": "m9", "role": "user", "source": {"kind": "tool", "callId": "call-1"},
                                                                         "content": [{"type": "tool-result", "toolCallId": "call-1", "content": [{"type": "text", "text": "文件内容"}]}]}}}}
                                                ],
                                                "hasMore": true
                                              }
                                              """,
                                              HarnessJsonContext.Default.SessionPageValue);

        Assert.NotNull(page);
        Assert.True(page.HasMore);

        var records = FollowFrameJson.ParseHistoryRecords(page.Records);
        Assert.Equal(3, records.Count);

        var entries = HarnessSessionService.MapEntries(records);
        Assert.Equal(2, entries.Count);

        // tool/call 与同 callId 的 tool/result 折叠为同一条目：保留发起位置与参数，补上结果。
        var tool = Assert.IsType<ToolActivity>(entries[0]);
        Assert.Equal(7, tool.Seq);
        Assert.Equal("call-1", tool.CallId);
        Assert.Equal("fs.read", tool.Name);
        Assert.Equal("{\"path\":\"a.md\"}", tool.ArgumentsJson);
        Assert.Equal(ToolActivityStatus.Succeeded, tool.Status);
        Assert.Equal("文件内容", tool.ResultText);
        Assert.Null(tool.ErrorReason);
        Assert.NotNull(tool.CompletedAt);

        var message = Assert.IsType<ConversationMessage>(entries[1]);
        Assert.Equal("调用完成", message.Content);
    }

    [Fact]
    public void FailedToolResultMapsErrorIdentityAndReason()
    {
        var records = ParseRecords("""
                                   [
                                     {"type": "tool/call", "seq": 3, "time": 1700000003000,
                                      "data": {"turn": 1, "step": 1, "callId": "call-9", "name": "shell.run", "arguments": "{\"cmd\":\"ls\"}"}},
                                     {"type": "tool/result", "seq": 4, "time": 1700000004000,
                                      "data": {"turn": 1, "step": 1,
                                               "message": {"id": "m4", "role": "user", "source": {"kind": "tool", "callId": "call-9"},
                                                           "content": [{"type": "tool-result", "toolCallId": "call-9", "isError": true,
                                                                        "content": [{"type": "text", "text": "exit 1"}]}]},
                                               "error": {"name": "ToolError", "code": "EEXIT", "reason": "命令以非零状态退出"}}}
                                   ]
                                   """);

        var entries = HarnessSessionService.MapEntries(records);

        var tool = Assert.IsType<ToolActivity>(Assert.Single(entries));
        Assert.Equal(ToolActivityStatus.Failed, tool.Status);
        Assert.Equal("exit 1", tool.ResultText);
        Assert.Equal("命令以非零状态退出", tool.ErrorReason);
    }

    [Fact]
    public void V4ToolResultMessageMapsTopLevelIdentityAndText()
    {
        // v4（含迁移后的 v3 历史）tool/result 是 first-class tool 消息：toolCallId/isError
        // 在消息顶层、文本块直接位于 content。回归：只解析 v3 wrapper 时结果文本恒为空、
        // 失败恒标成功（真实会话实测全部受影响）。
        var records = ParseRecords("""
                                   [
                                     {"type": "tool/call", "seq": 3, "time": 1700000003000,
                                      "data": {"turn": 1, "step": 1, "callId": "call-4", "name": "shell.run", "arguments": "{}"}},
                                     {"type": "tool/result", "seq": 4, "time": 1700000004000,
                                      "data": {"turn": 1, "step": 1,
                                               "message": {"id": "m6", "role": "tool", "source": {"kind": "tool", "callId": "call-4"},
                                                           "toolCallId": "call-4", "isError": true,
                                                           "content": [{"type": "text", "text": "命令失败"}]}}}
                                   ]
                                   """);

        var entries = HarnessSessionService.MapEntries(records);

        var tool = Assert.IsType<ToolActivity>(Assert.Single(entries));
        Assert.Equal("call-4", tool.CallId);
        Assert.Equal(ToolActivityStatus.Failed, tool.Status);
        Assert.Equal("命令失败", tool.ResultText);
    }

    [Fact]
    public void V4ToolResultWithoutSourceFallsBackToTopLevelCallId()
    {
        var records = ParseRecords("""
                                   [
                                     {"type": "tool/result", "seq": 5, "time": 1700000005000,
                                      "data": {"turn": 1, "step": 1,
                                               "message": {"id": "m7", "role": "tool", "toolCallId": "call-y", "isError": false,
                                                           "content": [{"type": "text", "text": "第一段"}, {"type": "text", "text": "第二段"}]}}}
                                   ]
                                   """);

        var entries = HarnessSessionService.MapEntries(records);

        var tool = Assert.IsType<ToolActivity>(Assert.Single(entries));
        Assert.Equal("call-y", tool.CallId);
        Assert.Equal(ToolActivityStatus.Succeeded, tool.Status);
        Assert.Equal("第一段\n第二段", tool.ResultText);
    }

    [Fact]
    public void OrphanToolResultBecomesStandaloneEntry()
    {
        // 窗口起点落在调用中间（或恢复期 repair 合成结果）：没有 tool/call 也展示结果条目。
        var records = ParseRecords("""
                                   [
                                     {"type": "tool/result", "seq": 5, "time": 1700000005000,
                                      "data": {"turn": 1, "step": 1,
                                               "message": {"id": "m5", "role": "user", "source": {"kind": "tool", "callId": "call-x"},
                                                           "content": [{"type": "tool-result", "toolCallId": "call-x",
                                                                        "content": [{"type": "text", "text": "结果文本"}]}]}}}
                                   ]
                                   """);

        var entries = HarnessSessionService.MapEntries(records);

        var tool = Assert.IsType<ToolActivity>(Assert.Single(entries));
        Assert.Equal("call-x", tool.CallId);
        Assert.Equal(ToolActivityStatus.Succeeded, tool.Status);
        Assert.Equal("结果文本", tool.ResultText);
    }

    [Fact]
    public void TurnBoundariesMapToBoundaryEntriesAndCarryTurnNumbers()
    {
        var records = ParseRecords("""
                                   [
                                     {"type": "assistant/message", "seq": 2, "time": 1700000002000,
                                      "data": {"turn": 3, "step": 0,
                                               "message": {"id": "m2", "role": "assistant", "content": [{"type": "text", "text": "回答"}], "source": {}}}},
                                     {"type": "turn/end", "seq": 3, "time": 1700000003000, "data": {"turn": 3}}
                                   ]
                                   """);

        var entries = HarnessSessionService.MapEntries(records);

        Assert.Equal(2, entries.Count);
        var message = Assert.IsType<ConversationMessage>(entries[0]);
        Assert.Equal(3, message.Turn);
        var boundary = Assert.IsType<TurnBoundary>(entries[1]);
        Assert.Equal(3, boundary.Turn);
        Assert.Equal(3, boundary.Seq);
    }

    [Fact]
    public void AssistantToolCallBlocksAreDetectedForProcessClassification()
    {
        // 内容含 tool-call 块的助手提交是轮次过程，不是可见回复：HasToolCalls 标注供折叠规则使用。
        var records = ParseRecords("""
                                   [
                                     {"type": "assistant/message", "seq": 1, "time": 1700000001000,
                                      "data": {"turn": 1, "step": 0,
                                               "message": {"id": "m1", "role": "assistant",
                                                           "content": [{"type": "text", "text": "先读取文件"},
                                                                       {"type": "tool-call", "id": "call-1", "name": "fs.read", "arguments": "{}"}],
                                                           "source": {}}}},
                                     {"type": "assistant/message", "seq": 2, "time": 1700000002000,
                                      "data": {"turn": 1, "step": 1,
                                               "message": {"id": "m2", "role": "assistant", "content": [{"type": "text", "text": "最终回复"}], "source": {}}}}
                                   ]
                                   """);

        var messages = HarnessSessionService.MapMessages(records);

        Assert.Equal(2, messages.Count);
        Assert.True(messages[0].HasToolCalls);
        Assert.Equal("先读取文件", messages[0].Content);
        Assert.False(messages[1].HasToolCalls);
    }

    private static IReadOnlyList<SessionWireEvent> ParseRecords(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray()
                       .Select(element => WireEventJson.ParseEvent(element.Clone()))
                       .OfType<SessionWireEvent>()
                       .ToArray();
    }

    [Fact]
    public void AssistantStreamChunkFrameParsesTextDelta()
    {
        var frame = AssistantStreamFrameJson.ParseFrame(JsonDocument.Parse("""
                                                                           {"type": "chunk", "attemptId": "attempt-1", "revision": 3, "index": 2, "time": 1700000003000,
                                                                            "chunk": {"type": "text-delta", "index": 0, "text": "增量文本"}}
                                                                           """).RootElement.Clone());

        var chunk = Assert.IsType<AssistantStreamFrame.StreamChunkFrame>(frame);
        Assert.Equal("attempt-1", chunk.AttemptId);
        Assert.Equal(3, chunk.Revision);
        var delta = Assert.IsType<StreamChunk.TextDelta>(chunk.Chunk);
        Assert.Equal("增量文本", delta.Text);
    }

    [Fact]
    public void RemoteEventFramesParseReadyAndEmit()
    {
        var ready = RemoteEventJson.Parse(JsonDocument
                                         .Parse("""{"type": "ready", "clientId": "client-1", "host": {"home": "C:/dsh"}}""")
                                         .RootElement.Clone());
        var readyFrame = Assert.IsType<RemoteEventFrame.Ready>(ready);
        Assert.Equal("client-1", readyFrame.ClientId);

        var emit = RemoteEventJson.Parse(JsonDocument
                                        .Parse("""{"type": "emit", "event": "api-session/status", "args": ["session-1", true]}""")
                                        .RootElement.Clone());
        var emitFrame = Assert.IsType<RemoteEventFrame.Emit>(emit);
        Assert.Equal("api-session/status", emitFrame.Event);
        Assert.Equal(2, emitFrame.Args.Count);
        Assert.Equal("session-1", emitFrame.Args[0].GetString());
        Assert.True(emitFrame.Args[1].GetBoolean());
    }

    [Fact]
    public void ApprovalWaterfallCarriesAgentRequestAndMapsAgentToSession()
    {
        var frame = RemoteEventJson.Parse(JsonDocument
                                         .Parse("""
                                                {
                                                  "type": "waterfall",
                                                  "event": "approval/request",
                                                  "eventId": "event-7",
                                                  "agentId": "session-42",
                                                  "request": {
                                                    "toolName": "fs.write",
                                                    "callId": "call-9",
                                                    "reason": "需要修改项目文件"
                                                  }
                                                }
                                                """)
                                         .RootElement.Clone());

        var waterfall = Assert.IsType<RemoteEventFrame.Waterfall>(frame);
        Assert.Equal("event-7", waterfall.EventId);
        Assert.Equal("session-42", waterfall.AgentId);

        var request = RemoteEventJson.TryGetApprovalRequest(waterfall);
        Assert.NotNull(request);
        Assert.Equal("fs.write", request!.ToolName);
        Assert.Equal("call-9", request.CallId);
        Assert.Equal("需要修改项目文件", request.Reason);
    }

    [Fact]
    public void WaterfallResultOutcomesUseStrictWireShapes()
    {
        var result = JsonSerializer.SerializeToElement(
                                                       new EventsResultRequest("client-1", "event-1",
                                                                               new EventsOutcomeWire("result",
                                                                                        "allowed-once")),
                                                       HarnessJsonContext.Default.EventsResultRequest);
        Assert.Equal("result", result.GetProperty("outcome").GetProperty("kind").GetString());
        Assert.Equal("allowed-once", result.GetProperty("outcome").GetProperty("value").GetString());
        Assert.False(result.GetProperty("outcome").TryGetProperty("error", out _));

        var rejected = JsonSerializer.SerializeToElement(
                                                         new EventsResultRequest("client-1", "event-2",
                                                                  new EventsOutcomeWire("rejected",
                                                                           Error : new EventsOutcomeErrorWire("Error",
                                                                                    "不支持的交互"))),
                                                         HarnessJsonContext.Default.EventsResultRequest);
        var error = rejected.GetProperty("outcome").GetProperty("error");
        Assert.Equal("rejected", rejected.GetProperty("outcome").GetProperty("kind").GetString());
        Assert.Equal("Error", error.GetProperty("name").GetString());
        Assert.Equal("不支持的交互", error.GetProperty("message").GetString());

        var next = JsonSerializer.SerializeToElement(
                                                     new EventsResultRequest("client-1", "event-3",
                                                                             new EventsOutcomeWire("next")),
                                                     HarnessJsonContext.Default.EventsResultRequest);
        Assert.Equal("next", next.GetProperty("outcome").GetProperty("kind").GetString());
        Assert.False(next.GetProperty("outcome").TryGetProperty("value", out _));
        Assert.False(next.GetProperty("outcome").TryGetProperty("error", out _));
    }

    [Fact]
    public void UnknownWaterfallRequestStillParsesForRejection()
    {
        var frame = RemoteEventJson.Parse(JsonDocument
                                         .Parse("""
                                                {
                                                  "type": "waterfall",
                                                  "event": "interaction/question",
                                                  "eventId": "event-8",
                                                  "agentId": "session-42",
                                                  "request": ["question"]
                                                }
                                                """)
                                         .RootElement.Clone());

        var waterfall = Assert.IsType<RemoteEventFrame.Waterfall>(frame);
        Assert.Equal("interaction/question", waterfall.Event);
        Assert.Null(RemoteEventJson.TryGetApprovalRequest(waterfall));
    }

    [Fact]
    public void SessionSummaryWireMapsToApplicationModel()
    {
        var wire = new SessionSummaryWire("session-1", 1700000000000, true, false,
                                          Projections :
                                          new SessionProjectionHintsWire(4, new Dictionary<string, JsonElement>
                                          {
                                              ["title"] = JsonDocument.Parse("\"会话标题\"").RootElement.Clone()
                                          }));

        var summary = HarnessSessionService.ToSummary(wire);

        Assert.Equal("session-1", summary.Id);
        Assert.Equal("会话标题", summary.Title);
        Assert.True(summary.Running);
        Assert.False(summary.Blank);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000), summary.UpdatedAt);
    }

    [Fact]
    public void SessionSummaryWireMapsBlankOnlyFromWireValue()
    {
        // blank 只信任响应值：格式迁移后 v3 旧会话的 sessionListMetadata 被投影缓存
        // 拒认，冷行统一回退 blank:false 且无标题——该形态与空白草稿不可区分，不得
        // 以投影缺失标记空白（会误藏有轮次但标题未生成的有效旧会话）。
        var validLegacy = new SessionSummaryWire("session-1", 1700000000000, false, false);
        var legacySummary = HarnessSessionService.ToSummary(validLegacy);
        Assert.False(legacySummary.Blank);
        Assert.Null(legacySummary.Title);

        var titledLegacy = new SessionSummaryWire("session-2", 1700000000000, false, false,
                                                 Projections :
                                                 new SessionProjectionHintsWire(4,
                                                 new Dictionary<string, JsonElement>
                                                 {
                                                     ["title"] = JsonDocument.Parse("\"迁移前旧会话\"").RootElement.Clone()
                                                 }));
        var titledSummary = HarnessSessionService.ToSummary(titledLegacy);
        Assert.False(titledSummary.Blank);
        Assert.Equal("迁移前旧会话", titledSummary.Title);

        var withMetadata = new SessionSummaryWire("session-3", 1700000000000, false, false,
                                                 Projections :
                                                 new SessionProjectionHintsWire(4,
                                                 new Dictionary<string, JsonElement>
                                                 {
                                                     ["sessionListMetadata"] = JsonDocument
                                                         .Parse("""{"blank":false,"lastPromptAt":null}""").RootElement.Clone()
                                                 }));
        Assert.False(HarnessSessionService.ToSummary(withMetadata).Blank);

        // 真正的空白草稿（元数据背书的 blank=true）保持既有过滤行为。
        var blankDraft = new SessionSummaryWire("session-4", 1700000000000, false, true,
                                               Projections :
                                               new SessionProjectionHintsWire(4,
                                               new Dictionary<string, JsonElement>
                                               {
                                                   ["sessionListMetadata"] = JsonDocument
                                                       .Parse("""{"blank":true,"lastPromptAt":null}""").RootElement.Clone()
                                               }));
        Assert.True(HarnessSessionService.ToSummary(blankDraft).Blank);
    }

    [Fact]
    public void WorkspaceFollowBaselineFrameParsesItems()
    {
        var frame = WorkspaceFrameJson.Parse(JsonDocument.Parse("""
                                                                {
                                                                  "type": "baseline",
                                                                  "value": {
                                                                    "items": [
                                                                      {
                                                                        "workspaceId": "ws-1",
                                                                        "path": "C:/Code/Main",
                                                                        "title": "主工作区",
                                                                        "sessionIds": ["session-a", "session-b"],
                                                                        "createdAt": "2026-09-19T10:00:00.000Z",
                                                                        "updatedAt": "2026-09-20T10:00:00.000Z"
                                                                      }
                                                                    ],
                                                                    "archivedSessionIds": []
                                                                  }
                                                                }
                                                                """).RootElement);

        var baseline  = Assert.IsType<WorkspaceFollowFrame.Baseline>(frame);
        var workspace = Assert.Single(baseline.Items);
        Assert.Equal("ws-1", workspace.WorkspaceId);
        Assert.Equal("主工作区", workspace.Title);
        Assert.Equal("C:/Code/Main", workspace.Path);
        Assert.Equal(["session-a", "session-b"], workspace.SessionIds);
        Assert.Equal(DateTimeOffset.Parse("2026-09-20T10:00:00.000Z"), workspace.UpdatedAt);
    }

    [Fact]
    public void WorkspaceFollowIncrementFramesParse()
    {
        var upsert = WorkspaceFrameJson.Parse(JsonDocument.Parse("""
                                                                 {"type":"upsert","workspace":{"workspaceId":"ws-2","path":"C:/Docs","title":"文档",
                                                                  "sessionIds":[],"createdAt":"2026-09-19T10:00:00.000Z","updatedAt":"2026-09-21T08:00:00.000Z"}}
                                                                 """).RootElement);
        var upsertFrame = Assert.IsType<WorkspaceFollowFrame.Upsert>(upsert);
        Assert.Equal("ws-2", upsertFrame.Workspace.WorkspaceId);
        Assert.Empty(upsertFrame.Workspace.SessionIds);

        var removed =
            WorkspaceFrameJson.Parse(JsonDocument.Parse("""{"type":"remove","workspaceId":"ws-2"}""").RootElement);
        var removedFrame = Assert.IsType<WorkspaceFollowFrame.Removed>(removed);
        Assert.Equal("ws-2", removedFrame.WorkspaceId);

        var order = WorkspaceFrameJson.Parse(JsonDocument.Parse("""{"type":"order","workspaceIds":["ws-2","ws-1"]}""")
                                                         .RootElement);
        var orderFrame = Assert.IsType<WorkspaceFollowFrame.Reordered>(order);
        Assert.Equal(["ws-2", "ws-1"], orderFrame.WorkspaceIds);

        // 归档帧暂无消费方：解析为 null 由泵跳过。
        Assert.Null(WorkspaceFrameJson.Parse(JsonDocument
                                            .Parse("""{"type":"archived","archivedSessionIds":["session-a"]}""")
                                            .RootElement));
    }

    [Fact]
    public void WorkspaceProjectionAppliesBaselineUpsertRemoveAndOrder()
    {
        var baseline = new WorkspaceFollowFrame.Baseline([
            new WorkspaceViewWire("ws-1", "C:/Main", "主工作区", ["session-a"],
                                  DateTimeOffset.Parse("2026-09-20T10:00:00Z")),
            new WorkspaceViewWire("ws-2", "C:/Docs", "文档", [], DateTimeOffset.Parse("2026-09-19T10:00:00Z"))
        ]);
        var items = HarnessWorkspaceService.ApplyFrame([], baseline);
        Assert.Equal(["ws-1", "ws-2"], items.Select(item => item.Id));

        // 已存在：原位替换；记账变化体现为新实例。
        var replaced =
            HarnessWorkspaceService.ApplyFrame(items,
                                               new WorkspaceFollowFrame.Upsert(new WorkspaceViewWire("ws-2", "C:/Docs",
                                                                                        "文档", ["session-b"],
                                                                                        DateTimeOffset
                                                                                           .Parse("2026-09-21T10:00:00Z"))));
        Assert.Equal(["ws-1", "ws-2"], replaced.Select(item => item.Id));
        Assert.Equal(["session-b"], replaced[1].SessionIds);

        // 旧投影不覆盖新（乱序到达）。
        var stale = HarnessWorkspaceService.ApplyFrame(replaced,
                                                       new WorkspaceFollowFrame.Upsert(new WorkspaceViewWire("ws-2",
                                                                         "C:/Docs", "文档", [],
                                                                         DateTimeOffset
                                                                            .Parse("2026-09-18T10:00:00Z"))));
        Assert.Equal(["session-b"], stale[1].SessionIds);

        // 新工作区插到头部（对齐参考客户端 upsert 语义）。
        var added =
            HarnessWorkspaceService.ApplyFrame(stale,
                                               new WorkspaceFollowFrame.Upsert(new WorkspaceViewWire("ws-3", "C:/New",
                                                                                        "新工作区", [],
                                                                                        DateTimeOffset
                                                                                           .Parse("2026-09-21T11:00:00Z"))));
        Assert.Equal(["ws-3", "ws-1", "ws-2"], added.Select(item => item.Id));

        // order 按给出的顺序重排，未知 id 排尾。
        var ordered =
            HarnessWorkspaceService.ApplyFrame(added, new WorkspaceFollowFrame.Reordered(["ws-2", "ws-9", "ws-3"]));
        Assert.Equal(["ws-2", "ws-3", "ws-1"], ordered.Select(item => item.Id));

        var removed = HarnessWorkspaceService.ApplyFrame(ordered, new WorkspaceFollowFrame.Removed("ws-3"));
        Assert.Equal(["ws-2", "ws-1"], removed.Select(item => item.Id));
    }

    [Fact]
    public void ModelCatalogMapsGroupsFailuresAndDefaultSelection()
    {
        var value = JsonSerializer.Deserialize("""
                                               {
                                                 "default": {"provider": "glm", "model": "glm-5.3"},
                                                 "routableProviders": ["glm", "broken"],
                                                 "groups": [
                                                   {"id": "glm", "name": "Zhipu GLM", "models": [
                                                     {"id": "glm-5.3", "name": "GLM 5.3", "description": "旗舰"},
                                                     {"id": "glm-5.3-flash", "name": "GLM 5.3 Flash"}
                                                   ]},
                                                   {"id": "empty", "name": "Empty", "models": []}
                                                 ],
                                                 "failures": [{"id": "broken", "name": "Broken", "message": "credentials unavailable"}]
                                               }
                                               """, HarnessJsonContext.Default.SessionModelCatalogValue);

        Assert.NotNull(value);
        var catalog = HarnessSessionService.ToCatalog(value!);

        Assert.Equal(new ModelSelection("glm", "glm-5.3"), catalog.Default);
        // 空模型组剔除；组内模型顺序保持目录顺序。
        var group = Assert.Single(catalog.Groups);
        Assert.Equal(("glm", "Zhipu GLM"), (group.Id, group.Name));
        Assert.Equal(["glm-5.3", "glm-5.3-flash"], group.Models.Select(model => model.Id));
        Assert.Equal("GLM 5.3 Flash", group.Models[1].Name);
        var failure = Assert.Single(catalog.Failures);
        Assert.Equal(("broken", "credentials unavailable"), (failure.Id, failure.Message));
    }

    [Fact]
    public void FollowSnapshotParsesModelSelectionProjection()
    {
        var frame = FollowFrameJson.Parse(JsonDocument.Parse("""
                                                             {
                                                               "type": "snapshot",
                                                               "cursor": 4,
                                                               "records": [],
                                                               "projections": {"asOfSeq": 4, "values": {
                                                                 "title": "示例标题",
                                                                 "modelSelection": {
                                                                   "lastUsed": {"provider": "glm", "model": "glm-5.3"},
                                                                   "next": {"provider": "glm", "model": "glm-5.3-flash", "reasoningEffort": "high"}
                                                                 }
                                                               }}
                                                             }
                                                             """).RootElement.Clone());

        var snapshot = Assert.IsType<FollowFrame.Snapshot>(frame);
        // next 优先于 lastUsed：它是下一次请求将使用的选型。
        Assert.Equal(new ModelSelection("glm", "glm-5.3-flash", "high"), snapshot.CurrentModel);
    }

    [Fact]
    public void ModelSelectionEventParsesAndFallsBackToLastUsedProjection()
    {
        var wireEvent = WireEventJson.ParseEvent(JsonDocument.Parse("""
                                                                    {"type": "model/selection", "seq": 6, "time": 1700000006000,
                                                                     "data": {"provider": "glm", "model": "glm-5.3"}}
                                                                    """).RootElement.Clone());
        Assert.NotNull(wireEvent);
        var selection = WireEventJson.TryGetModelSelection(wireEvent!);
        Assert.NotNull(selection);
        Assert.Equal(("glm", "glm-5.3"), (selection!.Provider, selection.Model));
        Assert.Null(selection.ReasoningEffort);

        // next 为空时回退 lastUsed；两者皆空（未选过型）为 null。
        var fallback = FollowFrameJson.Parse(JsonDocument.Parse("""
                                                                {
                                                                  "type": "snapshot",
                                                                  "cursor": 4,
                                                                  "records": [],
                                                                  "projections": {"asOfSeq": 4, "values": {
                                                                    "modelSelection": {"lastUsed": {"provider": "glm", "model": "glm-5.3"}, "next": null}
                                                                  }}
                                                                }
                                                                """).RootElement.Clone());
        var fallbackSnapshot = Assert.IsType<FollowFrame.Snapshot>(fallback);
        Assert.Equal(new ModelSelection("glm", "glm-5.3"), fallbackSnapshot.CurrentModel);

        var unselected = FollowFrameJson.Parse(JsonDocument.Parse("""
                                                                  {
                                                                    "type": "snapshot",
                                                                    "cursor": 0,
                                                                    "records": [],
                                                                    "projections": {"asOfSeq": 0, "values": {
                                                                      "modelSelection": {"lastUsed": null, "next": null}
                                                                    }}
                                                                  }
                                                                  """).RootElement.Clone());
        var unselectedSnapshot = Assert.IsType<FollowFrame.Snapshot>(unselected);
        Assert.Null(unselectedSnapshot.CurrentModel);
    }

    [Fact]
    public void FollowSnapshotParsesUsageAndStatsProjections()
    {
        var frame = FollowFrameJson.Parse(JsonDocument.Parse("""
                                                             {
                                                               "type": "snapshot",
                                                               "cursor": 7,
                                                               "records": [],
                                                               "projections": {"asOfSeq": 7, "values": {
                                                                 "tokenUsage": {
                                                                   "uncachedInputTokens": 96,
                                                                   "outputTokens": 240,
                                                                   "cacheReadTokens": 512,
                                                                   "cacheWriteTokens": 128
                                                                 },
                                                                 "sessionStats": {
                                                                   "turns": 2, "steps": 3, "llmMs": 4500, "toolMs": 800,
                                                                   "ttftMs": 900, "ttftSteps": 3, "decodeMs": 10800, "decodeTokens": 240
                                                                 }
                                                               }}
                                                             }
                                                             """).RootElement.Clone());

        var snapshot = Assert.IsType<FollowFrame.Snapshot>(frame);
        Assert.Equal(7, snapshot.ProjectionAsOfSeq);
        Assert.Equal(new SessionUsage(96, 240, 512, 128), snapshot.Usage);
        Assert.NotNull(snapshot.Stats);
        Assert.Equal((2L, 3L, 240L), (snapshot.Stats!.Turns, snapshot.Stats.Steps, snapshot.Stats.DecodeTokens));
        Assert.Equal(10800, snapshot.Stats.DecodeMs);

        // 投影缺失（后端未装 token-meter）时统计为 null、水位 0，不视为错误。
        var bare = FollowFrameJson.Parse(JsonDocument.Parse("""
                                                            {"type": "snapshot", "cursor": 1, "records": []}
                                                            """).RootElement.Clone());
        var bareSnapshot = Assert.IsType<FollowFrame.Snapshot>(bare);
        Assert.Null(bareSnapshot.Usage);
        Assert.Null(bareSnapshot.Stats);
        Assert.Equal(0, bareSnapshot.ProjectionAsOfSeq);
    }

    [Fact]
    public void SessionControlFramesParseBaselineAndProjectionUpdates()
    {
        var baseline = SessionControlFrameJson.Parse(JsonDocument.Parse("""
                                                                        {
                                                                          "type": "baseline",
                                                                          "value": {
                                                                            "jobs": {"session-1": []},
                                                                            "projections": {
                                                                              "session-1": {"asOfSeq": 12, "values": {
                                                                                "tokenUsage": {"uncachedInputTokens": 10, "outputTokens": 20, "cacheReadTokens": 30, "cacheWriteTokens": 0}
                                                                              }}
                                                                            }
                                                                          }
                                                                        }
                                                                        """).RootElement.Clone());
        var baselineFrame = Assert.IsType<SessionControlFrame.Baseline>(baseline);
        Assert.True(baselineFrame.Projections.TryGetValue("session-1", out var block));
        Assert.Equal(12, block!.AsOfSeq);
        Assert.Equal(new SessionUsage(10, 20, 30, 0),
                     ProjectionValuesJson.ParseUsage(block.Values.GetProperty("tokenUsage")));

        // projection 帧：tokenUsage/sessionStats 键解释为模型，其余键载荷为 null（忽略）。
        var usageUpdate = SessionControlFrameJson.Parse(JsonDocument.Parse("""
                                                                           {"type": "projection", "sessionId": "session-1", "key": "tokenUsage",
                                                                            "seq": 13, "value": {"uncachedInputTokens": 11, "outputTokens": 21, "cacheReadTokens": 31, "cacheWriteTokens": 1}}
                                                                           """).RootElement.Clone());
        var usageFrame = Assert.IsType<SessionControlFrame.ProjectionUpdate>(usageUpdate);
        Assert.Equal(("session-1", "tokenUsage", 13L),
                     (usageFrame.SessionId, usageFrame.Key, usageFrame.Seq));
        Assert.Equal(new SessionUsage(11, 21, 31, 1), usageFrame.Usage);
        Assert.Null(usageFrame.Stats);

        var statsUpdate = SessionControlFrameJson.Parse(JsonDocument.Parse("""
                                                                           {"type": "projection", "sessionId": "session-2", "key": "sessionStats",
                                                                            "seq": 5, "value": {"turns": 1, "steps": 1, "llmMs": 100, "toolMs": 0, "ttftMs": 50, "ttftSteps": 1, "decodeMs": 400, "decodeTokens": 10}}
                                                                           """).RootElement.Clone());
        var statsFrame = Assert.IsType<SessionControlFrame.ProjectionUpdate>(statsUpdate);
        Assert.Null(statsFrame.Usage);
        Assert.Equal((1L, 1L, 10L), (statsFrame.Stats!.Turns, statsFrame.Stats.Steps, statsFrame.Stats.DecodeTokens));

        // jobs 帧与未知投影键：无消费方，解析为 null / 无载荷。
        Assert.Null(SessionControlFrameJson.Parse(JsonDocument
                                                 .Parse("""{"type": "jobs", "sessionId": "session-1", "jobs": []}""")
                                                 .RootElement.Clone()));
        var otherKey = SessionControlFrameJson.Parse(JsonDocument
                                                    .Parse("""{"type": "projection", "sessionId": "s", "key": "title", "seq": 2, "value": "标题"}""")
                                                    .RootElement.Clone());
        var otherFrame = Assert.IsType<SessionControlFrame.ProjectionUpdate>(otherKey);
        Assert.Null(otherFrame.Usage);
        Assert.Null(otherFrame.Stats);
    }
}
