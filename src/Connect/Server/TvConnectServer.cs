using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using QmTui.Connect.Models;
using QmTui.Connect.Storage;
using QmTui.Models;
using QmTui.Utils;

namespace QmTui.Connect.Server;

public sealed class TvConnectServer : IDisposable
{
    private readonly ConnectStorage _storage;
    private readonly int _port;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private sealed class ClientSession : IDisposable
    {
        public WebSocket Socket { get; }
        public ConnectDevice? Device { get; set; }
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private bool _disposed;

        public ClientSession(WebSocket socket, ConnectDevice? device = null)
        {
            Socket = socket;
            Device = device;
        }

        public async Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
        {
            if (Socket.State != WebSocketState.Open || _disposed) return;
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (Socket.State != WebSocketState.Open || _disposed) return;
                await Socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public async Task SendSafeAsync(ReadOnlyMemory<byte> bytes)
        {
            try
            {
                await SendAsync(bytes).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("TvConnectServer", $"Safe send failed: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _sendLock.Dispose();
        }
    }

    private readonly ConcurrentDictionary<WebSocket, ClientSession> _activeClients = new();

    public int Port => _port;
    public int ActualPort { get; private set; } = 8765;
    public bool IsRunning => _listener?.IsListening ?? false;
    public int ConnectedCount => _activeClients.Count;

    // 事件通知
    public event Action<ConnectDevice>? DeviceConnected;
    public event Action<ConnectDevice>? DeviceDisconnected;
    public event Action<ConnectDevice, string, Action<bool>>? PairRequested;
    public event Action? PlayerStateRequested;
    public event Action? QueueStateRequested;
    public event Action<PlaySongCommand>? PlaySongRequested;
    public event Action<EnqueueNextCommand>? EnqueueNextRequested;
    public event Action? PauseRequested;
    public event Action? ResumeRequested;
    public event Action? PreviousRequested;
    public event Action? NextRequested;
    public event Action<long>? SeekRequested;
    public event Action<float>? SetVolumeRequested;
    public event Action<string>? SwitchTierRequested;
    public event Action<string?>? CycleLoopModeRequested;
    public event Action? TriggerAodRequested;
    public event Action? OpenPlayerRequested;
    public event Action<ToggleFavoriteCommand>? ToggleFavoriteRequested;
    public event Action<LyricsScrollPayload>? SyncLyricsScrollRequested;
    public event Action<LyricsSyncPayload>? SyncLyricsRequested;

    // HTTP 静态资源反向提供
    public Func<string?>? CurrentCoverPathProvider { get; set; }
    public Func<string, Task<string?>>? CoverPathByMidProvider { get; set; }
    public Func<string?>? CurrentLyricsTextProvider { get; set; }

    public TvConnectServer(ConnectStorage storage, int port = 8765)
    {
        _storage = storage;
        _port = port;
        ActualPort = port;
    }

    private static int FindAvailablePort(int startPort)
    {
        for (int candidate = startPort; candidate <= startPort + 10; candidate++)
        {
            try
            {
                var listener = new System.Net.Sockets.TcpListener(IPAddress.Any, candidate);
                listener.Start();
                listener.Stop();
                return candidate;
            }
            catch
            {
            }
        }
        return startPort;
    }

    public void Start()
    {
        if (_listener != null && _listener.IsListening) return;

        _cts = new CancellationTokenSource();
        int initialCandidate = FindAvailablePort(_port);

        for (int candidate = initialCandidate; candidate <= _port + 10; candidate++)
        {
            HttpListener? testListener = null;
            try
            {
                testListener = new HttpListener();
                testListener.Prefixes.Add($"http://*:{candidate}/");
                testListener.Start();
                _listener = testListener;
                ActualPort = candidate;
                AppLogger.Info("TvConnectServer", $"Melodist Connect WebSocket Server listening on port {ActualPort}");
                break;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("TvConnectServer", $"Could not bind to http://*:{candidate}/: {ex.Message}");
                try { testListener?.Close(); } catch { }

                try
                {
                    testListener = new HttpListener();
                    testListener.Prefixes.Add($"http://+:{candidate}/");
                    testListener.Start();
                    _listener = testListener;
                    ActualPort = candidate;
                    AppLogger.Info("TvConnectServer", $"Melodist Connect WebSocket Server listening on port {ActualPort} via prefix +");
                    break;
                }
                catch (Exception ex2)
                {
                    AppLogger.Warn("TvConnectServer", $"Could not bind to http://+:{candidate}/: {ex2.Message}");
                    try { testListener?.Close(); } catch { }
                }
            }
        }

        if (_listener == null || !_listener.IsListening)
        {
            AppLogger.Error("TvConnectServer", "Failed to start HttpListener on any port in range 8765-8775", null);
            return;
        }

        Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested && _listener.IsListening)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                    if (ctx.Request.IsWebSocketRequest)
                    {
                        _ = ProcessWebSocketRequestAsync(ctx);
                    }
                    else
                    {
                        _ = ProcessHttpRequestAsync(ctx);
                    }
                }
                catch (HttpListenerException) when (_cts.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("TvConnectServer", $"Error accepting HTTP context: {ex.Message}");
                }
            }
        }, _cts.Token);
    }

    private async Task ProcessWebSocketRequestAsync(HttpListenerContext ctx)
    {
        WebSocketContext wsCtx;
        try
        {
            wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLogger.Error("TvConnectServer", "Failed to accept WebSocket", ex);
            ctx.Response.StatusCode = 500;
            ctx.Response.Close();
            return;
        }

        var socket = wsCtx.WebSocket;
        var session = new ClientSession(socket);
        _activeClients[socket] = session;
        AppLogger.Info("TvConnectServer", $"WebSocket client connected from {ctx.Request.RemoteEndPoint}");

        var buffer = new byte[8192];
        var ms = new MemoryStream();

        try
        {
            while (socket.State == WebSocketState.Open && !_cts!.IsCancellationRequested)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None).ConfigureAwait(false);
                        break;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var text = Encoding.UTF8.GetString(ms.ToArray());
                    await HandleIncomingMessageAsync(socket, text).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("TvConnectServer", $"WebSocket connection error: {ex.Message}");
        }
        finally
        {
            if (_activeClients.TryRemove(socket, out var disconnectedSession))
            {
                if (disconnectedSession.Device != null)
                {
                    DeviceDisconnected?.Invoke(disconnectedSession.Device);
                }
                disconnectedSession.Dispose();
            }
            socket.Dispose();
            AppLogger.Info("TvConnectServer", "WebSocket client disconnected");
        }
    }

    private async Task HandleIncomingMessageAsync(WebSocket socket, string text)
    {
        ConnectMessage? msg = null;
        try
        {
            msg = JsonSerializer.Deserialize(text, ConnectJsonContext.Default.ConnectMessage);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("TvConnectServer", $"Invalid JSON payload: {ex.Message}");
            return;
        }

        if (msg == null) return;
        AppLogger.Info("TvConnectServer", $"Received connect action: {msg.Action}");

        switch (msg.Action)
        {
            case ConnectActions.Ping:
                await SendMessageAsync(socket, ConnectActions.Pong, "").ConfigureAwait(false);
                break;

            case ConnectActions.ReqGetPlayerState:
                PlayerStateRequested?.Invoke();
                break;

            case ConnectActions.ReqGetQueueState:
                QueueStateRequested?.Invoke();
                break;

            case ConnectActions.PairRequest:
                try
                {
                    var req = msg.DecodeData(ConnectJsonContext.Default.PairRequestPayload);
                    if (req != null)
                    {
                        bool isTrusted = _storage.IsDeviceTrusted(req.Device.Id, req.Device.Token) ||
                                         (!string.IsNullOrEmpty(req.PinCode) && req.PinCode == _storage.CurrentPinCode);

                        if (isTrusted)
                        {
                            _storage.SavePairedDevice(req.Device);
                            if (_activeClients.TryGetValue(socket, out var session))
                            {
                                session.Device = req.Device;
                            }
                            var local = _storage.GetLocalDevice(port: ActualPort);
                            var resp = new PairResponsePayload(
                                Accepted: true,
                                Message: "Paired successfully",
                                Device: local
                            );
                            await SendMessageAsync(socket, ConnectActions.PairResponse, resp, ConnectJsonContext.Default.PairResponsePayload).ConfigureAwait(false);
                            DeviceConnected?.Invoke(req.Device);
                            PlayerStateRequested?.Invoke();
                            QueueStateRequested?.Invoke();
                        }
                        else
                        {
                            // 校验失败或等待用户批准
                            PairRequested?.Invoke(req.Device, req.PinCode, async accept =>
                            {
                                if (accept)
                                {
                                    _storage.SavePairedDevice(req.Device);
                                    if (_activeClients.TryGetValue(socket, out var session))
                                    {
                                        session.Device = req.Device;
                                    }
                                    var local = _storage.GetLocalDevice(port: ActualPort);
                                    var resp = new PairResponsePayload(
                                        Accepted: true,
                                        Message: "Paired successfully",
                                        Device: local
                                    );
                                    await SendMessageAsync(socket, ConnectActions.PairResponse, resp, ConnectJsonContext.Default.PairResponsePayload).ConfigureAwait(false);
                                    DeviceConnected?.Invoke(req.Device);
                                    PlayerStateRequested?.Invoke();
                                    QueueStateRequested?.Invoke();
                                }
                                else
                                {
                                    var resp = new PairResponsePayload(Accepted: false, Message: "Pairing rejected by QMTUI user");
                                    await SendMessageAsync(socket, ConnectActions.PairResponse, resp, ConnectJsonContext.Default.PairResponsePayload).ConfigureAwait(false);
                                }
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error("TvConnectServer", "Error handling PairRequest", ex);
                }
                break;

            case ConnectActions.Disconnect:
                if (_activeClients.TryRemove(socket, out var disconnectedSession))
                {
                    if (disconnectedSession.Device != null)
                    {
                        DeviceDisconnected?.Invoke(disconnectedSession.Device);
                    }
                    disconnectedSession.Dispose();
                }
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnect", CancellationToken.None).ConfigureAwait(false); } catch { }
                break;

            case ConnectActions.CmdPlaySong:
                try
                {
                    var cmd = msg.DecodeData(ConnectJsonContext.Default.PlaySongCommand);
                    if (cmd != null) PlaySongRequested?.Invoke(cmd);
                }
                catch { }
                break;

            case ConnectActions.CmdEnqueueNext:
                try
                {
                    var cmd = msg.DecodeData(ConnectJsonContext.Default.EnqueueNextCommand);
                    if (cmd != null) EnqueueNextRequested?.Invoke(cmd);
                }
                catch { }
                break;

            case ConnectActions.CmdPause:
                PauseRequested?.Invoke();
                break;

            case ConnectActions.CmdResume:
                ResumeRequested?.Invoke();
                break;

            case ConnectActions.CmdPrevious:
                PreviousRequested?.Invoke();
                break;

            case ConnectActions.CmdNext:
                NextRequested?.Invoke();
                break;

            case ConnectActions.CmdSeek:
                try
                {
                    var cmd = msg.DecodeData(ConnectJsonContext.Default.SeekCommand);
                    if (cmd != null) SeekRequested?.Invoke(cmd.PositionMs);
                }
                catch { }
                break;

            case ConnectActions.CmdSetVolume:
                try
                {
                    var cmd = msg.DecodeData(ConnectJsonContext.Default.SetVolumeCommand);
                    if (cmd != null) SetVolumeRequested?.Invoke(cmd.Volume);
                }
                catch { }
                break;

            case ConnectActions.CmdSwitchTier:
                try
                {
                    var cmd = msg.DecodeData(ConnectJsonContext.Default.SwitchTierCommand);
                    if (cmd != null) SwitchTierRequested?.Invoke(cmd.Tier);
                }
                catch { }
                break;

            case ConnectActions.CmdTriggerAod:
                TriggerAodRequested?.Invoke();
                break;

            case ConnectActions.CmdOpenPlayer:
                OpenPlayerRequested?.Invoke();
                break;

            case ConnectActions.CmdCycleLoopMode:
                CycleLoopModeRequested?.Invoke(string.IsNullOrWhiteSpace(msg.Payload) ? null : msg.Payload);
                break;

            case ConnectActions.CmdGestureSwipe:
                // 手势滑动仅用于大屏封面视差与过渡联动，切歌由移动端统一在手势结算时下发 cmd_next / cmd_prev 控制。
                // 若在此处执行切歌，会导致手势滑动与后续的 cmd_next/cmd_prev 双重触发，连续跳过两首歌曲。
                break;

            case ConnectActions.CmdToggleFavorite:
                try
                {
                    var cmd = msg.DecodeData(ConnectJsonContext.Default.ToggleFavoriteCommand);
                    if (cmd != null) ToggleFavoriteRequested?.Invoke(cmd);
                }
                catch { }
                break;

            case ConnectActions.CmdSyncLyricsScroll:
                try
                {
                    var cmd = msg.DecodeData(ConnectJsonContext.Default.LyricsScrollPayload);
                    if (cmd != null) SyncLyricsScrollRequested?.Invoke(cmd);
                }
                catch { }
                break;

            case ConnectActions.CmdSyncLyrics:
                try
                {
                    var payload = msg.DecodeData(ConnectJsonContext.Default.LyricsSyncPayload);
                    if (payload != null) SyncLyricsRequested?.Invoke(payload);
                }
                catch { }
                break;
        }
    }

    public void BroadcastPlayerState(PlayerStateEvent evt)
    {
        try
        {
            Broadcast(ConnectActions.EventPlayState, evt, ConnectJsonContext.Default.PlayerStateEvent);
        }
        catch (Exception ex)
        {
            AppLogger.Error("TvConnectServer", "BroadcastPlayerState error", ex);
        }
    }

    public void BroadcastQueueState(QueueStateEvent evt)
    {
        try
        {
            Broadcast(ConnectActions.EventQueueState, evt, ConnectJsonContext.Default.QueueStateEvent);
        }
        catch (Exception ex)
        {
            AppLogger.Error("TvConnectServer", "BroadcastQueueState error", ex);
        }
    }

    public void BroadcastLyrics(LyricsSyncPayload payload)
    {
        try
        {
            Broadcast(ConnectActions.EventSyncLyrics, payload, ConnectJsonContext.Default.LyricsSyncPayload);
            AppLogger.Info("TvConnectServer", $"Broadcasted lyrics for {payload.Title} ({payload.SongMid}, {payload.Lyrics?.Count ?? 0} lines) to {_activeClients.Count} clients");
        }
        catch (Exception ex)
        {
            AppLogger.Error("TvConnectServer", "BroadcastLyrics error", ex);
        }
    }

    public void BroadcastPlaySong(ConnectSong song)
    {
        try
        {
            Broadcast(ConnectActions.CmdPlaySong, song, ConnectJsonContext.Default.ConnectSong);
        }
        catch (Exception ex)
        {
            AppLogger.Error("TvConnectServer", "BroadcastPlaySong error", ex);
        }
    }

    public void BroadcastNext()
    {
        BroadcastAction(ConnectActions.CmdNext);
    }

    public void BroadcastPrevious()
    {
        BroadcastAction(ConnectActions.CmdPrevious);
    }

    private void BroadcastAction(string action)
    {
        var msg = ConnectMessage.Create(action, "");
        var json = JsonSerializer.Serialize(msg, ConnectJsonContext.Default.ConnectMessage);
        var bytes = Encoding.UTF8.GetBytes(json);

        foreach (var session in _activeClients.Values)
        {
            if (session.Socket.State == WebSocketState.Open)
            {
                _ = session.SendSafeAsync(bytes);
            }
        }
    }

    private void Broadcast<T>(string action, T data, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo)
    {
        var msg = ConnectMessage.Create(action, data, jsonTypeInfo);
        var json = JsonSerializer.Serialize(msg, ConnectJsonContext.Default.ConnectMessage);
        var bytes = Encoding.UTF8.GetBytes(json);

        foreach (var session in _activeClients.Values)
        {
            if (session.Socket.State == WebSocketState.Open)
            {
                _ = session.SendSafeAsync(bytes);
            }
        }
    }

    private async Task SendMessageAsync<T>(WebSocket socket, string action, T data, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo)
    {
        if (socket.State != WebSocketState.Open) return;
        if (_activeClients.TryGetValue(socket, out var session))
        {
            var msg = ConnectMessage.Create(action, data, jsonTypeInfo);
            var json = JsonSerializer.Serialize(msg, ConnectJsonContext.Default.ConnectMessage);
            var bytes = Encoding.UTF8.GetBytes(json);
            await session.SendAsync(bytes).ConfigureAwait(false);
        }
    }

    private async Task SendMessageAsync(WebSocket socket, string action, string payload = "")
    {
        if (socket.State != WebSocketState.Open) return;
        if (_activeClients.TryGetValue(socket, out var session))
        {
            var msg = ConnectMessage.Create(action, payload);
            var json = JsonSerializer.Serialize(msg, ConnectJsonContext.Default.ConnectMessage);
            var bytes = Encoding.UTF8.GetBytes(json);
            await session.SendAsync(bytes).ConfigureAwait(false);
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch { }
        _listener = null;
        foreach (var session in _activeClients.Values)
        {
            session.Dispose();
        }
        _activeClients.Clear();
    }

    private async Task ProcessHttpRequestAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath?.ToLowerInvariant() ?? "";
            var isHead = string.Equals(ctx.Request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase);

            if (path.StartsWith("/cover"))
            {
                string? coverPath = null;
                var mid = ctx.Request.QueryString["mid"];
                var localCoverPath = ctx.Request.QueryString["path"];
                var coverName = ctx.Request.QueryString["name"];

                // 支持 /cover?name=<cached_cover_filename>
                if (!string.IsNullOrEmpty(coverName))
                {
                    try
                    {
                        var safeName = Path.GetFileName(coverName);
                        var cachedPath = Path.Combine(QmTui.Services.CacheManager.CoversDir, safeName);
                        if (File.Exists(cachedPath))
                        {
                            coverPath = cachedPath;
                        }
                    }
                    catch { }
                }

                // 优先支持 /cover/local?path=<url_encoded_path>
                if (string.IsNullOrEmpty(coverPath) && !string.IsNullOrEmpty(localCoverPath))
                {
                    try
                    {
                        var unescaped = Uri.UnescapeDataString(localCoverPath);
                        if (File.Exists(unescaped))
                        {
                            coverPath = unescaped;
                        }
                    }
                    catch { }
                }

                if (string.IsNullOrEmpty(coverPath) && !string.IsNullOrEmpty(mid) && CoverPathByMidProvider != null)
                {
                    try
                    {
                        coverPath = await CoverPathByMidProvider(mid).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warn("TvConnectServer", $"CoverPathByMidProvider failed for {mid}: {ex.Message}");
                    }
                }

                // 若指定了 mid 但未找到，或者未指定 mid 的 /cover/current，尝试回退当前正在播放的封面
                if (string.IsNullOrEmpty(coverPath) && (path.Contains("current") || string.IsNullOrEmpty(mid)))
                {
                    for (int i = 0; i < 20; i++)
                    {
                        coverPath = CurrentCoverPathProvider?.Invoke();
                        if (!string.IsNullOrEmpty(coverPath) && File.Exists(coverPath))
                        {
                            break;
                        }
                        await Task.Delay(100).ConfigureAwait(false);
                    }
                }

                if (!string.IsNullOrEmpty(coverPath) && File.Exists(coverPath))
                {
                    var ext = Path.GetExtension(coverPath).ToLowerInvariant();
                    ctx.Response.ContentType = ext switch
                    {
                        ".png" => "image/png",
                        ".webp" => "image/webp",
                        _ => "image/jpeg"
                    };
                    ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
                    ctx.Response.Headers.Add("Accept-Ranges", "bytes");
                    ctx.Response.Headers.Add("Cache-Control", "public, max-age=86400");
                    ctx.Response.StatusCode = (int)HttpStatusCode.OK;

                    var fi = new FileInfo(coverPath);
                    ctx.Response.ContentLength64 = fi.Length;

                    if (!isHead)
                    {
                        using var fs = File.OpenRead(coverPath);
                        await fs.CopyToAsync(ctx.Response.OutputStream).ConfigureAwait(false);
                    }
                    ctx.Response.Close();
                    AppLogger.Info("TvConnectServer", $"Served cover for {path}?mid={mid} ({fi.Length} bytes)");
                    return;
                }
                else
                {
                    ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    ctx.Response.Close();
                    return;
                }
            }
            else if (path.StartsWith("/stream/local"))
            {
                var localAudioPath = ctx.Request.QueryString["path"];
                if (string.IsNullOrEmpty(localAudioPath))
                {
                    ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    ctx.Response.Close();
                    return;
                }

                string? decodedPath = null;
                try
                {
                    decodedPath = Uri.UnescapeDataString(localAudioPath);
                }
                catch { }

                if (string.IsNullOrEmpty(decodedPath) || !File.Exists(decodedPath))
                {
                    ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    ctx.Response.Close();
                    return;
                }

                await ServeLocalAudioStreamAsync(ctx, decodedPath, isHead).ConfigureAwait(false);
                return;
            }
            else if (path.StartsWith("/lyrics/"))
            {
                var lyricsText = CurrentLyricsTextProvider?.Invoke();
                if (!string.IsNullOrEmpty(lyricsText))
                {
                    var bytes = Encoding.UTF8.GetBytes(lyricsText);
                    ctx.Response.ContentType = "text/plain; charset=utf-8";
                    ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
                    ctx.Response.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");
                    ctx.Response.StatusCode = (int)HttpStatusCode.OK;
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                    ctx.Response.Close();
                    return;
                }
            }

            ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
            ctx.Response.Close();
        }
        catch (Exception ex)
        {
            AppLogger.Debug("TvConnectServer", $"ProcessHttpRequest error: {ex.Message}");
            try
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                ctx.Response.Close();
            }
            catch { }
        }
    }

    private static async Task ServeLocalAudioStreamAsync(HttpListenerContext ctx, string filePath, bool isHead)
    {
        var fi = new FileInfo(filePath);
        long totalLength = fi.Length;
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var contentType = ext switch
        {
            ".flac" => "audio/flac",
            ".wav" => "audio/wav",
            ".ogg" or ".oga" => "audio/ogg",
            ".m4a" or ".aac" or ".mp4" => "audio/mp4",
            ".opus" => "audio/opus",
            _ => "audio/mpeg"
        };

        var rangeHeader = ctx.Request.Headers["Range"];
        long start = 0;
        long end = totalLength - 1;
        bool isRange = false;

        if (!string.IsNullOrWhiteSpace(rangeHeader) && rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            var rangeVal = rangeHeader["bytes=".Length..].Trim();
            if (rangeVal.StartsWith('-'))
            {
                if (long.TryParse(rangeVal[1..], out var suffix))
                {
                    start = Math.Max(0, totalLength - suffix);
                    end = totalLength - 1;
                    isRange = true;
                }
            }
            else
            {
                var parts = rangeVal.Split('-');
                if (long.TryParse(parts[0], out var s))
                {
                    start = s;
                    isRange = true;
                }
                if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) && long.TryParse(parts[1], out var e))
                {
                    end = Math.Min(e, totalLength - 1);
                    isRange = true;
                }
            }
        }

        if (isRange && (start > end || start >= totalLength || start < 0))
        {
            ctx.Response.StatusCode = 416;
            ctx.Response.Headers.Add("Content-Range", $"bytes */{totalLength}");
            ctx.Response.Close();
            return;
        }

        long contentLength = end - start + 1;
        ctx.Response.StatusCode = isRange ? (int)HttpStatusCode.PartialContent : (int)HttpStatusCode.OK;
        ctx.Response.ContentType = contentType;
        ctx.Response.Headers.Add("Accept-Ranges", "bytes");
        ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
        if (isRange)
        {
            ctx.Response.Headers.Add("Content-Range", $"bytes {start}-{end}/{totalLength}");
        }
        ctx.Response.ContentLength64 = contentLength;

        if (isHead)
        {
            ctx.Response.Close();
            return;
        }

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        if (start > 0)
        {
            fs.Seek(start, SeekOrigin.Begin);
        }

        var buffer = new byte[64 * 1024];
        long remaining = contentLength;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = await fs.ReadAsync(buffer.AsMemory(0, toRead)).ConfigureAwait(false);
            if (read <= 0) break;
            await ctx.Response.OutputStream.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            remaining -= read;
        }
        ctx.Response.Close();
    }

    public void Dispose()
    {
        Stop();
    }
}
