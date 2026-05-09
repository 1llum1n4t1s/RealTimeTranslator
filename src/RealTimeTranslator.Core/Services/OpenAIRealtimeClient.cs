using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SuperLightLogger;
using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.Core.Services;

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Failed
}

public sealed class OpenAIRealtimeClient : Interfaces.IOpenAIRealtimeClient
{
    private static readonly ILog Logger = LogManager.GetLogger<OpenAIRealtimeClient>();

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _sendTask;
    private Channel<byte[]>? _sendChannel;
    private OpenAIRealtimeSettings _settings = new();
    private int _reconnectAttempts;
    private int _disposed;
    private volatile bool _shouldReconnect;
    private volatile ConnectionState _state = ConnectionState.Disconnected;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    public ConnectionState State => _state;

    public event Action<string>? TranscriptDeltaReceived;
    public event Action<string>? TranscriptCompleted;
    public event Action<Exception>? ErrorReceived;
    public event Action<ConnectionState>? StateChanged;

    public async Task ConnectAsync(OpenAIRealtimeSettings settings, CancellationToken ct = default)
    {
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State == ConnectionState.Connected)
            {
                Logger.Warn("既に接続済み。先に切断します。");
                await CleanupAsync().ConfigureAwait(false);
            }

            _settings = settings;
            _shouldReconnect = true;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _sendChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(200)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true
            });

            await ConnectWebSocketAsync(_cts.Token).ConfigureAwait(false);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public void SendAudio(byte[] pcm16Audio)
    {
        if (pcm16Audio is null || pcm16Audio.Length == 0) return;
        if (State != ConnectionState.Connected) return;
        _sendChannel?.Writer.TryWrite(pcm16Audio);
    }

    public async Task DisconnectAsync()
    {
        _shouldReconnect = false;
        await _connectLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await CleanupAsync().ConfigureAwait(false);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
        DisconnectAsync().GetAwaiter().GetResult();
        _cts?.Dispose();
        _connectLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
        await DisconnectAsync().ConfigureAwait(false);
        _cts?.Dispose();
        _connectLock.Dispose();
    }

    public static async Task<(bool Success, string Message)> TestConnectionAsync(
        OpenAIRealtimeSettings settings, CancellationToken ct = default)
    {
        var uri = new Uri($"{settings.Endpoint}?model={Uri.EscapeDataString(settings.Model)}");

        if (uri.Scheme != "wss")
            return (false, $"セキュアでない WebSocket スキーム '{uri.Scheme}' は許可されていません。Endpoint には wss:// を使用してください。");

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", $"Bearer {settings.ApiKey}");

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            await ws.ConnectAsync(uri, timeoutCts.Token).ConfigureAwait(false);

            var buffer = new byte[4096];
            var result = await ws.ReceiveAsync(buffer, timeoutCts.Token).ConfigureAwait(false);
            var json = Encoding.UTF8.GetString(buffer, 0, result.Count);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            string message;
            if (type == "session.created")
            {
                message = "接続成功！APIキーは有効です。";
            }
            else if (type == "error")
            {
                var errorMsg = "不明なエラー";
                if (root.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var msg))
                    errorMsg = msg.GetString() ?? errorMsg;
                return (false, $"APIエラー: {errorMsg}");
            }
            else
            {
                message = "接続成功";
            }

            try
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, closeCts.Token)
                    .ConfigureAwait(false);
            }
            catch { /* best effort close */ }

            return (true, message);
        }
        catch (WebSocketException ex) when (ex.InnerException is HttpRequestException httpEx)
        {
            var msg = httpEx.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => "認証失敗: APIキーが無効です。",
                System.Net.HttpStatusCode.Forbidden => "アクセス拒否: このAPIにアクセスする権限がありません。",
                System.Net.HttpStatusCode.TooManyRequests => "レート制限に達しています。しばらく待ってください。",
                _ => $"接続エラー: HTTP {(int?)httpEx.StatusCode}"
            };
            return (false, msg);
        }
        catch (OperationCanceledException)
        {
            return (false, "タイムアウト: サーバーからの応答がありません。");
        }
        catch (Exception ex)
        {
            return (false, $"接続エラー: {ex.Message}");
        }
    }

    private async Task CleanupAsync()
    {
        var cts = _cts;
        _cts = null;
        cts?.Cancel();
        _sendChannel?.Writer.TryComplete();

        var tasks = new List<Task>(2);
        if (_receiveTask != null) tasks.Add(_receiveTask);
        if (_sendTask != null) tasks.Add(_sendTask);

        if (tasks.Count > 0)
        {
            try
            {
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Logger.Warn("送受信ループ停止がタイムアウト");
            }
            catch { /* best effort */ }
        }

        _receiveTask = null;
        _sendTask = null;

        if (_ws is { State: WebSocketState.Open or WebSocketState.CloseReceived })
        {
            try
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, closeCts.Token)
                    .ConfigureAwait(false);
            }
            catch { /* best effort */ }
        }

        _ws?.Dispose();
        _ws = null;
        cts?.Dispose();
        SetState(ConnectionState.Disconnected);
    }

    private async Task ConnectWebSocketAsync(CancellationToken ct)
    {
        SetState(ConnectionState.Connecting);
        _ws?.Dispose();
        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {_settings.ApiKey}");

        var uri = new Uri($"{_settings.Endpoint}?model={Uri.EscapeDataString(_settings.Model)}");

        if (uri.Scheme != "wss")
            throw new InvalidOperationException($"セキュアでない WebSocket スキーム '{uri.Scheme}' は許可されていません。Endpoint には wss:// を使用してください。");

        try
        {
            await _ws.ConnectAsync(uri, ct).ConfigureAwait(false);
            _reconnectAttempts = 0;
            SetState(ConnectionState.Connected);
            Logger.Info("OpenAI Realtime WebSocket 接続成功");

            await SendSessionUpdateAsync(ct).ConfigureAwait(false);

            _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts!.Token), _cts!.Token);
            _sendTask = Task.Run(() => SendLoopAsync(_cts!.Token), _cts!.Token);
        }
        catch (WebSocketException ex) when (ex.InnerException is HttpRequestException httpEx)
        {
            var statusCode = httpEx.StatusCode;
            var isFatal = statusCode is System.Net.HttpStatusCode.Unauthorized
                or System.Net.HttpStatusCode.Forbidden;
            var friendlyMsg = statusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized =>
                    "認証失敗（401）: APIキーが無効です。設定画面で正しいキーを入力してください。",
                System.Net.HttpStatusCode.Forbidden =>
                    "アクセス拒否（403）: このAPIにアクセスする権限がありません。",
                System.Net.HttpStatusCode.TooManyRequests =>
                    "レート制限（429）: しばらく待ってから再試行してください。",
                _ => $"WebSocket接続エラー: HTTP {(int?)statusCode} {statusCode}"
            };
            Logger.Error(friendlyMsg, ex);
            ErrorReceived?.Invoke(new InvalidOperationException(friendlyMsg, ex));
            SetState(isFatal ? ConnectionState.Failed : ConnectionState.Disconnected);
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error("WebSocket 接続失敗", ex);
            ErrorReceived?.Invoke(ex);
            SetState(ConnectionState.Disconnected);
            throw;
        }
    }

    private async Task SendSessionUpdateAsync(CancellationToken ct)
    {
        var sessionUpdate = new
        {
            type = "session.update",
            session = new
            {
                audio = new
                {
                    input = new
                    {
                        noise_reduction = new { type = "far_field" }
                    },
                    output = new { language = _settings.OutputLanguage }
                }
            }
        };

        var json = JsonSerializer.Serialize(sessionUpdate);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws!.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var audioData in _sendChannel!.Reader.ReadAllAsync(ct))
            {
                if (_ws is not { State: WebSocketState.Open }) continue;

                var message = new
                {
                    type = "input_audio_buffer.append",
                    audio = Convert.ToBase64String(audioData)
                };

                var json = JsonSerializer.Serialize(message);
                var bytes = Encoding.UTF8.GetBytes(json);
                await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Error("WebSocket 送信ループエラー", ex);
            ErrorReceived?.Invoke(ex);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[65536];
        using var messageStream = new MemoryStream();

        try
        {
            while (!ct.IsCancellationRequested && _ws is { State: WebSocketState.Open })
            {
                var result = await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Logger.Warn("WebSocket サーバーからクローズ受信");
                    break;
                }

                messageStream.Write(buffer, 0, result.Count);

                if (!result.EndOfMessage) continue;

                var json = Encoding.UTF8.GetString(messageStream.GetBuffer(), 0, (int)messageStream.Length);
                messageStream.SetLength(0);
                ProcessMessage(json);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            Logger.Warn("WebSocket 受信エラー", ex);
        }
        catch (Exception ex)
        {
            Logger.Error("WebSocket 受信ループ予期しないエラー", ex);
            ErrorReceived?.Invoke(ex);
        }

        if (!ct.IsCancellationRequested)
            _ = Task.Run(() => TryReconnectAsync(), CancellationToken.None);
    }

    private void ProcessMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeElement)) return;
            var type = typeElement.GetString();

            switch (type)
            {
                case "session.output_transcript.delta":
                case "response.audio_transcript.delta":
                case "output_transcript.delta":
                    if (root.TryGetProperty("delta", out var delta))
                        TranscriptDeltaReceived?.Invoke(delta.GetString() ?? "");
                    break;

                case "session.output_transcript.done":
                case "response.audio_transcript.done":
                case "output_transcript.done":
                    var transcript = "";
                    if (root.TryGetProperty("transcript", out var t))
                        transcript = t.GetString() ?? "";
                    TranscriptCompleted?.Invoke(transcript);
                    break;

                case "error":
                    var errorMsg = "Unknown error";
                    if (root.TryGetProperty("error", out var err))
                    {
                        if (err.TryGetProperty("message", out var msg))
                            errorMsg = msg.GetString() ?? errorMsg;

                        if (err.TryGetProperty("code", out var code))
                        {
                            var codeStr = code.GetString() ?? "";
                            if (codeStr == "rate_limit_exceeded")
                                errorMsg = "レート制限に達しました。しばらく待ってから再試行してください。";
                            else if (codeStr == "invalid_api_key")
                                errorMsg = "APIキーが無効です。設定画面で確認してください。";
                        }
                    }
                    Logger.Error($"OpenAI API エラー: {errorMsg}");
                    ErrorReceived?.Invoke(new InvalidOperationException(errorMsg));
                    break;
            }
        }
        catch (JsonException ex)
        {
            Logger.Warn("JSON パースエラー", ex);
        }
    }

    private async Task TryReconnectAsync()
    {
        if (!_shouldReconnect) return;

        _reconnectAttempts++;
        if (_reconnectAttempts > _settings.MaxReconnectAttempts)
        {
            Logger.Error($"再接続上限 ({_settings.MaxReconnectAttempts}) 到達");
            _shouldReconnect = false;
            SetState(ConnectionState.Failed);
            ErrorReceived?.Invoke(new InvalidOperationException(
                $"再接続の上限（{_settings.MaxReconnectAttempts}回）に達しました。接続を確認してください。"));
            return;
        }

        SetState(ConnectionState.Reconnecting);
        var shift = Math.Min(_reconnectAttempts - 1, 30);
        var delay = (int)Math.Min((long)_settings.ReconnectDelayMs << shift, 30000L);
        Logger.Info($"再接続試行 {_reconnectAttempts}/{_settings.MaxReconnectAttempts}（{delay}ms 後）");

        try
        {
            await Task.Delay(delay).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }

        if (!_shouldReconnect) return;

        await _connectLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_shouldReconnect) return;
            if (_state == ConnectionState.Connected) return;

            await CleanupAsync().ConfigureAwait(false);

            _cts = new CancellationTokenSource();
            _sendChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(200)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true
            });

            await ConnectWebSocketAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Warn("再接続失敗", ex);
        }
        finally
        {
            _connectLock.Release();
        }

        if (_shouldReconnect
            && _state != ConnectionState.Connected
            && _state != ConnectionState.Failed)
        {
            _ = Task.Run(() => TryReconnectAsync(), CancellationToken.None);
        }
    }

    private void SetState(ConnectionState state)
    {
        _state = state;
        StateChanged?.Invoke(state);
    }
}
