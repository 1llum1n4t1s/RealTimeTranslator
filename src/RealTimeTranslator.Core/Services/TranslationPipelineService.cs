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

        lock (_textLock)
        {
            _accumulatedText.Append(delta);

            if (!_latencyStopwatch.IsRunning)
                _latencyStopwatch.Restart();

            var now = DateTime.UtcNow;
            if (now - _lastEmitTime >= DeltaThrottle)
            {
                EmitPartialSubtitle();
            }
            else if (!_hasPendingDelta)
            {
                _hasPendingDelta = true;
                _throttleTimer.Change(DeltaThrottle, Timeout.InfiniteTimeSpan);
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

        var subtitle = new SubtitleItem
        {
            SegmentId = _currentSegmentId,
            OriginalText = _accumulatedText.ToString(),
            TranslatedText = "",
            IsFinal = false
        };

        SubtitleGenerated?.Invoke(this, subtitle);
    }

    private void OnTranscriptCompleted(string transcript)
    {
        // 確定字幕として出すべき「新規ぶん」と、 各文の SegmentId を計算して
        // ロックの外で emit するため、 lock 内ではプランだけ作って終わる。
        List<(string segmentId, string text, bool isSentenceEnd)> emissions;
        double latencyMs;

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

            // 既に確定字幕として出したぶんを差し引いて、 今回出すべき新規ぶんを得る。
            // 通常は effective が _lastFinalizedTranscript で始まるはずだが、 API 側で
            // 修正が入って prefix が崩れる稀なケースもあり得るので、 startsWith が成立しない時は
            // effective 全体を新規ぶんとして扱う (重複表示よりも欠落のほうがマシ)。
            string newPortion = effective.StartsWith(_lastFinalizedTranscript, StringComparison.Ordinal)
                ? effective[_lastFinalizedTranscript.Length..]
                : effective;

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

            // newPortion を句点で分割して、 完結した文ごとに新 SegmentId を割り当てる。
            // 末尾の未完結フラグメント (句点なし) は現 SegmentId で更新表示する。
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
                    }
                    start = i + 1;
                }
            }
            // 残りの未完結部分
            if (start < newPortion.Length)
            {
                var trailing = newPortion[start..];
                if (!string.IsNullOrWhiteSpace(trailing))
                {
                    emissions.Add((_currentSegmentId, trailing, false));
                }
            }

            // 累積基準を進める + partial 表示用の蓄積をクリア
            _lastFinalizedTranscript = effective;
            _accumulatedText.Clear();
            _lastEmitTime = DateTime.MinValue;
            _hasPendingDelta = false;
            _throttleTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _latencyStopwatch.Reset();
        }

        // ロック外で emit。 OverlayViewModel は SegmentId が一致する subtitle を更新、
        // 新 SegmentId は新規追加して overlay に履歴として残す。
        foreach (var (segmentId, text, isSentenceEnd) in emissions)
        {
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
}
