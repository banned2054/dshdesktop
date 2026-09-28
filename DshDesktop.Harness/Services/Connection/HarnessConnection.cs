using DshDesktop.Core.Models;
using DshDesktop.Harness.Exceptions;
using DshDesktop.Harness.Json;
using DshDesktop.Harness.Models.Events;
using DshDesktop.Harness.Models.Requests;
using DshDesktop.Harness.Models.Rpc;
using DshDesktop.Harness.Utils;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;

namespace DshDesktop.Harness.Services.Connection;

/// <summary>
///     与 Harness 后端的业务连接：令牌认证、一元 RPC、$events 事件代与流订阅。
///     载波断开按退避自动重建连接代；重连后重新下发快照、事件层提示重读状态，
///     绝不重发任何非幂等的用户操作。
/// </summary>
public sealed class HarnessConnection(Func<CancellationToken, Task<BackendConnectionInfo>> backend)
    : IAsyncDisposable
{
    private const           int      UnaryRetryAttempts     = 3;
    private static readonly TimeSpan GenerationReadyTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan BackoffBase            = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan BackoffMax             = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RpcTimeout             = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly Random        _jitter      = new();
    private readonly Lock          _sync        = new();
    private          bool          _disposed;
    private          string        _eventClientId = string.Empty;
    private          Task?         _eventsLoop;
    private          Task          _generationReady = Task.CompletedTask;

    private HttpClient?       _http;
    private HarnessStreamMux? _mux;
    private Task?             _reconnectLoop;

    public bool IsConnected
    {
        get
        {
            lock (_sync)
            {
                return _mux is { IsDead: false };
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task?             eventsLoop;
        HttpClient?       http;
        HarnessStreamMux? mux;
        lock (_sync)
        {
            if (_disposed) return;

            _disposed   = true;
            eventsLoop  = _eventsLoop;
            _eventsLoop = null;
            http        = _http;
            _http       = null;
            mux         = _mux;
            _mux        = null;
        }

        if (mux is not null) await mux.DisposeAsync().ConfigureAwait(false);

        if (eventsLoop is not null)
            try
            {
                await eventsLoop.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 关闭期的事件循环异常无需上抛。
            }

        http?.Dispose();
    }

    /// <summary>新的连接代就绪；从线缆派生的缓存（如会话列表）应重新读取。</summary>
    public event EventHandler? ConnectionReset;

    /// <summary>后端上报了会话活动（api-session/* 事件）。</summary>
    public event EventHandler? SessionActivity;

    /// <summary>
    ///     后端上报了可归属到具体会话的活动（api-session/*，含 added/removed/status/activity/error）。
    ///     无法从载荷归属会话的事件只触发 <see cref="SessionActivity" />。
    /// </summary>
    public event EventHandler<SessionActivityNotice>? SessionActivityAddressed;

    /// <summary>approval/request 瀑布到达；等待用户裁决（由 IToolApprovalService 消费）。</summary>
    public event EventHandler<RemoteEventFrame.Waterfall>? ApprovalRequested;

    /// <summary>后端取消了先前下达的瀑布请求（eventId）。</summary>
    public event EventHandler<string>? WaterfallCancelled;

    /// <summary>新事件代就绪；旧代的未决瀑布已随代失效，待决列表应清空。</summary>
    public event EventHandler? EventGenerationReset;

    /// <summary>确保连接代可用；并发调用单飞合并。</summary>
    public async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (IsConnected) return;

        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected || _disposed) return;

            var isReconnect = CurrentMux() is not null;
            await TeardownCurrentAsync().ConfigureAwait(false);

            var info    = await backend(cancellationToken).ConfigureAwait(false);
            var cookies = new CookieContainer();
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies        = true,
                CookieContainer   = cookies
            };
            var http = new HttpClient(handler)
            {
                BaseAddress = info.BaseUrl,
                Timeout     = RpcTimeout
            };
            HarnessStreamMux? mux        = null;
            Task?             eventsLoop = null;
            var               committed  = false;
            try
            {
                await HarnessAuth.AuthenticateAsync(http, info.AuthenticatedUri, cancellationToken)
                                 .ConfigureAwait(false);

                mux = await HarnessStreamMux.ConnectAsync(info.BaseUrl, cookies, cancellationToken)
                                            .ConfigureAwait(false);
                mux.Terminated += OnMuxTerminated;
                var ready = NewCompletion();
                eventsLoop = RunEventsLoopAsync(mux, http, ready);
                await ready.Task.WaitAsync(GenerationReadyTimeout, cancellationToken).ConfigureAwait(false);

                lock (_sync)
                {
                    _http            = http;
                    _mux             = mux;
                    _eventsLoop      = eventsLoop;
                    _generationReady = Task.CompletedTask;
                }

                committed = true;
            }
            finally
            {
                if (!committed)
                {
                    try
                    {
                        if (mux is not null)
                        {
                            mux.Terminated -= OnMuxTerminated;
                            await mux.DisposeAsync().ConfigureAwait(false);
                        }

                        if (eventsLoop is not null)
                            try
                            {
                                await eventsLoop.ConfigureAwait(false);
                            }
                            catch (Exception)
                            {
                                // Failed connection setup is reported by the original exception.
                            }
                    }
                    finally
                    {
                        http.Dispose();
                    }
                }
            }

            if (isReconnect) RaiseConnectionReset();
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>一元 RPC；只读调用在载波故障时按退避重试。</summary>
    public async Task<TValue> InvokeAsync<TValue, TRequest>(
        string                 method,
        TRequest               request,
        JsonTypeInfo<TRequest> requestType,
        JsonTypeInfo<TValue>   valueType,
        CancellationToken      cancellationToken,
        string                 argName = "request")
        where TRequest : notnull
    {
        for (var attempt = 0;; attempt++)
            try
            {
                await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                var http = CurrentHttp()
                        ?? throw new HarnessConnectionException("连接尚未建立。");
                var rpc = new HarnessRpcClient(http);
                return await rpc.InvokeAsync(method, request, requestType, valueType, cancellationToken, argName)
                                .ConfigureAwait(false);
            }
            catch (HarnessConnectionException) when (attempt < UnaryRetryAttempts - 1)
            {
                await Task.Delay(NextBackoff(attempt), cancellationToken).ConfigureAwait(false);
            }
    }

    /// <summary>调用无参一元方法（如 session/modelCatalog）；载波故障时同样按退避重试。</summary>
    public async Task<TValue> InvokeEmptyAsync<TValue>(
        string method, JsonTypeInfo<TValue> valueType, CancellationToken cancellationToken)
    {
        for (var attempt = 0;; attempt++)
            try
            {
                await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                var http = CurrentHttp()
                        ?? throw new HarnessConnectionException("连接尚未建立。");
                var rpc = new HarnessRpcClient(http);
                return await rpc.InvokeEmptyAsync(method, valueType, cancellationToken)
                                .ConfigureAwait(false);
            }
            catch (HarnessConnectionException) when (attempt < UnaryRetryAttempts - 1)
            {
                await Task.Delay(NextBackoff(attempt), cancellationToken).ConfigureAwait(false);
            }
    }

    /// <summary>
    ///     订阅会话流。载波错误由实现恢复：重建连接代后重新打开流，
    ///     消费者会先收到新的 Snapshot（整窗替换语义）。
    ///     业务错误（HarnessRpcException）是终态，直接抛出。
    /// </summary>
    public IAsyncEnumerable<FollowFrame> FollowAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        return FollowStreamAsync("session/follow", BuildFollowPayload(sessionId), FollowFrameJson.Parse,
                                 cancellationToken);
    }

    /// <summary>
    ///     订阅工作区状态流（workspace/follow，无请求参数）。恢复语义与会话流一致：
    ///     重连后重新收到 Baseline，消费者整体替换。
    /// </summary>
    public IAsyncEnumerable<WorkspaceFollowFrame> FollowWorkspacesAsync(CancellationToken cancellationToken = default)
    {
        return FollowStreamAsync("workspace/follow", BuildEmptyArgsPayload(), WorkspaceFrameJson.Parse,
                                 cancellationToken);
    }

    /// <summary>
    ///     订阅 Host 级会话控制状态流（session/control，无请求参数）。每代以 Baseline 开场，
    ///     之后为按会话的 projection 整值更新；重连后重新收到 Baseline。
    /// </summary>
    public IAsyncEnumerable<SessionControlFrame> FollowSessionControlAsync(
        CancellationToken cancellationToken = default)
    {
        return FollowStreamAsync("session/control", BuildEmptyArgsPayload(), SessionControlFrameJson.Parse,
                                 cancellationToken);
    }

    /// <summary>订阅任一复用流：开流、逐帧解析写入通道，载波故障按退避重开。</summary>
    private IAsyncEnumerable<TFrame> FollowStreamAsync<TFrame>(
        string            endpoint, JsonElement payload, Func<JsonElement, TFrame?> parse,
        CancellationToken cancellationToken)
        where TFrame : class
    {
        var channel = Channel.CreateUnbounded<TFrame>();
        _ = PumpStreamAsync(endpoint, payload, parse, channel.Writer, cancellationToken);
        return ReadChannelAsync(channel.Reader, cancellationToken);
    }

    /// <summary>读取一次会话快照（打开 follow、等待 snapshot、取消订阅）。</summary>
    public async Task<FollowFrame.Snapshot?> TakeSnapshotAsync(string sessionId, CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var mux      = CurrentMux() ?? throw new HarnessConnectionException("连接尚未建立。");
        var streamId = WireIds.NewStreamId();
        var reader = await mux
                          .OpenStreamAsync(streamId, "session/follow", BuildFollowPayload(sessionId), cancellationToken)
                          .ConfigureAwait(false);
        try
        {
            while (true)
            {
                MuxServerFrame frame;
                try
                {
                    frame = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (ChannelClosedException exception)
                {
                    throw new HarnessConnectionException("会话流通道已关闭。", exception);
                }

                switch (frame.Kind)
                {
                    case MuxFrameKind.Item when frame.Value is not null &&
                                                FollowFrameJson.Parse(frame.Value.Value) is FollowFrame.Snapshot
                                                    snapshot :
                        return snapshot;

                    case MuxFrameKind.Item :
                        continue;

                    case MuxFrameKind.Error :
                        throw new HarnessRpcException(frame.ErrorCode    ?? "gateway/unknown",
                                                      frame.ErrorMessage ?? "会话流错误。");

                    case MuxFrameKind.End :
                        return null;
                }
            }
        }
        finally
        {
            await mux.CancelStreamAsync(streamId).ConfigureAwait(false);
        }
    }

    private async Task PumpStreamAsync<TFrame>(string endpoint, JsonElement payload, Func<JsonElement, TFrame?> parse,
                                               ChannelWriter<TFrame> writer, CancellationToken cancellationToken)
        where TFrame : class
    {
        var attempt = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await PumpStreamOnceAsync(endpoint, payload, parse, writer, cancellationToken)
                       .ConfigureAwait(false);
                    break; // 流正常 End
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (HarnessConnectionException)
                {
                    // 载波错误：等待新代并按退避重开。
                }

                await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(NextBackoff(attempt++), cancellationToken).ConfigureAwait(false);
            }

            writer.TryComplete();
        }
        catch (Exception exception)
        {
            writer.TryComplete(exception);
        }
    }

    private async Task PumpStreamOnceAsync<TFrame>(string                     endpoint, JsonElement           payload,
                                                   Func<JsonElement, TFrame?> parse,    ChannelWriter<TFrame> writer,
                                                   CancellationToken          cancellationToken) where TFrame : class
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var mux      = CurrentMux() ?? throw new HarnessConnectionException("连接尚未建立。");
        var streamId = WireIds.NewStreamId();
        var reader   = await mux.OpenStreamAsync(streamId, endpoint, payload, cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                MuxServerFrame frame;
                try
                {
                    frame = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (ChannelClosedException exception)
                {
                    throw new HarnessConnectionException("复用流通道已关闭。", exception);
                }

                if (frame.Kind == MuxFrameKind.Error)
                    throw new HarnessRpcException(frame.ErrorCode ?? "gateway/unknown", frame.ErrorMessage ?? "复用流错误。");

                if (frame.Kind == MuxFrameKind.End) return;

                if (frame.Kind == MuxFrameKind.Item
                 && frame.Value is not null
                 && parse(frame.Value.Value) is { } parsed)
                    await writer.WriteAsync(parsed, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await mux.CancelStreamAsync(streamId).ConfigureAwait(false);
        }
    }

    private async IAsyncEnumerable<TFrame> ReadChannelAsync<TFrame>(ChannelReader<TFrame> reader,
                                                                    [EnumeratorCancellation]
                                                                    CancellationToken cancellationToken)
        where TFrame : class
    {
        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        while (reader.TryRead(out var frame))
            yield return frame;
    }

    private async Task RunEventsLoopAsync(HarnessStreamMux mux, HttpClient http, TaskCompletionSource ready)
    {
        var streamId = WireIds.NewStreamId();

        ChannelReader<MuxServerFrame> reader;
        try
        {
            reader = await mux.OpenStreamAsync(streamId, "$events", BuildEmptyArgsPayload(), CancellationToken.None)
                              .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ready.TrySetException(new HarnessConnectionException("无法打开事件流。", exception));
            mux.Terminate(exception);
            return;
        }

        var clientId = string.Empty;
        try
        {
            while (true)
            {
                var frame = await reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
                if (frame.Kind == MuxFrameKind.End) throw new HarnessConnectionException("后端关闭了事件流。");

                if (frame.Kind == MuxFrameKind.Error)
                    throw new HarnessRpcException(frame.ErrorCode ?? "gateway/unknown", frame.ErrorMessage ?? "事件流错误。");

                if (frame.Kind != MuxFrameKind.Item || frame.Value is null) continue;

                switch (RemoteEventJson.Parse(frame.Value.Value))
                {
                    case RemoteEventFrame.Ready generationReady :
                        clientId = generationReady.ClientId;
                        lock (_sync)
                        {
                            _eventClientId = clientId;
                        }

                        ready.TrySetResult();
                        RaiseEventGenerationReset();
                        break;

                    case RemoteEventFrame.Emit emit
                        when emit.Event.StartsWith("api-session/", StringComparison.Ordinal) :
                        RaiseSessionActivity();
                        if (RemoteEventJson.TryGetSessionId(emit, out var activitySessionId))
                            RaiseSessionActivityAddressed(
                                new SessionActivityNotice(emit.Event, activitySessionId));
                        break;

                    case RemoteEventFrame.Waterfall waterfall
                        when waterfall.Event == RemoteEventJson.ApprovalRequestEvent :
                        // 审批走交互闭环：交给待决列表等待用户裁决，不再自动拒绝。
                        RaiseApprovalRequested(waterfall);
                        break;

                    case RemoteEventFrame.Waterfall waterfall :
                        _ = ReplyWaterfallRejectedAsync(http, clientId, waterfall);
                        break;

                    case RemoteEventFrame.Cancelled cancelled :
                        RaiseWaterfallCancelled(cancelled.EventId);
                        break;
                }
            }
        }
        catch (Exception exception)
        {
            ready.TrySetException(new HarnessConnectionException("事件流终止。", exception));
            mux.Terminate(exception);
        }
        finally
        {
            await mux.CancelStreamAsync(streamId).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     回复一次瀑布裁决（$events/result，kind=result；approval 取值 allowed-once/rejected）。
    ///     幂等：eventId 与当前代或未决请求失配时由后端对账忽略，调用方可安全重试。
    /// </summary>
    public async Task ReplyWaterfallResultAsync(
        string eventId, bool allowed, CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var http     = CurrentHttp() ?? throw new HarnessConnectionException("连接尚未建立。");
        var clientId = CurrentEventClientId();
        var rpc      = new HarnessRpcClient(http);
        await rpc.InvokeAsync("$events/result",
                              new EventsResultRequest(clientId, eventId,
                                                      new EventsOutcomeWire("result",
                                                                            allowed
                                                                                ? "allowed-once"
                                                                                : "rejected")),
                              HarnessJsonContext.Default.EventsResultRequest,
                              HarnessJsonContext.Default.SessionAcceptedValue, cancellationToken)
                 .ConfigureAwait(false);
    }

    /// <summary>审批之外的瀑布请求（用户问题等）暂无交互界面：按协议回执拒绝，避免悬挂。</summary>
    private static async Task ReplyWaterfallRejectedAsync(
        HttpClient http, string clientId, RemoteEventFrame.Waterfall waterfall)
    {
        try
        {
            var rpc = new HarnessRpcClient(http);
            await rpc.InvokeAsync("$events/result",
                                  new EventsResultRequest(clientId, waterfall.EventId,
                                                          new EventsOutcomeWire("rejected",
                                                                                    Error : new
                                                                                        EventsOutcomeErrorWire("Error",
                                                                                            "桌面客户端暂不支持该交互。"))),
                                  HarnessJsonContext.Default.EventsResultRequest,
                                  HarnessJsonContext.Default.SessionAcceptedValue, CancellationToken.None)
                     .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 回执失败由服务端自身的请求超时兜底。
        }
    }

    private void OnMuxTerminated(Exception failure)
    {
        Task? running;
        lock (_sync)
        {
            if (_disposed) return;

            running = _reconnectLoop is { IsCompleted: false } ? _reconnectLoop : null;
        }

        if (running is not null)
        {
            lock (_sync)
            {
                _generationReady = running;
            }

            return;
        }

        var loop = ReconnectLoopAsync();
        lock (_sync)
        {
            _reconnectLoop   = loop;
            _generationReady = loop;
        }
    }

    private async Task ReconnectLoopAsync()
    {
        var attempt = 0;
        while (true)
        {
            lock (_sync)
            {
                if (_disposed) return;
            }

            await Task.Delay(NextBackoff(attempt++)).ConfigureAwait(false);
            try
            {
                await EnsureConnectedAsync(CancellationToken.None).ConfigureAwait(false);
                return;
            }
            catch (Exception)
            {
                // 继续退避重试，直到成功或连接被释放。
            }
        }
    }

    private async Task TeardownCurrentAsync()
    {
        Task?             eventsLoop;
        HttpClient?       http;
        HarnessStreamMux? mux;
        lock (_sync)
        {
            eventsLoop  = _eventsLoop;
            _eventsLoop = null;
            http        = _http;
            _http       = null;
            mux         = _mux;
            _mux        = null;
        }

        if (mux is not null) await mux.DisposeAsync().ConfigureAwait(false);

        if (eventsLoop is not null)
            try
            {
                await eventsLoop.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 旧代的退出异常无需上抛。
            }

        http?.Dispose();
    }

    private HttpClient? CurrentHttp()
    {
        lock (_sync)
        {
            return _http;
        }
    }

    private HarnessStreamMux? CurrentMux()
    {
        lock (_sync)
        {
            return _mux;
        }
    }

    private static JsonElement BuildFollowPayload(string sessionId)
    {
        var request = new SessionFollowRequest(new SessionAddress(sessionId), AssistantStream : true);
        var requestElement =
            JsonSerializer.SerializeToElement(request, HarnessJsonContext.Default.SessionFollowRequest);
        return JsonSerializer.SerializeToElement(new StreamPayloadWire(new Dictionary<string, JsonElement>
                                                                           { ["request"] = requestElement }),
                                                 HarnessJsonContext.Default.StreamPayloadWire);
    }

    private static JsonElement BuildEmptyArgsPayload()
    {
        return JsonSerializer.SerializeToElement(new StreamPayloadWire([]),
                                                 HarnessJsonContext.Default.StreamPayloadWire);
    }

    private TimeSpan NextBackoff(int attempt)
    {
        var exponential = Math.Min(BackoffBase.TotalMilliseconds * Math.Pow(2, Math.Max(0, attempt)),
                                   BackoffMax.TotalMilliseconds);
        double factor;
        lock (_sync)
        {
            factor = 0.5 + _jitter.NextDouble() * 0.5;
        }

        return TimeSpan.FromMilliseconds(exponential * factor);
    }

    private void RaiseConnectionReset()
    {
        try
        {
            ConnectionReset?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            // 事件处理器异常不影响连接管理。
        }
    }

    private void RaiseSessionActivity()
    {
        try
        {
            SessionActivity?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            // 事件处理器异常不影响连接管理。
        }
    }

    private void RaiseSessionActivityAddressed(SessionActivityNotice notice)
    {
        try
        {
            SessionActivityAddressed?.Invoke(this, notice);
        }
        catch (Exception)
        {
            // 事件处理器异常不影响连接管理。
        }
    }

    private void RaiseApprovalRequested(RemoteEventFrame.Waterfall waterfall)
    {
        try
        {
            ApprovalRequested?.Invoke(this, waterfall);
        }
        catch (Exception)
        {
            // 事件处理器异常不影响连接管理。
        }
    }

    private void RaiseWaterfallCancelled(string eventId)
    {
        try
        {
            WaterfallCancelled?.Invoke(this, eventId);
        }
        catch (Exception)
        {
            // 事件处理器异常不影响连接管理。
        }
    }

    private void RaiseEventGenerationReset()
    {
        try
        {
            EventGenerationReset?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
            // 事件处理器异常不影响连接管理。
        }
    }

    private string CurrentEventClientId()
    {
        lock (_sync)
        {
            return _eventClientId;
        }
    }

    private static TaskCompletionSource NewCompletion()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
