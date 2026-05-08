using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using SuperLightLogger;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;

namespace RealTimeTranslator.UI.Services;

public sealed class TranslationPipelineService : ITranslationPipelineService
{
    private static readonly ILog Logger = LogManager.GetLogger<TranslationPipelineService>();
    private static readonly TimeSpan DeltaThrottle = TimeSpan.FromMilliseconds(100);

    private readonly IAudioCaptureService _audioCaptureService;
    private readonly OpenAIRealtimeClient _realtimeClient;
    private readonly IOptionsMonitor<AppSettings> _settingsMonitor;

    private string _currentSegmentId = Guid.NewGuid().ToString();
    private readonly StringBuilder _accumulatedText = new();
    private readonly object _textLock = new();
    private DateTime _lastEmitTime = DateTime.MinValue;
    private bool _hasPendingDelta;
    private readonly Timer _throttleTimer;
    private readonly Stopwatch _latencyStopwatch = new();
    private bool _isRunning;
    private bool _disposed;

    public event EventHandler<SubtitleItem>? SubtitleGenerated;
    public event EventHandler<PipelineStatsEventArgs>? StatsUpdated;
    public event EventHandler<Exception>? ErrorOccurred;

    public TranslationPipelineService(
        IAudioCaptureService audioCaptureService,
        OpenAIRealtimeClient realtimeClient,
        IOptionsMonitor<AppSettings> settingsMonitor)
    {
        _audioCaptureService = audioCaptureService;
        _realtimeClient = realtimeClient;
        _settingsMonitor = settingsMonitor;
        _throttleTimer = new Timer(OnThrottleTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);

        _realtimeClient.TranscriptDeltaReceived += OnTranscriptDelta;
        _realtimeClient.TranscriptCompleted += OnTranscriptCompleted;
        _realtimeClient.ErrorReceived += OnClientError;
        _realtimeClient.StateChanged += OnConnectionStateChanged;
    }

    public async Task StartAsync(CancellationToken token)
    {
        if (_isRunning) return;

        var settings = _settingsMonitor.CurrentValue.OpenAIRealtime;
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            var ex = new InvalidOperationException("OpenAI APIキーが設定されていません。設定画面でキーを入力してください。");
            ErrorOccurred?.Invoke(this, ex);
            throw ex;
        }

        Logger.Info("翻訳パイプライン開始（OpenAI Realtime API）");

        await _realtimeClient.ConnectAsync(settings, token);
        _audioCaptureService.AudioDataAvailable += OnAudioDataAvailable;
        _isRunning = true;
        _latencyStopwatch.Start();

        StatsUpdated?.Invoke(this, new PipelineStatsEventArgs
        {
            StatusText = "API接続完了"
        });
    }

    public async Task StopAsync()
    {
        if (!_isRunning) return;

        Logger.Info("翻訳パイプライン停止");
        _audioCaptureService.AudioDataAvailable -= OnAudioDataAvailable;
        _isRunning = false;
        _latencyStopwatch.Stop();
        _throttleTimer.Change(Timeout.Infinite, Timeout.Infinite);

        await _realtimeClient.DisconnectAsync();

        StatsUpdated?.Invoke(this, new PipelineStatsEventArgs
        {
            StatusText = "停止"
        });
    }

    private void OnAudioDataAvailable(object? sender, AudioDataEventArgs e)
    {
        if (!_isRunning) return;

        try
        {
            var pcm16 = AudioFormatConverter.Float32ToPcm16(
                AudioFormatConverter.ResampleTo24kHz(e.AudioData));
            _realtimeClient.SendAudio(pcm16);
        }
        catch (Exception ex)
        {
            Logger.Error("音声データ変換エラー", ex);
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

        lock (_textLock)
        {
            segmentId = _currentSegmentId;
            latencyMs = _latencyStopwatch.Elapsed.TotalMilliseconds;

            _currentSegmentId = Guid.NewGuid().ToString();
            _accumulatedText.Clear();
            _lastEmitTime = DateTime.MinValue;
            _hasPendingDelta = false;
            _throttleTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _latencyStopwatch.Reset();
        }

        var subtitle = new SubtitleItem
        {
            SegmentId = segmentId,
            OriginalText = transcript,
            TranslatedText = transcript,
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _throttleTimer.Dispose();
        _realtimeClient.TranscriptDeltaReceived -= OnTranscriptDelta;
        _realtimeClient.TranscriptCompleted -= OnTranscriptCompleted;
        _realtimeClient.ErrorReceived -= OnClientError;
        _realtimeClient.StateChanged -= OnConnectionStateChanged;
        _audioCaptureService.AudioDataAvailable -= OnAudioDataAvailable;
    }
}
