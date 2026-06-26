using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Translation;
using SuperLightLogger;
using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.Core.Services;

/// <summary>
/// Azure AI Speech 翻訳の Realtime クライアント。 他プロバイダ (OpenAI/Gemini/Soniox/Speechmatics) は
/// 生 WebSocket を自前実装するが、 Azure は公開された生 WebSocket 翻訳プロトコルを持たないため
/// 公式 Speech SDK (<c>Microsoft.CognitiveServices.Speech</c>) の <see cref="TranslationRecognizer"/> +
/// <see cref="PushAudioInputStream"/> を <see cref="Interfaces.IRealtimeTranscriber"/> でラップする。
///
/// 主な特徴 / 差分:
///  - KeepAlive / セッション内の一時的な切断回復は SDK が内部管理するが、 Canceled(Error) で打ち切られた
///    場合は SDK が自動再開しないため、 非 fatal エラー時のみ本クラスが recognizer を張り直す reconnect を持つ
///  - 入力音声: <b>16kHz</b>/PCM16/mono (<see cref="InputSampleRate"/>=16000)。 PushStream に生 PCM16 を Write
///  - 源言語を明示指定する (SpeechRecognitionLanguage はロケール、 例 "en-US")。 出力言語へ翻訳
///  - <see cref="TranslationRecognizer.Recognized"/> の確定翻訳のみを delta として流す
///    (Recognizing の partial は破棄 — 確定だけ使い二重表示・無限成長を防ぐ)
///  - エラーは <see cref="TranslationRecognizer.Canceled"/> で受け、 <see cref="OpenAIApiException"/> に正規化
/// </summary>
public sealed class AzureSpeechTranslationClient : Interfaces.IRealtimeTranscriber
{
    private static readonly ILog Logger = LogManager.GetLogger<AzureSpeechTranslationClient>();

    private AzureSpeechSettings _settings = new();
    private TranslationRecognizer? _recognizer;
    private PushAudioInputStream? _pushStream;
    private AudioConfig? _audioConfig;
    private string _targetLang = "ja";

    private int _disposed;
    private long _totalDroppedAudioChunks;
    private long _totalAudioInputSamples16kHz;
    private long _totalDeltaCount;
    private volatile ConnectionState _state = ConnectionState.Disconnected;
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    // Azure SDK は Canceled(Error) 後に自動再開しないため、 非 fatal エラーでは自前で recognizer を張り直す。
    private volatile bool _shouldReconnect;
    private int _reconnectAttempts;
    private int _reconnectInFlight;
    // 終了要求 (Disconnect/Dispose) を reconnect ループへ伝える CTS と、 停止を await するためのタスク参照。
    // fire-and-forget だと _connectLock を待機/保持中の reconnect を待たずに Dispose して ObjectDisposedException
    // になるため、 必ず cancel + await してから cleanup / dispose する。
    private CancellationTokenSource _reconnectShutdownCts = new();
    private Task _reconnectLoopTask = Task.CompletedTask;
    private readonly object _reconnectTaskLock = new();

    public ConnectionState State => _state;

    /// <inheritdoc />
    public int InputSampleRate => 16000;

    /// <inheritdoc />
    public long DroppedAudioChunkCount => Interlocked.Read(ref _totalDroppedAudioChunks);

    /// <inheritdoc />
    public long TotalAudioInputSamples24kHz => (long)(Interlocked.Read(ref _totalAudioInputSamples16kHz) * 1.5);

    /// <inheritdoc />
    public long ServerReportedAudioInputTokens => 0;

    public event Action<string>? TranscriptDeltaReceived;
    public event Action<string>? TranscriptCompleted;
    public event Action<Exception>? ErrorReceived;
    public event Action<ConnectionState>? StateChanged;

