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
    private static readonly TimeSpan DeltaThrottle = TimeSpan.FromMilliseconds(100);

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
    private readonly Stopwatch _latencyStopwatch = new();
    private volatile bool _isRunning;
    private volatile bool _disposed;
    private DateTime _lastAudioErrorLogTime = DateTime.MinValue;

    // OpenAI Realtime Translation API は transcript.done を「セッション累積の全文」で
    // 返してくる挙動が観測されている (2026-05-16)。 そのまま finalText として overlay に出すと
    // 「まあ → まあ、 → まあ、ノ → ...」と1つの subtitle が際限なく成長する UX 不具合になる。
    // _lastFinalizedTranscript で「既に確定字幕として出した累積テキスト」を保持し、
    // done のたびに差分だけを抽出 → 句点で分割 → 文ごとに新 SegmentId で emit することで
    // 「会話が途切れず長文化する」問題を回避する。
    private string _lastFinalizedTranscript = string.Empty;

    // 句点として扱う文字。 ASCII 終端も入れて英語訳出力にも対応する。
    // 「、」(読点) は文の区切りではないので含めない (一文の中で複数現れるため)。
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
        ISettingsService settingsService)
    {
        _audioCaptureService = audioCaptureService;
        _realtimeClient = realtimeClient;
        _settingsMonitor = settingsMonitor;
        _settingsService = settingsService;
        _cachedRealtimeSettings = settingsMonitor.CurrentValue.OpenAIRealtime;
        _throttleTimer = new Timer(OnThrottleTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);

        _realtimeClient.TranscriptDeltaReceived += OnTranscriptDelta;
        _realtimeClient.TranscriptCompleted += OnTranscriptCompleted;
        _realtimeClient.ErrorReceived += OnClientError;
        _realtimeClient.StateChanged += OnConnectionStateChanged;
    }

    // rere レビュー P2 B1-007: ApplySettingsAsync は dead code として削除済み
    // (StartAsync 内で _settingsMonitor.CurrentValue から再取得して即上書きされていた)。

    public async Task StartAsync(CancellationToken token)
    {
        if (_isRunning) return;

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
            _lastFinalizedTranscript = string.Empty;
            _accumulatedText.Clear();
            _currentSegmentId = Guid.NewGuid().ToString();
            _lastEmitTime = DateTime.MinValue;
            _hasPendingDelta = false;
        }

        await _realtimeClient.ConnectAsync(settings, token);

        // WASAPI コールバックスレッドで重い変換を行うと audio glitch の原因になるため、
        // Channel に raw float[] を投入だけして変換は専用タスクで行う。
        _audioInputChannel = Channel.CreateBounded<float[]>(new BoundedChannelOptions(50)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
        _audioProcessingCts = new CancellationTokenSource();
        _audioProcessingTask = Task.Run(() => ProcessAudioLoopAsync(_audioProcessingCts.Token));

        _audioCaptureService.AudioDataAvailable += OnAudioDataAvailable;
        _isRunning = true;
        _latencyStopwatch.Start();

        StatsUpdated?.Invoke(this, new PipelineStatsEventArgs
        {
            StatusText = "API接続完了"
        });
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_isRunning) return;

        Logger.Info("翻訳パイプライン停止");
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
        // DroppedAudioChunkCount はキャプチャ → API 送信の Channel<byte[]>(200) で
        // BoundedChannelFullMode.DropOldest によって捨てられた音声チャンク累計。
        // 「字幕が抜ける」報告時に「ローカル NW 詰まりか OpenAI 側遅延か」を切り分ける。
        if (_realtimeClient is OpenAIRealtimeClient openAiClient)
        {
            Logger.Info($"セッション統計: DroppedAudioChunks={openAiClient.DroppedAudioChunkCount} 完結文emit={Interlocked.Read(ref _completedEmitCount)} partial emit={Interlocked.Read(ref _partialEmitCount)}");
        }

        Logger.Info("翻訳パイプライン停止 完了");

        StatsUpdated?.Invoke(this, new PipelineStatsEventArgs
        {
            StatusText = "停止"
        });
    }

    private void OnAudioDataAvailable(object? sender, AudioDataEventArgs e)
    {
        if (!_isRunning) return;

        // WASAPI コールバックスレッド（MMCSS）で重い処理を行うと音声バッファが overflow して
        // audio glitch / Silent パケット化を起こすため、Channel に投入するだけで即座に戻る。
        // BoundedChannel(50, DropOldest) で詰まり時は古いものを捨てる（再接続復帰後は新しい音声を優先）。
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
                    var pcm16 = AudioFormatConverter.Float32ToPcm16(
                        AudioFormatConverter.ResampleTo24kHz(audioData));
                    _realtimeClient.SendAudio(pcm16);
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
            _accumulatedText.Append(delta);

            if (!_latencyStopwatch.IsRunning)
                _latencyStopwatch.Restart();

            // ⭐ OpenAI Realtime Translation API は transcript.done を送ってこない
            // ケースがある (2026-05-17 観測: delta のみで会話が進み done が来ない)。
            // done を待つ設計だと SegmentId が永久に固定され「字幕が無限成長する」UX バグ
            // になるため、 delta 受信時にも _accumulatedText 内の句点を検出して
            // 完結文ごとに emit + SegmentId 切り替えを行う。
            var text = _accumulatedText.ToString();
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (Array.IndexOf(SentenceTerminators, text[i]) >= 0)
                {
                    var sentence = text[start..(i + 1)];
                    if (!string.IsNullOrWhiteSpace(sentence))
                    {
                        completedSentences.Add((_currentSegmentId, sentence));
                        _currentSegmentId = Guid.NewGuid().ToString();
                        _lastFinalizedTranscript += sentence;
                    }
                    start = i + 1;
                }
            }

            // ⭐ D-7 句読点 fallback: 句点 (SentenceTerminators) が来なくても _accumulatedText が
            // FallbackSplitThreshold (100文字) を超えた場合、 末尾の読点 (「、」「,」「・」) で
            // 強制分割する。 永久未確定字幕 (partial 表示が伸び続ける UX バグ) を防止。
            // すでに完結文を切り出した直後 (completedSentences.Count > 0) は trailing が短いので
            // fallback 不要。 完結文ゼロかつ trailing が閾値超のケースのみ発動する。
            if (completedSentences.Count == 0 && (text.Length - start) > FallbackSplitThreshold)
            {
                // 末尾から「、」を探す。 見つかった位置までを 1 文として fallback emit。
                int trailingLength = text.Length - start;
                for (int i = text.Length - 1; i > start; i--)
                {
                    if (Array.IndexOf(FallbackSplitTerminators, text[i]) >= 0)
                    {
                        var sentence = text[start..(i + 1)];
                        if (!string.IsNullOrWhiteSpace(sentence))
                        {
                            Logger.Info($"OnTranscriptDelta: 句読点 fallback 分割 (trailing={trailingLength}文字 句点なし) → '{TruncateForLog(sentence)}'");
                            completedSentences.Add((_currentSegmentId, sentence));
                            _currentSegmentId = Guid.NewGuid().ToString();
                            _lastFinalizedTranscript += sentence;
                            start = i + 1;
                        }
                        break;
                    }
                }
            }

            if (completedSentences.Count > 0)
            {
                // 完結文を切り出した → trailing だけ _accumulatedText に残す。
                var trailing = text[start..];
                _accumulatedText.Clear();
                _accumulatedText.Append(trailing);
                _lastEmitTime = DateTime.UtcNow;
                _hasPendingDelta = false;
                _throttleTimer.Change(Timeout.Infinite, Timeout.Infinite);
                emitPartial = trailing.Length > 0;
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

        // 頻度抑制: partial は throttle で 100ms ごとに発火するので全件 Info にすると爆発する。
        // 1, 10, 50, 100, ... と間引いて累積長と SegmentId 推移を観測できるようにする。
        var count = Interlocked.Increment(ref _partialEmitCount);
        if (ShouldLogAtCount(count))
        {
            Logger.Info($"partial emit #{count}: SegmentId={ShortSegmentId(segmentId)} 累積長={partialText.Length} 内容='{TruncateForLog(partialText)}'");
        }
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
                effective = _lastFinalizedTranscript + _accumulatedText.ToString();
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
            if (!effective.StartsWith(_lastFinalizedTranscript, StringComparison.Ordinal))
            {
                Logger.Warn($"OnTranscriptCompleted: prefix 不整合検出 — done 経路スキップ (finalized 長={_lastFinalizedTranscript.Length} effective 長={effective.Length} finalized 末尾='{TruncateForLog(_lastFinalizedTranscript.Length > 20 ? _lastFinalizedTranscript[^20..] : _lastFinalizedTranscript, 20)}' effective 先頭='{TruncateForLog(effective, 30)}')");
                _accumulatedText.Clear();
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
                if (Array.IndexOf(SentenceTerminators, newPortion[i]) >= 0)
                {
                    // start..i が 1 文 (句点含む) として完結
                    var sentence = newPortion[start..(i + 1)];
                    if (!string.IsNullOrWhiteSpace(sentence))
                    {
                        emissions.Add((_currentSegmentId, sentence, true));
                        _currentSegmentId = Guid.NewGuid().ToString();
                        // 確定累積を完結文ぶんだけ進める (trailing は次回更新時に再登場させる)
                        _lastFinalizedTranscript += sentence;
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

        StatsUpdated?.Invoke(this, new PipelineStatsEventArgs
        {
            ProcessingLatency = latencyMs,
            TranslationLatency = latencyMs,
            StatusText = $"翻訳完了 ({latencyMs:F0}ms)"
        });
    }

    private void OnClientError(Exception ex)
    {
        Logger.Error("OpenAI Realtime クライアントエラー", ex);
        ErrorOccurred?.Invoke(this, ex);
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
                _lastFinalizedTranscript = string.Empty;
                _accumulatedText.Clear();
                _currentSegmentId = Guid.NewGuid().ToString();
                _lastEmitTime = DateTime.MinValue;
                _hasPendingDelta = false;
                _throttleTimer.Change(Timeout.Infinite, Timeout.Infinite);
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

        StatsUpdated?.Invoke(this, new PipelineStatsEventArgs
        {
            StatusText = statusText
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            await StopAsync();
        }
        catch (Exception ex)
        {
            Logger.Error("TranslationPipelineService.DisposeAsync: 停止エラー", ex);
        }

        _throttleTimer.Dispose();
        _realtimeClient.TranscriptDeltaReceived -= OnTranscriptDelta;
        _realtimeClient.TranscriptCompleted -= OnTranscriptCompleted;
        _realtimeClient.ErrorReceived -= OnClientError;
        _realtimeClient.StateChanged -= OnConnectionStateChanged;
        _audioCaptureService.AudioDataAvailable -= OnAudioDataAvailable;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 同期版: UIスレッドでのデッドロックを回避するため Task.Run 経由で呼び出す
        try
        {
            Task.Run(() => StopAsync()).GetAwaiter().GetResult();
        }
        catch { /* DisposeAsync を推奨 */ }

        _throttleTimer.Dispose();
        _realtimeClient.TranscriptDeltaReceived -= OnTranscriptDelta;
        _realtimeClient.TranscriptCompleted -= OnTranscriptCompleted;
        _realtimeClient.ErrorReceived -= OnClientError;
        _realtimeClient.StateChanged -= OnConnectionStateChanged;
        _audioCaptureService.AudioDataAvailable -= OnAudioDataAvailable;
    }

    // 観測ログ用ヘルパー: 長文を 40 文字までに切り詰めて、 余ったぶんは "..." で省略する。
    // 高頻度ログで全文を出すとログが膨張するため、 観測には先頭プレフィックスで十分という方針。
    private static string TruncateForLog(string? text, int maxLength = 40)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }

    // 観測ログ用ヘルパー: GUID 形式の SegmentId を先頭 8 文字だけに省略する。
    // GUID 全体は 36 文字あって読みづらいが、 先頭 8 文字でも識別には十分なため。
    private static string ShortSegmentId(string? segmentId, int prefixLength = 8)
    {
        if (string.IsNullOrEmpty(segmentId)) return string.Empty;
        return segmentId.Length <= prefixLength ? segmentId : segmentId[..prefixLength];
    }

    // 観測ログ用ヘルパー: 高頻度ログのうちログに残すカウントを判定する。
    // 1, 10, 50, 100, 200, 300, ..., 1000, 1500, 2000, ..., 10000, 11000, ... と
    // 序盤は密に、 後半は粗にログを出して全体傾向と異常を両方追えるようにする。
    private static bool ShouldLogAtCount(long count)
    {
        if (count == 1 || count == 10 || count == 50) return true;
        if (count < 1000) return count % 100 == 0;
        if (count < 10000) return count % 500 == 0;
        return count % 1000 == 0;
    }
}
