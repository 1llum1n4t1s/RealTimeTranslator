using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using SuperLightLogger;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.Core.Services;

public sealed class TranslationPipelineService : ITranslationPipelineService, IAsyncDisposable
{
    private static readonly ILog Logger = LogManager.GetLogger<TranslationPipelineService>();
    // partial 表示の更新頻度。 短いほど字幕が機敏になるが UI スレッド負荷が増える。
    // ゆろさんの「翻訳が遅れているときに加速」要望で 100ms → 50ms → 30ms と段階的に短縮。
    // OpenAI Realtime API のサーバー側 VAD 律速 (silence_duration_ms はクライアント設定不可) は
    // 越えられないので、ここで詰められるのは partial 描画間隔だけ。
    private static readonly TimeSpan DeltaThrottle = TimeSpan.FromMilliseconds(30);

    private readonly IAudioCaptureService _audioCaptureService;
    private readonly IRealtimeTranscriber _realtimeClient;
    private OpenAIRealtimeSettings _cachedRealtimeSettings;
    private Channel<float[]>? _audioInputChannel;
    private Task? _audioProcessingTask;
    private CancellationTokenSource? _audioProcessingCts;

    private string _currentSegmentId = Guid.NewGuid().ToString();
    private readonly StringBuilder _accumulatedText = new();
    private readonly object _textLock = new();
    private DateTime _lastEmitTime = DateTime.MinValue;
    private bool _hasPendingDelta;
    private readonly Timer _throttleTimer;
    // 未確定の trailing が「確定字幕の表示時間」(Overlay.DisplayDuration) を過ぎても句点/done で確定しないとき、
    // その trailing を確定 (IsFinal=true) として emit + ログ記録するためのアイドルタイマー。
    // done が来ない発話 (2026-05-17 観測) や句点なしで終わる短い発話の取りこぼしを防ぐ。
    private readonly Timer _idleFinalizeTimer;
    private readonly Stopwatch _latencyStopwatch = new();
    private volatile bool _isRunning;
    // rere B1-006: Dispose / DisposeAsync の二重実行を防ぐ Interlocked 占有マーク。
    // 旧 volatile bool は check + set が非原子で、 並行 Dispose 時に両方が `if (_disposed) return;` を抜け
    // ObjectDisposedException を投げる経路があった。 OpenAIRealtimeClient も同型対策済み。
    // 0=alive, 1=disposed。
    private int _disposed;
    private DateTime _lastAudioErrorLogTime = DateTime.MinValue;

    // OpenAI Realtime Translation API は transcript.done を「セッション累積の全文」で
    // 返してくる挙動が観測されている (2026-05-16)。 そのまま finalText として overlay に出すと
    // 「まあ → まあ、 → まあ、ノ → ...」と1つの subtitle が際限なく成長する UX 不具合になる。
    // _lastFinalizedTranscript で「既に確定字幕として出した累積テキスト」を保持し、
    // done のたびに差分だけを抽出 → 句点で分割 → 文ごとに新 SegmentId で emit することで
    // 「会話が途切れず長文化する」問題を回避する。
    //
    // ⚠️ rere C1-P0-001 / A2-001: `+= sentence` の immutable string 再アロケート (O(n²)) を
    // 回避するため StringBuilder で保持。 累積長は数 KB〜数十 KB に達するため、 + 連結ごとに
    // 全文を allocate する旧設計だと 30 分以上の連続セッションで GC pressure が顕著になる。
    // 比較・末尾取得は ToString() でなく専用ヘルパー (StringBuilderStartsWith / 末尾切出し)
    // で済ませて ToString 経由のコピーを避ける。
    private readonly StringBuilder _lastFinalizedTranscript = new();

    // 句点として扱う文字。 ASCII 終端も入れて英語訳出力にも対応する。
    // 「、」(読点) は文の区切りではないので含めない (一文の中で複数現れるため)。
    // 注意: 半角ピリオド '.' は IsSentenceBoundaryAt で小数点保護 (例: 「6.3インチ」「3.14」)
    //       を行うため、 直接 IndexOf するのではなく必ず IsSentenceBoundaryAt 経由で判定する。
    private static readonly char[] SentenceTerminators = ['。', '！', '？', '!', '?', '.'];

    // 観測用カウンタ: partial / 完結文 emit の累計回数を頻度抑制ログで間引きながら吐く。
    // 「字幕が来ない」「文が切れない」「字幕が成長し続ける」系の調査で経路を可視化する。
    private long _partialEmitCount;
    private long _completedEmitCount;

    // 直前の ConnectionState を保持。 Reconnecting → Connected 遷移を検出して
    // 字幕状態 (_lastFinalizedTranscript / _currentSegmentId) をリセットするため (rere P0 #1)。
    private ConnectionState _lastConnectionState = ConnectionState.Disconnected;

    // D-7 句読点 fallback の閾値: _accumulatedText がこれを超えても句点が来ない場合、
    // 末尾の読点で強制分割する (永久未確定字幕を防止)。
    private const int FallbackSplitThreshold = 100;
    private static readonly char[] FallbackSplitTerminators = ['、', ',', '・'];

    // O(n²) 回避: _accumulatedText 内で最後に見つけた FallbackSplitTerminators の位置。
    // 毎 delta で新規追加部分だけ走査すれば済むように、 delta 到着ごとに更新する。
    // _accumulatedText.Clear() 時は必ず -1 にリセットすること。
    private int _lastFallbackCommaPos = -1;

    // ───────── VAD ゲート (Silence/InSpeech/Hangover 状態機) ─────────
    // BGM や効果音だけが鳴っているシーンで OpenAI 送信を抑制し token 浪費を防ぐ。
    // EnableVad=false の場合は素通し (旧挙動)。
    private enum VadState { Silence, InSpeech, Hangover }
    private readonly IVoiceActivityDetector _vad;
    // 16kHz / 32ms = 512 samples ごとに切り出すための accumulator (audio chunk 境界とフレーム
    // 境界がズレるため、 残ったサンプルを次 chunk と結合して使う)。
    private float[]? _frameAccumulator;
    private int _frameAccumulatorLen;
    // 発話冒頭の取りこぼし防止用に直近フレームを保持するリングバッファ。
    private readonly Queue<float[]> _preRollBuffer = new();
    private VadState _vadState = VadState.Silence;
    private int _hangoverFramesRemaining;
    // 自動 Pause 判定用 (最後に speech と判定された時刻)。
    private DateTime _lastSpeechUtc = DateTime.UtcNow;
    private DateTime _sessionStartUtc = DateTime.UtcNow;
    private double _skippedSecondsByVad;
    private Task? _autoPauseTask;
    private CancellationTokenSource? _autoPauseCts;
    // リアルタイム stats tick: silence で API response が来ない間も UI の経過時間 / VAD 節約秒数を 1 秒毎に更新する。
    // 旧実装は response.done + AutoPause + Stop でしか StatsUpdated が走らず、 「Start 直後の数十秒間 stats が止まる」体感バグだった。
    private Task? _statsTickTask;
    private CancellationTokenSource? _statsTickCts;
    // StartAsync / StopAsync を直列化する semaphore (rere B1-004 対応)。
    // 旧実装は _isRunning フラグだけで状態管理しており、 「Start 中 (ConnectAsync 待機中) に
    // OnSettingsSaved 経由で Stop が呼ばれる」シナリオで pipeline 側 `if (!_isRunning) return;` が
    // 空振りして Channel / Task が orphan として残る経路があった。 全状態遷移をこの semaphore で
    // 直列化することで、 Start と Stop が交差する経路を構造的に消す。
    private readonly SemaphoreSlim _startStopLock = new(1, 1);

