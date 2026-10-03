using DshDesktop.Core.Models;
using DshDesktop.Harness.Json;
using DshDesktop.Harness.Models.Events;
using DshDesktop.Harness.Models.Requests;
using DshDesktop.Harness.Models.Responses;
using DshDesktop.Harness.Models.Rpc;
using DshDesktop.Harness.Services.Connection;
using DshDesktop.Harness.Services.Permissions;
using DshDesktop.Harness.Services.Sessions;
using DshDesktop.Harness.Services.Workspaces;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using System.Text.Json;

namespace DshDesktop.Tests;

/// <summary>协议信封与线上帧解析的行为验证（真实样本形态）。</summary>
public sealed class HarnessProtocolJsonTests
{
    [Test]
    public void RequestEnvelopeCarriesCamelCaseWireShape()
    {
        var body = RpcEnvelope.BuildRequest("rpc-1", "session/prompt",
                                            new SessionPromptRequest("request-1", "session-1", "queue",
                                                                     [new PromptTextPart("你好")], "Asia/Shanghai"),
                                            HarnessJsonContext.Default.SessionPromptRequest);

        using var document = JsonDocument.Parse(body);
        var       root     = document.RootElement;
        ClassicAssert.AreEqual("client-request", root.GetProperty("type").GetString());
        ClassicAssert.AreEqual("rpc-1", root.GetProperty("rpcId").GetString());
        ClassicAssert.AreEqual("session/prompt", root.GetProperty("method").GetString());
        var request = root.GetProperty("payload")
                          .GetProperty("args")
                          .GetProperty("request");
        ClassicAssert.AreEqual("request-1", request.GetProperty("requestId").GetString());
        ClassicAssert.AreEqual("queue", request.GetProperty("mode").GetString());
        ClassicAssert.AreEqual("Asia/Shanghai", request.GetProperty("clientTimeZone").GetString());
        var part = request.GetProperty("content")[0];
        ClassicAssert.AreEqual("text", part.GetProperty("type").GetString());
        ClassicAssert.AreEqual("你好", part.GetProperty("text").GetString());
    }

    [Test]
    public void DiscoverModelsRequestSpreadsFlatArgsForGatewayDescriptor()
    {
        // llm/discoverModels 是多参方法（settingsNs + request 两形参）：args 必须扁平展开，
        // 包一层 request 会被网关以 arguments-invalid（missing "settingsNs"）拒绝（真实后端已验证）。
        var body = RpcEnvelope.BuildArgsRequest("rpc-2", "llm/discoverModels",
                                                new LlmDiscoverModelsRequest(
                                                                             "llm-pi-ai",
                                                                             new LlmDiscoveryProbeRequest("glm",
                                                                                      "https://relay.example/v1",
                                                                                      "openai-completions", null)),
                                                HarnessJsonContext.Default.LlmDiscoverModelsRequest);

        using var document = JsonDocument.Parse(body);
        var       args     = document.RootElement.GetProperty("payload").GetProperty("args");
        ClassicAssert.AreEqual("llm-pi-ai", args.GetProperty("settingsNs").GetString());
        var probe = args.GetProperty("request");
        ClassicAssert.AreEqual("glm", probe.GetProperty("provider").GetString());
        ClassicAssert.AreEqual("https://relay.example/v1", probe.GetProperty("baseURL").GetString());
        ClassicAssert.AreEqual("openai-completions", probe.GetProperty("api").GetString());
        ClassicAssert.IsFalse(probe.TryGetProperty("apiKey", out _));
    }

    [Test]
    public void SessionAddressSerializesAsKindSession()
    {
        var element =
            JsonSerializer.SerializeToElement(new SessionAddress("session-9"),
                                              HarnessJsonContext.Default.SessionAddress);
        ClassicAssert.AreEqual("session", element.GetProperty("kind").GetString());
        ClassicAssert.AreEqual("session-9", element.GetProperty("sessionId").GetString());
    }

    [Test]
    public void ResponseEnvelopeParsesSuccessAndFailure()
    {
        var success =
            RpcEnvelope.ParseResponse("""{"type":"server-response","rpcId":"rpc-1","result":{"ok":true,"value":{"accepted":true}}}""");
        ClassicAssert.AreEqual("rpc-1", success.RpcId);
        ClassicAssert.IsTrue(success.Ok);
        ClassicAssert.IsNotNull(success.Value);
        ClassicAssert.IsTrue(success.Value.Value.GetProperty("accepted").GetBoolean());

        var failure =
            RpcEnvelope.ParseResponse("""{"type":"server-response","rpcId":"rpc-2","result":{"ok":false,"error":{"code":"session/not-found","message":"会话不存在"}}}""");
        ClassicAssert.IsFalse(failure.Ok);
        ClassicAssert.AreEqual("session/not-found", failure.ErrorCode);
        ClassicAssert.AreEqual("会话不存在", failure.ErrorMessage);
    }

    [Test]
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

        Assert.That(frame, Is.TypeOf<FollowFrame.Snapshot>());
        var snapshot = (FollowFrame.Snapshot)frame;
        ClassicAssert.AreEqual(4, snapshot.Cursor);
        ClassicAssert.AreEqual("示例标题", snapshot.Title);
        ClassicAssert.IsFalse(snapshot.HasMore);
        ClassicAssert.AreEqual(2, snapshot.Records.Count);

        var userMessage = WireEventJson.TryGetMessage(snapshot.Records[0]);
        ClassicAssert.IsNotNull(userMessage);
        ClassicAssert.AreEqual("问题", WireEventJson.ExtractText(userMessage));

