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
/// Speechmatics Realtime の Realtime クライアント。 <see cref="GeminiLiveClient"/> を参照実装に
/// WebSocket 再接続 / KeepAlive / Channel 送信 / 統計 / 診断ログ + RecognitionStarted ハンドシェイクの
/// 構造を流用しつつ、 プロトコルを Speechmatics の v2 WebSocket に置き換えている。
///
/// OpenAI/Gemini との主な差分:
///  - 認証: <c>Authorization: Bearer &lt;api-key&gt;</c> ヘッダ
///  - 入力音声: <b>16kHz</b>/PCM16/mono (<see cref="InputSampleRate"/>=16000)。 <b>binary フレーム</b>で生 PCM16 を送る
///  - 接続後に <c>StartRecognition</c> メッセージ (audio_format / transcription_config.language(源言語) /
///    translation_config.target_languages) を送り、 <c>RecognitionStarted</c> を待ってから audio 送信を許可
///  - 受信は <c>AddTranslation</c> (確定翻訳) の results[].content を連結して delta として流す
///    (AddPartialTranslation/AddTranscript は破棄、 確定だけ採用して二重表示・無限成長を防ぐ)
///
/// ⚠️ Speechmatics は源言語を自動判定しない (transcription_config.language で明示)。 字幕分割は
/// OpenAI/Gemini 同様 TranslationPipelineService 側に委ねる。
/// </summary>
public sealed class SpeechmaticsRealtimeClient : Interfaces.IRealtimeTranscriber
{
    private static readonly ILog Logger = LogManager.GetLogger<SpeechmaticsRealtimeClient>();

