using DshDesktop.Core.Models;
using DshDesktop.Harness.Services.Connection;
using DshDesktop.Harness.Services.Sessions;
using DshDesktop.Harness.Services.Settings;
using DshDesktop.Infrastructure.Services.Backend;
using NUnit.Framework;
using NUnit.Framework.Legacy;

namespace DshDesktop.Tests;

/// <summary>
///     真实模型的完整对话往返验证：流式文本增量、正式消息提交、生成中取消与中断结局。
///     本测试会调用付费模型，仅在显式启用（DSH_E2E_REAL_MODEL=1，另需
///     DSH_E2E_RUNTIME_DIR 与 DSH_E2E_REAL_HOME=1）时运行；未启用时明确跳过。
///     启用后若目标凭据未被当前 Host 识别（credentials/describe 未配置），测试直接失败
///     而不是降级通过。注意：会在真实 home 中创建新会话并消耗模型额度。
/// </summary>
public sealed class RealModelConversationTests
{
    [Test]
    public async Task StreamedReplyArrivesInDeltasAndCancelInterruptsGeneration()
    {
        var runtimeDir     = Environment.GetEnvironmentVariable(RealBackendTestSupport.RuntimeDirVariable);
        var realHome       = Environment.GetEnvironmentVariable(RealBackendTestSupport.RealHomeVariable);
        var realModel      = Environment.GetEnvironmentVariable(RealBackendTestSupport.RealModelVariable);
        var node           = RealBackendTestSupport.FindNodeExecutable();
        var launcherScript = RealBackendTestSupport.FindLauncherScript();
        if (!string.Equals(realModel, "1", StringComparison.Ordinal))
            Assert.Ignore($"未设置 {RealBackendTestSupport.RealModelVariable}=1，跳过真实模型对话验证（会产生模型调用费用）。");
        if (string.IsNullOrWhiteSpace(runtimeDir))
            Assert.Ignore($"已启用真实模型验证，但未设置 {RealBackendTestSupport.RuntimeDirVariable}。");
        if (!string.Equals(realHome, "1", StringComparison.Ordinal))
            Assert.Ignore($"已启用真实模型验证，但未设置 {RealBackendTestSupport.RealHomeVariable}=1。");
        if (node is null) Assert.Ignore("PATH 中找不到 Node 可执行文件。");
        if (launcherScript is null) Assert.Ignore("找不到 launcher 脚本。");

        var dshHome = RealBackendTestSupport.ResolveSharedDshHome();
        if (!File.Exists(Path.Combine(dshHome, "settings.yaml")))
            Assert.Ignore($"已启用真实模型验证，但未找到 {Path.Combine(dshHome, "settings.yaml")}。");

        var (provider, model) =
            RealBackendTestSupport.ParseModel(Environment.GetEnvironmentVariable(RealBackendTestSupport
                                                 .ModelOverrideVariable) ?? "glm/glm-5.3-flash");
        var credentialRef = Environment.GetEnvironmentVariable(RealBackendTestSupport.CredentialRefVariable)
                         ?? "GLM_API_KEY";
        var root    = Path.Combine(Path.GetTempPath(), $"dsh-glm-e2e-{Guid.NewGuid():N}");
        var options = RealBackendTestSupport.BuildOptions(node!, launcherScript!, runtimeDir!, dshHome, root);

        var       hostService        = new NodeBackendHostService(options);
        var       connection         = new HarnessConnection(hostService.StartAsync);
        var       sessions           = new HarnessSessionService(connection);
        var       credentials        = new HarnessCredentialService(connection);
        using var followCancellation = new CancellationTokenSource();
        var       collector          = new RealBackendTestSupport.UpdateCollector(TestContext.Out);
        Task?     followTask         = null;
        try
        {
            await hostService.StartAsync().WaitAsync(TimeSpan.FromSeconds(150));

            var entries = await credentials.DescribeAsync([credentialRef]).WaitAsync(TimeSpan.FromSeconds(30));
            entries.TryGetValue(credentialRef, out var status);
            // 启用后配置不满足必须失败：静默降级会让“测试通过”掩盖没有真实回复的事实。
            ClassicAssert.IsTrue(status is { Configured: true },
                                 $"已启用真实模型验证，但当前 Host 未识别凭据引用 {credentialRef}"
                               + $"（configured={status?.Configured.ToString() ?? "无应答"}，"
                               + $"source={status?.Source                     ?? "未报告"}）。"
                               + "请先用 dsh 保存凭据（写入 DSH_HOME/.credentials.yaml），或在启动环境提供该引用。");
            TestContext.Out.WriteLine($"凭据 {credentialRef}：已配置（来源 {status!.Source ?? "未报告"}）。");

            var catalog = await sessions.GetModelCatalogAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.That(catalog.Groups.Any(group => group.Id == provider), Is.True);
            TestContext.Out.WriteLine($"默认模型：{catalog.Default?.Provider}/{catalog.Default?.Model}");

            var created = await sessions.CreateSessionAsync().WaitAsync(TimeSpan.FromSeconds(30));
            TestContext.Out.WriteLine($"新会话：{created.Id}");

            followTask = collector.StartAsync(sessions, created.Id, followCancellation);
            await RealBackendTestSupport.WaitForAsync(
                                                      () =>
                                                          collector
                                                             .FirstOrDefault<SessionUpdate.Snapshot>() is not null,
                                                      TimeSpan.FromSeconds(30), "follow 快照未到达");

            // ---- 第一轮：完整流式回复 ----
            await sessions.SendPromptAsync(created.Id, Guid.NewGuid().ToString(),
                                           "请用一句话回答：天空为什么是蓝色的？")
                          .WaitAsync(TimeSpan.FromSeconds(30));

            var started =
                await RealBackendTestSupport.WaitForAsync(() => collector.FirstOrDefault<SessionUpdate.StreamStarted>(),
                                                          TimeSpan.FromSeconds(60), "流式开始帧未出现");
            TestContext.Out.WriteLine($"流式开始：{started.AttemptId}");

            var delta = await RealBackendTestSupport.WaitForAsync(() => collector.Snapshot()
                                                                     .OfType<SessionUpdate.StreamTextDelta>()
                                                                     .FirstOrDefault(update => update.AttemptId ==
                                                                          started.AttemptId &&
                                                                          update.Text.Length > 0),
                                                                  TimeSpan.FromSeconds(180), "未在超时前收到任何流式文本增量");
            var streamedLength = collector.Snapshot().OfType<SessionUpdate.StreamTextDelta>()
                                          .Where(update => update.AttemptId == started.AttemptId)
                                          .Sum(update => update.Text.Length);
            TestContext.Out.WriteLine($"首个增量：{delta.Text}；累计流式字符：{streamedLength}");
            ClassicAssert.IsTrue(streamedLength > 0, "流式增量总长度为 0。");

            var assistant =
                await RealBackendTestSupport.WaitForAsync(() => collector.Snapshot()
                                                                         .OfType<SessionUpdate.MessageAppended>()
                                                                         .FirstOrDefault(update => update.Message is
                                                                          {
                                                                              Role          : MessageRole.Assistant,
                                                                              Content.Length: > 0
                                                                          }), TimeSpan.FromSeconds(180),
                                                          "助手正式回复未在超时前出现");
            TestContext.Out.WriteLine($"助手回复（{assistant.Message.Content.Length} 字符）：{assistant.Message.Content}");
            ClassicAssert.IsTrue(assistant.Message.Content.Length > 0, "助手回复内容为空。");

            var ended = await RealBackendTestSupport.WaitForAsync(collector.FirstOrDefault<SessionUpdate.StreamEnded>,
                                                                  TimeSpan.FromSeconds(30), "流式结束帧未出现");
            ClassicAssert.AreEqual(started.AttemptId, ended.AttemptId);
            ClassicAssert.AreEqual(StreamOutcomeKind.Committed, ended.Outcome);
            TestContext.Out.WriteLine($"流式结束：{ended.Outcome}");

            // ---- 第二轮：生成中取消，验证后端真正中断 ----
            await sessions.SendPromptAsync(created.Id, Guid.NewGuid().ToString(),
                                           "请写一篇关于海洋的长篇介绍，从地质、洋流到生态，篇幅越长越好。")
                          .WaitAsync(TimeSpan.FromSeconds(30));

            var cancelStarted =
                await RealBackendTestSupport.WaitForAsync(() => collector.Snapshot()
                                                                         .OfType<SessionUpdate.StreamStarted>()
                                                                         .FirstOrDefault(update => update.AttemptId !=
                                                                              started.AttemptId),
                                                          TimeSpan.FromSeconds(60), "第二轮流式开始帧未出现");
            TestContext.Out.WriteLine($"第二轮流式开始：{cancelStarted.AttemptId}");

            // 观察窗口：出现文本增量即取消。GLM 这类混合推理模型在长文任务上可能先进入
            // 只产出 reasoning-delta 的长思考阶段（应用层更新不透传思考增量），
            // 窗口结束仍无文本也照样取消——尝试已在运行，中断依然成立。
            var sawTextDelta        = false;
            var observationDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTimeOffset.UtcNow < observationDeadline)
            {
                if (collector.Snapshot().OfType<SessionUpdate.StreamTextDelta>()
                             .Any(update => update.AttemptId == cancelStarted.AttemptId))
                {
                    sawTextDelta = true;
                    break;
                }

                await Task.Delay(500);
            }

            var frameCounts = collector.Snapshot()
                                       .GroupBy(update => update.GetType().Name)
                                       .Select(group => $"{group.Key}×{group.Count()}");
            TestContext.Out.WriteLine($"取消前累计更新：{string.Join(", ", frameCounts)}；本轮文本增量已出现：{sawTextDelta}");

            await sessions.CancelAsync(created.Id).WaitAsync(TimeSpan.FromSeconds(15));
            TestContext.Out.WriteLine("已请求取消，等待后端停止生成…");

            var cancelled =
                await RealBackendTestSupport.WaitForAsync(() => collector.Snapshot().OfType<SessionUpdate.StreamEnded>()
                                                                         .FirstOrDefault(update => update.AttemptId ==
                                                                              cancelStarted.AttemptId),
                                                          TimeSpan.FromSeconds(60), "取消后未收到流式结束帧");
            // 协议语义（参考实现 agent.ts）：用户取消总是落盘结算（committed）——
            // 有可见内容时结算为带 interrupted 标记的 assistant/message，否则为
            // assistant/attempt 空结算；abandoned 仅出现在无法落盘的错误路径。
            ClassicAssert.AreEqual(StreamOutcomeKind.Committed, cancelled.Outcome);
            ClassicAssert.IsTrue(cancelled.SettlementEventType is "assistant/message" or "assistant/attempt",
                                 $"未预期的结算类型：{cancelled.SettlementEventType}");
            TestContext.Out.WriteLine($"取消后的结算：{cancelled.Outcome}/{cancelled.SettlementEventType}"
                                    + $"（取消时已有文本增量：{sawTextDelta}）");

            if (cancelled.SettlementEventType == "assistant/message")
            {
                var interrupted =
                    await RealBackendTestSupport.WaitForAsync(() => collector.Snapshot()
                                                                             .OfType<SessionUpdate.MessageAppended>()
                                                                             .FirstOrDefault(update =>
                                                                                  update.Message.Role ==
                                                                                  MessageRole.Assistant &&
                                                                                  update.Message.Content
                                                                                     .Contains("[已中断]")),
                                                              TimeSpan.FromSeconds(30), "取消后未收到带中断标注的助手消息");
                TestContext.Out
                           .WriteLine($"中断消息（{interrupted.Message.Content.Length} 字符）：{interrupted.Message.Content}");
            }

            // 取消不应破坏连接：只读调用仍然可用。
            var list = await sessions.GetSessionsAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.That(list.Any(summary => summary.Id == created.Id), Is.True);

            ClassicAssert.IsFalse(hostService.LastError is { Length: > 0 }, $"后端意外出错：{hostService.LastError}");
            collector.AssertNoSubscriptionFault();
            ClassicAssert.IsFalse(followTask.IsFaulted, "订阅任务异常退出。");
        }
        finally
        {
            followCancellation.Cancel();
            if (followTask is not null)
                try
                {
                    await followTask.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (TimeoutException)
                {
                    // 订阅未在宽限期内退出；连接释放会强制结束它。
                }

            await connection.DisposeAsync();
            await hostService.DisposeAsync();
        }
    }
}
