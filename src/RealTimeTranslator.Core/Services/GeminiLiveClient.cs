using System.Buffers;
using System.Net.NetworkInformation;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SuperLightLogger;
using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.Core.Services;

/// <summary>
/// Google Gemini Live Translate API (gemini-3.5-live-translate-preview) の Realtime クライアント。
/// <see cref="OpenAIRealtimeClient"/> を参照実装に、 WebSocket 再接続 / KeepAlive / Channel 送信 /
/// 統計 / 診断ログの構造を流用しつつ、 プロトコルを Gemini の BidiGenerateContent に置き換えている。
///
/// OpenAI との主な差分:
///  - 認証: URL query <c>?key=</c> (Authorization ヘッダではない)
///  - 入力音声: <b>16kHz</b>/PCM16/mono 固定 (OpenAI の 24kHz と異なる → <see cref="InputSampleRate"/>=16000)
///  - 接続後に <c>setup</c> メッセージで model + translationConfig を送る
///  - 音声は <c>realtimeInput.audio</c> (base64 + mimeType) で送る
///  - 受信は <c>serverContent.outputTranscription.text</c> を翻訳テキストとして拾う (出力音声は破棄)
///
/// ⚠️ プレビュー API のため、 setup/受信 JSON 形は実機で要検証 (translationConfig の階層、
/// outputTranscription が増分か累積か等)。 字幕分割は OpenAI 同様 TranslationPipelineService 側に委ねる
/// (本クライアントは outputTranscription.text を「常に delta」として <see cref="TranscriptDeltaReceived"/> に流す)。
/// </summary>
public sealed class GeminiLiveClient : Interfaces.IRealtimeTranscriber
{
    private static readonly ILog Logger = LogManager.GetLogger<GeminiLiveClient>();

