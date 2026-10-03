using DshDesktop.Core.Models;
using DshDesktop.Harness.Exceptions;
using DshDesktop.Harness.Json;
using DshDesktop.Harness.Models.Events;
using DshDesktop.Harness.Models.Requests;
using DshDesktop.Harness.Models.Responses;
using DshDesktop.Harness.Services.Connection;
using DshDesktop.Harness.Services.Sessions;
using NUnit.Framework;
using NUnit.Framework.Legacy;
using System.Text.Json;

namespace DshDesktop.Tests;

/// <summary>
///     历史未知空白会话后台核实（只读 session/projections）的针对性验证：
///     有效结论进入过滤与复用流程、竞争与重连不误隐藏、失败不形成请求风暴。
/// </summary>
public sealed class SessionBlankVerificationTests
{
    private const string BlankTrueMetadata  = """{"blank":true,"lastPromptAt":null}""";
    private const string BlankFalseMetadata = """{"blank":false,"lastPromptAt":123}""";
    private const string BadBlankMetadata   = """{"blank":"yes"}""";

    private static ListedSessionRow Row(string                   id,
                                        SessionBlankState        state        = SessionBlankState.Unknown,
                                        SessionListMetadataWire? listMetadata = null,
                                        string?                  cwd          = null)
    {
        return new ListedSessionRow(new SessionSummary(id, null, DateTimeOffset.FromUnixTimeMilliseconds(1_000), false,
                                                       state, cwd), listMetadata);
    }

    private static SessionProjectionsValue ProjectionsWithMetadata(string metadataJson)
    {
        using var document = JsonDocument.Parse(metadataJson);
        return new SessionProjectionsValue(7, new Dictionary<string, JsonElement>
        {
            [SessionProjectionsJson.ListMetadataKey] = document.RootElement.Clone()
        });
    }

    private static SessionProjectionsValue ProjectionsWithoutMetadata()
    {
        return new SessionProjectionsValue(3, []);
    }

