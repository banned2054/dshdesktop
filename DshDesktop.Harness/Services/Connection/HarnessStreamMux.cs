using DshDesktop.Harness.Exceptions;
using DshDesktop.Harness.Json;
using DshDesktop.Harness.Models.Rpc;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;

namespace DshDesktop.Harness.Services.Connection;

/// <summary>下行帧类别。</summary>
public enum MuxFrameKind
{
    Item,
    Error,
    End
}

/// <summary>一条下行帧；Item 的 value 可能为空（undefined）。</summary>
public sealed record MuxServerFrame(
    string       StreamId,
    MuxFrameKind Kind,
    JsonElement? Value,
    string?      ErrorCode,
    string?      ErrorMessage);

/// <summary>
///     /api/remote.mux 流复用客户端：一条 WebSocket 上按 streamId 多路打开
///     session/follow、$events 等流。载波死亡时所有流通道以连接异常完成。
/// </summary>
public sealed class HarnessStreamMux : IAsyncDisposable
{
    private readonly CancellationTokenSource _readCancellation = new();
    private readonly Task                    _readLoop;
    private readonly ClientWebSocket         _socket;

    private readonly ConcurrentDictionary<string, Channel<MuxServerFrame>> _streams = new();

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private volatile bool          _dead;

    private HarnessStreamMux(ClientWebSocket socket)
    {
        _socket   = socket;
        _readLoop = Task.Run(ReadLoopAsync);
    }

    public bool IsDead => _dead;

    public async ValueTask DisposeAsync()
    {
        _readCancellation.Cancel();
        Terminate(new OperationCanceledException("连接正在关闭。"));
        try
        {
            await _readLoop.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 读取循环的退出异常无需上抛。
        }

        _socket.Dispose();
        _writeLock.Dispose();
        _readCancellation.Dispose();
    }

    public event Action<Exception>? Terminated;

    public static async Task<HarnessStreamMux> ConnectAsync(
        Uri baseUrl, CookieContainer cookies, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.Cookies           = cookies;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        var builder = new UriBuilder(baseUrl)
        {
            Scheme = "ws",
            Path   = "/api/remote.mux",
            Query  = null
        };
        try
        {
            await socket.ConnectAsync(builder.Uri, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            socket.Dispose();
            throw new HarnessConnectionException("无法建立到后端的流连接。", exception);
        }

        return new HarnessStreamMux(socket);
    }

    /// <summary>打开一条流并返回其帧读取端；payload 形如 { "args": { ... } }。</summary>
    public async Task<ChannelReader<MuxServerFrame>> OpenStreamAsync(
        string streamId, string endpoint, JsonElement payload, CancellationToken cancellationToken)
    {
        if (_dead) throw new HarnessConnectionException("流连接已断开。");

        var channel =
            Channel.CreateUnbounded<MuxServerFrame>(new UnboundedChannelOptions
                                                        { SingleReader = true, SingleWriter = true });
        if (!_streams.TryAdd(streamId, channel)) throw new InvalidOperationException($"streamId 已在使用：{streamId}");

        try
        {
            await WriteFrameAsync(new MuxOpenMessage("open", streamId, endpoint, payload),
                                  HarnessJsonContext.Default.MuxOpenMessage, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _streams.TryRemove(streamId, out _);
            throw;
        }

        return channel.Reader;
    }

    /// <summary>取消一条流并回收通道；用于正常结束订阅或快照读取完成后。</summary>
    public async ValueTask CancelStreamAsync(string streamId)
    {
        if (!_streams.TryRemove(streamId, out var channel)) return;

        if (!_dead)
            try
            {
                await WriteFrameAsync(new MuxCancelMessage("cancel", streamId),
                                      HarnessJsonContext.Default.MuxCancelMessage, CancellationToken.None)
                   .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException or IOException)
            {
                // 连接已在关闭中，无需发送取消。
            }

        channel.Writer.TryComplete();
    }

    /// <summary>强制终止载波；所有流通道会以连接异常完成。</summary>
    public void Terminate(Exception reason)
    {
        if (_dead) return;

        _dead = true;
        var failure = new HarnessConnectionException("到后端的流连接已断开。", reason);
        foreach (var channel in _streams.Values) channel.Writer.TryComplete(failure);

        _streams.Clear();
        try
        {
            _socket.Abort();
        }
        catch (Exception)
        {
            // 套接字已关闭。
        }

        RaiseTerminated(failure);
    }

    private async Task ReadLoopAsync()
    {
        var receiveBuffer = new byte[64 * 1024];
        var messageBuffer = new ArrayBufferWriter<byte>();
        try
        {
            while (true)
            {
                var result = await _socket.ReceiveAsync(receiveBuffer, _readCancellation.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    throw new
                        HarnessConnectionException($"后端关闭了流连接（{(int)(_socket.CloseStatus ?? WebSocketCloseStatus.Empty)}）。");

                messageBuffer.Write(receiveBuffer.AsSpan(0, result.Count));
                if (!result.EndOfMessage) continue;

                using var document = JsonDocument.Parse(messageBuffer.WrittenMemory);
                Dispatch(document.RootElement);
                messageBuffer.Clear();
            }
        }
        catch (Exception exception)
        {
            Terminate(exception);
        }
    }

    private void Dispatch(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object                    ||
            !root.TryGetProperty("type", out var typeElement)         ||
            typeElement.ValueKind != JsonValueKind.String             ||
            !root.TryGetProperty("streamId", out var streamIdElement) ||
            streamIdElement.ValueKind != JsonValueKind.String)
        {
            // 非法帧按协议应关闭连接。
            Terminate(new HarnessConnectionException("后端发送了无法识别的流帧。"));
            return;
        }

        var            streamId = streamIdElement.GetString() ?? string.Empty;
        MuxServerFrame frame;
        switch (typeElement.GetString())
        {
            case "item" :
            {
                var value = root.TryGetProperty("value", out var valueElement) &&
                            valueElement.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
                    ? (JsonElement?)valueElement.Clone()
                    : null;
                frame = new MuxServerFrame(streamId, MuxFrameKind.Item, value, null, null);
                break;
            }

            case "error" :
            {
                var code = root.TryGetProperty("error", out var error)     &&
                           error.ValueKind == JsonValueKind.Object         &&
                           error.TryGetProperty("code", out var errorCode) &&
                           errorCode.ValueKind == JsonValueKind.String
                    ? errorCode.GetString()
                    : null;
                var message = root.TryGetProperty("error", out var errorMessage)        &&
                              errorMessage.ValueKind == JsonValueKind.Object            &&
                              errorMessage.TryGetProperty("message", out var errorText) &&
                              errorText.ValueKind == JsonValueKind.String
                    ? errorText.GetString()
                    : null;
                frame = new MuxServerFrame(streamId, MuxFrameKind.Error, null, code, message);
                break;
            }

            case "end" :
                frame = new MuxServerFrame(streamId, MuxFrameKind.End, null, null, null);
                break;

            default :
                return;
        }

        if (_streams.TryGetValue(streamId, out var channel))
        {
            channel.Writer.TryWrite(frame);
            if (frame.Kind == MuxFrameKind.End)
            {
                channel.Writer.TryComplete();
                _streams.TryRemove(streamId, out _);
            }
        }
    }

    private async Task WriteFrameAsync<T>(
        T message, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message, typeInfo);
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void RaiseTerminated(Exception failure)
    {
        try
        {
            Terminated?.Invoke(failure);
        }
        catch (Exception)
        {
            // 事件处理器异常不影响连接管理。
        }
    }
}
