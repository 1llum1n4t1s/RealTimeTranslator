using System.Buffers;
using System.Net.NetworkInformation;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SuperLightLogger;
using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.Core.Services;

// ConnectionState は Core.Models に移動済（Interface→Services 逆方向依存を解消するため）。

public sealed class OpenAIRealtimeClient : Interfaces.IRealtimeTranscriber
{
    private static readonly ILog Logger = LogManager.GetLogger<OpenAIRealtimeClient>();

    // 接続を許可する OpenAI Realtime のホスト。
    // settings.json が改竄されて任意の wss:// に Authorization ヘッダー（API キー）を送る経路を塞ぐ。
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.openai.com",
    };

    private static void ValidateEndpoint(Uri uri)
    {
        if (uri.Scheme != "wss")
            throw new InvalidOperationException(
                $"セキュアでない WebSocket スキーム '{uri.Scheme}' は許可されていません。Endpoint には wss:// を使用してください。");
        if (!AllowedHosts.Contains(uri.Host))
            throw new InvalidOperationException(
                $"Endpoint のホスト '{uri.Host}' は許可リスト外です（api.openai.com のみ）。settings.json を確認してください。");
    }

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private Task? _sendTask;
    private Channel<byte[]>? _sendChannel;
    private OpenAIRealtimeSettings _settings = new();
    private int _reconnectAttempts;
    private int _disposed;
    private int _reconnectInFlight; // 0 = idle, 1 = TryReconnectAsync 実行中（多重起動防止）
    private long _totalDroppedAudioChunks;
    private volatile bool _shouldReconnect;
    private volatile ConnectionState _state = ConnectionState.Disconnected;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    // 受信した event type を最初の 1 回だけ Info ログに出すための記憶（診断用）。
    // OpenAI Realtime Translation API がどんな event を返してくるか分からない状況で、
    // 「文が区切られず長文化する」原因を切り分けるためのフィールド。
    private readonly HashSet<string> _seenEventTypes = new(StringComparer.Ordinal);
    private readonly object _seenEventTypesLock = new();

    // ⚠️ 二重字幕対策 (2026-05-17 観測):
    // OpenAI Realtime API は同じ翻訳結果を `response.output_audio_transcript.*` と
    // `response.output_text.*` の 2 系統で並行発火するケースがあり、両方を素通しすると
    // 同じ文が別 SegmentId で 2 つ表示される (Discord 等で「今日はこれから行くよ。今日はこれから行くよ。」)。
    // 対策: 同じ response_id 内では「先に来た系統 (audio_transcript or text)」だけ採用し、
    //       後発の別系統は skip する。 単発フィールドで保持して辞書増殖を避ける。
    private string? _lastTranscriptResponseId;
    private string? _lastTranscriptEventGroup;
    private readonly object _transcriptDedupeLock = new();
    // 受信したdelta件数（done発火時の診断のため）
    private long _totalDeltaCount;
    private long _totalDoneCount;

    public ConnectionState State => _state;

    /// <summary>
    /// 送信前にチャネルから DropOldest で破棄された音声チャンクの累計数。
    /// 字幕が抜けた原因が NW 詰まりか API 遅延か判別する診断メトリクス。
    /// </summary>
    public long DroppedAudioChunkCount => Interlocked.Read(ref _totalDroppedAudioChunks);

    public OpenAIRealtimeClient()
    {
        // ネットワーク復帰イベントで再接続カウンタをリセットする。モバイル NW 切替や
        // 一時切断（5〜30 秒）で MaxReconnectAttempts に達した後でも、復帰後に再試行できるようにする。
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        // Dispose 後に発火する経路 (.NET イベントの invocation list は発火開始時にコピー
        // されるので、 ハンドラ解除と発火がレースすると Dispose 後にもハンドラが走る) を
        // 早期 return で塞ぐ。 これがないと _connectLock.WaitAsync で ObjectDisposedException
        // が UnobservedTaskException 経由でログに飛ぶ (rere P1 #5)。
        if (Interlocked.CompareExchange(ref _disposed, 0, 0) != 0) return;
        if (!e.IsAvailable) return;
        if (!_shouldReconnect) return;
        if (_state == ConnectionState.Connected) return;

        Logger.Info("ネットワーク復帰検知: 再接続カウンタをリセットして再接続を試みます");
        Interlocked.Exchange(ref _reconnectAttempts, 0);
        // Failed 状態だったら Reconnecting に戻して再試行ループを再起動
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
            _sendChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(100)
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
        // 再接続中・接続前は音声を捨てる（新セッションでコンテキスト連続性がないため送っても無駄）。
        if (State != ConnectionState.Connected) return;

        var writer = _sendChannel?.Writer;
        if (writer is null) return;

        if (!writer.TryWrite(pcm16Audio))
        {
            // BoundedChannel(100, DropOldest) で最古チャンクが捨てられた場合に到達する経路。
            // TryWrite 自体は DropOldest でも true を返すのが通常だが、Channel が Complete されていると false。
            // どちらにせよ書けなかった事実をメトリクス化する。
            Interlocked.Increment(ref _totalDroppedAudioChunks);
        }
    }

    public async Task DisconnectAsync()
    {
        _shouldReconnect = false;

        // 進行中の接続試行 (TryReconnectAsync が _connectLock を保持したまま ClientWebSocket.ConnectAsync を
        // 待っているケース) を即座に中断させる。これがないと _connectLock.WaitAsync が永久ブロックして
        // 「停止」ボタン押下後にアプリがフリーズする。
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { /* race: 既に Dispose 済み */ }

        // _connectLock.WaitAsync にも上限を設ける。`_cts.Cancel()` で進行中の接続は即抜けるはずだが、
        // 万一抜けない経路（NAudio / WebSocket 内部の同期 wait 等）でも UI を 3 秒以上ブロックしない。
        if (!await _connectLock.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false))
        {
            Logger.Warn("DisconnectAsync: _connectLock 取得が 3 秒でタイムアウト、強制クリーンアップに進む");
            // lock を取らずに CleanupAsync を呼ぶ。_shouldReconnect=false + cts.Cancel 済みのため
            // 並走する TryReconnectAsync はもう再接続を試みず、後続のクリーンアップだけ走る。
            try { await CleanupAsync().ConfigureAwait(false); }
            catch (Exception ex) { Logger.Warn("DisconnectAsync(timeout): CleanupAsync で例外", ex); }
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
        OpenAIRealtimeSettings settings, CancellationToken ct = default)
    {
        var uri = new Uri($"{settings.Endpoint}?model={Uri.EscapeDataString(settings.Model)}");

        try
        {
            ValidateEndpoint(uri);
        }
        catch (InvalidOperationException ex)
        {
            return (false, ex.Message);
        }

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
        // NAT 越え / プロキシ / モバイル NW で半切断（TCP は生きているように見えるがサーバ応答なし）を
        // 検知できるよう、ping/pong + pong タイムアウトを明示設定する。.NET 8+ で利用可能。
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        // rere I-1: 15s/20s だと RTT 揺れ (LTE 切替 / Wi-Fi ローミング / OpenAI 側 GC stall) で
        // false-positive 切断が起きやすい。 30s に伸ばして接続安定性を改善する。
        _ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(30);
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {_settings.ApiKey}");

        var uri = new Uri($"{_settings.Endpoint}?model={Uri.EscapeDataString(_settings.Model)}");

        // スキーム + ホスト両方を検証（settings.json 改竄で API キーを攻撃者サーバに送る経路を塞ぐ）。
        ValidateEndpoint(uri);

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
        // OpenAI Realtime Translation エンドポイント (/v1/realtime/translations) は
        // turn_detection を session.update でカスタマイズできない:
        //   - session.audio.input.turn_detection → Unknown parameter (v1.0.2 で発覚)
        //   - session.turn_detection           → Unknown parameter (v1.0.6 で発覚)
        // → turn_detection を送らない（デフォルトの server VAD に任せる）。
        // 通常の /v1/realtime エンドポイントとは仕様が異なる点に注意。

        // 出力言語が空 / null だと API 側でデフォルト（おそらく英語）にフォールバックして、
        // ユーザーが「日本語に翻訳されない」体験になる。settings.json の
        // OpenAIRealtime.OutputLanguage が空のときは "ja" にフォールバック。
        var outputLanguage = string.IsNullOrWhiteSpace(_settings.OutputLanguage)
            ? "ja"
            : _settings.OutputLanguage;

        Logger.Info($"OpenAI Realtime session.update: output.language='{outputLanguage}' (settings='{_settings.OutputLanguage ?? "<null>"}')");

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
                    output = new { language = outputLanguage }
                }
            }
        };

        var json = JsonSerializer.Serialize(sessionUpdate);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws!.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        // ホットパス最適化:
        // 旧: 匿名型 -> JsonSerializer.Serialize (string) -> Encoding.UTF8.GetBytes (byte[]) -> SendAsync
        //   1 メッセージあたり ~25KB 以上を Gen0 ヒープに通過させていた。
        // 新: ArrayBufferWriter<byte> + Utf8JsonWriter で UTF8 byte に直書き、ループ間で再利用。
        //   string 中間表現と byte[] コピーを排除し、Base64 string のみが allocate される（こちらは後の最適化対象）。
        var bufferWriter = new ArrayBufferWriter<byte>(initialCapacity: 8192);
        var jsonWriter = new Utf8JsonWriter(bufferWriter, new JsonWriterOptions { SkipValidation = true });

        try
        {
            await foreach (var audioData in _sendChannel!.Reader.ReadAllAsync(ct))
            {
                if (_ws is not { State: WebSocketState.Open }) continue;

                bufferWriter.ResetWrittenCount();
                jsonWriter.Reset(bufferWriter);
                jsonWriter.WriteStartObject();
                // OpenAI Realtime Translation エンドポイント (/v1/realtime/translations) は
                // `session.input_audio_buffer.append` 形式（session. プレフィックス必須）。
                // 通常の /v1/realtime エンドポイントの `input_audio_buffer.append` ではないので注意。
                jsonWriter.WriteString("type", "session.input_audio_buffer.append");
                jsonWriter.WriteString("audio", Convert.ToBase64String(audioData));
                jsonWriter.WriteEndObject();
                await jsonWriter.FlushAsync(ct).ConfigureAwait(false);

                await _ws.SendAsync(bufferWriter.WrittenMemory, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Error("WebSocket 送信ループエラー", ex);
            ErrorReceived?.Invoke(ex);
        }
        finally
        {
            await jsonWriter.DisposeAsync().ConfigureAwait(false);
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

                // UTF-16 string 化を経由せず byte のまま Parse することで、
                // メッセージ 1 件あたり 2 倍幅の string allocation を回避する。
                var length = (int)messageStream.Length;
                var jsonMemory = messageStream.GetBuffer().AsMemory(0, length);
                ProcessMessage(jsonMemory);
                messageStream.SetLength(0);
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

    // rere A2-006: 深ネスト JSON 攻撃を防ぐ MaxDepth 制限。
    // OpenAI Realtime API のレスポンスは通常 5-10 階層なので 32 で余裕、
    // 深ネスト時は JsonException で早期失敗してログに残す。
    private static readonly JsonDocumentOptions s_jsonDocumentOptions = new() { MaxDepth = 32 };

    private void ProcessMessage(ReadOnlyMemory<byte> json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, s_jsonDocumentOptions);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeElement)) return;
            var type = typeElement.GetString();
            if (string.IsNullOrEmpty(type)) return;

            // 診断ログ: 初見のevent typeはInfoで1回だけ吐く（文が区切られない問題のため）。
            // 既知typeも含めて全部最初の1回はログに出すことで、API実装が変わって新event名が来ても気付ける。
            bool isFirstSighting;
            lock (_seenEventTypesLock)
            {
                isFirstSighting = _seenEventTypes.Add(type);
            }
            if (isFirstSighting)
            {
                Logger.Info($"OpenAI Realtime event 初見: type='{type}'");
            }

            switch (type)
            {
                // 翻訳結果のストリーミング（delta）
                // ・現行 GPT Realtime API: response.output_audio_transcript.delta / response.output_text.delta
                // ・Translation 専用エンドポイント: session.output_transcript.delta / output_transcript.delta
                // ・互換のため旧 response.audio_transcript.delta も残す
                // 参照: https://platform.openai.com/docs/api-reference/realtime-server-events
                case "response.output_audio_transcript.delta":
                case "response.output_text.delta":
                case "session.output_transcript.delta":
                case "response.audio_transcript.delta":
                case "output_transcript.delta":
                    if (root.TryGetProperty("delta", out var delta))
                    {
                        var deltaStr = delta.GetString() ?? "";
                        // 二重字幕対策: 同じ response_id 内の別系統 (audio_transcript vs text) なら skip
                        if (!ShouldProcessTranscriptEvent(root, type))
                            break;
                        var count = Interlocked.Increment(ref _totalDeltaCount);
                        // 頻度抑制: delta は 1 文字〜数文字単位で高頻度に来るため、 全件 Info にすると
                        // ログが爆発する。 1, 10, 50, 100, ... と間引いて累計と直近 delta を観測する。
                        if (ShouldLogAtCount(count))
                        {
                            Logger.Info($"transcript.delta #{count}: type='{type}' 直近delta='{TruncateForLog(deltaStr, 20)}' 直近delta長={deltaStr.Length}");
                        }
                        TranscriptDeltaReceived?.Invoke(deltaStr);
                    }
                    break;

                // 翻訳結果の完了（done）— done では transcript または text プロパティに最終結果が入る
                case "response.output_audio_transcript.done":
                case "response.output_text.done":
                case "session.output_transcript.done":
                case "response.audio_transcript.done":
                case "output_transcript.done":
                {
                    var transcript = "";
                    if (root.TryGetProperty("transcript", out var t))
                        transcript = t.GetString() ?? "";
                    else if (root.TryGetProperty("text", out var txt))
                        transcript = txt.GetString() ?? "";

                    // 二重字幕対策: 同じ response_id 内の別系統なら skip (delta と同じガード)
                    if (!ShouldProcessTranscriptEvent(root, type))
                        break;

                    var doneCount = Interlocked.Increment(ref _totalDoneCount);
                    var deltaCount = Interlocked.Read(ref _totalDeltaCount);
                    Logger.Info($"transcript.done 受信: type='{type}' transcript長={transcript.Length} 内容='{TruncateForLog(transcript)}' 累計done={doneCount} 累計delta={deltaCount}");

                    TranscriptCompleted?.Invoke(transcript);
                    break;
                }

                // VAD / 入力バッファ系イベント（segment 区切りの間接シグナル）
                // 文が区切られない問題の診断のため明示的にログ化。done が来ない場合は
                // input_audio_buffer.committed / response.done を fallback の区切りとして使う。
                case "input_audio_buffer.speech_started":
                case "session.input_audio_buffer.speech_started":
                    Logger.Debug($"VAD: speech_started ({type})");
                    break;
                case "input_audio_buffer.speech_stopped":
                case "session.input_audio_buffer.speech_stopped":
                    Logger.Debug($"VAD: speech_stopped ({type})");
                    break;
                case "input_audio_buffer.committed":
                case "session.input_audio_buffer.committed":
                    Logger.Debug($"VAD: buffer committed ({type})");
                    break;

                // response.done: transcript.done が来ない API 仕様の場合の fallback 区切り。
                // 既に transcript.done で TranscriptCompleted を発火していれば、Pipeline 側で
                // 空 transcript の done として処理される（_accumulatedText が空なのでスキップされる）。
                case "response.done":
                case "session.response.done":
                {
                    var deltaCount = Interlocked.Read(ref _totalDeltaCount);
                    var doneCount = Interlocked.Read(ref _totalDoneCount);
                    Logger.Info($"response.done 受信: type='{type}' 累計delta={deltaCount} 累計transcript.done={doneCount}");
                    // transcript.done が一度も来ないまま response.done が来ているなら、
                    // それを区切りとして使う（空 transcript で発火 → Pipeline 側の fallbackText で確定）
                    TranscriptCompleted?.Invoke("");
                    break;
                }

                case "error":
                {
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
        }
        catch (JsonException ex)
        {
            Logger.Warn("JSON パースエラー", ex);
        }
    }

    private async Task TryReconnectAsync()
    {
        // CompareExchange で複数経路（ReceiveLoop 終了 + 末尾の再試行）から同時に呼ばれても
        // 1 つだけが実行される。while ループで全試行を消費し、末尾の自己再帰起動を排除する。
        if (Interlocked.CompareExchange(ref _reconnectInFlight, 1, 0) != 0)
            return;

        try
        {
            while (_shouldReconnect
                   && _state != ConnectionState.Connected
                   && _state != ConnectionState.Failed)
            {
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
                var baseDelay = (int)Math.Min((long)_settings.ReconnectDelayMs << shift, 30000L);
                // 同期再接続で OpenAI 側に集中アクセスしないよう ±20% の jitter を加える。
                // Random.Shared は thread-safe (.NET 6+)。
                var jitterPercent = (Random.Shared.NextDouble() * 0.4) - 0.2;
                var delay = (int)Math.Clamp(baseDelay * (1.0 + jitterPercent), 100, 30000);
                Logger.Info($"再接続試行 {_reconnectAttempts}/{_settings.MaxReconnectAttempts}（{delay}ms 後、base={baseDelay}ms）");

                // ⭐ rere P1 #7: Task.Delay と _connectLock.WaitAsync の両方に CancellationToken を渡して、
                // Disconnect / Dispose / アプリ終了で即座に reconnect ループを抜けられるようにする。
                // 旧実装は ct 未指定で、 停止後も最大 30 秒 reconnect 試行が走り続け、 ログ汚染と
                // バッテリー消費の原因になっていた。
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
                    _sendChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(100)
                    {
                        FullMode = BoundedChannelFullMode.DropOldest,
                        SingleReader = true
                    });

                    await ConnectWebSocketAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    Logger.Warn("再接続失敗", ex);
                    // ConnectWebSocketAsync が失敗してもループ継続して次の試行へ
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

    /// <summary>
    /// 二重字幕対策: 同じ response_id 内で「最初に来た系統 (audio_transcript or text)」だけ採用し、
    /// 後発の別系統 (output_text vs output_audio_transcript) は skip する。
    ///
    /// OpenAI Realtime API は同じ翻訳結果を以下の 2 系統で並行発火することがある:
    ///   - `response.output_audio_transcript.delta/done`: 音声出力に紐付く transcript
    ///   - `response.output_text.delta/done`: テキスト出力
    /// この両方を素通しすると、 1 つの response に対して同じ翻訳結果が 2 回 emit され、
    /// overlay に同じ文が別 SegmentId で並ぶ (2026-05-17 ゆろさん観測、 Discord で発生)。
    ///
    /// response_id を取得して保存し、 同 id 内の別系統 event は無視する。 新しい response_id が
    /// 来たら採用系統を切り替える。 単発フィールドで保持して辞書増殖を回避。
    /// </summary>
    private bool ShouldProcessTranscriptEvent(JsonElement root, string eventType)
    {
        if (!root.TryGetProperty("response_id", out var ridElement))
            return true; // response_id 取れない API バリアントは従来通り通す
        var responseId = ridElement.GetString();
        if (string.IsNullOrEmpty(responseId))
            return true;

        // event 系統を判定: text 系か、 audio_transcript / transcript 系か。
        // session.output_transcript / output_transcript / audio_transcript 系は全部「transcript」枠扱い。
        var group = eventType.Contains("output_text") ? "text" : "transcript";

        lock (_transcriptDedupeLock)
        {
            if (_lastTranscriptResponseId == responseId)
            {
                if (_lastTranscriptEventGroup == group)
                    return true; // 同 response_id 同系統の続き (delta が連続して来る正常経路)
                // 同 response_id 異系統 = 二重発火 → skip
                Logger.Info($"二重 transcript event を抑止: response_id={responseId} 採用='{_lastTranscriptEventGroup}' 抑止='{group}' eventType='{eventType}'");
                return false;
            }
            // 新しい response_id → 採用系統を更新
            _lastTranscriptResponseId = responseId;
            _lastTranscriptEventGroup = group;
            return true;
        }
    }

    // ログ整形ヘルパーは Core/Services/LogFormatting.cs に集約 (rere D1 修正)。
    // TruncateForLog / ShouldLogAtCount は static usings で簡潔化できるが、
    // 全 caller の Logger 呼び出しを 1 箇所ずつ修正する必要があるため、
    // ここでは local using で受けて短縮名のままにする。
    private static string TruncateForLog(string? text, int maxLength = LogFormatting.DefaultTruncateLength)
        => LogFormatting.TruncateForLog(text, maxLength);

    private static bool ShouldLogAtCount(long count)
        => LogFormatting.ShouldLogAtCount(count);
}