    // 接続を許可する Speechmatics のリージョンホスト。 settings 改竄で任意の wss:// に API キーを送る経路を塞ぐ。
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "eu2.rt.speechmatics.com",
        "eu.rt.speechmatics.com",
        "neu.rt.speechmatics.com",
        "wss.rt.speechmatics.com",
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
                $"Endpoint のホスト '{uri.IdnHost}' は許可リスト外です（*.rt.speechmatics.com のみ）。");
    }

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _sendTask;
    private const int SendChannelCapacity = 30;
    private Channel<byte[]>? _sendChannel;
    private SpeechmaticsSettings _settings = new();
    private int _reconnectAttempts;
    private int _disposed;
    private int _reconnectInFlight;
    private long _totalDroppedAudioChunks;
    private volatile bool _shouldReconnect;
    private volatile ConnectionState _state = ConnectionState.Disconnected;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly SemaphoreSlim _wsSendLock = new(1, 1);

    // RecognitionStarted ハンドシェイク待ち (Gemini の setupComplete と同型: ack 前の audio 送信を防ぐ)。
    private volatile TaskCompletionSource? _startedTcs;

    private readonly HashSet<string> _seenEventTypes = new(StringComparer.Ordinal);
    private readonly object _seenEventTypesLock = new();
    private const int MaxSeenEventTypes = 256;

    private long _totalDeltaCount;
    private long _totalAudioInputSamples16kHz;

    // 送信した AddAudio (binary) メッセージ数。 graceful stop の EndOfStream.last_seq_no に使う。
    private long _audioSeqNo;
    // graceful stop 時に EndOfStream を送ったあと、 サーバーの EndOfTranscript を待つためのシグナル。
    private volatile TaskCompletionSource<bool>? _endOfTranscriptSignal;

    public ConnectionState State => _state;

    /// <inheritdoc />
    public int InputSampleRate => 16000;

    /// <inheritdoc />
    public long DroppedAudioChunkCount => Interlocked.Read(ref _totalDroppedAudioChunks);

    /// <inheritdoc />
    public long TotalAudioInputSamples24kHz => (long)(Interlocked.Read(ref _totalAudioInputSamples16kHz) * 1.5);

    /// <inheritdoc />
    public long ServerReportedAudioInputTokens => 0;

    private readonly Interfaces.IDebugAudioRecorder? _debugAudioRecorder;

    public SpeechmaticsRealtimeClient(Interfaces.IDebugAudioRecorder? debugAudioRecorder = null)
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

        Logger.Info("ネットワーク復帰検知: 再接続カウンタをリセットして再接続を試みます (Speechmatics)");
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
    /// Speechmatics 固有設定で接続する。 TranslationPipelineService はこの具象メソッドを直接呼ぶ (Provider=Speechmatics)。
    /// </summary>
    public async Task ConnectAsync(SpeechmaticsSettings settings, CancellationToken ct = default)
    {
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State == ConnectionState.Connected)
            {
                Logger.Warn("既に接続済み。先に切断します。(Speechmatics)");
                await CleanupAsync().ConfigureAwait(false);
            }

            _settings = new SpeechmaticsSettings
            {
                ApiKey = settings.ApiKey,
                OutputLanguage = settings.OutputLanguage,
                SourceLanguage = settings.SourceLanguage,
                Endpoint = settings.Endpoint,
                ReconnectDelayMs = settings.ReconnectDelayMs,
                MaxReconnectAttempts = settings.MaxReconnectAttempts,
                SilencePaddingMs = settings.SilencePaddingMs,
                MaxPartialChars = settings.MaxPartialChars,
            };
            _shouldReconnect = true;

            Interlocked.Exchange(ref _totalAudioInputSamples16kHz, 0);
            Interlocked.Exchange(ref _totalDroppedAudioChunks, 0);
            Interlocked.Exchange(ref _totalDeltaCount, 0);
            // _audioSeqNo は ConnectWebSocketAsync (初回 / reconnect 共通) でリセットするためここでは不要。

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _sendChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(SendChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true
            });

            try
            {
                await ConnectWebSocketAsync(_cts.Token).ConfigureAwait(false);
            }
            catch
            {
                _shouldReconnect = false;
                await CleanupAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    async Task Interfaces.IRealtimeTranscriber.ConnectAsync(OpenAIRealtimeSettings settings, CancellationToken ct)
    {
        var mapped = new SpeechmaticsSettings
        {
            ApiKey = settings.ApiKey,
            OutputLanguage = settings.OutputLanguage,
            SourceLanguage = new SpeechmaticsSettings().SourceLanguage,
            Endpoint = new SpeechmaticsSettings().Endpoint,
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
        // graceful stop: 受信ループを cancel する前に EndOfStream を宣言し、 サーバーが返す最終
        // AddTranslation + EndOfTranscript を取りこぼさないよう短時間ドレインする。
        await TryFinalizeStreamAsync().ConfigureAwait(false);
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { }

        if (!await _connectLock.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false))
        {
            Logger.Warn("DisconnectAsync: _connectLock 取得が 3 秒でタイムアウト、強制クリーンアップに進む (Speechmatics)");
            try { await CleanupAsync().ConfigureAwait(false); }
            catch (Exception ex) { Logger.Warn("DisconnectAsync(timeout): CleanupAsync で例外 (Speechmatics)", ex); }
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

    /// <summary>
    /// graceful stop 時のみ呼ぶ。 Speechmatics は <c>EndOfStream</c> (<c>last_seq_no</c> = 送信した AddAudio 数)
    /// で「これ以上 audio は来ない」を宣言し、 サーバーが最終 <c>AddTranslation</c> + <c>EndOfTranscript</c> を返す。
    /// 受信ループ (この時点ではまだ cancel していない) がそれらを処理する猶予を最大数秒与えてから、 呼び出し元が
    /// cancel する。 best-effort: 失敗しても停止処理は続行する。
    /// </summary>
    private async Task TryFinalizeStreamAsync()
    {
        var ws = _ws;
        if (ws is not { State: WebSocketState.Open } || _state != ConnectionState.Connected)
            return;

        var eot = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _endOfTranscriptSignal = eot;
        try
        {
            // 先に送信チャンネルを閉じて送信ループに残バッファ (キュー済み AddAudio) を送り切らせる。
            // Speechmatics は EndOfStream 後の audio を無視するため、 これをしないと末尾の AddAudio が
            // EOS の後ろに送られて捨てられ、 最後の翻訳が失われる (Codex 指摘)。 last_seq_no は送り切った後に確定。
            _sendChannel?.Writer.TryComplete();
            var sendTask = _sendTask;
            if (sendTask != null)
            {
                try { await sendTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
                {
                    Logger.Debug($"Speechmatics 送信ループのドレイン待ちが想定内例外: {ex.GetType().Name}");
                }
            }

            var endOfStream = JsonSerializer.SerializeToUtf8Bytes(new
            {
                message = "EndOfStream",
                last_seq_no = Interlocked.Read(ref _audioSeqNo),
            });
            using var opCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _wsSendLock.WaitAsync(opCts.Token).ConfigureAwait(false);
            try
            {
                await ws.SendAsync(endOfStream, WebSocketMessageType.Text, true, opCts.Token).ConfigureAwait(false);
            }
            finally
            {
                _wsSendLock.Release();
            }

            // 受信ループが最終 AddTranslation + EndOfTranscript を処理するのを最大 3 秒待つ。
            await eot.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or WebSocketException or ObjectDisposedException)
        {
            Logger.Debug($"Speechmatics EndOfStream 送信/ドレイン 想定内例外: {ex.GetType().Name}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Speechmatics EndOfStream 送信/ドレイン 想定外例外: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _endOfTranscriptSignal = null;
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
        SpeechmaticsSettings settings, CancellationToken ct = default)
    {
        Uri uri;
        try
        {
            uri = new Uri(settings.Endpoint);
            ValidateEndpoint(uri);
        }
        catch (Exception ex) when (ex is InvalidOperationException or UriFormatException)
        {
            return (false, ex.Message);
        }

        if (string.IsNullOrWhiteSpace(settings.ApiKey))
            return (false, "Speechmatics APIキーが設定されていません。");

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", $"Bearer {settings.ApiKey}");
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            await ws.ConnectAsync(uri, timeoutCts.Token).ConfigureAwait(false);

            var startBytes = BuildStartRecognitionMessage(settings);
            await ws.SendAsync(startBytes, WebSocketMessageType.Text, true, timeoutCts.Token).ConfigureAwait(false);

            // Speechmatics は handshake 直後に Info (認識品質 / 同時接続数等) を RecognitionStarted ack より
            // 先に送ることがある。 最初の 1 通だけで成功判定すると、 無効な StartRecognition でも Info 先着で
            // 成功扱いになってしまう。 RecognitionStarted / Error が来るか timeout するまで読み続ける。
            string message = "接続成功！Speechmatics APIキーは有効です。";
            string? translationWarning = null;
            var buffer = new byte[8192];
            while (true)
            {
                var result = await ws.ReceiveAsync(buffer, timeoutCts.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    return (false, "サーバーが接続を閉じました。StartRecognition が拒否された可能性があります。");

                using var ms = new MemoryStream();
                ms.Write(buffer, 0, result.Count);
                while (!result.EndOfMessage)
                {
                    result = await ws.ReceiveAsync(buffer, timeoutCts.Token).ConfigureAwait(false);
                    ms.Write(buffer, 0, result.Count);
                }
                var json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);

                using var doc = JsonDocument.Parse(json, s_jsonDocumentOptions);
                var root = doc.RootElement;
                var msgType = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("message", out var mt)
                    ? mt.GetString() : null;

                if (msgType == "RecognitionStarted")
                    break; // ハンドシェイク成立 → 成功
                if (msgType == "Error")
                {
                    var reason = root.TryGetProperty("reason", out var r) ? r.GetString() : "不明なエラー";
                    return (false, $"APIエラー: {reason}");
                }
                if (msgType == "Warning")
                {
                    var wtype = root.TryGetProperty("type", out var wt) ? wt.GetString() : null;
                    if (IsTranslationDisablingWarning(wtype))
                        translationWarning = $"ただし翻訳が無効です ({wtype})。源言語 / 出力言語の組み合わせを確認してください。";
                }
                // Info / Warning / その他は読み飛ばして RecognitionStarted を待つ。
            }

            if (translationWarning != null)
                message = $"接続成功 (キーは有効) {translationWarning}";

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
                Logger.Warn("送受信ループ停止がタイムアウト (Speechmatics)");
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
            {
                Logger.Debug($"送受信ループ停止中の想定内例外 (Speechmatics): {ex.GetType().Name}: {ex.Message}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"送受信ループ停止中の想定外例外 (Speechmatics): {ex.GetType().Name}: {ex.Message}");
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
                Logger.Debug($"WebSocket.CloseAsync 想定内例外 (Speechmatics): {ex.GetType().Name}: {ex.Message}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"WebSocket.CloseAsync 想定外例外 (Speechmatics): {ex.GetType().Name}: {ex.Message}");
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
        // _audioSeqNo は WebSocket セッション単位 (StartRecognition ごと) の AddAudio 連番。 reconnect は
        // この経路だけを通り ConnectAsync を経由しないため、 ここでリセットしないと前セッションの値が残り、
        // 次の graceful stop で EndOfStream.last_seq_no が過大になりサーバーが来ない audio を待ち続ける
        // (EndOfTranscript が来ず末尾翻訳を取りこぼす)。 Codex 指摘。
        Interlocked.Exchange(ref _audioSeqNo, 0);
        _ws?.Dispose();
        _ws = new ClientWebSocket();
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        _ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(30);

        try
        {
            // Speechmatics は Authorization: Bearer ヘッダで認証する。
            _ws.Options.SetRequestHeader("Authorization", $"Bearer {_settings.ApiKey}");
            var uri = new Uri(_settings.Endpoint);
            ValidateEndpoint(uri);
            await _ws.ConnectAsync(uri, ct).ConfigureAwait(false);
            Interlocked.Exchange(ref _reconnectAttempts, 0);
            Logger.Info("Speechmatics Realtime WebSocket 接続成功");

            var startedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _startedTcs = startedTcs;

            _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts!.Token), _cts!.Token);
            _sendTask = Task.Run(() => SendLoopAsync(_cts!.Token), _cts!.Token);

            await SendStartRecognitionAsync(ct).ConfigureAwait(false);

            // RecognitionStarted を待ってから Connected にする (ack 前の audio 送信を防ぐ)。
            await WaitForStartedAsync(startedTcs.Task, ct).ConfigureAwait(false);

            if (_ws is not { State: WebSocketState.Open })
                throw new InvalidOperationException("Speechmatics StartRecognition 中にサーバーが接続を閉じました。");

            SetState(ConnectionState.Connected);
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
                    "認証失敗（401）: Speechmatics APIキーが無効です。設定画面で正しいキーを入力してください。",
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
                if (apiEx.IsFatal)
                    _shouldReconnect = false;
                ErrorReceived?.Invoke(apiEx);
                SetState(apiEx.IsFatal ? ConnectionState.Failed : ConnectionState.Disconnected);
                throw apiEx;
            }
            ErrorReceived?.Invoke(new InvalidOperationException(friendlyMsg, ex));
            SetState(ConnectionState.Disconnected);
            throw;
        }
        catch (OpenAIApiException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error("Speechmatics WebSocket 接続失敗", ex);
            ErrorReceived?.Invoke(ex);
            SetState(ConnectionState.Disconnected);
            throw;
        }
    }

    private async Task SendStartRecognitionAsync(CancellationToken ct)
    {
        var bytes = BuildStartRecognitionMessage(_settings);
        Logger.Info($"Speechmatics StartRecognition 送信: source='{ResolveLang(_settings.SourceLanguage, "en")}' target='{ResolveLang(_settings.OutputLanguage, "ja")}'");
        await _wsSendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _ws!.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _wsSendLock.Release();
        }
    }

    private async Task WaitForStartedAsync(Task startedTask, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await startedTask.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            Logger.Info("Speechmatics RecognitionStarted 受信 → audio 送信を許可");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // RecognitionStarted を待たずに Connected へ進むと ack 前に音声送信が始まる。
            // タイムアウトは失敗として扱い、 接続確立を中断する (再接続ループが拾う)。
            throw new TimeoutException("Speechmatics RecognitionStarted が 10 秒で未受信です。接続を開始できません。");
        }
    }

    private static string ResolveLang(string? lang, string fallback)
        => string.IsNullOrWhiteSpace(lang) ? fallback : lang;

    // StartRecognition メッセージ。 audio_format=raw/pcm_s16le/16000、 transcription_config.language=源言語、
    // translation_config.target_languages=[ターゲット]。
    private static byte[] BuildStartRecognitionMessage(SpeechmaticsSettings settings)
    {
        var start = new
        {
            message = "StartRecognition",
            audio_format = new
            {
                type = "raw",
                encoding = "pcm_s16le",
                sample_rate = 16000,
            },
            transcription_config = new
            {
                language = ResolveLang(settings.SourceLanguage, "en"),
                enable_partials = true,
            },
            translation_config = new
            {
                target_languages = new[] { ResolveLang(settings.OutputLanguage, "ja") },
                enable_partials = true,
            },
        };
        return JsonSerializer.SerializeToUtf8Bytes(start);
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        // Speechmatics は生 PCM16 を binary フレーム (AddAudio) で受ける。 チャンクをまとめて送る。
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

                await _wsSendLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await _ws.SendAsync(audioBatch.WrittenMemory, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false);
                }
                finally
                {
                    _wsSendLock.Release();
                }
                Interlocked.Add(ref _totalAudioInputSamples16kHz, audioBatch.WrittenCount / 2);
                // AddAudio 1 メッセージ = seq_no +1 (EndOfStream.last_seq_no 用)。
                Interlocked.Increment(ref _audioSeqNo);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Error("Speechmatics WebSocket 送信ループエラー", ex);
            ErrorReceived?.Invoke(ex);
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
                    // close payload に not_authorised / quota_exceeded 等の致命理由が入ることがある。
                    // in-band Error と同様に分類し、 fatal なら reconnect せず Failed + fatal バナーを出す
                    // (放置すると無効キー / クォータ枯渇でも reconnect 上限まで無駄に再試行する。 Codex 指摘)。
                    var closeDesc = result.CloseStatusDescription ?? string.Empty;
                    Logger.Warn($"Speechmatics WebSocket サーバーからクローズ受信 (status={result.CloseStatus}, desc='{LogFormatting.TruncateForLog(closeDesc)}')");
                    if (!string.IsNullOrWhiteSpace(closeDesc))
                    {
                        var closeKind = ClassifySpeechmaticsError(closeDesc, closeDesc);
                        if (closeKind != OpenAIApiErrorKind.Unknown)
                        {
                            var ex = new OpenAIApiException(closeKind, SpeechmaticsFriendlyMessageFor(closeKind, closeDesc), closeDesc);
                            if (ex.IsFatal)
                            {
                                _shouldReconnect = false;
                                SetState(ConnectionState.Failed);
                            }
                            ErrorReceived?.Invoke(ex);
                        }
                    }
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
            Logger.Warn("Speechmatics WebSocket 受信エラー", ex);
        }
        catch (Exception ex)
        {
            Logger.Error("Speechmatics WebSocket 受信ループ予期しないエラー", ex);
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
            if (root.ValueKind != JsonValueKind.Object) return;
            if (!root.TryGetProperty("message", out var msgEl)) return;
            var msgType = msgEl.GetString();
            if (string.IsNullOrEmpty(msgType)) return;
            LogFirstSighting(msgType);

            switch (msgType)
            {
                case "RecognitionStarted":
                    Logger.Info("Speechmatics RecognitionStarted (セッション確立)");
                    _startedTcs?.TrySetResult();
                    return;

                case "AddTranslation":
                    // 確定翻訳のみ採用。 results[].content を連結して delta として流す。
                    // (AddPartialTranslation は破棄 — 確定だけ使い二重表示・無限成長を防ぐ。)
                    // 出力言語が空白区切り (英 / 西 / 独 等) ならセグメント間に空白を入れる (CJK は直結)。
                    var text = JoinResults(root, !IsCjkOutputLanguage(_settings.OutputLanguage));
                    if (!string.IsNullOrEmpty(text))
                    {
                        var count = Interlocked.Increment(ref _totalDeltaCount);
                        if (LogFormatting.ShouldLogAtCount(count))
                            Logger.Info($"Speechmatics AddTranslation #{count}: '{LogFormatting.TruncateForLog(text, 20)}' 長={text.Length}");
                        TranscriptDeltaReceived?.Invoke(text);
                        // 公式ドキュメント (実機監査) より AddTranslation は pause 区切りの「確定済みセグメント」
                        // (AddPartialTranslation が work-in-progress)。 EndOfTranscript (ストリーム終端) まで確定を
                        // 待つと、 句読点なしの短い発話 ("Yes" 等) が partial のまま次発話と融合してしまう。 そこで
                        // Soniox の <end> トークンと同様、 セグメントごとに空 done を流して Pipeline 側に確定させる
                        // (空 done = _accumulatedText を完結文として flush + 新 SegmentId 発行)。 Codex 指摘。
                        TranscriptCompleted?.Invoke("");
                    }
                    return;

                case "EndOfTranscript":
                    Logger.Info("Speechmatics EndOfTranscript 受信");
                    TranscriptCompleted?.Invoke("");
                    _endOfTranscriptSignal?.TrySetResult(true);
                    return;

                case "Warning":
                    {
                        var wtype = root.TryGetProperty("type", out var wt) ? wt.GetString() : null;
                        var wreason = root.TryGetProperty("reason", out var wr) ? wr.GetString() ?? "" : "";
                        if (IsTerminalWarning(wtype))
                        {
                            // duration_limit_exceeded 等。 サーバーは以降の audio を無視し EndOfStream 相当に振る舞い、
                            // 最後に残りの AddTranslation + EndOfTranscript を送ってくる。 即 Abort すると受信ループが
                            // それらの前に終了し末尾翻訳を取りこぼすため、 SendAudio だけ止めて (Reconnecting) socket は
                            // 開いたまま EndOfTranscript or 短いタイムアウトを待ってから Abort → reconnect する (Codex 指摘)。
                            var msg = $"Speechmatics: 1 発話の長さ上限に達しました ({wtype})。以降の音声は無視されるため接続を張り直します。";
                            Logger.Warn(msg + (string.IsNullOrEmpty(wreason) ? "" : $" reason={LogFormatting.TruncateForLog(wreason)}"));
                            ErrorReceived?.Invoke(new InvalidOperationException(msg));
                            if (_shouldReconnect && _state == ConnectionState.Connected)
                            {
                                SetState(ConnectionState.Reconnecting); // SendAudio は State!=Connected で止まる。
                                var eot = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                                _endOfTranscriptSignal = eot;
                                var wsSnapshot = _ws;
                                _ = Task.Run(async () =>
                                {
                                    try { await eot.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                                    catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
                                    try { wsSnapshot?.Abort(); } catch { /* 受信ループ終了 → reconnect */ }
                                });
                            }
                        }
                        else if (IsTranslationDisablingWarning(wtype))
                        {
                            // 非対応の翻訳ペア等で翻訳が走らない → socket は開いたままだが字幕は一切出ない。
                            // 握りつぶすとユーザーは「繋がっているのに字幕が出ない」原因が分からないため通知する。
                            var msg = $"Speechmatics: 翻訳が無効です ({wtype})。源言語 ({ResolveLang(_settings.SourceLanguage, "en")}) → 出力言語 ({ResolveLang(_settings.OutputLanguage, "ja")}) の組み合わせがサポートされていない可能性があります。設定を確認してください。";
                            Logger.Warn(msg + (string.IsNullOrEmpty(wreason) ? "" : $" reason={LogFormatting.TruncateForLog(wreason)}"));
                            ErrorReceived?.Invoke(new InvalidOperationException(msg));
                        }
                        else
                        {
                            Logger.Info($"Speechmatics Warning (type='{wtype}'): {LogFormatting.TruncateForLog(wreason)}");
                        }
                        return;
                    }

                case "Error":
                    {
                        var reason = root.TryGetProperty("reason", out var r) ? r.GetString() ?? "Unknown error" : "Unknown error";
                        var type = root.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "";
                        var kind = ClassifySpeechmaticsError(type, reason);
                        var friendly = SpeechmaticsFriendlyMessageFor(kind, reason);
                        var ex = new OpenAIApiException(kind, friendly, reason);
                        Logger.Error($"Speechmatics API エラー (kind={kind} type='{type}'): {LogFormatting.TruncateForLog(reason)}");
                        if (ex.IsFatal)
                        {
                            _shouldReconnect = false;
                            SetState(ConnectionState.Failed);
                        }
                        _startedTcs?.TrySetException(ex);
                        ErrorReceived?.Invoke(ex);
                        return;
                    }

                // AddPartialTranslation / AddTranscript / AddPartialTranscript / AudioAdded / Info は破棄。
                default:
                    return;
            }
        }
        catch (JsonException ex)
        {
            Logger.Warn("Speechmatics JSON パースエラー", ex);
        }
    }

    // 出力言語が CJK (日本語 / 中国語 / 韓国語) なら単語間に空白を入れない。 それ以外 (英語 / スペイン語 /
    // ドイツ語等の空白区切り言語) は results セグメントを直結すると "Hello"+"world"="Helloworld" と繋がるため、
    // セグメント間に空白を入れる (句読点で始まるセグメントの前は入れない)。 Codex 指摘。
    internal static bool IsCjkOutputLanguage(string? lang)
    {
        var l = (lang ?? string.Empty).ToLowerInvariant();
        return l.StartsWith("ja") || l.StartsWith("zh") || l.StartsWith("ko") || l.StartsWith("yue");
    }

    internal static string JoinResults(JsonElement root, bool spaceDelimited)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            return string.Empty;
        var sb = new StringBuilder();
        foreach (var res in results.EnumerateArray())
        {
            if (res.ValueKind != JsonValueKind.Object) continue;
            if (!res.TryGetProperty("content", out var c) || c.ValueKind != JsonValueKind.String) continue;
            var content = c.GetString();
            if (string.IsNullOrEmpty(content)) continue;

            if (spaceDelimited && sb.Length > 0 && !StartsWithClosingPunctuation(content))
                sb.Append(' ');
            sb.Append(content);
        }
        return sb.ToString();
    }

    // 直前に空白を入れたくない「閉じ / 後続」系の句読点で始まるか (例: ", . ! ? ) ] 」)。
    private static bool StartsWithClosingPunctuation(string content)
    {
        var ch = content[0];
        return ch is ',' or '.' or '!' or '?' or ';' or ':' or ')' or ']' or '}'
            or '、' or '。' or '！' or '？' or '」' or '）' or '’' or '”';
    }

    // Speechmatics の in-band Error は provider 固有の type を返す。 既知 type を fatal 種別に先にマッピングし
    // (OpenAI 分類だと Unknown=非 fatal になり、 認証失敗 / クォータ枯渇でも再接続ループに陥って fatal バナーが
    // 出ない)、 残りは OpenAI 分類に委譲する (Codex 指摘)。
    internal static OpenAIApiErrorKind ClassifySpeechmaticsError(string? type, string reason)
    {
        // ⚠️ 公式ドキュメント (https://docs.speechmatics.com/rt-api-ref close-code / error type 表) を実機監査で確認:
        //  - quota_exceeded (4005) = 「契約あたりの同時接続数上限」= 一時的。 数秒バックオフで回復するため **非 fatal** (RateLimit)。
        //    課金枯渇ではない。 ここを QuotaExceeded(fatal) にすると、 同時接続上限に一瞬当たっただけで回復可能なセッションを
        //    永久に殺し、 誤った課金エラーバナーを出す (当初その誤分類で実装していた — 監査で修正)。
        //  - timelimit_exceeded (4006) = 「契約の使用量クォータ到達 (アカウントレベル)」= 回復不能なので **fatal** (QuotaExceeded)。
        //  - not_authorised (4001) = 認証失敗 = fatal。
        switch ((type ?? string.Empty).ToLowerInvariant())
        {
            case "not_authorised":
            case "not_authorized":
            case "invalid_api_key":
                return OpenAIApiErrorKind.InvalidApiKey;
            case "quota_exceeded":          // 同時接続数上限 (一時的) → リトライ可能
            case "rate_limit_exceeded":
                return OpenAIApiErrorKind.RateLimit;
            case "timelimit_exceeded":      // 契約使用量クォータ到達 (アカウントレベル) → 回復不能
            case "insufficient_funds":
                return OpenAIApiErrorKind.QuotaExceeded;
            case "not_allowed":             // 公式 (close 4003) = 要求した操作の権限なし → 回復不能
            case "forbidden":
            case "unsupported_language_pair":
                return OpenAIApiErrorKind.Forbidden;
            default:
                return OpenAIApiException.Classify(reason, type);
        }
    }

    // セッションを実質終了させる Speechmatics の Warning タイプ。 サーバーは以降の audio を無視し
    // EndOfStream 相当に振る舞うため、 通知 + reconnect で新セッションを張り直す必要がある。
    internal static bool IsTerminalWarning(string? type)
        => string.Equals(type, "duration_limit_exceeded", StringComparison.OrdinalIgnoreCase);

    // 翻訳が走らなくなる Speechmatics の Warning タイプ (socket は開いたまま字幕が出ない状態になる)。
    // これらは握りつぶさず ErrorReceived でユーザーに通知する。
    internal static bool IsTranslationDisablingWarning(string? type)
        => string.Equals(type, "unsupported_translation_pair", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "empty_translation_target_list", StringComparison.OrdinalIgnoreCase);

    internal static string SpeechmaticsFriendlyMessageFor(OpenAIApiErrorKind kind, string originalMessage) => kind switch
    {
        OpenAIApiErrorKind.QuotaExceeded =>
            "Speechmatics API のクォータ / 残高を超過しました。 Speechmatics ポータルで利用状況・課金設定を確認してください (https://portal.speechmatics.com)。",
        OpenAIApiErrorKind.InvalidApiKey =>
            "Speechmatics API キーが無効です。 設定画面で正しいキーを入力するか、 Speechmatics ポータルで再発行してください (https://portal.speechmatics.com)。",
        OpenAIApiErrorKind.RateLimit =>
            "Speechmatics API のレート制限 / 同時接続数上限に達しました。 しばらく待ってから再試行してください。",
        OpenAIApiErrorKind.Forbidden =>
            "Speechmatics API へのアクセス権限がありません。 プラン / 翻訳機能の利用可否を確認してください。",
        OpenAIApiErrorKind.BadRequest =>
            "Speechmatics API リクエストが不正でした。 設定値 (源言語 / 出力言語 / Endpoint) と翻訳ペアの対応を確認してください。",
        _ => string.IsNullOrWhiteSpace(originalMessage)
            ? "Speechmatics API から不明なエラーが返されました。"
            : $"Speechmatics API エラー: {originalMessage}",
    };

    private void LogFirstSighting(string eventType)
    {
        bool isFirst;
        lock (_seenEventTypesLock)
        {
            isFirst = _seenEventTypes.Count < MaxSeenEventTypes && _seenEventTypes.Add(eventType);
        }
        if (isFirst)
            Logger.Info($"Speechmatics Realtime event 初見: '{eventType}'");
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
                    Logger.Error($"Speechmatics 再接続上限 ({_settings.MaxReconnectAttempts}) 到達");
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
                Logger.Info($"Speechmatics 再接続試行 {currentAttempt}/{_settings.MaxReconnectAttempts}（{delay}ms 後）");

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
                    Logger.Warn("Speechmatics 再接続失敗", ex);
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