    private static Task UntilAsync(Func<bool> condition, int timeoutMilliseconds = 5000)
    {
        return Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
            ClassicAssert.IsTrue(condition(), "预期的异步状态未在超时前出现。");
        });
    }

    private static Task WaitForIdleAsync(SessionBlankVerifier verifier)
    {
        return verifier.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    // 场景 1 / 11：list 返回 Unknown、projections 返回 blank=true → 未选中行隐藏（ConfirmedBlank），
    // 但行保留在完整目录中（复用候选谓词可直接命中，不再额外创建会话）；已核实结果不被重复扫描。
    [Test]
    public async Task UnknownRowBecomesConfirmedBlankAndStaysInCatalogAfterProjectionsRead()
    {
        var setup = new VerifierSetup
        {
            Responder = (_, _) => Task.FromResult<SessionProjectionsValue?>(ProjectionsWithMetadata(BlankTrueMetadata))
        };
        using var verifier = setup.Build();
        var       rows     = new[] { Row("s-1", cwd : @"C:\workspace\demo") };

        // 首屏不等待：核实完成前列表保持 Unknown 可见。
        var first = verifier.Resolve(rows);
        ClassicAssert.AreEqual(SessionBlankState.Unknown, first.Single().BlankState);

        await WaitForIdleAsync(verifier);

        var resolved = verifier.Resolve(rows).ToArray();
        var summary  = resolved.Single();
        ClassicAssert.AreEqual(SessionBlankState.ConfirmedBlank, summary.BlankState);
        ClassicAssert.AreEqual(@"C:\workspace\demo", summary.Cwd);
        // 完整目录保留该行：复用候选（确认空白 + cwd 匹配）可直接命中。
        Assert.That(resolved.Any(item => item is
                                     { BlankState: SessionBlankState.ConfirmedBlank, Cwd: @"C:\workspace\demo" }),
                    Is.True);
        ClassicAssert.AreEqual(1, setup.Requests.Count);
        ClassicAssert.IsTrue(setup.ChangedCount > 0);
    }

    // 场景 3：projections 返回 blank=false → 状态转为 Engaged，会话保持可见。
    [Test]
    public async Task EngagedProjectionsResultKeepsSessionVisibleAsEngaged()
    {
        var setup = new VerifierSetup
        {
            Responder = (_, _) => Task.FromResult<SessionProjectionsValue?>(ProjectionsWithMetadata(BlankFalseMetadata))
        };
        using var verifier = setup.Build();
        var       rows     = new[] { Row("s-1") };

        verifier.Resolve(rows);
        await WaitForIdleAsync(verifier);

        ClassicAssert.AreEqual(SessionBlankState.Engaged, verifier.Resolve(rows).Single().BlankState);
        ClassicAssert.AreEqual(1, setup.Requests.Count);
    }

    // 场景 4：null、缺元数据、格式错误、RPC 失败、超时 → 一律不判定（保持 Unknown，不误隐藏），
    // 且同代内不重复扫描（null/结论抑制，失败退避）。
    [Test]
    public async Task InconclusiveProjectionsKeepSessionsUnknownWithoutRescanStorm()
    {
        var setup = new VerifierSetup
        {
            Responder = (sessionId, _) => sessionId switch
            {
                "s-null"  => Task.FromResult<SessionProjectionsValue?>(null),
                "s-empty" => Task.FromResult<SessionProjectionsValue?>(ProjectionsWithoutMetadata()),
                "s-bad"   => Task.FromResult<SessionProjectionsValue?>(ProjectionsWithMetadata(BadBlankMetadata)),
                "s-fail" => Task.FromException<SessionProjectionsValue?>(new HarnessRpcException("gateway/internal",
                                                                             "内部错误")),
                "s-timeout" => Task.FromException<SessionProjectionsValue?>(new OperationCanceledException()),
                _           => Task.FromResult<SessionProjectionsValue?>(null)
            }
        };
        using var verifier = setup.Build();
        var       ids      = new[] { "s-null", "s-empty", "s-bad", "s-fail", "s-timeout" };
        var       rows     = ids.Select(id => Row(id)).ToArray();

        verifier.Resolve(rows);
        await WaitForIdleAsync(verifier);
        ClassicAssert.AreEqual(ids.Length, setup.Requests.Count);
        foreach (var summary in verifier.Resolve(rows))
            ClassicAssert.AreEqual(SessionBlankState.Unknown, summary.BlankState);

        // 冻结时钟内再次刷新：不判定结论与退避都抑制重复请求。
        for (var round = 0; round < 5; round++)
        {
            verifier.Resolve(rows);
            await WaitForIdleAsync(verifier);
        }

        ClassicAssert.AreEqual(ids.Length, setup.Requests.Count);
    }

    // 场景 5：普通列表再次返回 Unknown 不撤销核实结果；行内权威元数据（blank=false）
    // 则升级为 Engaged 并写回，元数据随后消失时写回的结论仍然生效。
    [Test]
    public async Task ListRefreshDoesNotUndoVerifiedResults()
    {
        var setup = new VerifierSetup
        {
            Responder = (_, _) => Task.FromResult<SessionProjectionsValue?>(ProjectionsWithMetadata(BlankTrueMetadata))
        };
        using var verifier = setup.Build();
        var       rows     = new[] { Row("s-1") };

        verifier.Resolve(rows);
        await WaitForIdleAsync(verifier);
        ClassicAssert.AreEqual(SessionBlankState.ConfirmedBlank, verifier.Resolve(rows).Single().BlankState);

        // 迟到的普通列表 Unknown 行：不撤销 ConfirmedBlank。
        ClassicAssert.AreEqual(SessionBlankState.ConfirmedBlank, verifier.Resolve(rows).Single().BlankState);

        // 行内携带有效元数据 blank=false（会话已被使用）：权威证据升级为 Engaged。
        var engagedRow = new[]
        {
            Row("s-1", SessionBlankState.Engaged, new SessionListMetadataWire(false, 500))
        };
        ClassicAssert.AreEqual(SessionBlankState.Engaged, verifier.Resolve(engagedRow).Single().BlankState);

        // 元数据从列表消失：写回的 Engaged 结论不被 Unknown 行撤销。
        ClassicAssert.AreEqual(SessionBlankState.Engaged, verifier.Resolve(rows).Single().BlankState);
    }

    // 场景 6：并发上限 2 生效；同一会话的重复排队合并为一个在途请求。
    [Test]
    public async Task VerificationCapsConcurrencyAndMergesDuplicateEnqueues()
    {
        var setup = new VerifierSetup
        {
            Responder = async (_, cancellationToken) =>
            {
                await Task.Delay(30, cancellationToken);
                return ProjectionsWithMetadata(BlankTrueMetadata);
            }
        };
        using var verifier = setup.Build();
        var       rows     = Enumerable.Range(1, 6).Select(index => Row($"s-{index}")).ToArray();

        verifier.Resolve(rows);
        await WaitForIdleAsync(verifier);

        ClassicAssert.AreEqual(rows.Length, setup.Requests.Count);
        ClassicAssert.IsTrue(setup.PeakReaders <= SessionBlankVerifier.MaxConcurrency,
                             $"并发峰值 {setup.PeakReaders} 超过上限 {SessionBlankVerifier.MaxConcurrency}。");

        // 全部已有当前代结论：重复刷新不重新扫描。
        verifier.Resolve(rows);
        await WaitForIdleAsync(verifier);
        ClassicAssert.AreEqual(rows.Length, setup.Requests.Count);
    }

    [Test]
    public async Task DuplicateEnqueueMergesIntoSingleInFlightRequest()
    {
        var gate = new TaskCompletionSource();
        var setup = new VerifierSetup
        {
            Responder = async (_, cancellationToken) =>
            {
                await gate.Task.WaitAsync(cancellationToken);
                return ProjectionsWithMetadata(BlankTrueMetadata);
            }
        };
        using var verifier = setup.Build();
        var       rows     = new[] { Row("s-1") };

        verifier.Resolve(rows);
        await UntilAsync(() => setup.Requests.Count == 1);
        verifier.Resolve(rows);
        verifier.Resolve(rows);

        gate.TrySetResult();
        await WaitForIdleAsync(verifier);

        ClassicAssert.AreEqual(1, setup.Requests.Count);
    }

    // 场景 7：核实期间本端开始发送（发送被接受进入台账）→ 迟到的 blank=true 不隐藏会话。
    [Test]
    public async Task LateBlankResultAfterEngagementDoesNotHideSession()
    {
        var gate = new TaskCompletionSource();
        var setup = new VerifierSetup
        {
            Responder = async (_, cancellationToken) =>
            {
                await gate.Task.WaitAsync(cancellationToken);
                return ProjectionsWithMetadata(BlankTrueMetadata);
            }
        };
        using var verifier = setup.Build();
        var       rows     = new[] { Row("s-1") };

        verifier.Resolve(rows);
        await UntilAsync(() => setup.Requests.Count == 1);

        // 用户此时发送消息：服务层在发送被接受时同步调用 OnSessionEngaged。
        setup.EngagedIds.Add("s-1");
        verifier.OnSessionEngaged("s-1");
        gate.TrySetResult();
        await WaitForIdleAsync(verifier);

        ClassicAssert.AreEqual(SessionBlankState.Engaged, verifier.Resolve(rows).Single().BlankState);
    }

    // 场景 8：断线重连后旧代响应不写入新代状态；新代自动重验并采纳新结果。
    [Test]
    public async Task StaleResponseFromPreviousGenerationIsDiscardedAfterReconnect()
    {
        var firstGate  = new TaskCompletionSource();
        var secondGate = new TaskCompletionSource();
        var calls      = 0;
        var setup = new VerifierSetup
        {
            Responder = async (_, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    // 无视连接重置的取消：旧代响应模拟「已在线上」，仍交回收尾处理，
                    // 由 ApplyResult 按代际丢弃并触发新代自动重验。
                    await firstGate.Task;
                    return ProjectionsWithMetadata(BlankTrueMetadata);
                }

                await secondGate.Task;
                return ProjectionsWithMetadata(BlankFalseMetadata);
            }
        };
        using var verifier = setup.Build();
        var       rows     = new[] { Row("s-1") };

        verifier.Resolve(rows);
        await UntilAsync(() => setup.Requests.Count == 1);

        // 连接先重置、旧响应随后在线上返回：响应作废，新代自动重验。
        verifier.OnConnectionReset();
        firstGate.TrySetResult();
        await UntilAsync(() => setup.Requests.Count >= 2);

        // 旧代 blank=true 未写入：新代响应到达前会话保持 Unknown。
        ClassicAssert.AreEqual(SessionBlankState.Unknown, verifier.Resolve(rows).Single().BlankState);

        secondGate.TrySetResult();
        await WaitForIdleAsync(verifier);
        ClassicAssert.AreEqual(SessionBlankState.Engaged, verifier.Resolve(rows).Single().BlankState);
    }

    // 场景 9：其他客户端使用曾确认空白的会话后，活动使结论失效（重新可见），
    // 重验读到 blank=false 后转为 Engaged。
    [Test]
    public async Task CrossClientActivityRevokesVerifiedBlankAndReverifies()
    {
        var calls = 0;
        var setup = new VerifierSetup
        {
            Responder = (_, _) =>
                Task.FromResult<SessionProjectionsValue?>(ProjectionsWithMetadata(Interlocked.Increment(ref calls) == 1
                                                              ? BlankTrueMetadata
                                                              : BlankFalseMetadata))
        };
        using var verifier = setup.Build();
        var       rows     = new[] { Row("s-1") };

        verifier.Resolve(rows);
        await WaitForIdleAsync(verifier);
        ClassicAssert.AreEqual(SessionBlankState.ConfirmedBlank, verifier.Resolve(rows).Single().BlankState);

        verifier.OnSessionActivity(new SessionActivityNotice("api-session/activity", "s-1"));
        await WaitForIdleAsync(verifier);

        ClassicAssert.AreEqual(SessionBlankState.Engaged, verifier.Resolve(rows).Single().BlankState);
        ClassicAssert.AreEqual(2, setup.Requests.Count);
    }

    // 场景 9（竞争侧）：核实期间收到运行事件 → 迟到的 blank=true 作废并重验，
    // 新结论 blank=false 使会话以 Engaged 呈现。
    [Test]
    public async Task ActivityDuringVerificationDiscardsStaleBlankResult()
    {
        var firstGate  = new TaskCompletionSource();
        var secondGate = new TaskCompletionSource();
        var calls      = 0;
        var setup = new VerifierSetup
        {
            Responder = async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    await firstGate.Task.WaitAsync(cancellationToken);
                    return ProjectionsWithMetadata(BlankTrueMetadata);
                }

                await secondGate.Task.WaitAsync(cancellationToken);
                return ProjectionsWithMetadata(BlankFalseMetadata);
            }
        };
        using var verifier = setup.Build();
        var       rows     = new[] { Row("s-1") };

        verifier.Resolve(rows);
        await UntilAsync(() => setup.Requests.Count == 1);

        // 核实期间其他客户端让会话开始运行：活动版本使在途结果过期。
        verifier.OnSessionActivity(new SessionActivityNotice("api-session/status", "s-1"));
        firstGate.TrySetResult();
        secondGate.TrySetResult();
        // 重验结果写入会触发变更通知；等到它落地再断言（idle 信号可能在重排与重验完成之间先到）。
        await UntilAsync(() => setup.ChangedCount >= 1);
        await WaitForIdleAsync(verifier);

        ClassicAssert.IsTrue(setup.Requests.Count >= 2);
        ClassicAssert.AreEqual(SessionBlankState.Engaged, verifier.Resolve(rows).Single().BlankState);
    }

    // 场景 12：失败按指数退避并受每代尝试上限约束，不形成高频无限重试；会话保持 Unknown。
    [Test]
    public async Task FailuresBackOffAndStopWithoutRequestStorm()
    {
        var setup = new VerifierSetup
        {
            Responder = (_, _) =>
                Task.FromException<SessionProjectionsValue?>(new HarnessRpcException("gateway/internal", "内部错误"))
        };
        using var verifier = setup.Build();
        var       rows     = new[] { Row("s-1") };

        verifier.Resolve(rows);
        await WaitForIdleAsync(verifier);
        ClassicAssert.AreEqual(1, setup.Requests.Count);

        // 退避窗口内反复刷新：不产生新请求。
        for (var round = 0; round < 10; round++)
        {
            verifier.Resolve(rows);
            await WaitForIdleAsync(verifier);
        }

        ClassicAssert.AreEqual(1, setup.Requests.Count);

        // 推进时钟逐次触发退避重试，直至每代尝试上限。
        while (setup.Requests.Count < SessionBlankVerifier.MaxAttemptsPerGeneration)
        {
            setup.UtcNow += TimeSpan.FromSeconds(31);
            verifier.Resolve(rows);
            await WaitForIdleAsync(verifier);
        }

        setup.UtcNow += TimeSpan.FromSeconds(31);
        verifier.Resolve(rows);
        await WaitForIdleAsync(verifier);

        ClassicAssert.AreEqual(SessionBlankVerifier.MaxAttemptsPerGeneration, setup.Requests.Count);
        ClassicAssert.AreEqual(SessionBlankState.Unknown, verifier.Resolve(rows).Single().BlankState);
    }

    // 接口不受支持：全局停止核实、保留 Unknown 并可报告兼容性限制。
    [Test]
    public async Task UnsupportedProjectionsStopAllVerificationAndKeepUnknown()
    {
        var setup = new VerifierSetup
        {
            Responder = (_, _) =>
                Task.FromException<SessionProjectionsValue?>(new HarnessRpcException("session/projections-unavailable",
                                                                 "接口不可用"))
        };
        using var verifier = setup.Build();
        var       rows     = new[] { Row("s-1"), Row("s-2") };

        verifier.Resolve(rows);
        await WaitForIdleAsync(verifier);

        ClassicAssert.AreEqual(1, setup.Requests.Count);
        ClassicAssert.IsTrue(verifier.IsUnsupported);
        foreach (var summary in verifier.Resolve(rows))
            ClassicAssert.AreEqual(SessionBlankState.Unknown, summary.BlankState);
    }

    // 失效规则：删除事件与列表差集都清理判定；会话重新出现时重新核实而非沿用陈旧结论。
    [Test]
    public async Task RemovedAndVanishedSessionsCleanUpVerifiedState()
    {
        var setup = new VerifierSetup
        {
            Responder = (_, _) => Task.FromResult<SessionProjectionsValue?>(ProjectionsWithMetadata(BlankTrueMetadata))
        };
        using var verifier = setup.Build();
        var       rows     = new[] { Row("s-1") };

        verifier.Resolve(rows);
        await WaitForIdleAsync(verifier);
        ClassicAssert.AreEqual(SessionBlankState.ConfirmedBlank, verifier.Resolve(rows).Single().BlankState);

        // 删除事件清理结论：会话重新出现时保持 Unknown 并重新核实。
        verifier.OnSessionActivity(new SessionActivityNotice("api-session/removed", "s-1"));
        ClassicAssert.AreEqual(SessionBlankState.Unknown, verifier.Resolve(rows).Single().BlankState);
        await WaitForIdleAsync(verifier);

        // 列表差集同样清理：从列表消失后再出现，不沿用旧结论。
        verifier.Resolve([]);
        ClassicAssert.AreEqual(SessionBlankState.Unknown, verifier.Resolve(rows).Single().BlankState);
    }

    // 列表行元数据解析：只有真实布尔 blank 有效；blank=false 优先于 wire blank=true；
    // 格式错误的元数据视为无效，保持 Unknown。
    [Test]
    public void ListRowMetadataBooleanValueDecidesBlankState()
    {
        var engaged = HarnessSessionService.ToSummary(new SessionSummaryWire("s-1", 1, false, true,
                                                                             Projections :
                                                                             new SessionProjectionHintsWire("sequenced",
                                                                                 4,
                                                                                 MetadataValues(BlankFalseMetadata))));
        ClassicAssert.AreEqual(SessionBlankState.Engaged, engaged.BlankState);

        var blank = HarnessSessionService.ToSummary(new SessionSummaryWire("s-1", 1, false, false,
                                                                           Projections :
                                                                           new SessionProjectionHintsWire("cached", 4,
                                                                               MetadataValues(BlankTrueMetadata))));
        ClassicAssert.AreEqual(SessionBlankState.ConfirmedBlank, blank.BlankState);

        var malformed = HarnessSessionService.ToSummary(new SessionSummaryWire("s-1", 1, false, false,
                                                                               Projections :
                                                                               new SessionProjectionHintsWire("cached",
                                                                                   4,
                                                                                   MetadataValues(BadBlankMetadata))));
        ClassicAssert.AreEqual(SessionBlankState.Unknown, malformed.BlankState);

        var fallbackBlank =
            HarnessSessionService.ToSummary(new SessionSummaryWire("s-1", 1, false, true, Projections : null));
        ClassicAssert.AreEqual(SessionBlankState.ConfirmedBlank, fallbackBlank.BlankState);
    }

    // 协议 JSON：请求走 args[request]（默认参数名），result 为 null 时保持"无值"语义。
    [Test]
    public void SessionProjectionsWireRoundTrips()
    {
        var request = new SessionProjectionsRequest("session-1");
        var json    = JsonSerializer.Serialize(request, HarnessJsonContext.Default.SessionProjectionsRequest);
        ClassicAssert.AreEqual("""{"sessionId":"session-1"}""", json);

        var response =
            RpcEnvelope.ParseResponse("""{"rpcId":"r1","result":{"ok":true,"value":{"asOfSeq":9,"values":{"sessionListMetadata":{"blank":true,"lastPromptAt":123}}}}}""");
        ClassicAssert.IsTrue(response.Ok);
        var value = response.Value!.Value.Deserialize(HarnessJsonContext.Default.SessionProjectionsValue);
        ClassicAssert.IsNotNull(value);
        ClassicAssert.AreEqual(9, value.AsOfSeq);
        var metadata = SessionProjectionsJson.ParseListMetadata(value.Values![SessionProjectionsJson.ListMetadataKey]);
        ClassicAssert.IsTrue(metadata!.Blank);
        ClassicAssert.AreEqual(123, metadata.LastPromptAt);

        // 会话不存在：result 本身为 null → 不产生投影值实例，调用方不得判为空白。
        var missing = RpcEnvelope.ParseResponse("""{"rpcId":"r2","result":{"ok":true,"value":null}}""");
        ClassicAssert.IsTrue(missing.Ok);
        ClassicAssert.IsNull(missing.Value);
    }

    private static Dictionary<string, JsonElement> MetadataValues(string metadataJson)
    {
        using var document = JsonDocument.Parse(metadataJson);
        return new Dictionary<string, JsonElement>
        {
            [SessionProjectionsJson.ListMetadataKey] = document.RootElement.Clone()
        };
    }

    /// <summary>可控读取器与可注入时钟的核实器装配；Requests 记录每次真实查询。</summary>
    private sealed class VerifierSetup
    {
        public readonly HashSet<string> EngagedIds = [];
        public readonly List<string> Requests = [];
        public          int ChangedCount;
        public          int CurrentReaders;
        public          int PeakReaders;
        public          Func<string, CancellationToken, Task<SessionProjectionsValue?>>? Responder;
        public          DateTimeOffset UtcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public SessionBlankVerifier Build()
        {
            return new SessionBlankVerifier(ReadAsync, EngagedIds.Contains, () => ChangedCount++, () => UtcNow);
        }

        private async Task<SessionProjectionsValue?> ReadAsync(
            string sessionId, CancellationToken cancellationToken)
        {
            lock (this)
            {
                Requests.Add(sessionId);
                CurrentReaders++;
                if (CurrentReaders > PeakReaders) PeakReaders = CurrentReaders;
            }

            try
            {
                return Responder is null ? null : await Responder(sessionId, cancellationToken);
            }
            finally
            {
                lock (this)
                {
                    CurrentReaders--;
                }
            }
        }
    }
}
