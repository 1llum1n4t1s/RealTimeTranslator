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
/// Soniox Realtime Speech-to-Text + Translation (stt-rt-v5) の Realtime クライアント。
/// <see cref="GeminiLiveClient"/> を参照実装に WebSocket 再接続 / KeepAlive / Channel 送信 / 統計 /
/// 診断ログの構造を流用しつつ、 プロトコルを Soniox の transcribe-websocket に置き換えている。
///
/// OpenAI/Gemini との主な差分:
///  - 認証: 接続後の <c>config</c> JSON メッセージ内 <c>api_key</c> (URL query / Authorization ヘッダではない)
///  - 入力音声: <b>16kHz</b>/PCM16/mono (<see cref="InputSampleRate"/>=16000)。 <b>binary フレーム</b>で生 PCM16 を送る
///    (Gemini のような base64 JSON 包装ではない)
///  - 接続後に <c>config</c> メッセージ (api_key / model / audio_format=pcm_s16le / sample_rate / translation) を 1 度送る
///  - 受信は <c>tokens</c> 配列のうち <c>translation_status == "translation"</c> かつ <c>is_final == true</c> の
///    トークンの <c>text</c> を翻訳テキストとして連結し delta として流す (源言語は自動判定、 非翻訳トークンは破棄)
///
/// 字幕分割は OpenAI/Gemini 同様 TranslationPipelineService 側に委ねる (本クライアントは確定翻訳トークンの
/// 増分テキストを「常に delta」として <see cref="TranscriptDeltaReceived"/> に流す)。
/// </summary>
public sealed class SonioxRealtimeClient : Interfaces.IRealtimeTranscriber
{
    private static readonly ILog Logger = LogManager.GetLogger<SonioxRealtimeClient>();

