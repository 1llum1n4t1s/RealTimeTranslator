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

    public event EventHandler<SubtitleItem>? SubtitleGenerated;
    public event EventHandler<PipelineStatsEventArgs>? StatsUpdated;
    public event EventHandler<Exception>? ErrorOccurred;

    private readonly IOptionsMonitor<AppSettings> _settingsMonitor;

    public TranslationPipelineService(
        IAudioCaptureService audioCaptureService,
        IRealtimeTranscriber realtimeClient,
        IOptionsMonitor<AppSettings> settingsMonitor)
    {
        _audioCaptureService = audioCaptureService;
        _realtimeClient = realtimeClient;
        _settingsMonitor = settingsMonitor;
        _cachedRealtimeSettings = settingsMonitor.CurrentValue.OpenAIRealtime;
        _throttleTimer = new Timer(OnThrottleTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);

        _realtimeClient.TranscriptDeltaReceived += OnTranscriptDelta;
        _realtimeClient.TranscriptCompleted += OnTranscriptCompleted;
        _realtimeClient.ErrorReceived += OnClientError;
        _realtimeClient.StateChanged += OnConnectionStateChanged;
    }

    public Task ApplySettingsAsync(OpenAIRealtimeSettings settings, CancellationToken cancellationToken = default)
    {
        // 現状はキャッシュ更新のみだが、将来的に再接続処理を組み込みやすいよう
        // インターフェース規約に合わせて Task / CancellationToken を受け取る形にしている。
        cancellationToken.ThrowIfCancellationRequested();
        _cachedRealtimeSettings = settings;
        return Task.CompletedTask;
    }

    public async Task StartAsync(CancellationToken token)
    {
        if (_isRunning) return;

        // settings.json で変更したばかりの内容 (OutputLanguage 等) が反映されるよう、
        // 起動直前に IOptionsMonitor から最新値を取り直す。
        // 旧実装は _cachedRealtimeSettings (構築時 or ApplySettingsAsync の値) を
        // 使っていたが、 UI で言語切替後にすぐ「開始」を押すと古い設定で接続して
        // しまうケースがあった。DPAPI で暗号化されている API キーも復号して使う。
        var freshSettings = _settingsMonitor.CurrentValue.OpenAIRealtime;
        SettingsService.DecryptApiKeyInPlace(_settingsMonitor.CurrentValue);
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

        await _realtimeClient.DisconnectAsync().ConfigureAwait(false);

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
            // (v1.0.11 まで継続発生) になるため、 delta 受信時にも _accumulatedText 内の
            // 句点を検出して完結文ごとに emit + SegmentId 切り替えを行う。
            // done が来る場合は OnTranscriptCompleted 側で _lastFinalizedTranscript の
            // startsWith 差分で「既出ぶん」と認識されて二重 emit されない (整合性保持)。
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

            if (completedSentences.Count > 0)
            {
                // 完結文を切り出した → trailing だけ _accumulatedText に残す。
                // trailing は新 SegmentId で次の partial emit / 句点検出を受ける。
                var trailing = text[start..];
                _accumulatedText.Clear();
                _accumulatedText.Append(trailing);
                _lastEmitTime = DateTime.UtcNow;
                _hasPendingDelta = false;
                _throttleTimer.Change(Timeout.Infinite, Timeout.Infinite);
                // 新 SegmentId で trailing を即座に partial 表示する (throttle 待たない)。
                // 完結文 emit と同じ delta 内で trailing を見せないと「文末で字幕が消える瞬間」が
                // 生じて UX として違和感が出るため。
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

        // ロック外で完結文を emit。 OverlayViewModel は SegmentId が新規なら別字幕として
        // 履歴に追加、 既存 SegmentId なら update。 今回はループ前半で _currentSegmentId を
        // 切り替えているので、 各 completedSentences の segmentId はそれぞれ別の値。
        foreach (var (segmentId, sentence) in completedSentences)
        {
            var n = Interlocked.Increment(ref _completedEmitCount);
            // 完結文 emit は字幕区切れ調査の中核なので全件 Info ログ。
            // 「字幕が成長し続ける」報告時にここが鳴っていなければ delta only API 挙動か
            // 句点不在のどちらか、 鳴っていれば overlay 表示側の問題と切り分けられる。
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
            // partial 表示用 (trailing or throttled flush)。 EmitPartialSubtitle 自体は
            // ロックを取らないので、 ここで取り直して呼ぶ。
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

            // 既に確定字幕として出した「完結文の累積」を差し引いて、 今回出すべき新規ぶん
            // (= 前回 trailing + 新規完結文 + 新規 trailing) を得る。
            // ⚠️ _lastFinalizedTranscript には trailing を含めないことが重要。
            // trailing を含めて累積を進めてしまうと、 次の done で「trailing の続き」だけが
            // newPortion になり、 完結文を emit するときに前回 trailing が消える UX バグになる
            // (v1.0.9 で発生し v1.0.10 で修正)。
            // trailing 部分は newPortion 先頭に毎回再登場することで、 完結時に既存 SegmentId に
            // update され、 字幕が成長して 1 文として完結表示される設計。
            // API 側で修正が入って prefix が崩れる稀なケースは effective 全体を新規ぶんとして扱う
            // (重複表示よりも欠落のほうがマシ)。
            string newPortion = effective.StartsWith(_lastFinalizedTranscript, StringComparison.Ordinal)
                ? effective[_lastFinalizedTranscript.Length..]
                : effective;

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
            // 完結文 emit 時の SegmentId は _currentSegmentId (= 前回 trailing と同じ ID)
            // にすることで、 前回 trailing 表示中の字幕を「完結形」に update できる。
            // emit 後に _currentSegmentId を新規発行し、 _lastFinalizedTranscript には
            // 完結文ぶんだけ加算 (trailing は含めない)。
            // 末尾の未完結フラグメント (句点なし) は新しい _currentSegmentId で update 表示する。
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
            // 次回 done で続きが来たら、 startsWith 差分で newPortion 先頭に再登場し
            // 完結文として既存 SegmentId に update される。
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
        // (delta only API では呼ばれないが、 通常 API / 将来の挙動変化への観測経路)
        int completedCount = emissions.Count(e => e.isSentenceEnd);
        int trailingCount = emissions.Count(e => !e.isSentenceEnd);
        Logger.Info($"OnTranscriptCompleted: newPortion長={newPortionLength} 完結文={completedCount}件 trailing={trailingCount}件 latency={latencyMs:F0}ms");

        // ロック外で emit。 OverlayViewModel は SegmentId が一致する subtitle を更新、
        // 新 SegmentId は新規追加して overlay に履歴として残す。
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