    // Azure 翻訳ターゲットコードへ正規化 (ほとんどは ISO 639-1 短縮形そのまま。 中文のみ簡体字を既定に)。
    private static readonly Dictionary<string, string> AzureTargetLanguageMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zh"] = "zh-Hans",
    };

    internal static string MapToAzureTargetLanguage(string? lang)
    {
        var resolved = string.IsNullOrWhiteSpace(lang) ? "ja" : lang;
        return AzureTargetLanguageMap.TryGetValue(resolved, out var mapped) ? mapped : resolved;
    }

    private static string ResolveSourceLocale(string? locale)
        => string.IsNullOrWhiteSpace(locale) ? "en-US" : locale;

    /// <summary>
    /// Azure 固有設定で接続する。 TranslationPipelineService はこの具象メソッドを直接呼ぶ (Provider=Azure)。
    /// </summary>
    public async Task ConnectAsync(AzureSpeechSettings settings, CancellationToken ct = default)
    {
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State == ConnectionState.Connected)
            {
                Logger.Warn("既に接続済み。先に切断します。(Azure)");
                await CleanupAsync().ConfigureAwait(false);
            }

            _settings = new AzureSpeechSettings
            {
                ApiKey = settings.ApiKey,
                Region = settings.Region,
                OutputLanguage = settings.OutputLanguage,
                SourceLanguage = settings.SourceLanguage,
                ReconnectDelayMs = settings.ReconnectDelayMs,
                MaxReconnectAttempts = settings.MaxReconnectAttempts,
                SilencePaddingMs = settings.SilencePaddingMs,
                MaxPartialChars = settings.MaxPartialChars,
            };

            Interlocked.Exchange(ref _totalAudioInputSamples16kHz, 0);
            Interlocked.Exchange(ref _totalDroppedAudioChunks, 0);
            Interlocked.Exchange(ref _totalDeltaCount, 0);
            Interlocked.Exchange(ref _reconnectAttempts, 0);
            _shouldReconnect = true;
            // 前回 disconnect でキャンセル済みなら shutdown CTS を作り直す (新セッションの reconnect が即終了しないように)。
            // 直前の reconnect は DisconnectAsync の StopReconnectLoopAsync で停止・await 済みのため安全に差し替えられる。
            if (_reconnectShutdownCts.IsCancellationRequested)
            {
                _reconnectShutdownCts.Dispose();
                _reconnectShutdownCts = new CancellationTokenSource();
            }

            if (string.IsNullOrWhiteSpace(_settings.ApiKey) || string.IsNullOrWhiteSpace(_settings.Region))
                throw new InvalidOperationException("Azure Speech の APIキー / Region が設定されていません。");

            SetState(ConnectionState.Connecting);
            try
            {
                StartRecognizer();
                await _recognizer!.StartContinuousRecognitionAsync().WaitAsync(ct).ConfigureAwait(false);
                Logger.Info($"Azure Speech 翻訳開始: region='{_settings.Region}' source='{ResolveSourceLocale(_settings.SourceLanguage)}' target='{_targetLang}'");
                SetState(ConnectionState.Connected);
            }
            catch
            {
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
    /// <see cref="ConnectAsync(AzureSpeechSettings, CancellationToken)"/> を呼ぶ。 region 不明のため
    /// 既定 region で委譲する (実用は具象経路)。
    /// </summary>
    async Task Interfaces.IRealtimeTranscriber.ConnectAsync(OpenAIRealtimeSettings settings, CancellationToken ct)
    {
        var mapped = new AzureSpeechSettings
        {
            ApiKey = settings.ApiKey,
            OutputLanguage = settings.OutputLanguage,
            ReconnectDelayMs = settings.ReconnectDelayMs,
            MaxReconnectAttempts = settings.MaxReconnectAttempts,
        };
        await ConnectAsync(mapped, ct).ConfigureAwait(false);
    }

    private void StartRecognizer()
    {
        // SpeechTranslationConfig は Speech SDK 1.43 では IDisposable 非実装のため using 不可 (CS1674)。
        var config = SpeechTranslationConfig.FromSubscription(_settings.ApiKey, _settings.Region);
        config.SpeechRecognitionLanguage = ResolveSourceLocale(_settings.SourceLanguage);
        _targetLang = MapToAzureTargetLanguage(_settings.OutputLanguage);
        config.AddTargetLanguage(_targetLang);

        // 16kHz / 16bit / mono PCM の push stream。 TranslationPipelineService が 16k にリサンプルした PCM16 を流す。
        using var format = AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1);
        _pushStream = AudioInputStream.CreatePushStream(format);
        _audioConfig = AudioConfig.FromStreamInput(_pushStream);
        _recognizer = new TranslationRecognizer(config, _audioConfig);

        _recognizer.Recognized += OnRecognized;
        _recognizer.Canceled += OnCanceled;
        _recognizer.SessionStopped += OnSessionStopped;
    }

    private void OnRecognized(object? sender, TranslationRecognitionEventArgs e)
    {
        // 確定 (Recognized) の翻訳のみを delta として流す。 partial (Recognizing) は破棄。
        if (e.Result.Reason != ResultReason.TranslatedSpeech) return;
        if (!e.Result.Translations.TryGetValue(_targetLang, out var text) || string.IsNullOrEmpty(text)) return;

        var count = Interlocked.Increment(ref _totalDeltaCount);
        if (LogFormatting.ShouldLogAtCount(count))
            Logger.Info($"Azure 翻訳確定 #{count}: '{LogFormatting.TruncateForLog(text, 20)}' 長={text.Length}");
        TranscriptDeltaReceived?.Invoke(text);
        // 1 発話の区切り。 空 done を流して Pipeline の trailing 確定を促す。
        TranscriptCompleted?.Invoke("");
    }

    private void OnCanceled(object? sender, TranslationRecognitionCanceledEventArgs e)
    {
        if (e.Reason != CancellationReason.Error)
        {
            Logger.Info($"Azure 認識キャンセル (理由={e.Reason})");
            return;
        }

        var kind = e.ErrorCode switch
        {
            CancellationErrorCode.AuthenticationFailure => OpenAIApiErrorKind.InvalidApiKey,
            CancellationErrorCode.Forbidden => OpenAIApiErrorKind.Forbidden,
            CancellationErrorCode.TooManyRequests => OpenAIApiErrorKind.RateLimit,
            CancellationErrorCode.BadRequest => OpenAIApiErrorKind.BadRequest,
            _ => OpenAIApiErrorKind.Unknown,
        };
        var friendly = AzureFriendlyMessageFor(kind, e.ErrorDetails ?? string.Empty);
        var ex = new OpenAIApiException(kind, friendly, e.ErrorDetails ?? $"ErrorCode={e.ErrorCode}");
        Logger.Error($"Azure 認識エラー (kind={kind} code={e.ErrorCode}): {LogFormatting.TruncateForLog(e.ErrorDetails)}");
        ErrorReceived?.Invoke(ex);

        if (ex.IsFatal)
        {
            // 回復不能 (認証 / クォータ / 権限)。 再接続せず Failed に倒す → pipeline がキャプチャ停止。
            _shouldReconnect = false;
            SetState(ConnectionState.Failed);
            return;
        }

        // 非 fatal (レート制限 / BadRequest / 一時的なサービス・ネットワーク断)。 Azure SDK は Canceled(Error)
        // 後に自動再開しないため、 Disconnected のまま放置すると SendAudio が黙って捨てられ「繋がってるのに
        // 字幕が出ない」状態になる。 recognizer を張り直す reconnect を起動する (Codex 指摘)。
        if (_shouldReconnect && Interlocked.CompareExchange(ref _disposed, 0, 0) == 0)
        {
            SetState(ConnectionState.Reconnecting);
            lock (_reconnectTaskLock)
            {
                // 同時に複数走らせない (in-flight ガードもあるが、 await 対象のタスク参照を 1 本に保つ)。
                if (_reconnectLoopTask.IsCompleted)
                {
                    var token = _reconnectShutdownCts.Token;
                    _reconnectLoopTask = Task.Run(() => TryReconnectAsync(token), CancellationToken.None);
                }
            }
        }
        else
        {
            SetState(ConnectionState.Disconnected);
        }
    }

    /// <summary>
    /// 非 fatal な Canceled(Error) 後に recognizer を張り直す。 他クライアントの WebSocket reconnect と同型
    /// (指数バックオフ + 上限 + in-flight ガード)。 _shouldReconnect=false (= ユーザー停止) で抜ける。
    /// </summary>
    private async Task TryReconnectAsync(CancellationToken token)
    {
        if (Interlocked.CompareExchange(ref _reconnectInFlight, 1, 0) != 0) return;
        try
        {
            while (_shouldReconnect
                   && !token.IsCancellationRequested
                   && _state != ConnectionState.Connected
                   && _state != ConnectionState.Failed
                   && Interlocked.CompareExchange(ref _disposed, 0, 0) == 0)
            {
                var attempt = Interlocked.Increment(ref _reconnectAttempts);
                if (attempt > _settings.MaxReconnectAttempts)
                {
                    Logger.Error($"Azure 再接続上限 ({_settings.MaxReconnectAttempts}) 到達");
                    _shouldReconnect = false;
                    SetState(ConnectionState.Failed);
                    ErrorReceived?.Invoke(new InvalidOperationException(
                        $"Azure 再接続の上限（{_settings.MaxReconnectAttempts}回）に達しました。接続を確認してください。"));
                    return;
                }

                var shift = Math.Min(attempt - 1, 30);
                var baseDelay = (int)Math.Min((long)_settings.ReconnectDelayMs << shift, 30000L);
                var jitter = (Random.Shared.NextDouble() * 0.4) - 0.2;
                var delay = (int)Math.Clamp(baseDelay * (1.0 + jitter), 100, 30000);
                Logger.Info($"Azure 再接続試行 {attempt}/{_settings.MaxReconnectAttempts}（{delay}ms 後）");
                try { await Task.Delay(delay, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }

                if (!_shouldReconnect || token.IsCancellationRequested) return;

                // lock 取得は try/finally の外。 OCE で取得失敗した場合に Release してしまわないため。
                try { await _connectLock.WaitAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                try
                {
                    if (!_shouldReconnect || token.IsCancellationRequested
                        || _state == ConnectionState.Connected || _state == ConnectionState.Failed)
                        return;

                    await CleanupRecognizerAsync().ConfigureAwait(false);
                    SetState(ConnectionState.Connecting);
                    StartRecognizer();
                    await _recognizer!.StartContinuousRecognitionAsync().WaitAsync(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
                    Logger.Info("Azure 再接続成功");
                    Interlocked.Exchange(ref _reconnectAttempts, 0);
                    SetState(ConnectionState.Connected);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"Azure 再接続失敗: {ex.GetType().Name}: {ex.Message}");
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

    /// <summary>
    /// reconnect ループに終了を要求し、 完了を待つ。 Disconnect / Dispose から呼ぶ。 これを待たずに
    /// _connectLock / CTS を破棄すると、 lock 待機/保持中の reconnect が ObjectDisposedException になる。
    /// </summary>
    private async Task StopReconnectLoopAsync()
    {
        _shouldReconnect = false;
        try { _reconnectShutdownCts.Cancel(); }
        catch (ObjectDisposedException) { }

        Task t;
        lock (_reconnectTaskLock) { t = _reconnectLoopTask; }
        if (!t.IsCompleted)
        {
            try { await t.WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                Logger.Warn($"Azure reconnect ループ停止待ちが想定内例外: {ex.GetType().Name}");
            }
        }
    }

    private void OnSessionStopped(object? sender, SessionEventArgs e)
    {
        Logger.Info("Azure セッション停止");
    }

    internal static string AzureFriendlyMessageFor(OpenAIApiErrorKind kind, string originalMessage) => kind switch
    {
        OpenAIApiErrorKind.QuotaExceeded =>
            "Azure Speech のクォータ / 残高を超過しました。 Azure ポータルで利用状況・課金を確認してください。",
        OpenAIApiErrorKind.InvalidApiKey =>
            "Azure Speech のキーが無効です。 設定画面で正しいキーと Region を入力してください (Azure ポータルの Speech リソース「キーとエンドポイント」)。",
        OpenAIApiErrorKind.RateLimit =>
            "Azure Speech のレート制限に達しました。 しばらく待ってから再試行してください。",
        OpenAIApiErrorKind.Forbidden =>
            "Azure Speech へのアクセス権限がありません。 リソースのプラン / Region を確認してください。",
        OpenAIApiErrorKind.BadRequest =>
            "Azure Speech のリクエストが不正でした。 源言語ロケール / 出力言語 / Region を確認してください。",
        _ => string.IsNullOrWhiteSpace(originalMessage)
            ? "Azure Speech から不明なエラーが返されました。 キー / Region / ネットワークを確認してください。"
            : $"Azure Speech エラー: {originalMessage}",
    };

    public void SendAudio(byte[] pcm16Audio)
    {
        if (pcm16Audio is null || pcm16Audio.Length == 0) return;
        if (State != ConnectionState.Connected) return;

        var push = _pushStream;
        if (push is null)
        {
            Interlocked.Increment(ref _totalDroppedAudioChunks);
            return;
        }

        try
        {
            push.Write(pcm16Audio);
            Interlocked.Add(ref _totalAudioInputSamples16kHz, pcm16Audio.Length / 2);
        }
        catch (Exception ex)
        {
            // push stream が既に閉じられている (停止中) 等。 落とさず破棄カウントだけ進める。
            Interlocked.Increment(ref _totalDroppedAudioChunks);
            Logger.Debug($"Azure PushStream.Write 失敗 (破棄): {ex.GetType().Name}: {ex.Message}");
        }
    }

    public async Task DisconnectAsync()
    {
        // 先に reconnect ループを cancel + await してから lock を取る (lock 競合 / 破棄後アクセスを防ぐ)。
        await StopReconnectLoopAsync().ConfigureAwait(false);
        if (!await _connectLock.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false))
        {
            Logger.Warn("DisconnectAsync: _connectLock 取得が 3 秒でタイムアウト、強制クリーンアップに進む (Azure)");
            try { await CleanupAsync().ConfigureAwait(false); }
            catch (Exception ex) { Logger.Warn("DisconnectAsync(timeout): CleanupAsync で例外 (Azure)", ex); }
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

    // recognizer / pushStream / audioConfig だけを破棄する (状態遷移は呼び出し元に委ねる)。
    // reconnect は Connecting → Connected に進めたいので Disconnected を挟みたくない。
    private async Task CleanupRecognizerAsync()
    {
        var recognizer = _recognizer;
        if (recognizer != null)
        {
            // 先にハンドラを外す → 破棄に伴う Canceled で reconnect が二重起動するのを防ぐ。
            recognizer.Recognized -= OnRecognized;
            recognizer.Canceled -= OnCanceled;
            recognizer.SessionStopped -= OnSessionStopped;

            try
            {
                await recognizer.StopContinuousRecognitionAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Debug($"Azure StopContinuousRecognitionAsync 想定内例外: {ex.GetType().Name}: {ex.Message}");
            }
        }

        try { _pushStream?.Close(); } catch { /* 二重 Close は無視 */ }

        _recognizer?.Dispose();
        _audioConfig?.Dispose();
        _pushStream?.Dispose();
        _recognizer = null;
        _audioConfig = null;
        _pushStream = null;
    }

    private async Task CleanupAsync()
    {
        await CleanupRecognizerAsync().ConfigureAwait(false);
        SetState(ConnectionState.Disconnected);
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
        _shouldReconnect = false;
        // DisconnectAsync が reconnect ループを cancel + await するため、 ここに来た時点でループは停止済み。
        DisconnectAsync().GetAwaiter().GetResult();
        _reconnectShutdownCts.Dispose();
        _connectLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
        _shouldReconnect = false;
        await DisconnectAsync().ConfigureAwait(false);
        _reconnectShutdownCts.Dispose();
        _connectLock.Dispose();
    }

    /// <summary>
    /// 接続テスト: 短時間 recognizer を起動し、 即時の Canceled (認証失敗等) が来ないかを見る。
    /// 音声を送らないので翻訳結果は出ないが、 キー / Region が無効なら Canceled が即発火する。
    /// </summary>
    public static async Task<(bool Success, string Message)> TestConnectionAsync(
        AzureSpeechSettings settings, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
            return (false, "Azure Speech のキーが設定されていません。");
        if (string.IsNullOrWhiteSpace(settings.Region))
            return (false, "Azure Speech の Region が設定されていません。");

        TranslationRecognizer? recognizer = null;
        PushAudioInputStream? push = null;
        AudioConfig? audioConfig = null;
        try
        {
            // SpeechTranslationConfig は Speech SDK 1.43 では IDisposable 非実装のため using 不可 (CS1674)。
            var config = SpeechTranslationConfig.FromSubscription(settings.ApiKey, settings.Region);
            config.SpeechRecognitionLanguage = ResolveSourceLocale(settings.SourceLanguage);
            config.AddTargetLanguage(MapToAzureTargetLanguage(settings.OutputLanguage));
            using var format = AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1);
            push = AudioInputStream.CreatePushStream(format);
            audioConfig = AudioConfig.FromStreamInput(push);
            recognizer = new TranslationRecognizer(config, audioConfig);

            var tcs = new TaskCompletionSource<(bool, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
            recognizer.Canceled += (_, e) =>
            {
                if (e.Reason == CancellationReason.Error)
                {
                    var kind = e.ErrorCode switch
                    {
                        CancellationErrorCode.AuthenticationFailure => OpenAIApiErrorKind.InvalidApiKey,
                        CancellationErrorCode.Forbidden => OpenAIApiErrorKind.Forbidden,
                        _ => OpenAIApiErrorKind.Unknown,
                    };
                    tcs.TrySetResult((false, AzureFriendlyMessageFor(kind, e.ErrorDetails ?? string.Empty)));
                }
            };

            await recognizer.StartContinuousRecognitionAsync().WaitAsync(ct).ConfigureAwait(false);

            // 認証失敗なら数秒以内に Canceled が来る。 来なければ接続自体は成立とみなす。
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                var result = await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 呼び出し元 (ユーザー) が接続テストを中断 → 成功扱いにせずキャンセルを伝播。
                throw;
            }
            catch (OperationCanceledException)
            {
                // 4 秒 timeout で Canceled が来なかった = 認証は通った (キー / Region 有効)。
                return (true, "接続成功！Azure Speech のキー / Region は有効です。");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 呼び出し元キャンセルは汎用 catch でエラー文字列化せず、 そのまま伝播する。
            throw;
        }
        catch (Exception ex)
        {
            return (false, $"接続エラー: {ex.Message}");
        }
        finally
        {
            try { if (recognizer != null) await recognizer.StopContinuousRecognitionAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch { }
            try { push?.Close(); } catch { }
            recognizer?.Dispose();
            audioConfig?.Dispose();
            push?.Dispose();
        }
    }

    private void SetState(ConnectionState state)
    {
        _state = state;
        StateChanged?.Invoke(state);
    }
}