    public event EventHandler<SubtitleItem>? SubtitleGenerated;
    public event EventHandler<PipelineStatsEventArgs>? StatsUpdated;
    public event EventHandler<Exception>? ErrorOccurred;

    private readonly IOptionsMonitor<AppSettings> _settingsMonitor;
    // rere B1-003: SettingsService.DecryptApiKeyInPlace の static 直叩き (Service Locator) を
    // ISettingsService 注入経由に置換。 テスト時のモック差し替え可能性が回復。
    private readonly ISettingsService _settingsService;

    public TranslationPipelineService(
        IAudioCaptureService audioCaptureService,
        IRealtimeTranscriber realtimeClient,
        IOptionsMonitor<AppSettings> settingsMonitor,
        ISettingsService settingsService,
        IVoiceActivityDetector vad)
    {
        _audioCaptureService = audioCaptureService;
        _realtimeClient = realtimeClient;
        _settingsMonitor = settingsMonitor;
        _settingsService = settingsService;
        _vad = vad;
        _cachedRealtimeSettings = settingsMonitor.CurrentValue.OpenAIRealtime;
        _throttleTimer = new Timer(OnThrottleTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);
        _idleFinalizeTimer = new Timer(OnIdleFinalizeTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);

        _realtimeClient.TranscriptDeltaReceived += OnTranscriptDelta;
        _realtimeClient.TranscriptCompleted += OnTranscriptCompleted;
        _realtimeClient.ErrorReceived += OnClientError;
        _realtimeClient.StateChanged += OnConnectionStateChanged;
    }


