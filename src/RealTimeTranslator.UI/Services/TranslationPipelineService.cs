using System.Diagnostics;
using System.Threading.Channels;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;

namespace RealTimeTranslator.UI.Services;

/// <summary>
/// 翻訳パイプラインサービス
/// 音声キャプチャ、VAD、キューイング、ASR、翻訳の処理フローを管理します
/// </summary>
public class TranslationPipelineService : ITranslationPipelineService
{
    private const int MaxTranslationParallelism = 2;
    private const int ChannelCapacity = 100;

    private readonly IAudioCaptureService _audioCaptureService;
    private readonly IVADService _vadService;
    private readonly IASRService _asrService;
    private readonly ITranslationService _translationService;
    private readonly AppSettings _settings;

    private Channel<SpeechSegmentWorkItem>? _translationChannel;
    private Task? _translationProcessingTask;
    private CancellationTokenSource? _processingCancellation;
    private long _segmentSequence;

    /// <summary>
    /// 字幕が生成されたときに発火するイベント
    /// </summary>
    public event EventHandler<SubtitleItem>? SubtitleGenerated;

    /// <summary>
    /// パイプラインの統計情報が更新されたときに発火するイベント
    /// </summary>
    public event EventHandler<PipelineStatsEventArgs>? StatsUpdated;

    /// <summary>
    /// エラーが発生したときに発火するイベント
    /// </summary>
    public event EventHandler<Exception>? ErrorOccurred;

    /// <summary>
    /// TranslationPipelineService コンストラクタ
    /// </summary>
    /// <param name="audioCaptureService">音声キャプチャサービス</param>
    /// <param name="vadService">音声活動検出サービス</param>
    /// <param name="asrService">音声認識サービス</param>
    /// <param name="translationService">翻訳サービス</param>
    /// <param name="settings">アプリケーション設定</param>
    public TranslationPipelineService(
        IAudioCaptureService audioCaptureService,
        IVADService vadService,
        IASRService asrService,
        ITranslationService translationService,
        AppSettings settings)
    {
        _audioCaptureService = audioCaptureService;
        _vadService = vadService;
        _asrService = asrService;
        _translationService = translationService;
        _settings = settings;

        _audioCaptureService.AudioDataAvailable += OnAudioDataAvailable;
    }