    // 接続を許可する Soniox のホスト。 settings 改竄で任意の wss:// に API キーを送る経路を塞ぐ。
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "stt-rt.soniox.com",
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
                $"Endpoint のホスト '{uri.IdnHost}' は許可リスト外です（stt-rt.soniox.com のみ）。");
    }

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _sendTask;
    private const int SendChannelCapacity = 30;
    private Channel<byte[]>? _sendChannel;
    private SonioxSettings _settings = new();
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

    // graceful stop 時に end-of-stream フレームを送ったあと、 サーバーの最終 finished を待つためのシグナル。
    private volatile TaskCompletionSource<bool>? _finishedSignal;

    // connect (config 送信) 後のグレース期間中に受信ループが in-band error を観測したか。 400 config 拒否は
    // 非 fatal (BadRequest) で Failed にならないため、 Failed 判定だけでは取りこぼす (Codex 指摘)。
    private volatile bool _errorObservedSinceConnect;

    // ⚠️ Soniox は 16kHz 送信。 OpenAI 互換の「24kHz 換算サンプル数」を返すため ×1.5 する。
    private long _totalAudioInputSamples16kHz;

    public ConnectionState State => _state;

    /// <inheritdoc />
    public int InputSampleRate => 16000;

    /// <inheritdoc />
    public long DroppedAudioChunkCount => Interlocked.Read(ref _totalDroppedAudioChunks);

    /// <inheritdoc />
    public long TotalAudioInputSamples24kHz => (long)(Interlocked.Read(ref _totalAudioInputSamples16kHz) * 1.5);

    /// <inheritdoc />
    // Soniox は token 課金だが usage 報告形式が realtime JSON に含まれないため 0 のまま
    // (TranslationPipelineService 側が送信秒数からの fallback 推定に倒れる)。
    public long ServerReportedAudioInputTokens => 0;

    private readonly Interfaces.IDebugAudioRecorder? _debugAudioRecorder;

    public SonioxRealtimeClient(Interfaces.IDebugAudioRecorder? debugAudioRecorder = null)
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

        Logger.Info("ネットワーク復帰検知: 再接続カウンタをリセットして再接続を試みます (Soniox)");
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
    /// Soniox 固有設定で接続する。 TranslationPipelineService はこの具象メソッドを直接呼ぶ (Provider=Soniox のとき)。
    /// </summary>
    public async Task ConnectAsync(SonioxSettings settings, CancellationToken ct = default)
    {
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State == ConnectionState.Connected)
            {
                Logger.Warn("既に接続済み。先に切断します。(Soniox)");
                await CleanupAsync().ConfigureAwait(false);
            }

            // セッション開始時のスナップショットを保持する (hot-swap 契約: 走行中の設定変更は次の Start で反映)。
            _settings = new SonioxSettings
            {
                ApiKey = settings.ApiKey,
                OutputLanguage = settings.OutputLanguage,
                Model = settings.Model,
                Endpoint = settings.Endpoint,
                ReconnectDelayMs = settings.ReconnectDelayMs,
                MaxReconnectAttempts = settings.MaxReconnectAttempts,
                SilencePaddingMs = settings.SilencePaddingMs,
                MaxPartialChars = settings.MaxPartialChars,
            };
            _shouldReconnect = true;

            // 新しい Start ごとにセッション統計をリセット (singleton 共有のため前回累積の混入を防ぐ)。
            Interlocked.Exchange(ref _totalAudioInputSamples16kHz, 0);
            Interlocked.Exchange(ref _totalDroppedAudioChunks, 0);
            Interlocked.Exchange(ref _totalDeltaCount, 0);

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

    /// <summary>
    /// interface 契約 (OpenAIRealtimeSettings) を満たすための明示実装。 通常は Pipeline が具象
    /// <see cref="ConnectAsync(SonioxSettings, CancellationToken)"/> を呼ぶ。 取れる範囲をマップして委譲する。
    /// </summary>
    async Task Interfaces.IRealtimeTranscriber.ConnectAsync(OpenAIRealtimeSettings settings, CancellationToken ct)
    {
        var mapped = new SonioxSettings
        {
            ApiKey = settings.ApiKey,
            OutputLanguage = settings.OutputLanguage,
            Model = new SonioxSettings().Model,
            Endpoint = new SonioxSettings().Endpoint,
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
        // graceful stop: 受信ループを cancel する前に end-of-stream を宣言し、 サーバーが返す最終
        // translation tokens + finished を取りこぼさないよう短時間ドレインする。
        await TryFinalizeStreamAsync().ConfigureAwait(false);
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { }

        if (!await _connectLock.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false))
        {
            Logger.Warn("DisconnectAsync: _connectLock 取得が 3 秒でタイムアウト、強制クリーンアップに進む (Soniox)");
            try { await CleanupAsync().ConfigureAwait(false); }
            catch (Exception ex) { Logger.Warn("DisconnectAsync(timeout): CleanupAsync で例外 (Soniox)", ex); }
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
    /// graceful stop 時のみ呼ぶ。 Soniox プロトコルでは空 WebSocket フレームで end-of-stream を宣言すると
    /// サーバーが最後の translation tokens を確定し <c>finished</c> を返す。 受信ループ (この時点ではまだ
    /// cancel していない) がそれらを処理する猶予を最大数秒与えてから、 呼び出し元が cancel する。
    /// best-effort: 失敗しても停止処理は続行する。
    /// </summary>
    private async Task TryFinalizeStreamAsync()
    {
        var ws = _ws;
        if (ws is not { State: WebSocketState.Open } || _state != ConnectionState.Connected)
            return;

        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _finishedSignal = finished;
        try
        {
            // 先に送信チャンネルを閉じて送信ループに残バッファ (キュー済み PCM) を送り切らせる。
            // これをしないと end-of-stream マーカーの後ろにキュー音声が送られたり cancel で捨てられ、
            // 末尾の発話フレームが翻訳されないまま失われる (Codex 指摘)。
            _sendChannel?.Writer.TryComplete();
            var sendTask = _sendTask;
            if (sendTask != null)
            {
                try { await sendTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
                {
                    Logger.Debug($"Soniox 送信ループのドレイン待ちが想定内例外: {ex.GetType().Name}");
                }
            }

            using var opCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _wsSendLock.WaitAsync(opCts.Token).ConfigureAwait(false);
            try
            {
                // 空 binary フレーム = end-of-stream シグナル。
                await ws.SendAsync(ReadOnlyMemory<byte>.Empty, WebSocketMessageType.Binary, true, opCts.Token).ConfigureAwait(false);
            }
            finally
            {
                _wsSendLock.Release();
            }

            // 受信ループが最終 tokens + finished を処理するのを最大 3 秒待つ。
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or WebSocketException or ObjectDisposedException)
        {
            Logger.Debug($"Soniox end-of-stream 送信/ドレイン 想定内例外: {ex.GetType().Name}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Soniox end-of-stream 送信/ドレイン 想定外例外: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _finishedSignal = null;
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
        SonioxSettings settings, CancellationToken ct = default)
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
            return (false, "Soniox APIキーが設定されていません。");

        using var ws = new ClientWebSocket();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            await ws.ConnectAsync(uri, timeoutCts.Token).ConfigureAwait(false);

            // config を送って、 error が返らなければ API キー有効とみなす。
            var configBytes = BuildConfigMessage(settings);
            await ws.SendAsync(configBytes, WebSocketMessageType.Text, true, timeoutCts.Token).ConfigureAwait(false);

            // Soniox は config 受理時に pre-audio ack を返さない (プロトコル上、 config 後はすぐ audio 送信)。
            // そのため応答受信を 10 秒フルでブロックすると、 正常なキーでもサーバーが何も返さずタイムアウト扱いに
            // なってしまう。 短いグレース期間だけ error を待ち、 何も来なければ「キー有効」とみなす (error は通常即時)。
            string message = "接続成功！Soniox APIキーは有効です。";
            using (var graceCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                graceCts.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    var buffer = new byte[8192];
                    var result = await ws.ReceiveAsync(buffer, graceCts.Token).ConfigureAwait(false);
                    if (result.MessageType != WebSocketMessageType.Close)
                    {
                        var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        using var doc = JsonDocument.Parse(json, s_jsonDocumentOptions);
                        var root = doc.RootElement;
                        if (root.ValueKind == JsonValueKind.Object &&
                            (root.TryGetProperty("error_code", out _) || root.TryGetProperty("error_message", out _)))
                        {
                            var errMsg = root.TryGetProperty("error_message", out var m) ? m.GetString() : "不明なエラー";
                            return (false, $"APIエラー: {errMsg}");
                        }
                        // 非 error メッセージ (tokens 等) を受信 → 接続成立とみなす。
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // グレース期間内に error が来なかった → キー有効 (正常系。 タイムアウト扱いにしない)。
                }
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
                Logger.Warn("送受信ループ停止がタイムアウト (Soniox)");
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
            {
                Logger.Debug($"送受信ループ停止中の想定内例外 (Soniox): {ex.GetType().Name}: {ex.Message}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"送受信ループ停止中の想定外例外 (Soniox): {ex.GetType().Name}: {ex.Message}");
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
                Logger.Debug($"WebSocket.CloseAsync 想定内例外 (Soniox): {ex.GetType().Name}: {ex.Message}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"WebSocket.CloseAsync 想定外例外 (Soniox): {ex.GetType().Name}: {ex.Message}");
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

        try
        {
            // Soniox は API キーを config メッセージで渡すため URL は素の endpoint のまま。
            var uri = new Uri(_settings.Endpoint);
            ValidateEndpoint(uri);
            await _ws.ConnectAsync(uri, ct).ConfigureAwait(false);
            Interlocked.Exchange(ref _reconnectAttempts, 0);
            Logger.Info("Soniox Realtime WebSocket 接続成功");

            // 受信・送信ループを起動。 SendAudio は State!=Connected で弾くので config 送信完了まで audio は流れない。
            _errorObservedSinceConnect = false; // loop 起動前にリセット (グレース期間中の error 観測検出用)。
            _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts!.Token), _cts!.Token);
            _sendTask = Task.Run(() => SendLoopAsync(_cts!.Token), _cts!.Token);

            // config メッセージ送信 (Soniox は pre-audio ack を返さない)。
            await SendConfigAsync(ct).ConfigureAwait(false);

            // config 直後に受信ループが in-band error (無効キー / 非対応言語) を処理して Failed に
            // している可能性がある。 短いグレースで観測してから Connected にする (Failed を上書きして
            // 拒否後に audio 送信を始めてしまうのを防ぐ。 Codex 指摘)。
            try { await Task.Delay(250, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }

            if (_ws is not { State: WebSocketState.Open })
                throw new InvalidOperationException("Soniox config 送信後にサーバーが接続を閉じました。");

            // Failed (fatal error) だけでなく、 グレース期間中に観測した非 fatal の config 拒否 (400 BadRequest 等)
            // でも connect を中断する。 これをしないと拒否されたセッションで audio capture を開始してしまう。
            if (_state == ConnectionState.Failed || _errorObservedSinceConnect)
                // 受信ループが既に ErrorReceived 発火済み。 Connected で上書きせず connect を中断する
                // (OpenAIApiException は下の catch で二重通知せず伝播)。
                throw new OpenAIApiException(OpenAIApiErrorKind.Unknown,
                    "Soniox 接続が config 直後に失敗しました (キー / 言語設定を確認してください)。", "config rejected");

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
                    "認証失敗（401）: Soniox APIキーが無効です。設定画面で正しいキーを入力してください。",
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
            // ProcessMessage が Soniox error で ErrorReceived (+ fatal 時 Failed) 済み。 二重通知せず伝播。
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error("Soniox WebSocket 接続失敗", ex);
            ErrorReceived?.Invoke(ex);
            SetState(ConnectionState.Disconnected);
            throw;
        }
    }

    private async Task SendConfigAsync(CancellationToken ct)
    {
        var bytes = BuildConfigMessage(_settings);
        Logger.Info($"Soniox config 送信: model='{_settings.Model}' targetLang='{ResolveLang(_settings.OutputLanguage)}'");
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

    private static string ResolveLang(string? lang)
        => string.IsNullOrWhiteSpace(lang) ? "ja" : lang;

    // config メッセージ (Soniox は接続後最初に 1 度だけ送る JSON)。
    // translation.type="one_way" + target_language で源言語自動判定 → ターゲットへ一方向翻訳。
    // audio_format=pcm_s16le / sample_rate=16000 / num_channels=1 で 16kHz mono PCM16 を binary 送信。
    private static byte[] BuildConfigMessage(SonioxSettings settings)
    {
        var config = new
        {
            api_key = settings.ApiKey,
            model = settings.Model,
            audio_format = "pcm_s16le",
            sample_rate = 16000,
            num_channels = 1,
            enable_endpoint_detection = true,
            translation = new
            {
                type = "one_way",
                target_language = ResolveLang(settings.OutputLanguage),
            },
        };
        return JsonSerializer.SerializeToUtf8Bytes(config);
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        // Soniox は生 PCM16 を binary フレームで受ける (base64 JSON 包装は不要)。 チャンクをまとめて送る。
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
                // 送信 PCM16 (2 bytes/sample, 16kHz mono) のサンプル数を累積。
                Interlocked.Add(ref _totalAudioInputSamples16kHz, audioBatch.WrittenCount / 2);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Error("Soniox WebSocket 送信ループエラー", ex);
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
                    Logger.Warn("Soniox WebSocket サーバーからクローズ受信");
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
            Logger.Warn("Soniox WebSocket 受信エラー", ex);
        }
        catch (Exception ex)
        {
            Logger.Error("Soniox WebSocket 受信ループ予期しないエラー", ex);
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

            // error: Soniox は {"error_code": <int>, "error_message": <string>} を返す。
            if (root.TryGetProperty("error_code", out _) || root.TryGetProperty("error_message", out _))
            {
                var originalMessage = root.TryGetProperty("error_message", out var m)
                    ? m.GetString() ?? "Unknown error" : "Unknown error";
                var code = root.TryGetProperty("error_code", out var c) ? c.ToString() : "";
                var kind = ClassifySonioxError(code, originalMessage);
                var friendly = SonioxFriendlyMessageFor(kind, originalMessage);
                var ex = new OpenAIApiException(kind, friendly, originalMessage);
                Logger.Error($"Soniox API エラー (kind={kind} code='{code}'): {LogFormatting.TruncateForLog(originalMessage)}");
                _errorObservedSinceConnect = true; // connect グレース期間中なら ConnectWebSocketAsync が拾って中断する。
                if (ex.IsFatal)
                {
                    _shouldReconnect = false;
                    SetState(ConnectionState.Failed);
                }
                ErrorReceived?.Invoke(ex);
                return;
            }

            // tokens: 翻訳トークン (translation_status == "translation") かつ確定 (is_final == true) のみを連結。
            // 源言語トークン (translation_status != "translation") は字幕に出さない。 非確定トークンは
            // 次メッセージで差し替わるため、 確定したものだけ delta として流す (二重表示・無限成長を防ぐ)。
            if (root.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Array)
            {
                LogFirstSighting("tokens");
                var sb = new StringBuilder();
                var sawEndpoint = false;
                foreach (var tok in tokens.EnumerateArray())
                {
                    if (tok.ValueKind != JsonValueKind.Object) continue;
                    var text = tok.TryGetProperty("text", out var t) ? t.GetString() : null;
                    // endpoint detection 有効時、 発話境界は特殊トークン "<end>" で届く。 字幕には出さず、
                    // ここで文を確定させる (短い無音区切りの発話が次発話と融合するのを防ぐ。 Codex 指摘)。
                    if (string.Equals(text, "<end>", StringComparison.Ordinal)) { sawEndpoint = true; continue; }
                    var status = tok.TryGetProperty("translation_status", out var st) ? st.GetString() : null;
                    if (!string.Equals(status, "translation", StringComparison.Ordinal)) continue;
                    var isFinal = tok.TryGetProperty("is_final", out var f) && f.ValueKind == JsonValueKind.True;
                    if (!isFinal) continue;
                    if (!string.IsNullOrEmpty(text)) sb.Append(text);
                }
                if (sb.Length > 0)
                {
                    var delta = sb.ToString();
                    var count = Interlocked.Increment(ref _totalDeltaCount);
                    if (LogFormatting.ShouldLogAtCount(count))
                        Logger.Info($"Soniox translation delta #{count}: '{LogFormatting.TruncateForLog(delta, 20)}' 長={delta.Length}");
                    TranscriptDeltaReceived?.Invoke(delta);
                }
                if (sawEndpoint)
                {
                    LogFirstSighting("endpoint");
                    TranscriptCompleted?.Invoke("");
                }
            }

            // finished: ストリーム終了シグナル。 空 done を流して Pipeline の trailing 確定を促す。
            if (root.TryGetProperty("finished", out var fin) && fin.ValueKind == JsonValueKind.True)
            {
                LogFirstSighting("finished");
                TranscriptCompleted?.Invoke("");
                _finishedSignal?.TrySetResult(true);
            }
        }
        catch (JsonException ex)
        {
            Logger.Warn("Soniox JSON パースエラー", ex);
        }
    }

    // Soniox は数値 error_code (HTTP 類似) を返す。 既知コード / メッセージを先に判定して fatal 種別に正しく
    // マッピングし (402 残高枯渇等は OpenAI 分類だと Unknown=非 fatal になり再接続ループに陥る)、 残りは
    // OpenAI 分類に委譲する (Codex 指摘)。
    internal static OpenAIApiErrorKind ClassifySonioxError(string code, string message)
    {
        var m = message ?? string.Empty;
        if (code == "402"
            || m.Contains("balance", StringComparison.OrdinalIgnoreCase)
            || m.Contains("quota", StringComparison.OrdinalIgnoreCase)
            || m.Contains("budget", StringComparison.OrdinalIgnoreCase)
            || m.Contains("exhausted", StringComparison.OrdinalIgnoreCase))
            return OpenAIApiErrorKind.QuotaExceeded;
        if (code == "401") return OpenAIApiErrorKind.InvalidApiKey;
        if (code == "403") return OpenAIApiErrorKind.Forbidden;
        if (code == "429") return OpenAIApiErrorKind.RateLimit;
        if (code == "400") return OpenAIApiErrorKind.BadRequest;
        return OpenAIApiException.Classify(message, code);
    }

    // Soniox error 用の日本語補助メッセージ (OpenAI の billing/api-keys URL でユーザーを誤誘導しないため差し替え)。
    internal static string SonioxFriendlyMessageFor(OpenAIApiErrorKind kind, string originalMessage) => kind switch
    {
        OpenAIApiErrorKind.QuotaExceeded =>
            "Soniox API のクォータ / 残高を超過しました。 Soniox ダッシュボードで利用状況・課金設定を確認してください (https://console.soniox.com)。",
        OpenAIApiErrorKind.InvalidApiKey =>
            "Soniox API キーが無効です。 設定画面で正しいキーを入力するか、 Soniox ダッシュボードで再発行してください (https://console.soniox.com)。",
        OpenAIApiErrorKind.RateLimit =>
            "Soniox API のレート制限に達しました。 しばらく待ってから再試行してください。",
        OpenAIApiErrorKind.Forbidden =>
            "Soniox API へのアクセス権限がありません。 プラン / モデル (stt-rt-v5) の利用可否を確認してください。",
        OpenAIApiErrorKind.BadRequest =>
            "Soniox API リクエストが不正でした。 設定値 (Model / Endpoint / OutputLanguage) を確認してください。",
        _ => string.IsNullOrWhiteSpace(originalMessage)
            ? "Soniox API から不明なエラーが返されました。"
            : $"Soniox API エラー: {originalMessage}",
    };

    private void LogFirstSighting(string eventType)
    {
        bool isFirst;
        lock (_seenEventTypesLock)
        {
            isFirst = _seenEventTypes.Count < MaxSeenEventTypes && _seenEventTypes.Add(eventType);
        }
        if (isFirst)
            Logger.Info($"Soniox Realtime event 初見: '{eventType}'");
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
                    Logger.Error($"Soniox 再接続上限 ({_settings.MaxReconnectAttempts}) 到達");
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
                Logger.Info($"Soniox 再接続試行 {currentAttempt}/{_settings.MaxReconnectAttempts}（{delay}ms 後）");

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
                    Logger.Warn("Soniox 再接続失敗", ex);
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