    public async Task StartAsync(CancellationToken token)
    {
        // rere B1-004: Start ↔ Stop の TOCTOU を消すため semaphore で全初期化を直列化。
        // Stop が並行に走った場合は Stop が完了するまでブロック → Start は全部完了済み状態から走り直す。
        await _startStopLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_isRunning) return;
            await StartCoreAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _startStopLock.Release();
        }
    }

    private async Task StartCoreAsync(CancellationToken token)
    {
        // settings.json で変更したばかりの内容 (OutputLanguage 等) が反映されるよう、
        // 起動直前に IOptionsMonitor から最新値を取り直す。
        // 旧実装は _cachedRealtimeSettings (構築時 or ApplySettingsAsync の値) を
        // 使っていたが、 UI で言語切替後にすぐ「開始」を押すと古い設定で接続して
        // しまうケースがあった。DPAPI で暗号化されている API キーも復号して使う。
        var freshSettings = _settingsMonitor.CurrentValue.OpenAIRealtime;
        _settingsService.DecryptApiKey(_settingsMonitor.CurrentValue);
        _cachedRealtimeSettings = freshSettings;
        Logger.Info($"StartAsync: OutputLanguage='{freshSettings.OutputLanguage}' Model='{freshSettings.Model}'");

        var settings = _cachedRealtimeSettings;
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            var ex = new InvalidOperationException("OpenAI APIキーが設定されていません。設定画面でキーを入力してください。");
            ErrorOccurred?.Invoke(this, ex);
            throw ex;
        }

        Logger.Info("翻訳パイプライン開始（OpenAI Realtime API）");

        // 前セッションの累積 transcript / SegmentId をリセットしないと、 再 Start 時に
        // 「前回 done で確定したテキストを prefix として保持」した状態から始まり、
        // 新セッションの最初の done で全文が emit されない (or 重複検知される) 不具合になる。
        lock (_textLock)
        {
            _lastFinalizedTranscript.Clear();
            _accumulatedText.Clear();
            _lastFallbackCommaPos = -1;
            _currentSegmentId = Guid.NewGuid().ToString();
            _lastEmitTime = DateTime.MinValue;
            _hasPendingDelta = false;
            _idleFinalizeTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        // rere B1-011: Core layer 統一で ConfigureAwait(false) 付与。
        await _realtimeClient.ConnectAsync(settings, token).ConfigureAwait(false);

        // WASAPI コールバックスレッドで重い変換を行うと audio glitch の原因になるため、
        // Channel に raw float[] を投入だけして変換は専用タスクで行う。
        // 容量 25: ゆろさんの「遅れてる時の加速」要望で 50 → 25 に半減。
        // 詰まり時の最大遅延が約 4 秒 → 約 2 秒に短縮され、DropOldest が早く走って追いつきが速くなる。
        // 下げすぎると正常時にも音声欠落が起きるので 25 が穏当 (1 chunk ≒ 80ms 想定で 2 秒分のバッファ)。
        _audioInputChannel = Channel.CreateBounded<float[]>(new BoundedChannelOptions(25)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
        _audioProcessingCts = new CancellationTokenSource();
        _audioProcessingTask = Task.Run(() => ProcessAudioLoopAsync(_audioProcessingCts.Token));

        // VAD ゲート初期化 (新セッション開始時に状態を完全リセット)。
        // 旧セッションの hidden state / preroll が残っていると、 開始直後の判定が
        // ブレるので必ずここで初期化する。
        _vad.Reset();
        _preRollBuffer.Clear();
        _vadState = VadState.Silence;
        _hangoverFramesRemaining = 0;
        _frameAccumulator = null;
        _frameAccumulatorLen = 0;
        _skippedSecondsByVad = 0;
        _sessionStartUtc = DateTime.UtcNow;
        _lastSpeechUtc = DateTime.UtcNow;

        // 自動 Pause 監視ループ起動 (5 秒間隔ポーリング、 設定で AutoPauseOnSilenceSec=0 なら no-op)。
        _autoPauseCts = new CancellationTokenSource();
        _autoPauseTask = Task.Run(() => AutoPauseLoopAsync(_autoPauseCts.Token));

        // リアルタイム stats tick 起動 (1 秒周期で UI に経過時間 / トークン / VAD 節約秒数を反映)。
        _statsTickCts = new CancellationTokenSource();
        _statsTickTask = Task.Run(() => StatsTickLoopAsync(_statsTickCts.Token));

        _audioCaptureService.AudioDataAvailable += OnAudioDataAvailable;
        _isRunning = true;
        _latencyStopwatch.Start();

        StatsUpdated?.Invoke(this, new PipelineStatsEventArgs
        {
            StatusText = "API接続完了",
            SessionDuration = TimeSpan.Zero,
            InputAudioTokensEstimate = 0,
            EstimatedCostUsd = 0m,
            SkippedSecondsByVad = 0,
        });
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        // rere B1-004: Start 中の Stop 呼び出し (OnSettingsSaved 経由等) も含めて直列化。
        // Start が走っている最中は完了を待ってから Stop を実行する。
        await _startStopLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_isRunning) return;
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _startStopLock.Release();
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        Logger.Info("翻訳パイプライン停止");
        // 未確定のまま残っている trailing を確定字幕として emit + ログ記録してから停止する
        // (停止時に「未確定の文字が確定されず消える」データロスを防ぐ)。
        FinalizePendingPartial("停止");
        _audioCaptureService.AudioDataAvailable -= OnAudioDataAvailable;
        _isRunning = false;
        _latencyStopwatch.Stop();
        _throttleTimer.Change(Timeout.Infinite, Timeout.Infinite);

        // ⭐ WASAPI ネイティブ解放を UI スレッドから外す。
        // AudioCaptureService.StopCapture() は内部で NAudio の WasapiCapture.StopRecording +
        // Dispose を同期実行する。 これらは native callback スレッド完了待ち (WaitForSingleObject 系)
        // を含むため、 UI スレッドから直接呼ぶと「停止ボタン押下でアプリ全体フリーズ」になる
        // (2026-05-17 ゆろさん環境で観測)。 Task.Run + WaitAsync(3s) で別スレッドに逃がし、
        // タイムアウト時もログを残してフリーズを防ぐ。
        try
        {
            await Task.Run(() => _audioCaptureService.StopCapture())
                      .WaitAsync(TimeSpan.FromSeconds(3))
                      .ConfigureAwait(false);
            Logger.Info("audio キャプチャ停止 完了");
        }
        catch (TimeoutException)
        {
            Logger.Warn("audio キャプチャ停止が 3 秒を超過 (バックグラウンドで継続)");
        }
        catch (Exception ex)
        {
            Logger.Warn("audio キャプチャ停止中の例外", ex);
        }

        // audio 処理タスクの停止: writer 完了 → cts キャンセル → タスク完了待ち
        _audioInputChannel?.Writer.TryComplete();
        _audioProcessingCts?.Cancel();
        if (_audioProcessingTask is { } task)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { Logger.Warn("audio 処理ループ停止がタイムアウト"); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Logger.Warn("audio 処理ループ停止中の例外", ex); }
        }
        _audioProcessingTask = null;
        _audioProcessingCts?.Dispose();
        _audioProcessingCts = null;
        _audioInputChannel = null;

        // 自動 Pause 監視ループの停止 (audio 処理停止と独立にキャンセル → 完了待ち)。
        _autoPauseCts?.Cancel();
        if (_autoPauseTask is { } apTask)
        {
            try { await apTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { Logger.Warn("自動 Pause タスク停止がタイムアウト"); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Logger.Warn("自動 Pause タスク停止中の例外", ex); }
        }
        _autoPauseTask = null;
        _autoPauseCts?.Dispose();
        _autoPauseCts = null;

        // リアルタイム stats tick の停止 (Stop ボタン押下後は更新不要)。
        _statsTickCts?.Cancel();
        if (_statsTickTask is { } stTask)
        {
            try { await stTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { Logger.Warn("stats tick タスク停止がタイムアウト"); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Logger.Warn("stats tick タスク停止中の例外", ex); }
        }
        _statsTickTask = null;
        _statsTickCts?.Dispose();
        _statsTickCts = null;

        // WebSocket 切断も外部待機を入れて UI スレッドに戻る前に確実に完了させる
        try
        {
            await _realtimeClient.DisconnectAsync()
                  .WaitAsync(TimeSpan.FromSeconds(3))
                  .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Logger.Warn("WebSocket 切断が 3 秒を超過 (バックグラウンドで継続)");
        }
        catch (Exception ex)
        {
            Logger.Warn("WebSocket 切断中の例外", ex);
        }

        // ⭐ rere P2 F-7: セッション統計を確実にログに残し、 NW 詰まり指標を可視化。
        // DroppedAudioChunkCount はキャプチャ → API 送信の Channel<byte[]>(100) で
        // BoundedChannelFullMode.DropOldest によって捨てられた音声チャンク累計。
        // 「字幕が抜ける」報告時に「ローカル NW 詰まりか OpenAI 側遅延か」を切り分ける。
        if (_realtimeClient is OpenAIRealtimeClient openAiClient)
        {
            Logger.Info($"セッション統計: DroppedAudioChunks={openAiClient.DroppedAudioChunkCount} 完結文emit={Interlocked.Read(ref _completedEmitCount)} partial emit={Interlocked.Read(ref _partialEmitCount)}");
        }

        Logger.Info("翻訳パイプライン停止 完了");

        StatsUpdated?.Invoke(this, BuildCurrentStats("停止"));
    }

    private void OnAudioDataAvailable(object? sender, AudioDataEventArgs e)
    {
        if (!_isRunning) return;

        // WASAPI コールバックスレッド（MMCSS）で重い処理を行うと音声バッファが overflow して
        // audio glitch / Silent パケット化を起こすため、Channel に投入するだけで即座に戻る。
        // BoundedChannel(25, DropOldest) で詰まり時は古いものを捨てる（再接続復帰後は新しい音声を優先）。
        _audioInputChannel?.Writer.TryWrite(e.AudioData);
    }

    /// <summary>
    /// WASAPI とは別のスレッドで audio chunks を消費し、resample + PCM16 変換 + 送信を行う。
    /// </summary>
    private async Task ProcessAudioLoopAsync(CancellationToken ct)
    {
        var reader = _audioInputChannel?.Reader;
        if (reader is null) return;

        try
        {
            await foreach (var audioData in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var audioCaptureSettings = _settingsMonitor.CurrentValue.AudioCapture;
                    if (!audioCaptureSettings.EnableVad)
                    {
                        // VAD 無効: 旧パス (素通し送信)。 後方互換 & 緊急時の VAD バイパス用。
                        var pcm16 = AudioFormatConverter.Float32ToPcm16(
                            AudioFormatConverter.ResampleTo24kHz(audioData));
                        _realtimeClient.SendAudio(pcm16);
                    }
                    else
                    {
                        // VAD 有効: 16kHz raw audio を 512 サンプル単位でフレーム化 → speech 判定 →
                        // 発話区間のみ送信。 PreRoll + Hangover で発話冒頭/末尾の取りこぼし防止。
                        ProcessAudioWithVadGate(audioData, audioCaptureSettings);
                    }
                }
                catch (Exception ex)
                {
                    // エラーログを1秒に1回に制限（高頻度の音声イベントでログが溢れることを防止）
                    var now = DateTime.UtcNow;
                    if ((now - _lastAudioErrorLogTime).TotalSeconds >= 1.0)
                    {
                        _lastAudioErrorLogTime = now;
                        Logger.Error("音声データ変換エラー", ex);
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* StopAsync 経由の停止 */ }
        catch (Exception ex)
        {
            Logger.Error("audio 処理ループ予期しないエラー", ex);
            ErrorOccurred?.Invoke(this, ex);
        }
    }

    private void OnTranscriptDelta(string delta)
    {
        if (string.IsNullOrEmpty(delta)) return;

        // 完結文をロック外で emit するため、 ロック内ではプランだけ作って退出する。
        List<(string segmentId, string sentence)> completedSentences = new();
        bool emitPartial = false;

        lock (_textLock)
        {
            // O(n²) → O(n) 最適化: 新規追加部分のみを走査する。
            // appendStart = 追加前の累積長。 scanFrom = 走査開始位置 (直前文字がピリオド小数点保護に必要なため -1)。
            int appendStart = _accumulatedText.Length;
            _accumulatedText.Append(delta);
            int totalLen = _accumulatedText.Length;

            if (!_latencyStopwatch.IsRunning)
                _latencyStopwatch.Restart();

            // ⭐ OpenAI Realtime Translation API は transcript.done を送ってこない
            // ケースがある (2026-05-17 観測: delta のみで会話が進み done が来ない)。
            // done を待つ設計だと SegmentId が永久に固定され「字幕が無限成長する」UX バグ
            // になるため、 delta 受信時にも _accumulatedText 内の句点を検出して
            // 完結文ごとに emit + SegmentId 切り替えを行う。
            //
            // 走査範囲: appendStart - 1 から totalLen まで (直前文字のピリオド小数点保護のため -1)。
            // StringBuilder.indexer は O(1) なので ToString() 不要。
            int start = 0;
            int scanFrom = Math.Max(0, appendStart - 1);
            for (int i = scanFrom; i < totalLen; i++)
            {
                if (IsSentenceBoundaryAt(_accumulatedText, i, totalLen, isFinalContext: false))
                {
                    // StringBuilder から部分文字列を切り出す (完結文 1 件分のみなので小さい)
                    var sentence = _accumulatedText.ToString(start, i + 1 - start);
                    if (!string.IsNullOrWhiteSpace(sentence))
                    {
                        completedSentences.Add((_currentSegmentId, sentence));
                        _currentSegmentId = Guid.NewGuid().ToString();
                        _lastFinalizedTranscript.Append(sentence);
                    }
                    start = i + 1;
                }

                // D-7 fallback: 走査中に FallbackSplitTerminators を見つけたら位置を記録。
                // 句点が見つかれば start が進むので自然にリセットされる。
                if (Array.IndexOf(FallbackSplitTerminators, _accumulatedText[i]) >= 0 && i >= start)
                {
                    _lastFallbackCommaPos = i;
                }
            }

            // ⭐ D-7 句読点 fallback: 句点 (SentenceTerminators) が来なくても _accumulatedText が
            // FallbackSplitThreshold (100文字) を超えた場合、 末尾の読点 (「、」「,」「・」) で
            // 強制分割する。 永久未確定字幕 (partial 表示が伸び続ける UX バグ) を防止。
            // すでに完結文を切り出した直後 (completedSentences.Count > 0) は trailing が短いので
            // fallback 不要。 完結文ゼロかつ trailing が閾値超のケースのみ発動する。
            if (completedSentences.Count == 0 && (totalLen - start) > FallbackSplitThreshold)
            {
                // _lastFallbackCommaPos が有効な範囲にあれば O(1) で分割。
                if (_lastFallbackCommaPos > start)
                {
                    int splitAt = _lastFallbackCommaPos;
                    var sentence = _accumulatedText.ToString(start, splitAt + 1 - start);
                    if (!string.IsNullOrWhiteSpace(sentence))
                    {
                        int trailingLength = totalLen - start;
                        Logger.Info($"OnTranscriptDelta: 句読点 fallback 分割 (trailing={trailingLength}文字 句点なし) → '{TruncateForLog(sentence)}'");
                        completedSentences.Add((_currentSegmentId, sentence));
                        _currentSegmentId = Guid.NewGuid().ToString();
                        _lastFinalizedTranscript.Append(sentence);
                        start = splitAt + 1;
                    }
                }
            }

            if (completedSentences.Count > 0)
            {
                // 完結文を切り出した → trailing だけ _accumulatedText に残す。
                if (start > 0)
                {
                    _accumulatedText.Remove(0, start);
                }
                // _lastFallbackCommaPos を新しい offset 基準にシフト
                _lastFallbackCommaPos = _lastFallbackCommaPos >= start ? _lastFallbackCommaPos - start : -1;
                _lastEmitTime = DateTime.UtcNow;
                _hasPendingDelta = false;
                _throttleTimer.Change(Timeout.Infinite, Timeout.Infinite);
                emitPartial = _accumulatedText.Length > 0;
            }
            else
            {
                // 完結文無し → 従来の throttled partial emit 経路
                var now = DateTime.UtcNow;
                if (now - _lastEmitTime >= DeltaThrottle)
                {
                    emitPartial = true;
                }
                else if (!_hasPendingDelta)
                {
                    _hasPendingDelta = true;
                    _throttleTimer.Change(DeltaThrottle, Timeout.InfiniteTimeSpan);
                }
            }
        }

        // ロック外で完結文を emit。
        foreach (var (segmentId, sentence) in completedSentences)
        {
            var n = Interlocked.Increment(ref _completedEmitCount);
            Logger.Info($"完結文 emit (delta経路) #{n}: SegmentId={ShortSegmentId(segmentId)} 長さ={sentence.Length} 内容='{TruncateForLog(sentence)}'");

            var subtitle = new SubtitleItem
            {
                SegmentId = segmentId,
                OriginalText = string.Empty,
                TranslatedText = sentence,
                IsFinal = true,
            };
            SubtitleGenerated?.Invoke(this, subtitle);
        }

        if (emitPartial)
        {
            lock (_textLock)
            {
                EmitPartialSubtitle();
            }
        }

        // 未確定 trailing が残っていれば DisplayDuration 後のアイドル確定をリスケジュール
        // (毎 delta でリスケされるため、 無活動が DisplayDuration 継続して初めて発火する)。
        SyncIdleFinalizeTimer();
    }

    private void OnThrottleTimerElapsed(object? state)
    {
        lock (_textLock)
        {
            if (_hasPendingDelta)
                EmitPartialSubtitle();
        }
    }

    private void EmitPartialSubtitle()
    {
        _lastEmitTime = DateTime.UtcNow;
        _hasPendingDelta = false;

        var partialText = _accumulatedText.ToString();
        var segmentId = _currentSegmentId;

        var subtitle = new SubtitleItem
        {
            SegmentId = segmentId,
            OriginalText = partialText,
            TranslatedText = "",
            IsFinal = false
        };

        SubtitleGenerated?.Invoke(this, subtitle);

        // 頻度抑制: partial は throttle (DeltaThrottle、 現在 30ms) ごとに発火するので全件 Info にすると爆発する。
        // 1, 10, 50, 100, ... と間引いて累積長と SegmentId 推移を観測できるようにする。
        var count = Interlocked.Increment(ref _partialEmitCount);
        if (ShouldLogAtCount(count))
        {
            Logger.Info($"partial emit #{count}: SegmentId={ShortSegmentId(segmentId)} 累積長={partialText.Length} 内容='{TruncateForLog(partialText)}'");
        }
    }

    /// <summary>
    /// アイドル確定タイマー発火: 未確定の trailing が「確定字幕の表示時間」(Overlay.DisplayDuration) を過ぎても
    /// 句点・done で確定しなかった場合に、 その trailing を確定 (IsFinal=true) として emit + ログ記録する。
    /// done が来ない発話や、 句点なしで終わる短い発話の取りこぼしを防ぐ救済経路。
    /// </summary>
    private void OnIdleFinalizeTimerElapsed(object? state)
    {
        FinalizePendingPartial("無音タイムアウト");
    }

    /// <summary>
    /// 未確定 trailing (_accumulatedText) の有無に応じてアイドル確定タイマーを再設定する。
    /// trailing が残っていれば DisplayDuration 後に発火するようリスケジュール (毎 delta/done で呼ばれ、
    /// 無活動が DisplayDuration 継続して初めて発火する)、 trailing が空なら停止する。
    /// </summary>
    private void SyncIdleFinalizeTimer()
    {
        lock (_textLock)
        {
            if (_accumulatedText.Length > 0)
            {
                // DisplayDuration は秒 (既定 5.0)。 「未確定が放置されたら確定扱いにする」しきい値に流用する。
                // 0 や極端に小さい値で誤発火しないよう下限 1 秒でガードする。
                // NaN / Infinity / 極大値 (double.MaxValue 等) は TimeSpan.FromSeconds が ArgumentException /
                // OverflowException を投げてアイドルタイマー設定ごとクラッシュさせるため (stst 隊員7 告発)、
                // 非有限値は既定 5 秒へ倒し、 有限値は [1秒, 3600秒] にクランプして防御する。
                var rawDuration = _settingsMonitor.CurrentValue.Overlay.DisplayDuration;
                var safeDuration = double.IsFinite(rawDuration) ? Math.Clamp(rawDuration, 1.0, 3600.0) : 5.0;
                var idle = TimeSpan.FromSeconds(safeDuration);
                _idleFinalizeTimer.Change(idle, Timeout.InfiniteTimeSpan);
            }
            else
            {
                _idleFinalizeTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }
    }

    /// <summary>
    /// 未確定の trailing (_accumulatedText) を確定字幕 (IsFinal=true) として emit + ログ記録し、 状態を進める。
    /// 停止時 (StopCoreAsync) とアイドルタイムアウト (OnIdleFinalizeTimerElapsed) から呼ばれる。
    /// 空 / 空白のみのときは何もしない。 SegmentId は partial 表示時と同一にして overlay 側の partial を確定表示に置換させる。
    /// </summary>
    private void FinalizePendingPartial(string reason)
    {
        string sentence;
        string segmentId;
        lock (_textLock)
        {
            sentence = _accumulatedText.ToString();
            if (string.IsNullOrWhiteSpace(sentence))
            {
                _idleFinalizeTimer.Change(Timeout.Infinite, Timeout.Infinite);
                return;
            }

            segmentId = _currentSegmentId;
            // 確定累積へ反映 (後続 done の prefix 一致を維持するため、 delta 経路の確定と同じ扱い)。
            _lastFinalizedTranscript.Append(sentence);
            _accumulatedText.Clear();
            _lastFallbackCommaPos = -1;
            _currentSegmentId = Guid.NewGuid().ToString();
            _hasPendingDelta = false;
            _idleFinalizeTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _throttleTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        Logger.Info($"未確定文を確定 emit ({reason}): SegmentId={ShortSegmentId(segmentId)} 長さ={sentence.Length} 内容='{TruncateForLog(sentence)}'");

        // ロック外で emit (SubtitleGenerated ハンドラ側の再入によるデッドロックを避ける)。
        SubtitleGenerated?.Invoke(this, new SubtitleItem
        {
            SegmentId = segmentId,
            OriginalText = string.Empty,
            TranslatedText = sentence,
            IsFinal = true,
        });
    }

    private void OnTranscriptCompleted(string transcript)
    {
        // 確定字幕として出すべき「新規ぶん」と、 各文の SegmentId を計算して
        // ロックの外で emit するため、 lock 内ではプランだけ作って終わる。
        List<(string segmentId, string text, bool isSentenceEnd)> emissions;
        double latencyMs;
        int newPortionLength;

        lock (_textLock)
        {
            latencyMs = _latencyStopwatch.Elapsed.TotalMilliseconds;

            // API から transcript が空で done が来た場合 (response.done fallback 経路など) は
            // 直前まで delta で蓄積してきた _accumulatedText を確定字幕として使う。
            // transcript が空でなければ「セッション累積の全文」が来ている前提で扱う。
            string effective;
            if (string.IsNullOrEmpty(transcript))
            {
                // fallback 経路 (response.done で transcript が空): 既存の確定累積 + 直前の delta 蓄積。
                // この経路は稀なので ToString() の O(N) コストは許容。
                effective = _lastFinalizedTranscript.ToString() + _accumulatedText.ToString();
            }
            else
            {
                effective = transcript;
            }

            // ⚠️ rere レビュー P0 #1 修正: prefix 不一致時の「effective 全体を newPortion 扱い」
            // を廃止し、 skip + Warn ログに倒す。
            //
            // 旧設計では `effective.StartsWith(_lastFinalizedTranscript) ? slice : effective` で
            // 不一致時に effective 全体を newPortion として再 emit していたが、 これは
            // 「_lastFinalizedTranscript と effective に重複する部分」を含めて再 emit し、
            // 新 SegmentId で overlay に追加されるため重複字幕を作る経路があった。
            // 「重複表示よりも欠落のほうがマシ」方針で skip に倒す。
            // 再接続経由の不一致は OnConnectionStateChanged のリセットで吸収するので、
            // ここに来るのは「API 側で transcript 正規化が入った」「順序入れ替わり」等の
            // 稀ケースのみで実質ゼロ件のはず。
            if (!StringBuilderIsPrefixOf(_lastFinalizedTranscript, effective))
            {
                Logger.Warn($"OnTranscriptCompleted: prefix 不整合検出 — done 経路スキップ (finalized 長={_lastFinalizedTranscript.Length} effective 長={effective.Length} finalized 末尾='{TruncateForLog(TailOf(_lastFinalizedTranscript, 20), 20)}' effective 先頭='{TruncateForLog(effective, 30)}')");
                _accumulatedText.Clear();
                _lastFallbackCommaPos = -1;
                _lastEmitTime = DateTime.MinValue;
                _hasPendingDelta = false;
                _throttleTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _latencyStopwatch.Reset();
                return;
            }

            string newPortion = effective[_lastFinalizedTranscript.Length..];
            newPortionLength = newPortion.Length;

            if (string.IsNullOrEmpty(newPortion))
            {
                // 累積 done が来たが新規ぶんが無い (同 transcript を 2 回受信した等)
                // → emit すべきものがない。 状態だけリセットして抜ける。
                _accumulatedText.Clear();
                _lastFallbackCommaPos = -1;
                _lastEmitTime = DateTime.MinValue;
                _hasPendingDelta = false;
                _throttleTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _latencyStopwatch.Reset();
                Logger.Debug("OnTranscriptCompleted: 新規ぶんなし。emit スキップ");
                return;
            }

            // newPortion を句点で分割して、 完結した文ごとに emit する。
            emissions = new List<(string, string, bool)>();
            int start = 0;
            for (int i = 0; i < newPortion.Length; i++)
            {
                // done は確定文脈なので isFinalContext=true: 末尾ピリオド + 直前数字でも遠慮なく区切る
                // (次に来る delta はないため保留する意味がない)。 ただし「6.3インチ」のような
                // 中間の数字小数点は引き続き保護される。
                if (IsSentenceBoundaryAt(newPortion, i, isFinalContext: true))
                {
                    // start..i が 1 文 (句点含む) として完結
                    var sentence = newPortion[start..(i + 1)];
                    if (!string.IsNullOrWhiteSpace(sentence))
                    {
                        emissions.Add((_currentSegmentId, sentence, true));
                        _currentSegmentId = Guid.NewGuid().ToString();
                        // 確定累積を完結文ぶんだけ進める (trailing は次回更新時に再登場させる)
                        _lastFinalizedTranscript.Append(sentence);
                    }
                    start = i + 1;
                }
            }
            // 残りの未完結部分 (trailing) は _currentSegmentId で emit。
            if (start < newPortion.Length)
            {
                var trailing = newPortion[start..];
                if (!string.IsNullOrWhiteSpace(trailing))
                {
                    emissions.Add((_currentSegmentId, trailing, false));
                }
            }

            // partial 表示用の delta 蓄積はクリア (次の done までの partial 用に再使用)
            _accumulatedText.Clear();
            _lastFallbackCommaPos = -1;
            _lastEmitTime = DateTime.MinValue;
            _hasPendingDelta = false;
            _throttleTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _latencyStopwatch.Reset();
        }

        // 概況ログ: done 経路で何が起きたかをまとめて 1 行で出す。
        int completedCount = emissions.Count(e => e.isSentenceEnd);
        int trailingCount = emissions.Count(e => !e.isSentenceEnd);
        Logger.Info($"OnTranscriptCompleted: newPortion長={newPortionLength} 完結文={completedCount}件 trailing={trailingCount}件 latency={latencyMs:F0}ms");

        // ロック外で emit。
        foreach (var (segmentId, text, isSentenceEnd) in emissions)
        {
            if (isSentenceEnd)
            {
                var n = Interlocked.Increment(ref _completedEmitCount);
                Logger.Info($"完結文 emit (done経路) #{n}: SegmentId={ShortSegmentId(segmentId)} 長さ={text.Length} 内容='{TruncateForLog(text)}'");
            }

            var subtitle = new SubtitleItem
            {
                SegmentId = segmentId,
                OriginalText = string.Empty,
                TranslatedText = text,
                IsFinal = true,
            };
            SubtitleGenerated?.Invoke(this, subtitle);
        }

        // 累積フィールド (Tokens/Cost/SessionDuration/SkippedSecondsByVad) を 0 上書きしないよう BuildCurrentStats で埋める。
        var doneStats = BuildCurrentStats($"翻訳完了 ({latencyMs:F0}ms)");
        doneStats.ProcessingLatency = latencyMs;
        doneStats.TranslationLatency = latencyMs;
        StatsUpdated?.Invoke(this, doneStats);

        // done 後も未完結 trailing が残るケース (partial 表示中) のため、 アイドル確定タイマーを同期する。
        SyncIdleFinalizeTimer();
    }

    private void OnClientError(Exception ex)
    {
        Logger.Error("OpenAI Realtime クライアントエラー", ex);
        ErrorOccurred?.Invoke(this, ex);

        // 2026-05-17 ゆろさんログ起点の修正:
        // OpenAI API の致命的エラー (Quota / InvalidApiKey / Forbidden) は再接続しても回復しないため、
        // パイプラインを止めて音声キャプチャも即停止する。 OS から音声を取りっぱなしになる経路や、
        // 無駄な Channel 蓄積を避ける。 UI 側は ErrorOccurred イベント経由で警告バナーを出す。
        if (ex is OpenAIApiException { IsFatal: true } apiEx)
        {
            try
            {
                Logger.Info($"OnClientError: 致命的 API エラー ({apiEx.Kind}) を検知、 AudioCapture を即停止");
                _audioCaptureService.StopCapture();
            }
            catch (Exception stopEx)
            {
                Logger.Warn($"OnClientError: AudioCapture 停止に失敗 — {stopEx.Message}");
            }

            // UI のステータス文字列も警告に切替え (StatsUpdated 経由)。
            // 累積統計は BuildCurrentStats で必ず埋める (Fatal エラー時も「これまで何 token 使ったか」を維持)。
            StatsUpdated?.Invoke(this, BuildCurrentStats(apiEx.FriendlyMessage));
        }
    }

    private void OnConnectionStateChanged(ConnectionState state)
    {
        var previousState = _lastConnectionState;
        _lastConnectionState = state;

        // 再接続成功時 (Reconnecting → Connected) に字幕状態をリセットする (rere P0 #1)。
        // 新セッションの transcript.done は累積カウンタゼロから始まるため、 旧セッションの
        // _lastFinalizedTranscript を保持したままだと StartsWith 仮定が崩れて、 done 経路で
        // 重複字幕や欠落が発生する経路がある。 OnTranscriptCompleted の skip 防御だけでは
        // 不十分なので、 ここでも明示的にリセットしてゼロから累積し直す。
        if (state == ConnectionState.Connected && previousState == ConnectionState.Reconnecting)
        {
            lock (_textLock)
            {
                Logger.Info($"OnConnectionStateChanged: 再接続成功 — 字幕状態をリセット (旧 finalized 長={_lastFinalizedTranscript.Length})");
                _lastFinalizedTranscript.Clear();
                _accumulatedText.Clear();
                _lastFallbackCommaPos = -1;
                _currentSegmentId = Guid.NewGuid().ToString();
                _lastEmitTime = DateTime.MinValue;
                _hasPendingDelta = false;
                _throttleTimer.Change(Timeout.Infinite, Timeout.Infinite);
                _idleFinalizeTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }

        var statusText = state switch
        {
            ConnectionState.Connecting => "接続中...",
            ConnectionState.Connected => "API接続完了",
            ConnectionState.Reconnecting => "再接続中...",
            ConnectionState.Failed => "接続失敗 — APIキーとネットワークを確認してください",
            ConnectionState.Disconnected => "切断",
            _ => ""
        };

        // 接続状態変化時も累積統計を維持 (再接続中も「これまで何 token 使ったか」を消さない)。
        StatsUpdated?.Invoke(this, BuildCurrentStats(statusText));
    }

    public async ValueTask DisposeAsync()
    {
        // rere B1-006: Interlocked で並行 Dispose を排他制御。
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;

        try
        {
            // rere B1-011: Core layer の await は ConfigureAwait(false) で統一。
            await StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Error("TranslationPipelineService.DisposeAsync: 停止エラー", ex);
        }

        _throttleTimer.Dispose();
        _idleFinalizeTimer.Dispose();
        _realtimeClient.TranscriptDeltaReceived -= OnTranscriptDelta;
        _realtimeClient.TranscriptCompleted -= OnTranscriptCompleted;
        _realtimeClient.ErrorReceived -= OnClientError;
        _realtimeClient.StateChanged -= OnConnectionStateChanged;
        _audioCaptureService.AudioDataAvailable -= OnAudioDataAvailable;
        _startStopLock.Dispose();
    }

    public void Dispose()
    {
        // rere B1-006: Interlocked で並行 Dispose を排他制御。
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;

        // 同期版: UIスレッドでのデッドロックを回避するため Task.Run 経由で呼び出す
        try
        {
            Task.Run(() => StopAsync()).GetAwaiter().GetResult();
        }
        catch { /* DisposeAsync を推奨 */ }

        _throttleTimer.Dispose();
        _idleFinalizeTimer.Dispose();
        _realtimeClient.TranscriptDeltaReceived -= OnTranscriptDelta;
        _realtimeClient.TranscriptCompleted -= OnTranscriptCompleted;
        _realtimeClient.ErrorReceived -= OnClientError;
        _realtimeClient.StateChanged -= OnConnectionStateChanged;
        _audioCaptureService.AudioDataAvailable -= OnAudioDataAvailable;
        _startStopLock.Dispose();
    }

    // TruncateForLog / ShouldLogAtCount は Core/Services/LogFormatting.cs に集約 (rere D1 修正)。
    // 呼び出し名はそのまま維持し、 中身を委譲する形で 1 箇所管理に統一。
    private static string TruncateForLog(string? text, int maxLength = LogFormatting.DefaultTruncateLength)
        => LogFormatting.TruncateForLog(text, maxLength);

    private static bool ShouldLogAtCount(long count)
        => LogFormatting.ShouldLogAtCount(count);

    // SegmentId 専用の短縮ヘルパー (GUID 先頭 8 文字)。 LogFormatting に置く程の汎用性なし。
    private static string ShortSegmentId(string? segmentId, int prefixLength = 8)
    {
        if (string.IsNullOrEmpty(segmentId)) return string.Empty;
        return segmentId.Length <= prefixLength ? segmentId : segmentId[..prefixLength];
    }

    // StringBuilder が string の prefix と一致するかを ToString() 経由せず char ごとに比較する。
    // rere C1-P0-001 / A2-001: _lastFinalizedTranscript の StartsWith 比較を ToString() 抜きで実現。
    // O(min(sb.Length, text.Length)) で、 不一致が早期発見できれば更に短縮される。
    private static bool StringBuilderIsPrefixOf(StringBuilder sb, string text)
    {
        if (sb.Length > text.Length) return false;
        for (int i = 0; i < sb.Length; i++)
        {
            if (sb[i] != text[i]) return false;
        }
        return true;
    }

    // StringBuilder の末尾 maxLength 文字を string として取り出す (ログ用)。
    // sb.Length <= maxLength なら全部、 それ以上なら末尾 maxLength のみ。
    private static string TailOf(StringBuilder sb, int maxLength)
    {
        if (sb.Length == 0) return string.Empty;
        if (sb.Length <= maxLength) return sb.ToString();
        return sb.ToString(sb.Length - maxLength, maxLength);
    }

    /// <summary>
    /// 指定位置の文字が「文の区切り (= 句点)」か判定する。
    /// ピリオド '.' は数字に挟まれている場合 (例: 「6.3インチ」「3.14」) は小数点として保護し、
    /// 区切らない。 また、 delta 受信中 (<paramref name="isFinalContext"/>=false) かつ末尾ピリオド
    /// 直前が数字の場合は「次の delta で続きの数字が来るかも」と判断して保留する
    /// (false を返す)。 done 受信時 (isFinalContext=true) は遠慮なく区切る。
    /// </summary>
    /// <remarks>
    /// テスト容易化のため internal (InternalsVisibleTo=RealTimeTranslator.Tests)。
    /// </remarks>
    internal static bool IsSentenceBoundaryAt(string text, int i, bool isFinalContext)
    {
        if (i < 0 || i >= text.Length) return false;
        char c = text[i];
        if (Array.IndexOf(SentenceTerminators, c) < 0) return false;

        // ピリオド以外 (。 ！ ？ ! ?) は問答無用で区切り
        if (c != '.') return true;

        bool prevIsDigit = i > 0 && text[i - 1] >= '0' && text[i - 1] <= '9';
        bool nextIsDigit = i + 1 < text.Length && text[i + 1] >= '0' && text[i + 1] <= '9';

        // 小数点 (例: 6.3 / 3.14): 区切らない
        if (prevIsDigit && nextIsDigit) return false;

        // delta 受信中の「末尾ピリオド + 直前数字」: 次の delta で「3インチ」みたいな
        // 続きが来る可能性 → 区切らず保留 (続きが来てから判定)。
        // done 確定時 (isFinalContext=true) は遠慮なく区切る。
        if (!isFinalContext && prevIsDigit && i == text.Length - 1) return false;

        return true;
    }

    /// <summary>
    /// StringBuilder 版オーバーロード。 OnTranscriptDelta の O(n²) → O(n) 最適化で
    /// ToString() を回避して StringBuilder.indexer (O(1)) で直接判定するために追加。
    /// ロジックは string 版と完全に同一。
    /// </summary>
    internal static bool IsSentenceBoundaryAt(StringBuilder sb, int i, int length, bool isFinalContext)
    {
        if (i < 0 || i >= length) return false;
        char c = sb[i];
        if (Array.IndexOf(SentenceTerminators, c) < 0) return false;

        if (c != '.') return true;

        bool prevIsDigit = i > 0 && sb[i - 1] >= '0' && sb[i - 1] <= '9';
        bool nextIsDigit = i + 1 < length && sb[i + 1] >= '0' && sb[i + 1] <= '9';

        if (prevIsDigit && nextIsDigit) return false;
        if (!isFinalContext && prevIsDigit && i == length - 1) return false;

        return true;
    }

    // ═══════════════════════════════════════════════════════════════
    // VAD ゲート + 自動 Pause + 統計計算 (案 D + 案 G 実装)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// VAD 有効時の音声処理: 16kHz raw audio を VAD フレームサイズ単位 (512 サンプル/32ms)
    /// に切り出して順次 <see cref="ProcessVadFrame"/> へ流す。 audio chunk 境界とフレーム境界が
    /// ズレるため、 _frameAccumulator に残り部分を保持して次 chunk と結合する。
    /// テスト目的で internal (InternalsVisibleTo=RealTimeTranslator.Tests)。
    /// </summary>
    internal void ProcessAudioWithVadGate(float[] audio16kHz, AudioCaptureSettings settings)
    {
        int frameSize = _vad.RequiredFrameSize;
        if (_frameAccumulator is null || _frameAccumulator.Length != frameSize)
        {
            _frameAccumulator = new float[frameSize];
            _frameAccumulatorLen = 0;
        }

        int offset = 0;
        while (offset < audio16kHz.Length)
        {
            int copy = Math.Min(frameSize - _frameAccumulatorLen, audio16kHz.Length - offset);
            Array.Copy(audio16kHz, offset, _frameAccumulator, _frameAccumulatorLen, copy);
            _frameAccumulatorLen += copy;
            offset += copy;

            if (_frameAccumulatorLen == frameSize)
            {
                ProcessVadFrame(_frameAccumulator, settings);
                _frameAccumulatorLen = 0;
            }
        }
    }

    /// <summary>
    /// 1 フレーム (512 サンプル / 32ms) を VAD で判定し、 Silence/InSpeech/Hangover 状態機を回す。
    /// テスト目的で internal (InternalsVisibleTo=RealTimeTranslator.Tests)。
    /// </summary>
    internal void ProcessVadFrame(float[] frame16kHz, AudioCaptureSettings settings)
    {
        float prob;
        try
        {
            prob = _vad.DetectSpeechProb(frame16kHz);
        }
        catch (Exception ex)
        {
            // VAD 推論失敗時は安全側 (= 送信) に倒し、 誤って発話を捨てない。
            Logger.Warn($"VAD 推論失敗 (発話と見なして素通し): {ex.Message}");
            SendFrameToClient(frame16kHz);
            _lastSpeechUtc = DateTime.UtcNow;
            return;
        }

        bool isSpeech = prob >= settings.VadThreshold;
        double frameSeconds = (double)frame16kHz.Length / _vad.SampleRate;
        // ms 値をフレーム数換算 (最低 1 / 0 を保証)。 frameMs は VAD の RequiredFrameSize と
        // SampleRate から動的計算するため、 別 VAD (例: v4 / 8kHz モード) に差し替えても破綻しない。
        double frameMs = frameSeconds * 1000.0;
        int preRollCapacity = Math.Max(1, (int)(settings.VadPreRollMs / frameMs));
        int hangoverFrames = Math.Max(0, (int)(settings.VadHangoverMs / frameMs));


        switch (_vadState)
        {
            case VadState.Silence:
                if (isSpeech)
                {
                    // PreRoll バッファを丸ごと送信して発話冒頭の取りこぼしを補う。
                    while (_preRollBuffer.Count > 0)
                    {
                        var preRollFrame = _preRollBuffer.Dequeue();
                        SendFrameToClient(preRollFrame);
                    }
                    SendFrameToClient(frame16kHz);
                    _vadState = VadState.InSpeech;
                    _lastSpeechUtc = DateTime.UtcNow;
                }
                else
                {
                    // PreRoll に積む (古いものは押し出す = OpenAI 送信スキップとして秒数加算)。
                    while (_preRollBuffer.Count >= preRollCapacity)
                    {
                        _preRollBuffer.Dequeue();
                        _skippedSecondsByVad += frameSeconds;
                    }
                    var copy = new float[frame16kHz.Length];
                    Array.Copy(frame16kHz, copy, frame16kHz.Length);
                    _preRollBuffer.Enqueue(copy);
                }
                break;

            case VadState.InSpeech:
                SendFrameToClient(frame16kHz);
                if (isSpeech)
                {
                    _lastSpeechUtc = DateTime.UtcNow;
                }
                else
                {
                    _vadState = VadState.Hangover;
                    _hangoverFramesRemaining = hangoverFrames;
                }
                break;

            case VadState.Hangover:
                SendFrameToClient(frame16kHz);
                if (isSpeech)
                {
                    _vadState = VadState.InSpeech;
                    _lastSpeechUtc = DateTime.UtcNow;
                    _hangoverFramesRemaining = 0;
                }
                else
                {
                    _hangoverFramesRemaining--;
                    if (_hangoverFramesRemaining <= 0)
                    {
                        _vadState = VadState.Silence;
                        _preRollBuffer.Clear();
                        // LSTM state は連続性が高い方が誤検出が減るためここでは Reset しない
                        // (Start 時にだけ Reset を呼ぶ)。
                    }
                }
                break;
        }
    }

    /// <summary>VAD ゲート通過分のフレームを 24kHz PCM16 に変換して OpenAI へ送信。</summary>
    private void SendFrameToClient(float[] frame16kHz)
    {
        var pcm16 = AudioFormatConverter.Float32ToPcm16(
            AudioFormatConverter.ResampleTo24kHz(frame16kHz));
        _realtimeClient.SendAudio(pcm16);
    }

    /// <summary>
    /// 5 秒間隔で「最後に speech 検出してから AutoPauseOnSilenceSec 経過したか」を監視し、
    /// 超過していたらキャプチャを自動停止して UI に通知する。
    /// 設定で AutoPauseOnSilenceSec=0 (default) なら何もしない。
    /// 1 回発火したら自身を終了 (ユーザーが Start 押し直したら新タスクが起動される)。
    /// </summary>
    private async Task AutoPauseLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                if (!_isRunning) continue;

                var settings = _settingsMonitor.CurrentValue.AudioCapture;
                if (!settings.EnableVad || settings.AutoPauseOnSilenceSec <= 0) continue;

                var elapsed = (DateTime.UtcNow - _lastSpeechUtc).TotalSeconds;
                if (elapsed < settings.AutoPauseOnSilenceSec) continue;

                Logger.Info($"自動 Pause 発火: {elapsed:F0}秒間 speech 未検出のためキャプチャを停止します");
                try
                {
                    _audioCaptureService.StopCapture();
                }
                catch (Exception ex)
                {
                    Logger.Warn($"自動 Pause で StopCapture 失敗: {ex.Message}");
                }

                StatsUpdated?.Invoke(this, BuildCurrentStats($"⏸️ 約{(int)elapsed}秒間 speech 未検出のため自動停止しました"));

                // 1 回発火で抜ける (ユーザーが Start 押し直すと新タスクが起動)。
                break;
            }
        }
        catch (OperationCanceledException) { /* StopAsync 経由の停止 */ }
        catch (Exception ex)
        {
            Logger.Warn($"自動 Pause ループで予期しないエラー: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 1 秒周期で StatsUpdated を発火する tick ループ。
    /// API response が来ない silence 区間でも UI の経過時間 / 累計トークン / VAD 節約秒数を
    /// リアルタイム表示するために存在する。 StatusText は空文字を渡し (MainViewModel 側で空なら
    /// 既存値を維持する設計) Status ラベルを上書きしない。
    /// </summary>
    private async Task StatsTickLoopAsync(CancellationToken ct)
    {
        // rere F-005: ループ全体を 1 度の try/catch で包むと、 ハンドラ側の例外で stats tick 全体が止まり
        // UI が「時計止まった = アプリ死んだ」と誤解される。 try/catch を内側に入れて 1 周スキップで継続させる。
        // ErrorOccurred を発火して UI 側にバナー通知する経路も追加。 ただし 1 秒周期で連発を避けるため、
        // 前回エラーから 60 秒以上経過時のみ ErrorOccurred を発火する rate-limit を入れる。
        DateTime lastErrorReportUtc = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                if (!_isRunning) continue;
                StatsUpdated?.Invoke(this, BuildCurrentStats(string.Empty));
            }
            catch (OperationCanceledException)
            {
                // StopAsync 経由の正常終了
                return;
            }
            catch (Exception ex)
            {
                Logger.Error($"stats tick ループで予期しないエラー (1 周スキップ): {ex.Message}", ex);
                if ((DateTime.UtcNow - lastErrorReportUtc).TotalSeconds >= 60)
                {
                    lastErrorReportUtc = DateTime.UtcNow;
                    ErrorOccurred?.Invoke(this, ex);
                }
            }
        }
    }

    /// <summary>
    /// 現セッションの完全な統計スナップショットを構築する。 StatusText のみイベント特有値、
    /// 他フィールド (SessionDuration / Tokens / Cost / SkippedSecondsByVad) は累積値で必ず埋める。
    /// rere C2-001 / B2-005 対応: 個別 invoke で累積フィールドを忘れて UI を 0 に巻き戻す経路を構造的に消す。
    /// ProcessingLatency / TranslationLatency が必要な箇所は呼び出し側で result.X = ... と追加 set する。
    /// </summary>
    private PipelineStatsEventArgs BuildCurrentStats(string statusText = "")
    {
        return new PipelineStatsEventArgs
        {
            StatusText = statusText,
            SessionDuration = DateTime.UtcNow - _sessionStartUtc,
            InputAudioTokensEstimate = ComputeCurrentTokens(),
            EstimatedCostUsd = ComputeCurrentCostUsd(),
            SkippedSecondsByVad = _skippedSecondsByVad,
        };
    }

    /// <summary>
    /// 現時点の audio input tokens 累計を返す (サーバー報告値優先、 取れない場合は送信秒数推定)。
    /// </summary>
    private long ComputeCurrentTokens()
    {
        var serverTokens = _realtimeClient.ServerReportedAudioInputTokens;
        if (serverTokens > 0) return serverTokens;
        return CostEstimator.EstimateTokensFromSamples(
            _realtimeClient.TotalAudioInputSamples24kHz, 24000);
    }

    /// <summary>現時点の推定コスト (USD)。 モデル名は OpenAIRealtime.Model から解決。</summary>
    private decimal ComputeCurrentCostUsd()
    {
        var model = _settingsMonitor.CurrentValue.OpenAIRealtime.Model;
        return CostEstimator.EstimateUsd(model, ComputeCurrentTokens());
    }
}