    /// <summary>
    /// パイプラインを開始します
    /// </summary>
    /// <param name="token">キャンセルトークン</param>
    /// <returns>非同期操作のタスク</returns>
    public async Task StartAsync(CancellationToken token)
    {
        await StopAsync();

        // VADモデルのロード完了を待機
        LoggerService.LogDebug("[Pipeline] VADモデルのロード完了を待機中...");
        await _vadService.EnsureModelLoadedAsync();
        LoggerService.LogDebug("[Pipeline] VADモデルのロード完了");
        _vadService.ResetForNewSession();

        _processingCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        _segmentSequence = 0;

        _translationChannel = Channel.CreateBounded<SpeechSegmentWorkItem>(new BoundedChannelOptions(ChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        _translationProcessingTask = Task.Run(() => ProcessTranslationQueueAsync(_translationChannel.Reader, _processingCancellation.Token), _processingCancellation.Token);
    }

    /// <summary>
    /// パイプラインを停止します
    /// </summary>
    /// <returns>非同期操作のタスク</returns>
    public async Task StopAsync()
    {
        _processingCancellation?.Cancel();
        _translationChannel?.Writer.TryComplete();

        if (_translationProcessingTask != null)
        {
            try
            {
                await _translationProcessingTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // タイムアウトまたはキャンセル、無視
            }
        }

        _processingCancellation?.Dispose();
        _processingCancellation = null;
        _translationChannel = null;
        _translationProcessingTask = null;
    }

    /// <summary>
    /// 音声データ受信時のハンドラー
    /// VADで発話区間を検出し、キューに追加します
    /// </summary>
    private void OnAudioDataAvailable(object? sender, AudioDataEventArgs e)
    {
        if (_translationChannel == null || _processingCancellation == null || _processingCancellation.IsCancellationRequested)
            return;

        try
        {
            var segments = _vadService.DetectSpeech(e.AudioData);

            foreach (var segment in segments)
            {
                if (_processingCancellation.IsCancellationRequested)
                    return;

                var sequence = Interlocked.Increment(ref _segmentSequence);
                var workItem = new SpeechSegmentWorkItem(sequence, segment);

                if (!_translationChannel.Writer.TryWrite(workItem))
                {
                    LoggerService.LogWarning($"[キュー] セグメント#{sequence}をキューに追加できないため破棄しました (ID: {segment.Id})");
                }
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex);
        }
    }

    /// <summary>
    /// 翻訳キューを処理するメインループ
    /// </summary>
    private async Task ProcessTranslationQueueAsync(ChannelReader<SpeechSegmentWorkItem> reader, CancellationToken token)
    {
        var semaphore = new SemaphoreSlim(MaxTranslationParallelism);
        var pendingTasks = new List<Task>();

        try
        {
            await foreach (var item in reader.ReadAllAsync(token))
            {
                await semaphore.WaitAsync(token);
                pendingTasks.RemoveAll(t => t.IsCompleted);

                var task = HandleTranslationItemAsync(item, semaphore, token);
                pendingTasks.Add(task);
            }
        }
        catch (OperationCanceledException)
        {
            // キャンセル時は無視
        }
        finally
        {
            // 実行中のタスクがsemaphore.Release()を呼ぶ前にDisposeするとObjectDisposedExceptionになるため、
            // 全タスクの完了を待ってからDisposeする
            pendingTasks.RemoveAll(t => t.IsCompleted);
            if (pendingTasks.Count > 0)
            {
                try
                {
                    await Task.WhenAll(pendingTasks).WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception)
                {
                    // タイムアウトまたはタスクエラー、無視
                }
            }
            semaphore.Dispose();
        }
    }

    /// <summary>
    /// 個別のセグメントを処理（ASR→翻訳）
    /// </summary>
    private async Task HandleTranslationItemAsync(SpeechSegmentWorkItem item, SemaphoreSlim semaphore, CancellationToken token)
    {
        try
        {
            if (token.IsCancellationRequested)
                return;

            var sw = Stopwatch.StartNew();
            var sourceLang = _settings.Translation.SourceLanguage.ToString();
            var targetLang = _settings.Translation.TargetLanguage.ToString();

            // ステップ1: ASRで音声を文字起こし
            if (!_asrService.IsModelLoaded)
                return;

            var asrResult = await _asrService.TranscribeAccurateAsync(item.Segment);
            if (string.IsNullOrWhiteSpace(asrResult.Text))
                return;
            if (IsBlankAudioText(asrResult.Text))
            {
                LoggerService.LogDebug("[Pipeline] [BLANK_AUDIO] を検出したため翻訳と字幕をスキップします");
                return;
            }

            // ステップ2: 翻訳
            string translatedText = asrResult.Text;
            if (_translationService.IsModelLoaded)
            {
                var transResult = await _translationService.TranslateAsync(asrResult.Text, sourceLang, targetLang);
                translatedText = transResult.TranslatedText;
            }

            sw.Stop();

            // 結果通知
            var subtitle = new SubtitleItem
            {
                SegmentId = item.Segment.Id,
                OriginalText = asrResult.Text,
                TranslatedText = translatedText,
                IsFinal = true
            };
            SubtitleGenerated?.Invoke(this, subtitle);

            // 統計通知
            StatsUpdated?.Invoke(this, new PipelineStatsEventArgs
            {
                ProcessingLatency = sw.ElapsedMilliseconds,
                TranslationLatency = (double)(sw.ElapsedMilliseconds - asrResult.ProcessingTimeMs)
            });
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, ex);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// 音声セグメント作業項目
    /// </summary>
    private sealed record SpeechSegmentWorkItem(long Sequence, SpeechSegment Segment);

    /// <summary>
    /// 空白の音声判定（Whisperのプレースホルダーを除外）
    /// </summary>
    /// <param name="text">判定対象のテキスト</param>
    /// <returns>空白の音声なら true</returns>
    private static bool IsBlankAudioText(string text)
    {
        var normalized = text.Trim();
        return normalized.Equals("[BLANK_AUDIO]", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("[BLANK AUDIO]", StringComparison.OrdinalIgnoreCase);
    }

    private bool _disposed = false;

    /// <summary>
    /// TranslationPipelineService のディスポーズ
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _audioCaptureService.AudioDataAvailable -= OnAudioDataAvailable;

        // デッドロック回避: GetAwaiter().GetResult()を使用し、タイムアウト付きで待機
        try
        {
            _processingCancellation?.Cancel();
            _translationChannel?.Writer.TryComplete();

            if (_translationProcessingTask != null && !_translationProcessingTask.IsCompleted)
            {
                // Task.WaitではなくGetAwaiter().GetResult()を使用
                // ただし、タイムアウトが必要な場合はTask.Wait(timeout)を使用
                var waitTask = Task.Run(async () =>
                {
                    if (_translationProcessingTask != null)
                    {
                        await _translationProcessingTask.ConfigureAwait(false);
                    }
                });

                if (!waitTask.Wait(TimeSpan.FromSeconds(5)))
                {
                    LoggerService.LogWarning("TranslationPipelineService.Dispose: Processing task did not complete within timeout");
                }
            }
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"TranslationPipelineService.Dispose: Error during disposal: {ex.Message}");
        }
        finally
        {
            _processingCancellation?.Dispose();
        }
    }
}