        var assistantMessage = WireEventJson.TryGetMessage(snapshot.Records[1]);
        ClassicAssert.IsNotNull(assistantMessage);
        ClassicAssert.AreEqual("回答", WireEventJson.ExtractText(assistantMessage));
        ClassicAssert.AreEqual("先想一想", WireEventJson.ExtractReasoning(assistantMessage));
        ClassicAssert.IsTrue(WireEventJson.IsInterrupted(snapshot.Records[1]));
    }

    [Test]
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
        ClassicAssert.AreEqual(2, messages.Count);
        ClassicAssert.AreEqual("你好", messages[0].Content);
        ClassicAssert.AreEqual(MessageRole.User, messages[0].Role);
        ClassicAssert.AreEqual("回复", messages[1].Content);
        ClassicAssert.AreEqual(MessageRole.Assistant, messages[1].Role);
    }

    [Test]
    public void SessionPageRequestSerializesBackwardCursorWireShape()
    {
        var element =
            JsonSerializer.SerializeToElement(new SessionPageRequest(new SessionAddress("session-1"), 42, 9, 50),
                                              HarnessJsonContext.Default.SessionPageRequest);

        ClassicAssert.AreEqual("session", element.GetProperty("address").GetProperty("kind").GetString());
        ClassicAssert.AreEqual("session-1", element.GetProperty("address").GetProperty("sessionId").GetString());
        ClassicAssert.AreEqual(42, element.GetProperty("throughSeq").GetInt64());
        ClassicAssert.AreEqual(9, element.GetProperty("beforeSeq").GetInt64());
        ClassicAssert.AreEqual(50, element.GetProperty("maxMessages").GetInt32());
    }

    [Test]
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

        ClassicAssert.IsNotNull(page);
        ClassicAssert.IsTrue(page.HasMore);

        var records = FollowFrameJson.ParseHistoryRecords(page.Records);
        ClassicAssert.AreEqual(3, records.Count);

        var entries = HarnessSessionService.MapEntries(records);
        ClassicAssert.AreEqual(2, entries.Count);

        // tool/call 与同 callId 的 tool/result 折叠为同一条目：保留发起位置与参数，补上结果。
        Assert.That(entries[0], Is.TypeOf<ToolActivity>());
        var tool = (ToolActivity)entries[0];
        ClassicAssert.AreEqual(7, tool.Seq);
        ClassicAssert.AreEqual("call-1", tool.CallId);
        ClassicAssert.AreEqual("fs.read", tool.Name);
        ClassicAssert.AreEqual("{\"path\":\"a.md\"}", tool.ArgumentsJson);
        ClassicAssert.AreEqual(ToolActivityStatus.Succeeded, tool.Status);
        ClassicAssert.AreEqual("文件内容", tool.ResultText);
        ClassicAssert.IsNull(tool.ErrorReason);
        ClassicAssert.IsNotNull(tool.CompletedAt);

        Assert.That(entries[1], Is.TypeOf<ConversationMessage>());
        var message = (ConversationMessage)entries[1];
        ClassicAssert.AreEqual("调用完成", message.Content);
    }

    [Test]
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

        Assert.That(entries.Single(), Is.TypeOf<ToolActivity>());
        var tool = (ToolActivity)entries.Single();
        ClassicAssert.AreEqual(ToolActivityStatus.Failed, tool.Status);
        ClassicAssert.AreEqual("exit 1", tool.ResultText);
        ClassicAssert.AreEqual("命令以非零状态退出", tool.ErrorReason);
    }

    [Test]
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

        Assert.That(entries.Single(), Is.TypeOf<ToolActivity>());
        var tool = (ToolActivity)entries.Single();
        ClassicAssert.AreEqual("call-4", tool.CallId);
        ClassicAssert.AreEqual(ToolActivityStatus.Failed, tool.Status);
        ClassicAssert.AreEqual("命令失败", tool.ResultText);
    }

    [Test]
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

        Assert.That(entries.Single(), Is.TypeOf<ToolActivity>());
        var tool = (ToolActivity)entries.Single();
        ClassicAssert.AreEqual("call-y", tool.CallId);
        ClassicAssert.AreEqual(ToolActivityStatus.Succeeded, tool.Status);
        ClassicAssert.AreEqual("第一段\n第二段", tool.ResultText);
    }

    [Test]
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

        Assert.That(entries.Single(), Is.TypeOf<ToolActivity>());
        var tool = (ToolActivity)entries.Single();
        ClassicAssert.AreEqual("call-x", tool.CallId);
        ClassicAssert.AreEqual(ToolActivityStatus.Succeeded, tool.Status);
        ClassicAssert.AreEqual("结果文本", tool.ResultText);
    }

    [Test]
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

        ClassicAssert.AreEqual(2, entries.Count);
        Assert.That(entries[0], Is.TypeOf<ConversationMessage>());
        var message = (ConversationMessage)entries[0];
        ClassicAssert.AreEqual(3, message.Turn);
        Assert.That(entries[1], Is.TypeOf<TurnBoundary>());
        var boundary = (TurnBoundary)entries[1];
        ClassicAssert.AreEqual(3, boundary.Turn);
        ClassicAssert.AreEqual(3, boundary.Seq);
    }

    [Test]
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

        ClassicAssert.AreEqual(2, messages.Count);
        ClassicAssert.IsTrue(messages[0].HasToolCalls);
        ClassicAssert.AreEqual("先读取文件", messages[0].Content);
        ClassicAssert.IsFalse(messages[1].HasToolCalls);
    }

    private static IReadOnlyList<SessionWireEvent> ParseRecords(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray()
                       .Select(element => WireEventJson.ParseEvent(element.Clone()))
                       .OfType<SessionWireEvent>()
                       .ToArray();
    }

    [Test]
    public void AssistantStreamChunkFrameParsesTextDelta()
    {
        var frame = AssistantStreamFrameJson.ParseFrame(JsonDocument.Parse("""
                                                                           {"type": "chunk", "attemptId": "attempt-1", "revision": 3, "index": 2, "time": 1700000003000,
                                                                            "chunk": {"type": "text-delta", "index": 0, "text": "增量文本"}}
                                                                           """).RootElement.Clone());

        Assert.That(frame, Is.TypeOf<AssistantStreamFrame.StreamChunkFrame>());
        var chunk = (AssistantStreamFrame.StreamChunkFrame)frame;
        ClassicAssert.AreEqual("attempt-1", chunk.AttemptId);
        ClassicAssert.AreEqual(3, chunk.Revision);
        Assert.That(chunk.Chunk, Is.TypeOf<StreamChunk.TextDelta>());
        var delta = (StreamChunk.TextDelta)chunk.Chunk;
        ClassicAssert.AreEqual("增量文本", delta.Text);
    }

    [Test]
    public void RemoteEventFramesParseReadyAndEmit()
    {
        var ready = RemoteEventJson.Parse(JsonDocument
                                         .Parse("""{"type": "ready", "clientId": "client-1", "host": {"home": "C:/dsh"}}""")
                                         .RootElement.Clone());
        Assert.That(ready, Is.TypeOf<RemoteEventFrame.Ready>());
        var readyFrame = (RemoteEventFrame.Ready)ready;
        ClassicAssert.AreEqual("client-1", readyFrame.ClientId);

        var emit = RemoteEventJson.Parse(JsonDocument
                                        .Parse("""{"type": "emit", "event": "api-session/status", "args": ["session-1", true]}""")
                                        .RootElement.Clone());
        Assert.That(emit, Is.TypeOf<RemoteEventFrame.Emit>());
        var emitFrame = (RemoteEventFrame.Emit)emit;
        ClassicAssert.AreEqual("api-session/status", emitFrame.Event);
        ClassicAssert.AreEqual(2, emitFrame.Args.Count);
        ClassicAssert.AreEqual("session-1", emitFrame.Args[0].GetString());
        ClassicAssert.IsTrue(emitFrame.Args[1].GetBoolean());
    }

    [Test]
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

        Assert.That(frame, Is.TypeOf<RemoteEventFrame.Waterfall>());
        var waterfall = (RemoteEventFrame.Waterfall)frame;
        ClassicAssert.AreEqual("event-7", waterfall.EventId);
        ClassicAssert.AreEqual("session-42", waterfall.AgentId);

        var request = RemoteEventJson.TryGetApprovalRequest(waterfall);
        ClassicAssert.IsNotNull(request);
        ClassicAssert.AreEqual("fs.write", request!.ToolName);
        ClassicAssert.AreEqual("call-9", request.CallId);
        ClassicAssert.AreEqual("需要修改项目文件", request.Reason);
    }

    [Test]
    public void WaterfallResultOutcomesUseStrictWireShapes()
    {
        var result =
            JsonSerializer.SerializeToElement(new EventsResultRequest("client-1", "event-1",
                                                                      new EventsOutcomeWire("result", "allowed-once")),
                                              HarnessJsonContext.Default.EventsResultRequest);
        ClassicAssert.AreEqual("result", result.GetProperty("outcome").GetProperty("kind").GetString());
        ClassicAssert.AreEqual("allowed-once", result.GetProperty("outcome").GetProperty("value").GetString());
        ClassicAssert.IsFalse(result.GetProperty("outcome").TryGetProperty("error", out _));

        var rejected =
            JsonSerializer.SerializeToElement(new EventsResultRequest("client-1", "event-2",
                                                                      new EventsOutcomeWire("rejected",
                                                                               Error : new
                                                                                   EventsOutcomeErrorWire("Error",
                                                                                            "不支持的交互"))),
                                              HarnessJsonContext.Default.EventsResultRequest);
        var error = rejected.GetProperty("outcome").GetProperty("error");
        ClassicAssert.AreEqual("rejected", rejected.GetProperty("outcome").GetProperty("kind").GetString());
        ClassicAssert.AreEqual("Error", error.GetProperty("name").GetString());
        ClassicAssert.AreEqual("不支持的交互", error.GetProperty("message").GetString());

        var next = JsonSerializer.SerializeToElement(
                                                     new EventsResultRequest("client-1", "event-3",
                                                                             new EventsOutcomeWire("next")),
                                                     HarnessJsonContext.Default.EventsResultRequest);
        ClassicAssert.AreEqual("next", next.GetProperty("outcome").GetProperty("kind").GetString());
        ClassicAssert.IsFalse(next.GetProperty("outcome").TryGetProperty("value", out _));
        ClassicAssert.IsFalse(next.GetProperty("outcome").TryGetProperty("error", out _));
    }

    [Test]
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

        Assert.That(frame, Is.TypeOf<RemoteEventFrame.Waterfall>());
        var waterfall = (RemoteEventFrame.Waterfall)frame;
        ClassicAssert.AreEqual("interaction/question", waterfall.Event);
        ClassicAssert.IsNull(RemoteEventJson.TryGetApprovalRequest(waterfall));
    }

    [Test]
    public void SessionSummaryWireMapsToApplicationModel()
    {
        var wire = new SessionSummaryWire("session-1", 1700000000000, true, false,
                                          Projections :
                                          new SessionProjectionHintsWire("sequenced", 4,
                                                                         new Dictionary<string, JsonElement>
                                                                         {
                                                                             ["title"] = JsonDocument
                                                                                .Parse("\"会话标题\"")
                                                                                .RootElement.Clone()
                                                                         }));

        var summary = HarnessSessionService.ToSummary(wire);

        ClassicAssert.AreEqual("session-1", summary.Id);
        ClassicAssert.AreEqual("会话标题", summary.Title);
        ClassicAssert.IsTrue(summary.Running);
        // wire blank=false 但投影只有 title（无 sessionListMetadata）：元数据缺失 → 未知。
        ClassicAssert.AreEqual(SessionBlankState.Unknown, summary.BlankState);
        ClassicAssert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000), summary.UpdatedAt);
    }

    [Test]
    public void SessionSummaryWireMapsBlankStateFromMetadataPresence()
    {
        // 空白判定对齐参考实现 sessionListMetadata：blank=true 是权威空白；blank=false
        // 只有在行投影携带 sessionListMetadata 时才解释为已开始；元数据缺失（v3 旧会话
        // 被投影缓存拒认的冷行、cache miss）是保守回退，标记未知并保持可见。
        var metadataLess        = new SessionSummaryWire("session-1", 1700000000000, false, false);
        var metadataLessSummary = HarnessSessionService.ToSummary(metadataLess);
        ClassicAssert.AreEqual(SessionBlankState.Unknown, metadataLessSummary.BlankState);
        ClassicAssert.IsNull(metadataLessSummary.Title);

        var titledCold = new SessionSummaryWire("session-2", 1700000000000, false, false,
                                                Projections :
                                                new SessionProjectionHintsWire("cached", 4,
                                                                               new Dictionary<string, JsonElement>
                                                                               {
                                                                                   ["title"] = JsonDocument
                                                                                      .Parse("\"迁移前旧会话\"")
                                                                                      .RootElement.Clone()
                                                                               }));
        var titledSummary = HarnessSessionService.ToSummary(titledCold);
        ClassicAssert.AreEqual(SessionBlankState.Unknown, titledSummary.BlankState);
        ClassicAssert.AreEqual("迁移前旧会话", titledSummary.Title);

        var engaged = new SessionSummaryWire("session-3", 1700000000000, false, false,
                                             Projections :
                                             new SessionProjectionHintsWire("cached", 4,
                                                                            new Dictionary<string, JsonElement>
                                                                            {
                                                                                ["sessionListMetadata"] = JsonDocument
                                                                                   .Parse("""{"blank":false,"lastPromptAt":null}""")
                                                                                   .RootElement.Clone()
                                                                            }));
        ClassicAssert.AreEqual(SessionBlankState.Engaged, HarnessSessionService.ToSummary(engaged).BlankState);

        // 确认空白（元数据背书的 blank=true）；cwd 进入应用模型供复用候选匹配。
        var blankDraft = new SessionSummaryWire("session-4", 1700000000000, false, true, Cwd : "C:/Code/Sample",
                                                Projections :
                                                new SessionProjectionHintsWire("sequenced", 0,
                                                                               new Dictionary<string, JsonElement>
                                                                               {
                                                                                   ["sessionListMetadata"] =
                                                                                       JsonDocument
                                                                                          .Parse("""{"blank":true,"lastPromptAt":null}""")
                                                                                          .RootElement.Clone()
                                                                               }));
        var blankSummary = HarnessSessionService.ToSummary(blankDraft);
        ClassicAssert.AreEqual(SessionBlankState.ConfirmedBlank, blankSummary.BlankState);
        ClassicAssert.AreEqual("C:/Code/Sample", blankSummary.Cwd);
    }

    [Test]
    public void WorkspaceFollowBaselineFrameParsesItemsAndRegistrySets()
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
                                                                    "archivedSessionIds": ["session-c", "session-d"]
                                                                  }
                                                                }
                                                                """).RootElement);

        Assert.That(frame, Is.TypeOf<WorkspaceFollowFrame.Baseline>());
        var baseline = (WorkspaceFollowFrame.Baseline)frame;
        Assert.That(baseline.Items, Has.Count.EqualTo(1));
        var workspace = baseline.Items.Single();
        ClassicAssert.AreEqual("ws-1", workspace.WorkspaceId);
        ClassicAssert.AreEqual("主工作区", workspace.Title);
        ClassicAssert.AreEqual("C:/Code/Main", workspace.Path);
        ClassicAssert.AreEqual(new[] { "session-a", "session-b" }, workspace.SessionIds);
        ClassicAssert.AreEqual(DateTimeOffset.Parse("2026-09-20T10:00:00.000Z"), workspace.UpdatedAt);
        // 基线携带 registry 级归档全量集合（feed.ts baseline()）；缺它冷启动
        // 会把已归档会话当正常行展示，点归档被后端 gate 拒绝。置顶集合（pinned 帧）
        // 属上游协议但本端已改为自有置顶方案，不再消费。
        ClassicAssert.AreEqual(new[] { "session-c", "session-d" }, baseline.ArchivedSessionIds);
    }

    [Test]
    public void WorkspaceFollowIncrementFramesParse()
    {
        var upsert = WorkspaceFrameJson.Parse(JsonDocument.Parse("""
                                                                 {"type":"upsert","workspace":{"workspaceId":"ws-2","path":"C:/Docs","title":"文档",
                                                                  "sessionIds":[],"createdAt":"2026-09-19T10:00:00.000Z","updatedAt":"2026-09-21T08:00:00.000Z"}}
                                                                 """).RootElement);
        Assert.That(upsert, Is.TypeOf<WorkspaceFollowFrame.Upsert>());
        var upsertFrame = (WorkspaceFollowFrame.Upsert)upsert;
        ClassicAssert.AreEqual("ws-2", upsertFrame.Workspace.WorkspaceId);
        ClassicAssert.IsEmpty(upsertFrame.Workspace.SessionIds);

        var removed =
            WorkspaceFrameJson.Parse(JsonDocument.Parse("""{"type":"remove","workspaceId":"ws-2"}""").RootElement);
        Assert.That(removed, Is.TypeOf<WorkspaceFollowFrame.Removed>());
        var removedFrame = (WorkspaceFollowFrame.Removed)removed;
        ClassicAssert.AreEqual("ws-2", removedFrame.WorkspaceId);

        var order = WorkspaceFrameJson.Parse(JsonDocument.Parse("""{"type":"order","workspaceIds":["ws-2","ws-1"]}""")
                                                         .RootElement);
        Assert.That(order, Is.TypeOf<WorkspaceFollowFrame.Reordered>());
        var orderFrame = (WorkspaceFollowFrame.Reordered)order;
        ClassicAssert.AreEqual(new[] { "ws-2", "ws-1" }, orderFrame.WorkspaceIds);

        // 归档帧：解析为 registry 级集合帧，由工作区服务维护（复用候选排除归档会话）。
        var archived = WorkspaceFrameJson.Parse(JsonDocument
                                               .Parse("""{"type":"archived","archivedSessionIds":["session-a"]}""")
                                               .RootElement);
        Assert.That(archived, Is.TypeOf<WorkspaceFollowFrame.Archived>());
        var archivedFrame = (WorkspaceFollowFrame.Archived)archived;
        ClassicAssert.AreEqual(new[] { "session-a" }, archivedFrame.ArchivedSessionIds);
    }

    [Test]
    public void WorkspaceProjectionAppliesBaselineUpsertRemoveAndOrder()
    {
        var baseline = new WorkspaceFollowFrame.Baseline([
                                                             new WorkspaceViewWire("ws-1", "C:/Main", "主工作区",
                                                                      ["session-a"],
                                                                      DateTimeOffset.Parse("2026-09-20T10:00:00Z")),
                                                             new WorkspaceViewWire("ws-2", "C:/Docs", "文档", [],
                                                                      DateTimeOffset.Parse("2026-09-19T10:00:00Z"))
                                                         ],
                                                         ["session-archived"]);
        var items = HarnessWorkspaceService.ApplyFrame([], baseline);
        ClassicAssert.AreEqual(new[] { "ws-1", "ws-2" }, items.Select(item => item.Id));

        // 已存在：原位替换；记账变化体现为新实例。
        var replaced =
            HarnessWorkspaceService.ApplyFrame(items,
                                               new WorkspaceFollowFrame.Upsert(new WorkspaceViewWire("ws-2", "C:/Docs",
                                                                                        "文档", ["session-b"],
                                                                                        DateTimeOffset
                                                                                           .Parse("2026-09-21T10:00:00Z"))));
        ClassicAssert.AreEqual(new[] { "ws-1", "ws-2" }, replaced.Select(item => item.Id));
        ClassicAssert.AreEqual(new[] { "session-b" }, replaced[1].SessionIds);

        // 旧投影不覆盖新（乱序到达）。
        var stale = HarnessWorkspaceService.ApplyFrame(replaced,
                                                       new WorkspaceFollowFrame.Upsert(new WorkspaceViewWire("ws-2",
                                                                         "C:/Docs", "文档", [],
                                                                         DateTimeOffset
                                                                            .Parse("2026-09-18T10:00:00Z"))));
        ClassicAssert.AreEqual(new[] { "session-b" }, stale[1].SessionIds);

        // 新工作区插到头部（对齐参考客户端 upsert 语义）。
        var added =
            HarnessWorkspaceService.ApplyFrame(stale,
                                               new WorkspaceFollowFrame.Upsert(new WorkspaceViewWire("ws-3", "C:/New",
                                                                                        "新工作区", [],
                                                                                        DateTimeOffset
                                                                                           .Parse("2026-09-21T11:00:00Z"))));
        ClassicAssert.AreEqual(new[] { "ws-3", "ws-1", "ws-2" }, added.Select(item => item.Id));

        // order 按给出的顺序重排，未知 id 排尾。
        var ordered =
            HarnessWorkspaceService.ApplyFrame(added, new WorkspaceFollowFrame.Reordered(["ws-2", "ws-9", "ws-3"]));
        ClassicAssert.AreEqual(new[] { "ws-2", "ws-3", "ws-1" }, ordered.Select(item => item.Id));

        var removed = HarnessWorkspaceService.ApplyFrame(ordered, new WorkspaceFollowFrame.Removed("ws-3"));
        ClassicAssert.AreEqual(new[] { "ws-2", "ws-1" }, removed.Select(item => item.Id));
    }

    [Test]
    public void WorkspaceRenameDeleteRequestsAndResponsesCarryWireShapes()
    {
        var renameBody = JsonSerializer.Serialize(new WorkspaceRenameRequest("ws-1", "改名后"),
                                                  HarnessJsonContext.Default.WorkspaceRenameRequest);
        using (var document = JsonDocument.Parse(renameBody))
        {
            var root = document.RootElement;
            ClassicAssert.AreEqual("ws-1", root.GetProperty("workspaceId").GetString());
            ClassicAssert.AreEqual("改名后", root.GetProperty("title").GetString());
        }

        var deleteBody = JsonSerializer.Serialize(new WorkspaceDeleteRequest("ws-2"),
                                                  HarnessJsonContext.Default.WorkspaceDeleteRequest);
        using (var document = JsonDocument.Parse(deleteBody))
        {
            ClassicAssert.AreEqual("ws-2", document.RootElement.GetProperty("workspaceId").GetString());
        }

        var rename =
            JsonSerializer
               .Deserialize("""{"workspace":{"workspaceId":"ws-1","path":"C:/Main","title":"改名后","sessionIds":["session-a"],"updatedAt":"2026-09-20T10:00:00Z"}}""",
                            HarnessJsonContext.Default.WorkspaceRenameValue);
        Assert.That(rename, Is.TypeOf<WorkspaceRenameValue>());
        ClassicAssert.AreEqual("改名后", ((WorkspaceRenameValue)rename).Workspace.Title);

        var deleted =
            JsonSerializer.Deserialize("""{"deleted":true}""", HarnessJsonContext.Default.WorkspaceDeleteValue);
        Assert.That(deleted, Is.TypeOf<WorkspaceDeleteValue>());
        ClassicAssert.IsTrue(((WorkspaceDeleteValue)deleted).Deleted);
    }

    [Test]
    public void SessionForkRenameRequestsAndValuesCarryWireShapes()
    {
        var forkBody = JsonSerializer.Serialize(new SessionForkRequest("session-1"),
                                                HarnessJsonContext.Default.SessionForkRequest);
        using (var document = JsonDocument.Parse(forkBody))
        {
            ClassicAssert.AreEqual("session-1", document.RootElement.GetProperty("sessionId").GetString());
        }

        var renameBody = JsonSerializer.Serialize(new SessionRenameRequest("session-1", "新标题"),
                                                  HarnessJsonContext.Default.SessionRenameRequest);
        using (var document = JsonDocument.Parse(renameBody))
        {
            var root = document.RootElement;
            ClassicAssert.AreEqual("session-1", root.GetProperty("sessionId").GetString());
            ClassicAssert.AreEqual("新标题", root.GetProperty("title").GetString());
        }

        var forked = JsonSerializer.Deserialize("""{"sessionId":"session-9"}""",
                                                HarnessJsonContext.Default.SessionForkValue);
        Assert.That(forked, Is.TypeOf<SessionForkValue>());
        ClassicAssert.AreEqual("session-9", ((SessionForkValue)forked).SessionId);

        var renamed = JsonSerializer.Deserialize("""{"title":"接受后","seq":42}""",
                                                 HarnessJsonContext.Default.SessionRenameValue);
        Assert.That(renamed, Is.TypeOf<SessionRenameValue>());
        var renameValue = (SessionRenameValue)renamed;
        ClassicAssert.AreEqual("接受后", renameValue.Title);
        ClassicAssert.AreEqual(42, renameValue.Seq);
    }

    [Test]
    public void ModelCatalogMapsGroupsFailuresAndDefaultSelection()
    {
        var value = JsonSerializer.Deserialize("""
                                               {
                                                 "default": {"provider": "glm", "model": "glm-5.3"},
                                                 "routableProviders": ["glm", "broken"],
                                                 "groups": [
                                                   {"id": "glm", "name": "Zhipu GLM", "models": [
                                                     {"id": "glm-5.3", "name": "GLM 5.3", "description": "旗舰"},
                                                     {"id": "glm-5.3-flash", "name": "GLM 5.3 Flash", "reasoning": {
                                                       "efforts": [{"id": "low", "name": "Low"}, {"id": "high", "name": "High"}],
                                                       "defaultEffort": "low"
                                                     }}
                                                   ]},
                                                   {"id": "empty", "name": "Empty", "models": []}
                                                 ],
                                                 "failures": [{"id": "broken", "name": "Broken", "message": "credentials unavailable"}]
                                               }
                                               """, HarnessJsonContext.Default.SessionModelCatalogValue);

        ClassicAssert.IsNotNull(value);
        var catalog = HarnessSessionService.ToCatalog(value!);

        ClassicAssert.AreEqual(new ModelSelection("glm", "glm-5.3"), catalog.Default);
        // 空模型组剔除；组内模型顺序保持目录顺序。
        Assert.That(catalog.Groups, Has.Count.EqualTo(1));
        var group = catalog.Groups.Single();
        ClassicAssert.AreEqual(("glm", "Zhipu GLM"), (group.Id, group.Name));
        ClassicAssert.AreEqual(new[] { "glm-5.3", "glm-5.3-flash" }, group.Models.Select(model => model.Id));
        ClassicAssert.AreEqual("GLM 5.3 Flash", group.Models[1].Name);
        Assert.That(catalog.Failures, Has.Count.EqualTo(1));
        var failure = catalog.Failures.Single();
        ClassicAssert.AreEqual(("broken", "credentials unavailable"), (failure.Id, failure.Message));

        // 模型 reasoning 元数据：受支持档位与默认档位映射到应用模型，供界面按模型过滤。
        ClassicAssert.IsNull(group.Models[0].Reasoning);
        var reasoning = group.Models[1].Reasoning;
        ClassicAssert.IsNotNull(reasoning);
        ClassicAssert.AreEqual(new[] { "low", "high" }, reasoning!.Efforts.Select(effort => effort.Id));
        ClassicAssert.AreEqual("Low", reasoning.Efforts[0].Name);
        ClassicAssert.AreEqual("low", reasoning.DefaultEffort);
        // 跨模型携带档位的裁决：支持的保留，不支持回退默认档位。
        ClassicAssert.AreEqual("low", reasoning.Resolve("low"));
        ClassicAssert.AreEqual("low", reasoning.Resolve("off"));
        ClassicAssert.IsNull(reasoning.Resolve(null));
    }

    [Test]
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

        Assert.That(frame, Is.TypeOf<FollowFrame.Snapshot>());
        var snapshot = (FollowFrame.Snapshot)frame;
        // next 优先于 lastUsed：它是下一次请求将使用的选型。
        ClassicAssert.AreEqual(new ModelSelection("glm", "glm-5.3-flash", "high"), snapshot.CurrentModel);
    }

    [Test]
    public void ModelSelectionEventParsesAndFallsBackToLastUsedProjection()
    {
        var wireEvent = WireEventJson.ParseEvent(JsonDocument.Parse("""
                                                                    {"type": "model/selection", "seq": 6, "time": 1700000006000,
                                                                     "data": {"provider": "glm", "model": "glm-5.3"}}
                                                                    """).RootElement.Clone());
        ClassicAssert.IsNotNull(wireEvent);
        var selection = WireEventJson.TryGetModelSelection(wireEvent!);
        ClassicAssert.IsNotNull(selection);
        ClassicAssert.AreEqual(("glm", "glm-5.3"), (selection!.Provider, selection.Model));
        ClassicAssert.IsNull(selection.ReasoningEffort);

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
        Assert.That(fallback, Is.TypeOf<FollowFrame.Snapshot>());
        var fallbackSnapshot = (FollowFrame.Snapshot)fallback;
        ClassicAssert.AreEqual(new ModelSelection("glm", "glm-5.3"), fallbackSnapshot.CurrentModel);

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
        Assert.That(unselected, Is.TypeOf<FollowFrame.Snapshot>());
        var unselectedSnapshot = (FollowFrame.Snapshot)unselected;
        ClassicAssert.IsNull(unselectedSnapshot.CurrentModel);
    }

    [Test]
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

        Assert.That(frame, Is.TypeOf<FollowFrame.Snapshot>());
        var snapshot = (FollowFrame.Snapshot)frame;
        ClassicAssert.AreEqual(7, snapshot.ProjectionAsOfSeq);
        ClassicAssert.AreEqual(new SessionUsage(96, 240, 512, 128), snapshot.Usage);
        ClassicAssert.IsNotNull(snapshot.Stats);
        ClassicAssert.AreEqual((2L, 3L, 240L),
                               (snapshot.Stats!.Turns, snapshot.Stats.Steps, snapshot.Stats.DecodeTokens));
        ClassicAssert.AreEqual(10800, snapshot.Stats.DecodeMs);

        // 投影缺失（后端未装 token-meter）时统计为 null、水位 0，不视为错误。
        var bare = FollowFrameJson.Parse(JsonDocument.Parse("""
                                                            {"type": "snapshot", "cursor": 1, "records": []}
                                                            """).RootElement.Clone());
        Assert.That(bare, Is.TypeOf<FollowFrame.Snapshot>());
        var bareSnapshot = (FollowFrame.Snapshot)bare;
        ClassicAssert.IsNull(bareSnapshot.Usage);
        ClassicAssert.IsNull(bareSnapshot.Stats);
        ClassicAssert.AreEqual(0, bareSnapshot.ProjectionAsOfSeq);
    }

    [Test]
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
        Assert.That(baseline, Is.TypeOf<SessionControlFrame.Baseline>());
        var baselineFrame = (SessionControlFrame.Baseline)baseline;
        ClassicAssert.IsTrue(baselineFrame.Projections.TryGetValue("session-1", out var block));
        ClassicAssert.AreEqual(12, block!.AsOfSeq);
        ClassicAssert.AreEqual(new SessionUsage(10, 20, 30, 0),
                               ProjectionValuesJson.ParseUsage(block.Values.GetProperty("tokenUsage")));

        // projection 帧：tokenUsage/sessionStats 键解释为模型，其余键载荷为 null（忽略）。
        var usageUpdate = SessionControlFrameJson.Parse(JsonDocument.Parse("""
                                                                           {"type": "projection", "sessionId": "session-1", "key": "tokenUsage",
                                                                            "seq": 13, "value": {"uncachedInputTokens": 11, "outputTokens": 21, "cacheReadTokens": 31, "cacheWriteTokens": 1}}
                                                                           """).RootElement.Clone());
        Assert.That(usageUpdate, Is.TypeOf<SessionControlFrame.ProjectionUpdate>());
        var usageFrame = (SessionControlFrame.ProjectionUpdate)usageUpdate;
        ClassicAssert.AreEqual(("session-1", "tokenUsage", 13L),
                               (usageFrame.SessionId, usageFrame.Key, usageFrame.Seq));
        ClassicAssert.AreEqual(new SessionUsage(11, 21, 31, 1), usageFrame.Usage);
        ClassicAssert.IsNull(usageFrame.Stats);

        var statsUpdate = SessionControlFrameJson.Parse(JsonDocument.Parse("""
                                                                           {"type": "projection", "sessionId": "session-2", "key": "sessionStats",
                                                                            "seq": 5, "value": {"turns": 1, "steps": 1, "llmMs": 100, "toolMs": 0, "ttftMs": 50, "ttftSteps": 1, "decodeMs": 400, "decodeTokens": 10}}
                                                                           """).RootElement.Clone());
        Assert.That(statsUpdate, Is.TypeOf<SessionControlFrame.ProjectionUpdate>());
        var statsFrame = (SessionControlFrame.ProjectionUpdate)statsUpdate;
        ClassicAssert.IsNull(statsFrame.Usage);
        ClassicAssert.AreEqual((1L, 1L, 10L),
                               (statsFrame.Stats!.Turns, statsFrame.Stats.Steps, statsFrame.Stats.DecodeTokens));

        // jobs 帧与未知投影键：无消费方，解析为 null / 无载荷。
        ClassicAssert.IsNull(SessionControlFrameJson.Parse(JsonDocument
                                                          .Parse("""{"type": "jobs", "sessionId": "session-1", "jobs": []}""")
                                                          .RootElement.Clone()));
        var otherKey = SessionControlFrameJson.Parse(JsonDocument
                                                    .Parse("""{"type": "projection", "sessionId": "s", "key": "title", "seq": 2, "value": "标题"}""")
                                                    .RootElement.Clone());
        Assert.That(otherKey, Is.TypeOf<SessionControlFrame.ProjectionUpdate>());
        var otherFrame = (SessionControlFrame.ProjectionUpdate)otherKey;
        ClassicAssert.IsNull(otherFrame.Usage);
        ClassicAssert.IsNull(otherFrame.Stats);
    }

    [Test]
    public void PermissionCatalogWireParsesAndMapsToCoreCatalog()
    {
        var wire = JsonSerializer.Deserialize("""
                                              {"options":[{"value":"read-only","name":"read-only"},
                                                {"value":"workspace-write","name":"workspace-write","description":"工作区内修改"},
                                                {"value":"danger-full-access","name":"danger-full-access"},
                                                {"value":"auto","name":"auto"}],
                                               "defaultOptions":[{"value":"workspace-write","name":"workspace-write"}],
                                               "defaultPreset":"workspace-write"}
                                              """,
                                              HarnessJsonContext.Default.PermissionCatalogWire);

        Assert.That(wire, Is.TypeOf<PermissionCatalogWire>());
        var catalog = HarnessPermissionPresetService.ToCatalog((PermissionCatalogWire)wire);
        ClassicAssert.AreEqual("workspace-write", catalog.DefaultPreset);
        ClassicAssert.AreEqual(4, catalog.Options.Count);
        var customized = catalog.Options.Single(option => option.Value == "workspace-write");
        ClassicAssert.AreEqual("工作区内修改", customized.Description);
        ClassicAssert.IsNull(catalog.Options.Single(option => option.Value == "auto").Description);
    }

    [Test]
    public void CommandExecuteRequestBuildsFlatNamedArgsEnvelope()
    {
        var body = RpcEnvelope.BuildArgsRequest("rpc-1", "commands/execute",
                                                new CommandExecuteRequest("session-1", "/permission auto", []),
                                                HarnessJsonContext.Default.CommandExecuteRequest);

        using var document = JsonDocument.Parse(body);
        var       root     = document.RootElement;
        ClassicAssert.AreEqual("commands/execute", root.GetProperty("method").GetString());
        var args = root.GetProperty("payload").GetProperty("args");
        // 扁平命名参数表：与宿主 commands.execute 的形参一一对应，无 request 包装。
        ClassicAssert.AreEqual("session-1", args.GetProperty("agentId").GetString());
        ClassicAssert.AreEqual("/permission auto", args.GetProperty("line").GetString());
        ClassicAssert.AreEqual(JsonValueKind.Array, args.GetProperty("submittedAttachments").ValueKind);
        ClassicAssert.AreEqual(0, args.GetProperty("submittedAttachments").GetArrayLength());
        ClassicAssert.IsFalse(args.TryGetProperty("request", out _));
    }

    [Test]
    public void CommandsExecuteUndefinedResultMeansCommandMissing()
    {
        // 宿主无该命令时 result 无 value：信封层解析为 null（服务层翻译为 matched=false）。
        var response = RpcEnvelope.ParseResponse("""{"type":"server-response","rpcId":"rpc-1","result":{"ok":true}}""");
        ClassicAssert.IsTrue(response.Ok);
        ClassicAssert.IsNull(response.Value);
    }

    [Test]
    public void ControlFrameParsesPermissionsProjectionKey()
    {
        var update = SessionControlFrameJson.Parse(JsonDocument.Parse("""
                                                                      {"type": "projection", "sessionId": "session-1", "key": "permissions",
                                                                       "seq": 7, "value": {"currentValue": "workspace-write"}}
                                                                      """).RootElement.Clone());
        Assert.That(update, Is.TypeOf<SessionControlFrame.ProjectionUpdate>());
        var frame = (SessionControlFrame.ProjectionUpdate)update;
        ClassicAssert.AreEqual(("session-1", "permissions", 7L), (frame.SessionId, frame.Key, frame.Seq));
        ClassicAssert.AreEqual("workspace-write", frame.PermissionValue);
        ClassicAssert.IsNull(frame.Usage);
        ClassicAssert.IsNull(frame.Stats);

        // currentValue 形状不符（非字符串）时保持 null，不产生伪基线。
        var malformed = SessionControlFrameJson.Parse(JsonDocument.Parse("""
                                                                         {"type": "projection", "sessionId": "s", "key": "permissions",
                                                                          "seq": 8, "value": {"currentValue": 42}}
                                                                         """).RootElement.Clone());
        Assert.That(malformed, Is.TypeOf<SessionControlFrame.ProjectionUpdate>());
        ClassicAssert.IsNull(((SessionControlFrame.ProjectionUpdate)malformed).PermissionValue);
    }

    [Test]
    public void BaselineValuesCarryParsablePermissionsProjection()
    {
        var baseline = SessionControlFrameJson.Parse(JsonDocument.Parse("""
                                                                        {"type": "baseline",
                                                                         "value": {"projections": {"session-1": {"asOfSeq": 12, "values": {
                                                                           "permissions": {"currentValue": "read-only"}}}}}}
                                                                        """).RootElement.Clone());
        Assert.That(baseline, Is.TypeOf<SessionControlFrame.Baseline>());
        var frame = (SessionControlFrame.Baseline)baseline;
        ClassicAssert.IsTrue(frame.Projections.TryGetValue("session-1", out var block));
        ClassicAssert.AreEqual("read-only",
                               ProjectionValuesJson.ParsePermissions(block!.Values.GetProperty("permissions")));
    }

    [Test]
    public void FollowSnapshotCarriesPermissionsProjection()
    {
        var frame = FollowFrameJson.Parse(JsonDocument.Parse("""
                                                             {"type": "snapshot",
                                                              "header": {"version": 4, "id": "session-1", "createdAt": 1700000000000, "isSeeded": false},
                                                              "cursor": 4, "records": [], "hasMore": false,
                                                              "projections": {"asOfSeq": 9, "values": {"permissions": {"currentValue": "danger-full-access"}}}}
                                                             """).RootElement.Clone());
        Assert.That(frame, Is.TypeOf<FollowFrame.Snapshot>());
        var snapshot = (FollowFrame.Snapshot)frame;
        ClassicAssert.AreEqual(9, snapshot.ProjectionAsOfSeq);
        ClassicAssert.AreEqual("danger-full-access", snapshot.CurrentPermission);
    }

    [Test]
    public void FollowSnapshotReplaysWorkspaceChangesRecordsAfterSnapshotUpdate()
    {
        var frame = FollowFrameJson.Parse(JsonDocument.Parse("""
                                                             {"type": "snapshot",
                                                              "header": {"version": 4, "id": "session-1", "createdAt": 1700000000000, "isSeeded": false},
                                                              "cursor": 5,
                                                              "records": [
                                                                {"type": "event", "event": {"type": "workspace/changes", "seq": 3, "time": 1700000003000, "data": {"turn": 1}}},
                                                                {"type": "event", "event": {"type": "workspace/changes", "seq": 5, "time": 1700000005000, "data": {"turn": 2}}}],
                                                              "hasMore": false}
                                                             """).RootElement.Clone());
        Assert.That(frame, Is.TypeOf<FollowFrame.Snapshot>());
        var snapshotFrame = (FollowFrame.Snapshot)frame;

        var updates = HarnessSessionService.MapFollowFrame(snapshotFrame).ToList();

        // 快照窗口内的改动摘要按记录顺序重放，且位于整窗替换之后。
        ClassicAssert.AreEqual(3, updates.Count);
        ClassicAssert.IsInstanceOf<SessionUpdate.Snapshot>(updates[0]);
        Assert.That(updates[1], Is.TypeOf<SessionUpdate.WorkspaceChanged>());
        var first = (SessionUpdate.WorkspaceChanged)updates[1];
        Assert.That(updates[2], Is.TypeOf<SessionUpdate.WorkspaceChanged>());
        var second = (SessionUpdate.WorkspaceChanged)updates[2];
        ClassicAssert.AreEqual((1L, 3L), (first.Turn, first.Seq));
        ClassicAssert.AreEqual((2L, 5L), (second.Turn, second.Seq));
    }

    [Test]
    public void WorkspaceChangesEventFrameMapsToWorkspaceChangedUpdate()
    {
        var frame = FollowFrameJson.Parse(JsonDocument.Parse("""
                                                             {"type": "event",
                                                              "event": {"type": "workspace/changes", "seq": 41, "time": 1727840000000, "data": {"turn": 3}}}
                                                             """).RootElement.Clone());

        Assert.That(frame, Is.TypeOf<FollowFrame.EventFrame>());
        var updates = HarnessSessionService.MapFollowFrame((FollowFrame.EventFrame)frame).ToList();

        Assert.That(updates.Single(), Is.TypeOf<SessionUpdate.WorkspaceChanged>());
        var changed = (SessionUpdate.WorkspaceChanged)updates.Single();
        ClassicAssert.AreEqual((3L, 41L), (changed.Turn, changed.Seq));
    }

    [Test]
    public void EmitFrameParsesPermissionCatalogChangedEvent()
    {
        var frame = RemoteEventJson.Parse(JsonDocument
                                         .Parse("""{"type": "emit", "event": "permission-presets/catalog-changed", "args": []}""")
                                         .RootElement.Clone());
        Assert.That(frame, Is.TypeOf<RemoteEventFrame.Emit>());
        var emit = (RemoteEventFrame.Emit)frame;
        ClassicAssert.AreEqual(RemoteEventJson.PermissionCatalogChangedEvent, emit.Event);
        ClassicAssert.IsEmpty(emit.Args);
    }
}
