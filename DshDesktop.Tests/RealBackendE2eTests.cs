using DshDesktop.Core.Models;
using DshDesktop.Harness.Exceptions;
using DshDesktop.Harness.Json;
using DshDesktop.Harness.Models.Events;
using DshDesktop.Harness.Models.Requests;
using DshDesktop.Harness.Models.Responses;
using DshDesktop.Harness.Services.Connection;
using DshDesktop.Harness.Services.Sessions;
using DshDesktop.Harness.Services.Workspaces;
using DshDesktop.Infrastructure.Services.Backend;
using Xunit;
using Xunit.Abstractions;

namespace DshDesktop.Tests;

/// <summary>
///     对真实 Harness Host 的端到端验证（隔离 DSH_HOME，无模型凭据）：
///     独立 Node 启动、令牌认证、会话列表、创建会话、发送消息、订阅快照与增量、取消调用与优雅关闭。
///     仅在设置 DSH_E2E_RUNTIME_DIR（prepare-dev-runtime.mjs 生成的目录）时运行，缺失时明确跳过。
///     隔离 home 没有模型凭据，不存在真实生成：这里的取消只验证不破坏连接，
///     生成中取消的真实中断由 RealModelConversationTests（显式启用 + 凭据校验）覆盖。
/// </summary>
public sealed class RealBackendE2ETests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task HostBootsListCreatePromptFollowAndCancelAllWork()
    {
        var runtimeDir     = Environment.GetEnvironmentVariable(RealBackendTestSupport.RuntimeDirVariable);
        var node           = RealBackendTestSupport.FindNodeExecutable();
        var launcherScript = RealBackendTestSupport.FindLauncherScript();
        Skip.If(string.IsNullOrWhiteSpace(runtimeDir),
                $"未设置 {RealBackendTestSupport.RuntimeDirVariable}，跳过真实后端 E2E 验证。");
        Skip.If(node is null, "PATH 中找不到 Node 可执行文件，跳过。");
        Skip.If(launcherScript is null, "找不到 launcher 脚本，跳过。");

        var root = Path.Combine(Path.GetTempPath(), $"dsh-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var options = RealBackendTestSupport.BuildOptions(node!, launcherScript!, runtimeDir!,
                                                          Path.Combine(root, "dsh-home"), root);

        var       hostService        = new NodeBackendHostService(options);
        var       connection         = new HarnessConnection(hostService.StartAsync);
        var       sessions           = new HarnessSessionService(connection);
        using var followCancellation = new CancellationTokenSource();
        var       collector          = new RealBackendTestSupport.UpdateCollector(output);
        Task?     followTask         = null;
        try
        {
            var info = await hostService.StartAsync().WaitAsync(TimeSpan.FromSeconds(150));
            Assert.StartsWith("http://127.0.0.1:", info.AuthenticatedUri.ToString());

            var list = await sessions.GetSessionsAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotNull(list);

            var created = await sessions.CreateSessionAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.StartsWith("session-", created.Id);

            followTask = collector.StartAsync(sessions, created.Id, followCancellation);

            await RealBackendTestSupport.WaitForAsync(() =>
                                                          collector
                                                             .FirstOrDefault<SessionUpdate.Snapshot>() is not null,
                                                      TimeSpan.FromSeconds(30), "follow 快照未到达");

            await sessions.SendPromptAsync(created.Id, Guid.NewGuid().ToString(), "端到端验证消息")
                          .WaitAsync(TimeSpan.FromSeconds(30));

            await RealBackendTestSupport.WaitForAsync(() => collector.Snapshot()
                                                                     .Any(update =>
                                                                              update is SessionUpdate.MessageAppended
                                                                              {
                                                                                  Message.Role: MessageRole.User
                                                                              }), TimeSpan.FromSeconds(30),
                                                      "用户消息未出现在会话流中");

            var stored = await sessions.GetMessagesAsync(created.Id).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Contains(stored, message => message.Content.Contains("端到端验证消息"));

            // session/page 真实往返：新会话没有更早历史，验证请求形状与响应解析（空页 + hasMore=false）。
            var openingSnapshot = collector.FirstOrDefault<SessionUpdate.Snapshot>();
            Assert.NotNull(openingSnapshot);
            var olderPage = await sessions
                                 .LoadOlderAsync(created.Id, openingSnapshot.Cursor, openingSnapshot.WindowStartSeq)
                                 .WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(olderPage.HasMore);
            Assert.True(olderPage.WindowStartSeq >= 0);

            // 快照统计投影：tokenUsage/sessionStats 随快照可解析（新会话全零也算到达）。
            await RealBackendTestSupport.WaitForAsync(() => collector.Snapshot()
                                                                     .Any(update => update is SessionUpdate
                                                                             .UsageUpdated), TimeSpan.FromSeconds(30),
                                                      "快照 usage 投影未到达");
            var openingUsage = collector.Snapshot().OfType<SessionUpdate.UsageUpdated>().First();
            output.WriteLine($"快照 usage 投影：未命中 {openingUsage.Usage.UncachedInputTokens}"
                           + $" · 缓存读 {openingUsage.Usage.CacheReadTokens}"
                           + $" · 输出 {openingUsage.Usage.OutputTokens}");

            // session/control 真实往返：Host 级状态流 Baseline 到达且可解析（jobs 帧被忽略）。
            using (var controlCancellation =
                   CancellationTokenSource.CreateLinkedTokenSource(followCancellation.Token))
            {
                var controlBaselineTask = Task.Run(async () =>
                {
                    await foreach (var frame in connection.FollowSessionControlAsync(controlCancellation.Token))
                        if (frame is SessionControlFrame.Baseline baseline)
                            return baseline;

                    return null;
                });
                var controlBaseline = await controlBaselineTask.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.NotNull(controlBaseline);
                output.WriteLine($"control 基线投影会话数：{controlBaseline!.Projections.Count}");
                controlCancellation.Cancel();
            }

            // workspace/follow 真实往返：隔离 home 无已登记工作区，基线允许为空，
            // 但必须到达且可解析（Baseline 无条件触发一次变更通知）。
            var workspaces      = new HarnessWorkspaceService(connection);
            var baselineArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            workspaces.WorkspacesChanged += (_, _) => baselineArrived.TrySetResult();
            // 首次调用启动订阅并立即返回（可能为空）；基线到达后事件通知。
            await workspaces.GetWorkspacesAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await baselineArrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var baselineWorkspaces = await workspaces.GetWorkspacesAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(baselineWorkspaces);
            output.WriteLine($"workspace 基线条目数：{baselineWorkspaces.Count}");
            await workspaces.DisposeAsync();

            // 隔离 home 无凭据，不存在真实生成：取消只要求幂等且不破坏连接；
            // 会话不在运行态时后端可能拒绝取消，属协议允许的答复。
            try
            {
                await sessions.CancelAsync(created.Id).WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch (HarnessRpcException)
            {
                // 不视为失败；真实中断验证见 RealModelConversationTests。
            }

            // 取消后连接与只读调用仍可用。
            var listAfterCancel = await sessions.GetSessionsAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Contains(listAfterCancel, summary => summary.Id == created.Id);

            Assert.False(hostService.LastError is { Length: > 0 }, $"后端意外出错：{hostService.LastError}");
            collector.AssertNoSubscriptionFault();
            Assert.False(followTask.IsFaulted, "订阅任务异常退出。");
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
            try
            {
                Directory.Delete(root, true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Host 建立的链接目录可能短暂占用；留给系统临时目录清理。
            }
        }
    }

    /// <summary>
    ///     真实 Host 上的模式（Agent 预设）绑定：session/create 携带 agentPreset 创建后，
    ///     响应回显会话实际挂载的预设（后端从 Agent 取，非请求回声）；不携带时回显内置
    ///     默认 standard。隔离 DSH_HOME，无模型凭据。
    /// </summary>
    [SkippableFact]
    public async Task SessionCreateBindsRequestedAgentPresetOnRealHost()
    {
        var runtimeDir     = Environment.GetEnvironmentVariable(RealBackendTestSupport.RuntimeDirVariable);
        var node           = RealBackendTestSupport.FindNodeExecutable();
        var launcherScript = RealBackendTestSupport.FindLauncherScript();
        Skip.If(string.IsNullOrWhiteSpace(runtimeDir),
                $"未设置 {RealBackendTestSupport.RuntimeDirVariable}，跳过真实后端 E2E 验证。");
        Skip.If(node is null, "PATH 中找不到 Node 可执行文件，跳过。");
        Skip.If(launcherScript is null, "找不到 launcher 脚本，跳过。");

        var root = Path.Combine(Path.GetTempPath(), $"dsh-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var options = RealBackendTestSupport.BuildOptions(node!, launcherScript!, runtimeDir!,
                                                          Path.Combine(root, "dsh-home"), root);

        var hostService = new NodeBackendHostService(options);
        var connection  = new HarnessConnection(hostService.StartAsync);
        try
        {
            await hostService.StartAsync().WaitAsync(TimeSpan.FromSeconds(150));

            var bound = await connection
                             .InvokeAsync("session/create",
                                          new SessionCreateRequest(AgentPreset : "minimal"),
                                          HarnessJsonContext.Default.SessionCreateRequest,
                                          HarnessJsonContext.Default.SessionCreateValue,
                                          CancellationToken.None)
                             .WaitAsync(TimeSpan.FromSeconds(30));
            Assert.StartsWith("session-", bound.SessionId);
            Assert.Equal("minimal", bound.AgentPreset);

            var fallback = await connection
                                .InvokeAsync("session/create",
                                             new SessionCreateRequest(),
                                             HarnessJsonContext.Default.SessionCreateRequest,
                                             HarnessJsonContext.Default.SessionCreateValue,
                                             CancellationToken.None)
                                .WaitAsync(TimeSpan.FromSeconds(30));
            Assert.StartsWith("session-", fallback.SessionId);
            Assert.Equal("standard", fallback.AgentPreset);
        }
        finally
        {
            await connection.DisposeAsync();
            await hostService.DisposeAsync();
            try
            {
                Directory.Delete(root, true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Host 建立的链接目录可能短暂占用；留给系统临时目录清理。
            }
        }
    }

    /// <summary>
    ///     真实 Host 上的空白复用链路：登记工作区、按工作区创建空白会话、列表三态空白
    ///     （wire blank=true + sessionListMetadata → 确认空白，cwd 随行携带）、按
    ///     sessionId 收养复用（同一会话，不产生第二个 SessionId）与归档帧回流
    ///     （ArchivedSessionIds 更新）。隔离 DSH_HOME，无模型凭据。
    /// </summary>
    [SkippableFact]
    public async Task WorkspaceRegistrationBlankListAndSessionAdoptionRoundTrip()
    {
        var runtimeDir     = Environment.GetEnvironmentVariable(RealBackendTestSupport.RuntimeDirVariable);
        var node           = RealBackendTestSupport.FindNodeExecutable();
        var launcherScript = RealBackendTestSupport.FindLauncherScript();
        Skip.If(string.IsNullOrWhiteSpace(runtimeDir),
                $"未设置 {RealBackendTestSupport.RuntimeDirVariable}，跳过真实后端 E2E 验证。");
        Skip.If(node is null, "PATH 中找不到 Node 可执行文件，跳过。");
        Skip.If(launcherScript is null, "找不到 launcher 脚本，跳过。");

        var root         = Path.Combine(Path.GetTempPath(), $"dsh-e2e-{Guid.NewGuid():N}");
        var workspaceDir = Path.Combine(root, "ws");
        Directory.CreateDirectory(workspaceDir);
        var options = RealBackendTestSupport.BuildOptions(node!, launcherScript!, runtimeDir!,
                                                          Path.Combine(root, "dsh-home"), root);

        var hostService = new NodeBackendHostService(options);
        var connection  = new HarnessConnection(hostService.StartAsync);
        var sessions    = new HarnessSessionService(connection);
        var workspaces  = new HarnessWorkspaceService(connection);
        try
        {
            await hostService.StartAsync().WaitAsync(TimeSpan.FromSeconds(150));

            // 登记工作区（幂等协议）：返回工作区行。
            // 登记工作区（走工作区服务的登记方法，对齐桌面端添加工作区链路）。
            var registered = await workspaces.RegisterWorkspaceAsync(workspaceDir)
                                             .WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(workspaceDir, registered.Path);

            // 幂等：重复登记同一目录返回既有行，不报错也不产生第二条。
            var reregistered = await workspaces.RegisterWorkspaceAsync(workspaceDir)
                                               .WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(registered.Id, reregistered.Id);
            var workspaceId = registered.Id;

            // 按工作区创建会话：真实归属由 workspace/follow 记账回流。
            var created = await sessions.CreateSessionAsync(workspaceId).WaitAsync(TimeSpan.FromSeconds(30));
            await workspaces.GetWorkspacesAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await RealBackendTestSupport.WaitForAsync(
                                                      async () => (await workspaces.GetWorkspacesAsync())
                                                                         .Any(workspace => workspace.Id == workspaceId &&
                                                                                  workspace.SessionIds
                                                                                     .Contains(created.Id)),
                                                      TimeSpan.FromSeconds(30), "工作区记账未回流");

            // 列表三态：活跃新会话 blank=true（sessionListMetadata 背书）→ 确认空白；cwd 随行。
            var row = (await sessions.GetSessionsAsync().WaitAsync(TimeSpan.FromSeconds(30)))
               .Single(summary => summary.Id == created.Id);
            Assert.Equal(SessionBlankState.ConfirmedBlank, row.BlankState);
            Assert.Equal(workspaceDir, row.Cwd);
            output.WriteLine($"新会话行：blankState={row.BlankState} cwd={row.Cwd} title={row.Title ?? "<null>"}");

            // 按身份收养：同一 SessionId 复用，不产生第二个会话。
            var adopted = await sessions.CreateSessionAsync(workspaceId, created.Id)
                                        .WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(created.Id, adopted.Id);
            var listAfterAdopt = await sessions.GetSessionsAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Single(listAfterAdopt, summary => summary.Id == created.Id);

            // 归档协议往返：归档集合经 workspace/archived 帧回流到服务投影。
            // 置顶已迁到本端自有方案（本地配置文件），不再走 workspace/pinSession 协议。
            var archived = await connection
                                .InvokeAsync("workspace/archiveSession",
                                             new WorkspaceArchiveSessionRequest(created.Id),
                                             HarnessJsonContext.Default.WorkspaceArchiveSessionRequest,
                                             HarnessJsonContext.Default.WorkspaceArchiveValue,
                                             CancellationToken.None)
                                .WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Contains(created.Id, archived.ArchivedSessionIds);
            await RealBackendTestSupport.WaitForAsync(
                                                      () => workspaces.ArchivedSessionIds.Contains(created.Id),
                                                      TimeSpan.FromSeconds(30), "归档集合投影未回流");
            output.WriteLine($"归档投影回流：{string.Join(',', workspaces.ArchivedSessionIds)}");

            Assert.False(hostService.LastError is { Length: > 0 }, $"后端意外出错：{hostService.LastError}");
        }
        finally
        {
            await connection.DisposeAsync();
            await workspaces.DisposeAsync();
            await hostService.DisposeAsync();
            try
            {
                Directory.Delete(root, true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Host 建立的链接目录可能短暂占用；留给系统临时目录清理。
            }
        }
    }

    /// <summary>
    ///     阶段 3 只读诊断：真实共享 home 上仅调用 session/list（不创建、不发送、不订阅——
    ///     参考实现保证列表从不触发投影重算，不写任何存储），输出空白三态的聚合计数，
    ///     核实历史空白会话残留原因（v3 投影缓存被拒认的冷行 → wire blank=false 且无
    ///     sessionListMetadata → 未知可见）。输出只含计数与会话 id 前缀，不含标题或正文。
    ///     需要 DSH_E2E_RUNTIME_DIR 与 DSH_E2E_REAL_HOME=1 双重显式启用。
    /// </summary>
    [SkippableFact]
    public async Task RealHomeListBlankStateReadOnlyDiagnostic()
    {
        var runtimeDir     = Environment.GetEnvironmentVariable(RealBackendTestSupport.RuntimeDirVariable);
        var realHome       = Environment.GetEnvironmentVariable(RealBackendTestSupport.RealHomeVariable);
        var node           = RealBackendTestSupport.FindNodeExecutable();
        var launcherScript = RealBackendTestSupport.FindLauncherScript();
        Skip.If(string.IsNullOrWhiteSpace(runtimeDir),
                $"未设置 {RealBackendTestSupport.RuntimeDirVariable}，跳过。");
        Skip.If(!string.Equals(realHome, "1", StringComparison.Ordinal),
                $"未设置 {RealBackendTestSupport.RealHomeVariable}=1，跳过真实 home 诊断。");
        Skip.If(node is null, "PATH 中找不到 Node 可执行文件，跳过。");
        Skip.If(launcherScript is null, "找不到 launcher 脚本，跳过。");

        var dshHome = RealBackendTestSupport.ResolveSharedDshHome();
        Skip.If(!Directory.Exists(Path.Combine(dshHome, "sessions")), "共享 home 无会话存储，跳过。");

        var root = Path.Combine(Path.GetTempPath(), $"dsh-diag-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var options = RealBackendTestSupport.BuildOptions(node!, launcherScript!, runtimeDir!, dshHome, root);

        var hostService = new NodeBackendHostService(options);
        var connection  = new HarnessConnection(hostService.StartAsync);
        var sessions    = new HarnessSessionService(connection);
        try
        {
            await hostService.StartAsync().WaitAsync(TimeSpan.FromSeconds(150));
            var list = await sessions.GetSessionsAsync().WaitAsync(TimeSpan.FromSeconds(60));

            foreach (var group in list.GroupBy(summary => summary.BlankState)
                                      .OrderBy(group => group.Key))
                output.WriteLine($"{group.Key}: {group.Count()} 个会话");

            var unknown = list.Where(summary => summary.BlankState == SessionBlankState.Unknown).ToArray();
            output.WriteLine($"未知状态会话（元数据缺失，保持可见）：{unknown.Length} 个；"
                           + $"其中带标题投影 {unknown.Count(summary => summary.Title is not null)} 个。");
            Assert.NotEmpty(list);
        }
        finally
        {
            await connection.DisposeAsync();
            await hostService.DisposeAsync();
            try
            {
                Directory.Delete(root, true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    ///     真实 Host 上的只读 projections 协议与历史残留复现（隔离 DSH_HOME，无模型凭据）。
    ///     阶段 1：对真实后端调用 session/projections——全新空白会话返回
    ///     {asOfSeq, values.sessionListMetadata(blank=true)}；接受 prompt 但无 turn/start
    ///     的会话 blank=true 且 lastPromptAt 非空；不存在的会话 result 为 null；
    ///     前后列表数量与会话消息不变（不创建 Agent、不修改会话日志）。
    ///     阶段 2：在隔离 home 内删除两个会话的投影缓存文档（复现 v3 缓存被拒认的历史
    ///     残留形态），同一 home 重启 Host：list 对冷行返回 Unknown；服务层后台核实经
    ///     只读 projections 把它们转为 ConfirmedBlank（无 turn/start 的样本官方折叠仍判空白）。
    /// </summary>
    [SkippableFact]
    public async Task ProjectionsProtocolAndColdCacheMissRoundTripOnRealHost()
    {
        var runtimeDir     = Environment.GetEnvironmentVariable(RealBackendTestSupport.RuntimeDirVariable);
        var node           = RealBackendTestSupport.FindNodeExecutable();
        var launcherScript = RealBackendTestSupport.FindLauncherScript();
        Skip.If(string.IsNullOrWhiteSpace(runtimeDir),
                $"未设置 {RealBackendTestSupport.RuntimeDirVariable}，跳过真实后端 E2E 验证。");
        Skip.If(node is null, "PATH 中找不到 Node 可执行文件，跳过。");
        Skip.If(launcherScript is null, "找不到 launcher 脚本，跳过。");

        var root    = Path.Combine(Path.GetTempPath(), $"dsh-e2e-{Guid.NewGuid():N}");
        var dshHome = Path.Combine(root, "dsh-home");
        Directory.CreateDirectory(root);
        var options = RealBackendTestSupport.BuildOptions(node!, launcherScript!, runtimeDir!, dshHome, root);

        string blankId;
        string promptedId;
        var    promptedStarted = false;
        var    hostService     = new NodeBackendHostService(options);
        var    connection      = new HarnessConnection(hostService.StartAsync);
        var    sessions        = new HarnessSessionService(connection);
        try
        {
            await hostService.StartAsync().WaitAsync(TimeSpan.FromSeconds(150));

            // 两个真实样本：全新空白会话；接受 prompt 的样本（折叠异步，阶段 1 轮询到稳定）。
            blankId    = (await sessions.CreateSessionAsync().WaitAsync(TimeSpan.FromSeconds(30))).Id;
            promptedId = (await sessions.CreateSessionAsync().WaitAsync(TimeSpan.FromSeconds(30))).Id;
            await sessions.SendPromptAsync(promptedId, "request-probe", "只入队不生成：隔离 home 没有模型凭据。")
                          .WaitAsync(TimeSpan.FromSeconds(30));

            // 真实 RPC：session/projections 参数走 args.request（与 runtime 描述符一致）。
            var blankValue = await ReadProjectionsAsync(connection, blankId).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotNull(blankValue);
            var blankMetadata = SessionProjectionsJson.TryParseListMetadata(blankValue!.Values);
            Assert.NotNull(blankMetadata);
            Assert.True(blankMetadata!.Blank);
            output.WriteLine($"空白会话投影：asOfSeq={blankValue.AsOfSeq} blank={blankMetadata.Blank} "
                           + $"lastPromptAt={blankMetadata.LastPromptAt?.ToString() ?? "<null>"}");

            // prompt 折叠与 agent 尝试是异步的：轮询投影到状态稳定。本环境无凭据时
            // prompt 会触发 agent 尝试并立即失败（turn/start 已发生 → blank=false 不可逆）；
            // 若后端行为不同（仅入队）则保持 blank=true，两种都是有效投影，阶段 2 期望随此观测。
            SessionListMetadataWire? promptedMetadata = null;
            var                      settleBy         = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTimeOffset.UtcNow < settleBy)
            {
                var probe = await ReadProjectionsAsync(connection, promptedId).WaitAsync(TimeSpan.FromSeconds(30));
                promptedMetadata = probe is null ? null : SessionProjectionsJson.TryParseListMetadata(probe.Values);
                if (promptedMetadata is { LastPromptAt: not null, Blank: false }) break;

                await Task.Delay(500);
            }

            Assert.NotNull(promptedMetadata);
            Assert.NotNull(promptedMetadata!.LastPromptAt);
            promptedStarted = !promptedMetadata.Blank;
            output.WriteLine($"prompt 样本投影：blank={promptedMetadata.Blank}（已开始={promptedStarted}） "
                           + $"lastPromptAt={promptedMetadata.LastPromptAt}");

            // 只读性基线：等待折叠稳定后取快照，随后重复查询验证数量不变。
            var countBefore    = (await sessions.GetSessionsAsync().WaitAsync(TimeSpan.FromSeconds(30))).Count;
            var messagesBefore = await sessions.GetMessagesAsync(promptedId).WaitAsync(TimeSpan.FromSeconds(30));

            // 会话不存在：result 本身为 null。
            Assert.Null(await ReadProjectionsAsync(connection, "session-does-not-exist")
                           .WaitAsync(TimeSpan.FromSeconds(30)));

            // 只读性：重复查询后列表数量与会话消息不变。
            await ReadProjectionsAsync(connection, blankId).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(countBefore, (await sessions.GetSessionsAsync().WaitAsync(TimeSpan.FromSeconds(30))).Count);
            Assert.Equal(messagesBefore.Count,
                         (await sessions.GetMessagesAsync(promptedId).WaitAsync(TimeSpan.FromSeconds(30))).Count);
        }
        finally
        {
            await connection.DisposeAsync();
            await hostService.DisposeAsync();
        }

        // 阶段 2：删除隔离 home 内两个会话的投影缓存文档（历史残留形态：冷行缓存缺失）。
        var cacheSessionsDir = FindProjectionCacheSessionsDir(dshHome);
        Skip.If(cacheSessionsDir is null, "隔离 home 未找到 session_projcache/sessions 存储目录，跳过阶段 2。");
        foreach (var sessionId in new[] { blankId, promptedId })
        foreach (var document in Directory.GetFiles(cacheSessionsDir!, $"{sessionId}*"))
            File.Delete(document);

        var restartedHost       = new NodeBackendHostService(options);
        var restartedConnection = new HarnessConnection(restartedHost.StartAsync);
        var restartedSessions   = new HarnessSessionService(restartedConnection);
        try
        {
            await restartedHost.StartAsync().WaitAsync(TimeSpan.FromSeconds(150));

            // 同一 home 重启后冷行缓存缺失：wire blank=false 且无 sessionListMetadata → Unknown。
            var coldRows = (await restartedSessions.GetSessionsAsync().WaitAsync(TimeSpan.FromSeconds(60)))
                          .Where(summary => summary.Id == blankId || summary.Id == promptedId)
                          .ToArray();
            Assert.Equal(2, coldRows.Length);
            Assert.All(coldRows, row => Assert.Equal(SessionBlankState.Unknown, row.BlankState));
            output.WriteLine("缓存缺失后冷行（list 原先状态）："
                           + string.Join(", ", coldRows.Select(row => $"{row.Id[..14]}…={row.BlankState}")));

            // 服务层后台核实（只读 projections）：Unknown → 有效空白状态，历史残留被确认并隐藏。
            var targetIds = new HashSet<string>([blankId, promptedId]);
            await RealBackendTestSupport.WaitForAsync(
                                                      async () => (await restartedSessions.GetSessionsAsync())
                                                                         .Where(summary => targetIds.Contains(summary.Id))
                                                                         .All(summary => summary.BlankState !=
                                                                                         SessionBlankState.Unknown),
                                                      TimeSpan.FromSeconds(60), "后台核实未将冷行 Unknown 转为有效状态");

            var verified = (await restartedSessions.GetSessionsAsync().WaitAsync(TimeSpan.FromSeconds(30)))
                          .Where(summary => targetIds.Contains(summary.Id))
                          .ToArray();
            // 核实结论与阶段 1 的投影一致：全新空白 → 确认空白；prompt 已触发 turn/start → 已开始。
            var blankRow    = verified.Single(row => row.Id == blankId);
            var promptedRow = verified.Single(row => row.Id == promptedId);
            Assert.Equal(SessionBlankState.ConfirmedBlank, blankRow.BlankState);
            Assert.Equal(promptedStarted ? SessionBlankState.Engaged : SessionBlankState.ConfirmedBlank,
                         promptedRow.BlankState);
            output.WriteLine("核实后状态：" + string.Join(", ", verified.Select(row => $"{row.Id[..14]}…={row.BlankState}")));
            Assert.False(restartedHost.LastError is { Length: > 0 }, $"后端意外出错：{restartedHost.LastError}");
        }
        finally
        {
            await restartedConnection.DisposeAsync();
            await restartedHost.DisposeAsync();
            try
            {
                Directory.Delete(root, true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Host 建立的链接目录可能短暂占用；留给系统临时目录清理。
            }
        }
    }

    /// <summary>
    ///     真实共享 home 的只读历史核实：对 list 中全部 Unknown 会话逐个调用
    ///     session/projections（只读，不激活 Agent、不写任何存储），只输出聚合计数
    ///     （确认空白 / 已开始 / 不判定 / 失败）与核实前后 Unknown 数量对比，
    ///     不输出会话标题、正文或凭据。需要 DSH_E2E_RUNTIME_DIR 与 DSH_E2E_REAL_HOME=1。
    /// </summary>
    [SkippableFact]
    public async Task RealHomeProjectionsBlankVerificationReadOnly()
    {
        var runtimeDir     = Environment.GetEnvironmentVariable(RealBackendTestSupport.RuntimeDirVariable);
        var realHome       = Environment.GetEnvironmentVariable(RealBackendTestSupport.RealHomeVariable);
        var node           = RealBackendTestSupport.FindNodeExecutable();
        var launcherScript = RealBackendTestSupport.FindLauncherScript();
        Skip.If(string.IsNullOrWhiteSpace(runtimeDir),
                $"未设置 {RealBackendTestSupport.RuntimeDirVariable}，跳过。");
        Skip.If(!string.Equals(realHome, "1", StringComparison.Ordinal),
                $"未设置 {RealBackendTestSupport.RealHomeVariable}=1，跳过真实 home 核实。");
        Skip.If(node is null, "PATH 中找不到 Node 可执行文件，跳过。");
        Skip.If(launcherScript is null, "找不到 launcher 脚本，跳过。");

        var dshHome = RealBackendTestSupport.ResolveSharedDshHome();
        Skip.If(!Directory.Exists(Path.Combine(dshHome, "sessions")), "共享 home 无会话存储，跳过。");

        var root = Path.Combine(Path.GetTempPath(), $"dsh-diag-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var options = RealBackendTestSupport.BuildOptions(node!, launcherScript!, runtimeDir!, dshHome, root);

        var hostService = new NodeBackendHostService(options);
        var connection  = new HarnessConnection(hostService.StartAsync);
        var sessions    = new HarnessSessionService(connection);
        try
        {
            await hostService.StartAsync().WaitAsync(TimeSpan.FromSeconds(150));
            var list = await sessions.GetSessionsAsync().WaitAsync(TimeSpan.FromSeconds(60));
            var unknown = list.Where(summary => summary.BlankState == SessionBlankState.Unknown)
                              .Select(summary => summary.Id)
                              .ToArray();
            output.WriteLine($"核实前：总数 {list.Count}，确认空白 "
                           + $"{list.Count(summary => summary.BlankState     == SessionBlankState.ConfirmedBlank)}，"
                           + $"已开始 {list.Count(summary => summary.BlankState == SessionBlankState.Engaged)}，"
                           + $"未知 {unknown.Length}");

            // 逐个只读核实（顺序执行，不对共享 home 施加并发压力）。
            int confirmedBlank = 0, engaged = 0, inconclusive = 0, failures = 0;
            foreach (var sessionId in unknown)
                try
                {
                    var value = await ReadProjectionsAsync(connection, sessionId).WaitAsync(TimeSpan.FromSeconds(30));
                    var metadata = value is null ? null : SessionProjectionsJson.TryParseListMetadata(value.Values);
                    if (metadata is null) inconclusive++;
                    else if (metadata.Blank) confirmedBlank++;
                    else engaged++;
                }
                catch (Exception)
                {
                    failures++;
                }

            output.WriteLine($"只读核实 {unknown.Length} 个未知会话：确认空白 {confirmedBlank}，"
                           + $"已开始（blank=false）{engaged}，不判定 {inconclusive}，失败 {failures}");
            output.WriteLine($"核实后该批 Unknown 将降为 {inconclusive + failures} 个"
                           + "（结论仅在客户端内存，未写任何存储或缓存）");

            // 只读性：核实后列表数量不变。
            var after = await sessions.GetSessionsAsync().WaitAsync(TimeSpan.FromSeconds(60));
            Assert.Equal(list.Count, after.Count);
        }
        finally
        {
            await connection.DisposeAsync();
            await hostService.DisposeAsync();
            try
            {
                Directory.Delete(root, true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>session/projections 只读调用（真实 Host 协议往返）。</summary>
    private static Task<SessionProjectionsValue?> ReadProjectionsAsync(
        HarnessConnection connection, string sessionId)
    {
        return connection.InvokeAsync("session/projections",
                                      new SessionProjectionsRequest(sessionId),
                                      HarnessJsonContext.Default.SessionProjectionsRequest,
                                      HarnessJsonContext.Default.SessionProjectionsValue,
                                      CancellationToken.None);
    }

    /// <summary>在 home 目录下定位 session_projcache/sessions 存储目录（per-record json 布局）。</summary>
    private static string? FindProjectionCacheSessionsDir(string dshHome)
    {
        return Directory
              .EnumerateDirectories(dshHome, "session_projcache", SearchOption.AllDirectories)
              .Select(directory => Path.Combine(directory, "sessions"))
              .FirstOrDefault(Directory.Exists);
    }
}