    // 接続を許可する Gemini のホスト。 settings 改竄で任意の wss:// に API キーを送る経路を塞ぐ。
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "generativelanguage.googleapis.com",
    };

    private static void ValidateEndpoint(Uri uri)
    {
        if (uri.Scheme != "wss")
            throw new InvalidOperationException(
                $"セキュアでない WebSocket スキーム '{uri.Scheme}' は許可されていません。Endpoint には wss:// を使用してください。");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException(
                "Endpoint に user-info (user:pass@) を含めることはできません。");
        if (!AllowedHosts.Contains(uri.IdnHost))
            throw new InvalidOperationException(
                $"Endpoint のホスト '{uri.IdnHost}' は許可リスト外です（generativelanguage.googleapis.com のみ）。");
    }

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _sendTask;
    private const int SendChannelCapacity = 30;
    private Channel<byte[]>? _sendChannel;
    private GeminiLiveSettings _settings = new();
    private int _reconnectAttempts;
    private int _disposed;
    private int _reconnectInFlight;
    private long _totalDroppedAudioChunks;
    private volatile bool _shouldReconnect;
    private volatile ConnectionState _state = ConnectionState.Disconnected;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly SemaphoreSlim _wsSendLock = new(1, 1);

    private readonly HashSet<string> _seenEventTypes = new(StringComparer.Ordinal);
    private readonly object _seenEventTypesLock = new();
    private const int MaxSeenEventTypes = 256;

    private long _totalDeltaCount;

    // ⚠️ Gemini は 16kHz 送信。 OpenAI 互換の「24kHz 換算サンプル数」を返すため ×1.5 する
    // (コスト/経過時間の表示が provider 非依存で正しい秒数になる)。 内部は 16k サンプル数で貯める。
    private long _totalAudioInputSamples16kHz;

    public ConnectionState State => _state;

    /// <inheritdoc />
    public int InputSampleRate => 16000;

    /// <inheritdoc />
    public long DroppedAudioChunkCount => Interlocked.Read(ref _totalDroppedAudioChunks);

    /// <inheritdoc />
    // 16k サンプル数 → 24k 換算 (×1.5)。 CostEstimator/時間表示は 24kHz 前提なので合わせる。
    public long TotalAudioInputSamples24kHz => (long)(Interlocked.Read(ref _totalAudioInputSamples16kHz) * 1.5);

    /// <inheritdoc />
    // Gemini は audio input tokens の usage 報告形式が未確認。 取れないので 0 のまま
    // (TranslationPipelineService 側が送信秒数からの fallback 推定に倒れる)。
    public long ServerReportedAudioInputTokens => 0;

    private readonly Interfaces.IDebugAudioRecorder? _debugAudioRecorder;

    public GeminiLiveClient(Interfaces.IDebugAudioRecorder? debugAudioRecorder = null)
    {
        _debugAudioRecorder = debugAudioRecorder;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (Interlocked.CompareExchange(ref _disposed, 0, 0) != 0) return;
        if (!e.IsAvailable) return;
        if (!_shouldReconnect) return;
        if (_state == ConnectionState.Connected) return;

        Logger.Info("ネットワーク復帰検知: 再接続カウンタをリセットして再接続を試みます (Gemini)");
        Interlocked.Exchange(ref _reconnectAttempts, 0);
        if (_state == ConnectionState.Failed)
        {
            SetState(ConnectionState.Reconnecting);
        }
        _ = Task.Run(() => TryReconnectAsync(), CancellationToken.None);
    }

    public event Action<string>? TranscriptDeltaReceived;
    public event Action<string>? TranscriptCompleted;
    public event Action<Exception>? ErrorReceived;
    public event Action<ConnectionState>? StateChanged;

    /// <summary>
    /// Gemini 固有設定で接続する。 TranslationPipelineService はこの具象メソッドを直接呼ぶ
    /// (Provider=Gemini のとき)。 interface 経由 (<see cref="Interfaces.IRealtimeTranscriber.ConnectAsync"/>)
    /// は防御的に OpenAIRealtimeSettings からマップして本メソッドへ委譲する。
    /// </summary>
    public async Task ConnectAsync(GeminiLiveSettings settings, CancellationToken ct = default)
    {
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State == ConnectionState.Connected)
            {
                Logger.Warn("既に接続済み。先に切断します。(Gemini)");
                await CleanupAsync().ConfigureAwait(false);
            }

            _settings = settings;
            _shouldReconnect = true;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _sendChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(SendChannelCapacity)
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

    /// <summary>
    /// interface 契約 (OpenAIRealtimeSettings) を満たすための明示実装。 通常は Pipeline が
    /// 具象 <see cref="ConnectAsync(GeminiLiveSettings, CancellationToken)"/> を呼ぶため使われないが、
    /// IRealtimeTranscriber 経由でも最低限接続できるよう、 取れる範囲をマップする (echo は既定 true)。
    /// </summary>
    async Task Interfaces.IRealtimeTranscriber.ConnectAsync(OpenAIRealtimeSettings settings, CancellationToken ct)
    {
        var mapped = new GeminiLiveSettings
        {
            ApiKey = settings.ApiKey,
            OutputLanguage = settings.OutputLanguage,
            Model = string.IsNullOrWhiteSpace(settings.Model) ? new GeminiLiveSettings().Model : settings.Model,
            Endpoint = new GeminiLiveSettings().Endpoint,
            ReconnectDelayMs = settings.ReconnectDelayMs,
            MaxReconnectAttempts = settings.MaxReconnectAttempts,
        };
        await ConnectAsync(mapped, ct).ConfigureAwait(false);
    }

    public void SendAudio(byte[] pcm16Audio)
    {
        if (pcm16Audio is null || pcm16Audio.Length == 0) return;
        if (State != ConnectionState.Connected) return;

        _debugAudioRecorder?.WritePcm16(pcm16Audio);

        var channel = _sendChannel;
        if (channel is null) return;
        var reader = channel.Reader;
        var writer = channel.Writer;

        bool wasFull = reader.Count >= SendChannelCapacity;
        if (!writer.TryWrite(pcm16Audio))
        {
            Interlocked.Increment(ref _totalDroppedAudioChunks);
        }
        else if (wasFull)
        {
            Interlocked.Increment(ref _totalDroppedAudioChunks);
        }
    }

    public async Task DisconnectAsync()
    {
        _shouldReconnect = false;
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { }

        if (!await _connectLock.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false))
        {
            Logger.Warn("DisconnectAsync: _connectLock 取得が 3 秒でタイムアウト、強制クリーンアップに進む (Gemini)");
            try { await CleanupAsync().ConfigureAwait(false); }
            catch (Exception ex) { Logger.Warn("DisconnectAsync(timeout): CleanupAsync で例外 (Gemini)", ex); }
            return;
        }
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
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        DisconnectAsync().GetAwaiter().GetResult();
        _cts?.Dispose();
        _connectLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        await DisconnectAsync().ConfigureAwait(false);
        _cts?.Dispose();
        _connectLock.Dispose();
    }

    public static async Task<(bool Success, string Message)> TestConnectionAsync(
        GeminiLiveSettings settings, CancellationToken ct = default)
    {
        Uri uri;
        try
        {
            uri = new Uri($"{settings.Endpoint}?key={Uri.EscapeDataString(settings.ApiKey)}");
            ValidateEndpoint(uri);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UriFormatException)
        {
            return (false, ex.Message);
        }

        if (string.IsNullOrWhiteSpace(settings.ApiKey))
            return (false, "Gemini APIキーが設定されていません。");

        using var ws = new ClientWebSocket();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            await ws.ConnectAsync(uri, timeoutCts.Token).ConfigureAwait(false);

            // setup を送って setupComplete が返れば API キー有効。
            var setupBytes = BuildSetupMessage(settings);
            await ws.SendAsync(setupBytes, WebSocketMessageType.Text, true, timeoutCts.Token).ConfigureAwait(false);

            var buffer = new byte[8192];
            var result = await ws.ReceiveAsync(buffer, timeoutCts.Token).ConfigureAwait(false);
            var json = Encoding.UTF8.GetString(buffer, 0, result.Count);

            using var doc = JsonDocument.Parse(json, s_jsonDocumentOptions);
            var root = doc.RootElement;

            string message;
            if (root.TryGetProperty("setupComplete", out _))
            {
                message = "接続成功！Gemini APIキーは有効です。";
            }
            else if (root.TryGetProperty("error", out var err))
            {
                var errorMsg = err.TryGetProperty("message", out var m) ? m.GetString() : "不明なエラー";
                return (false, $"APIエラー: {errorMsg}");
            }
            else
            {
                message = "接続成功 (setup 応答を受信)";
            }

            try
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, closeCts.Token).ConfigureAwait(false);
            }
            catch { }

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
                Logger.Warn("送受信ループ停止がタイムアウト (Gemini)");
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
            {
                Logger.Debug($"送受信ループ停止中の想定内例外 (Gemini): {ex.GetType().Name}: {ex.Message}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"送受信ループ停止中の想定外例外 (Gemini): {ex.GetType().Name}: {ex.Message}");
            }
        }

        _receiveTask = null;
        _sendTask = null;

        if (_ws is { State: WebSocketState.Open or WebSocketState.CloseReceived })
        {
            try
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, closeCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
            {
                Logger.Debug($"WebSocket.CloseAsync 想定内例外 (Gemini): {ex.GetType().Name}: {ex.Message}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"WebSocket.CloseAsync 想定外例外 (Gemini): {ex.GetType().Name}: {ex.Message}");
            }
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
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        _ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(30);

        // Gemini は API キーを URL query ?key= で渡す (Authorization ヘッダではない)。
        var uri = new Uri($"{_settings.Endpoint}?key={Uri.EscapeDataString(_settings.ApiKey)}");
        ValidateEndpoint(uri);

        try
        {
            await _ws.ConnectAsync(uri, ct).ConfigureAwait(false);
            Interlocked.Exchange(ref _reconnectAttempts, 0);
            SetState(ConnectionState.Connected);
            Logger.Info("Gemini Live WebSocket 接続成功");

            await SendSetupAsync(ct).ConfigureAwait(false);

            _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts!.Token), _cts!.Token);
            _sendTask = Task.Run(() => SendLoopAsync(_cts!.Token), _cts!.Token);
        }
        catch (WebSocketException ex) when (ex.InnerException is HttpRequestException httpEx)
        {
            var statusCode = httpEx.StatusCode;
            var kind = statusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => OpenAIApiErrorKind.InvalidApiKey,
                System.Net.HttpStatusCode.Forbidden => OpenAIApiErrorKind.Forbidden,
                System.Net.HttpStatusCode.TooManyRequests => OpenAIApiErrorKind.RateLimit,
                _ => OpenAIApiErrorKind.Unknown,
            };
            var friendlyMsg = statusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized =>
                    "認証失敗（401）: Gemini APIキーが無効です。設定画面で正しいキーを入力してください。",
                System.Net.HttpStatusCode.Forbidden =>
                    "アクセス拒否（403）: このAPIにアクセスする権限がありません。",
                System.Net.HttpStatusCode.TooManyRequests =>
                    "レート制限（429）: しばらく待ってから再試行してください。",
                _ => $"WebSocket接続エラー: HTTP {(int?)statusCode} {statusCode}"
            };
            Logger.Error(friendlyMsg, ex);
            if (kind != OpenAIApiErrorKind.Unknown)
            {
                var apiEx = new OpenAIApiException(kind, friendlyMsg, ex.Message);
                ErrorReceived?.Invoke(apiEx);
                SetState(apiEx.IsFatal ? ConnectionState.Failed : ConnectionState.Disconnected);
                throw apiEx;
            }
            ErrorReceived?.Invoke(new InvalidOperationException(friendlyMsg, ex));
            SetState(ConnectionState.Disconnected);
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error("Gemini WebSocket 接続失敗", ex);
            ErrorReceived?.Invoke(ex);
            SetState(ConnectionState.Disconnected);
            throw;
        }
    }

    private async Task SendSetupAsync(CancellationToken ct)
    {
        var bytes = BuildSetupMessage(_settings);
        Logger.Info($"Gemini Live setup 送信: model='{_settings.Model}' targetLang='{ResolveLang(_settings.OutputLanguage)}' echo={_settings.EchoTargetLanguage}");
        await _ws!.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    private static string ResolveLang(string? lang)
        => string.IsNullOrWhiteSpace(lang) ? "ja" : lang;

    // setup メッセージを組み立てる。
    // ⚠️ プレビュー API のため translationConfig の階層 (generationConfig 内) は実機要検証。
    private static byte[] BuildSetupMessage(GeminiLiveSettings settings)
    {
        var setup = new
        {
            setup = new
            {
                model = settings.Model,
                generationConfig = new
                {
                    responseModalities = new[] { "AUDIO" },
                    inputAudioTranscription = new { },
                    outputAudioTranscription = new { },
                    translationConfig = new
                    {
                        targetLanguageCode = ResolveLang(settings.OutputLanguage),
                        echoTargetLanguage = settings.EchoTargetLanguage,
                    },
                },
            },
        };
        return JsonSerializer.SerializeToUtf8Bytes(setup);
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        var bufferWriter = new ArrayBufferWriter<byte>(initialCapacity: 8192);
        var jsonWriter = new Utf8JsonWriter(bufferWriter, new JsonWriterOptions { SkipValidation = true });
        var audioBatch = new ArrayBufferWriter<byte>(initialCapacity: 16384);

        try
        {
            await foreach (var firstChunk in _sendChannel!.Reader.ReadAllAsync(ct))
            {
                if (_ws is not { State: WebSocketState.Open }) continue;

                audioBatch.ResetWrittenCount();
                var span = audioBatch.GetSpan(firstChunk.Length);
                firstChunk.CopyTo(span);
                audioBatch.Advance(firstChunk.Length);
                while (_sendChannel.Reader.TryRead(out var extraChunk))
                {
                    span = audioBatch.GetSpan(extraChunk.Length);
                    extraChunk.CopyTo(span);
                    audioBatch.Advance(extraChunk.Length);
                }

                bufferWriter.ResetWrittenCount();
                jsonWriter.Reset(bufferWriter);
                // {"realtimeInput":{"audio":{"data":"<base64>","mimeType":"audio/pcm;rate=16000"}}}
                jsonWriter.WriteStartObject();
                jsonWriter.WriteStartObject("realtimeInput"u8);
                jsonWriter.WriteStartObject("audio"u8);
                jsonWriter.WriteBase64String("data"u8, audioBatch.WrittenSpan);
                jsonWriter.WriteString("mimeType"u8, "audio/pcm;rate=16000"u8);
                jsonWriter.WriteEndObject();
                jsonWriter.WriteEndObject();
                jsonWriter.WriteEndObject();
                await jsonWriter.FlushAsync(ct).ConfigureAwait(false);

                await _wsSendLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await _ws.SendAsync(bufferWriter.WrittenMemory, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                }
                finally
                {
                    _wsSendLock.Release();
                }
                // 送信 PCM16 (2 bytes/sample, 16kHz mono) のサンプル数を累積。
                Interlocked.Add(ref _totalAudioInputSamples16kHz, audioBatch.WrittenCount / 2);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Error("Gemini WebSocket 送信ループエラー", ex);
            ErrorReceived?.Invoke(ex);
        }
        finally
        {
            await jsonWriter.DisposeAsync().ConfigureAwait(false);
        }
    }

    private const int MessageStreamCapacityThreshold = 256 * 1024;

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        var messageStream = new MemoryStream();

        try
        {
            while (!ct.IsCancellationRequested && _ws is { State: WebSocketState.Open })
            {
                var result = await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Logger.Warn("Gemini WebSocket サーバーからクローズ受信");
                    break;
                }

                messageStream.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;

                var length = (int)messageStream.Length;
                var jsonMemory = messageStream.GetBuffer().AsMemory(0, length);
                ProcessMessage(jsonMemory);
                messageStream.SetLength(0);

                if (messageStream.Capacity > MessageStreamCapacityThreshold)
                {
                    messageStream.Dispose();
                    messageStream = new MemoryStream();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            Logger.Warn("Gemini WebSocket 受信エラー", ex);
        }
        catch (Exception ex)
        {
            Logger.Error("Gemini WebSocket 受信ループ予期しないエラー", ex);
            ErrorReceived?.Invoke(ex);
        }
        finally
        {
            messageStream.Dispose();
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (!ct.IsCancellationRequested)
        {
            if (_shouldReconnect && _state == ConnectionState.Connected)
                SetState(ConnectionState.Reconnecting);
            _ = Task.Run(() => TryReconnectAsync(), CancellationToken.None);
        }
    }

    private static readonly JsonDocumentOptions s_jsonDocumentOptions = new() { MaxDepth = 32 };

    private void ProcessMessage(ReadOnlyMemory<byte> json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, s_jsonDocumentOptions);
            var root = doc.RootElement;
            // 配列/プリミティブ root は無視 (以降の TryGetProperty が InvalidOperationException を投げるため)。
            if (root.ValueKind != JsonValueKind.Object) return;

            // setupComplete: セッション確立シグナル。
            if (root.TryGetProperty("setupComplete", out _))
            {
                LogFirstSighting("setupComplete");
                Logger.Info("Gemini Live setup 完了 (セッション確立)");
                return;
            }

            // serverContent: 翻訳結果 (outputTranscription) + 出力音声 (modelTurn、破棄)。
            if (root.TryGetProperty("serverContent", out var serverContent))
            {
                LogFirstSighting("serverContent");

                // 翻訳テキスト: outputTranscription.text を「常に delta」として流す。
                // Pipeline 側の OnTranscriptDelta が句点分割 + D-7 fallback + 類似抑制で完結文化する。
                // ⚠️ outputTranscription が増分前提。 実機で累積と判明したら前回 text との差分計算が要る。
                if (serverContent.TryGetProperty("outputTranscription", out var outTr) &&
                    outTr.TryGetProperty("text", out var textEl))
                {
                    var text = textEl.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        var count = Interlocked.Increment(ref _totalDeltaCount);
                        if (LogFormatting.ShouldLogAtCount(count))
                            Logger.Info($"Gemini outputTranscription #{count}: '{LogFormatting.TruncateForLog(text, 20)}' 長={text.Length}");
                        TranscriptDeltaReceived?.Invoke(text);
                    }
                }

                // turnComplete: 1 発話の区切り。 空 done を流して Pipeline の trailing 確定を促す。
                if (serverContent.TryGetProperty("turnComplete", out var tc) &&
                    tc.ValueKind == JsonValueKind.True)
                {
                    TranscriptCompleted?.Invoke("");
                }
                return;
            }

            // error: Gemini が返すエラー。
            if (root.TryGetProperty("error", out var err))
            {
                var originalMessage = err.TryGetProperty("message", out var m) ? m.GetString() ?? "Unknown error" : "Unknown error";
                var code = err.TryGetProperty("code", out var c) ? c.ToString() : "";
                var kind = OpenAIApiException.Classify(originalMessage, code);
                var friendly = OpenAIApiException.FriendlyMessageFor(kind, originalMessage);
                var ex = new OpenAIApiException(kind, friendly, originalMessage);
                Logger.Error($"Gemini API エラー (kind={kind} code='{code}'): {LogFormatting.TruncateForLog(originalMessage)}");
                if (ex.IsFatal)
                {
                    _shouldReconnect = false;
                    SetState(ConnectionState.Failed);
                }
                ErrorReceived?.Invoke(ex);
                return;
            }

            // goAway 等の未知 event は初見だけ記録 (診断)。
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in root.EnumerateObject())
                {
                    LogFirstSighting(prop.Name);
                    break;
                }
            }
        }
        catch (JsonException ex)
        {
            Logger.Warn("Gemini JSON パースエラー", ex);
        }
    }

    private void LogFirstSighting(string eventType)
    {
        bool isFirst;
        lock (_seenEventTypesLock)
        {
            isFirst = _seenEventTypes.Count < MaxSeenEventTypes && _seenEventTypes.Add(eventType);
        }
        if (isFirst)
            Logger.Info($"Gemini Live event 初見: '{eventType}'");
    }

    private async Task TryReconnectAsync()
    {
        if (Interlocked.CompareExchange(ref _reconnectInFlight, 1, 0) != 0)
            return;

        try
        {
            while (_shouldReconnect
                   && _state != ConnectionState.Connected
                   && _state != ConnectionState.Failed)
            {
                var currentAttempt = Interlocked.Increment(ref _reconnectAttempts);
                if (currentAttempt > _settings.MaxReconnectAttempts)
                {
                    Logger.Error($"Gemini 再接続上限 ({_settings.MaxReconnectAttempts}) 到達");
                    _shouldReconnect = false;
                    SetState(ConnectionState.Failed);
                    ErrorReceived?.Invoke(new InvalidOperationException(
                        $"再接続の上限（{_settings.MaxReconnectAttempts}回）に達しました。接続を確認してください。"));
                    return;
                }

                SetState(ConnectionState.Reconnecting);
                var shift = Math.Min(currentAttempt - 1, 30);
                var baseDelay = (int)Math.Min((long)_settings.ReconnectDelayMs << shift, 30000L);
                var jitterPercent = (Random.Shared.NextDouble() * 0.4) - 0.2;
                var delay = (int)Math.Clamp(baseDelay * (1.0 + jitterPercent), 100, 30000);
                Logger.Info($"Gemini 再接続試行 {currentAttempt}/{_settings.MaxReconnectAttempts}（{delay}ms 後）");

                var ctsSnapshot = _cts;
                var ct = ctsSnapshot?.Token ?? CancellationToken.None;
                try
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }

                if (!_shouldReconnect) return;

                try
                {
                    await _connectLock.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                try
                {
                    if (!_shouldReconnect) return;
                    if (_state == ConnectionState.Connected) return;

                    await CleanupAsync().ConfigureAwait(false);

                    _cts = new CancellationTokenSource();
                    _sendChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(SendChannelCapacity)
                    {
                        FullMode = BoundedChannelFullMode.DropOldest,
                        SingleReader = true
                    });

                    await ConnectWebSocketAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    Logger.Warn("Gemini 再接続失敗", ex);
                }
                finally
                {
                    _connectLock.Release();
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _reconnectInFlight, 0);
        }
    }

    private void SetState(ConnectionState state)
    {
        _state = state;
        StateChanged?.Invoke(state);
    }
}
