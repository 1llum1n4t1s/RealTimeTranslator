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

    public event EventHandler<SubtitleItem>? SubtitleGenerated;
    public event EventHandler<PipelineStatsEventArgs>? StatsUpdated;
    public event EventHandler<Exception>? ErrorOccurred;

    public TranslationPipelineService(
        IAudioCaptureService audioCaptureService,
        IRealtimeTranscriber realtimeClient,
        IOptionsMonitor<AppSettings> settingsMonitor)
    {
        _audioCaptureService = audioCaptureService;
        _realtimeClient = realtimeClient;
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

        var settings = _cachedRealtimeSettings;
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            var ex = new InvalidOperationException("OpenAI APIキーが設定されていません。設定画面でキーを入力してください。");
            ErrorOccurred?.Invoke(this, ex);
            throw ex;
        }

        Logger.Info("翻訳パイプライン開始（OpenAI Realtime API）");

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
        string segmentId;
        double latencyMs;
        string fallbackText;

        lock (_textLock)
        {
            segmentId = _currentSegmentId;
            latencyMs = _latencyStopwatch.Elapsed.TotalMilliseconds;
            // done で transcript が空のとき、直前まで delta で蓄積した文字列を確定字幕として使う。
            // これがないと「partial 表示 → 空の done で字幕が消える」UX バグになる。
            fallbackText = _accumulatedText.ToString();

            _currentSegmentId = Guid.NewGuid().ToString();
            _accumulatedText.Clear();
            _lastEmitTime = DateTime.MinValue;
            _hasPendingDelta = false;
            _throttleTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _latencyStopwatch.Reset();
        }

        var finalText = string.IsNullOrEmpty(transcript) ? fallbackText : transcript;
        if (string.IsNullOrEmpty(finalText))
        {
            // done も partial も両方空なら、UI に通知する意味がない（空の SubtitleItem は overlay に空表示を残す）
            Logger.Debug("OnTranscriptCompleted: 空の done を受信、字幕通知をスキップ");
            return;
        }

        var subtitle = new SubtitleItem
        {
            SegmentId = segmentId,
            OriginalText = string.Empty,
            TranslatedText = finalText,
            IsFinal = true
        };

        SubtitleGenerated?.Invoke(this, subtitle);

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
