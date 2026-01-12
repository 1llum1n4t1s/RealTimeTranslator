using System.Diagnostics;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;
using Whisper.net;
using Whisper.net.Ggml;
using static RealTimeTranslator.Core.Services.LoggerService;

namespace RealTimeTranslator.Translation.Services;

/// <summary>
/// Whisper.net ベースのASR（自動音声認識）サービス
/// OpenAI Whisper を使用した高精度な音声認識
/// GPU（CUDA/Vulkan/CPU）で高速実行
/// </summary>
public class WhisperASRService : IASRService
{
    private const string ServiceName = "音声認識";
    private const string ModelLabel = "Whisper音声認識モデル";

    private readonly TranslationSettings _settings;
    private readonly ModelDownloadService _downloadService;

    private bool _isModelLoaded = false;
    private WhisperProcessor? _processor;
    private WhisperFactory? _factory;

    private Dictionary<string, string> _correctionDictionary = new();
    private string _initialPrompt = string.Empty;
    private List<string> _hotwords = new();
    private readonly SemaphoreSlim _transcribeLock = new(1, 1);

    public bool IsModelLoaded => _isModelLoaded;

    public event EventHandler<ModelDownloadProgressEventArgs>? ModelDownloadProgress;
    public event EventHandler<ModelStatusChangedEventArgs>? ModelStatusChanged;

    public WhisperASRService(TranslationSettings settings, ModelDownloadService downloadService)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _downloadService = downloadService ?? throw new ArgumentNullException(nameof(downloadService));

        _downloadService.DownloadProgress += (sender, e) => ModelDownloadProgress?.Invoke(this, e);
        _downloadService.StatusChanged += (sender, e) => ModelStatusChanged?.Invoke(this, e);
    }

    protected virtual void OnModelStatusChanged(ModelStatusChangedEventArgs e)
    {
        ModelStatusChanged?.Invoke(this, e);
    }

    /// <summary>
    /// ASRエンジンを初期化
    /// </summary>
    public async Task InitializeAsync()
    {
        OnModelStatusChanged(new ModelStatusChangedEventArgs(
            ServiceName,
            ModelLabel,
            ModelStatusType.Info,
            "音声認識モデルの初期化を開始しました。"));

        try
        {
            // モデルファイルをダウンロード/確認
            const string defaultModelFileName = "ggml-large-v3.bin";
            const string downloadUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3.bin";

            var modelPath = Path.Combine(_settings.ModelPath, "asr");
            var modelFilePath = await _downloadService.EnsureModelAsync(
                modelPath,
                defaultModelFileName,
                downloadUrl,
                ServiceName,
                ModelLabel).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(modelFilePath))
            {
                throw new FileNotFoundException($"Failed to ensure model file for: {modelPath}");
            }

            // モデルをロード
            await Task.Run(() => LoadModelFromPath(modelFilePath)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"ASR initialization error: {ex}");
            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                ModelLabel,
                ModelStatusType.LoadFailed,
                "音声認識モデルの初期化に失敗しました。",
                ex));
            _isModelLoaded = false;
        }
    }

    /// <summary>
    /// 低遅延ASRで音声を文字起こし（仮字幕用）
    /// </summary>
    public async Task<TranscriptionResult> TranscribeFastAsync(SpeechSegment segment)
    {
        if (!_isModelLoaded || _processor == null)
        {
            LogError($"[TranscribeFastAsync] モデルが読み込まれていません");
            return new TranscriptionResult
            {
                SegmentId = segment.Id,
                Text = string.Empty,
                IsFinal = false,
                Confidence = 0f,
                ProcessingTimeMs = 0
            };
        }

        return await TranscribeInternalAsync(segment, isFast: true).ConfigureAwait(false);
    }

    /// <summary>
    /// 高精度ASRで音声を文字起こし（確定字幕用）
    /// </summary>
    public async Task<TranscriptionResult> TranscribeAccurateAsync(SpeechSegment segment)
    {
        if (!_isModelLoaded || _processor == null)
        {
            LogError($"[TranscribeAccurateAsync] モデルが読み込まれていません");
            return new TranscriptionResult
            {
                SegmentId = segment.Id,
                Text = string.Empty,
                IsFinal = true,
                Confidence = 0f,
                ProcessingTimeMs = 0
            };
        }

        return await TranscribeInternalAsync(segment, isFast: false).ConfigureAwait(false);
    }

    private async Task<TranscriptionResult> TranscribeInternalAsync(SpeechSegment segment, bool isFast)
    {
        await _transcribeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var sw = Stopwatch.StartNew();

            LogDebug($"[TranscribeInternalAsync] 音声認識開始: SegmentId={segment.Id}, AudioLength={segment.AudioData.Length}, Mode={(isFast ? "Fast" : "Accurate")}");

            // Whisper で音声を認識
            var recognizedSegments = new List<string>();
            await foreach (var whisperSegment in _processor!.ProcessAsync(segment.AudioData).ConfigureAwait(false))
            {
                var text = whisperSegment.Text.Trim();
                recognizedSegments.Add(text);
                LogDebug($"[TranscribeInternalAsync] 認識セグメント: {text}");
            }

            var recognizedText = string.Join(" ", recognizedSegments);
            if (string.IsNullOrWhiteSpace(recognizedText))
            {
                LogDebug($"[TranscribeInternalAsync] 音声から認識されたテキストがありません");
                sw.Stop();
                return new TranscriptionResult
                {
                    SegmentId = segment.Id,
                    Text = string.Empty,
                    IsFinal = !isFast,
                    Confidence = 0f,
                    DetectedLanguage = "en",
                    ProcessingTimeMs = sw.ElapsedMilliseconds
                };
            }

            LogDebug($"[TranscribeInternalAsync] 認識完了: {recognizedText}");

            // 誤変換補正辞書を適用
            var correctedText = ApplyCorrectionDictionary(recognizedText);

            sw.Stop();
            LogDebug($"[TranscribeInternalAsync] 処理完了: Result='{correctedText}', Time={sw.ElapsedMilliseconds}ms");

            return new TranscriptionResult
            {
                SegmentId = segment.Id,
                Text = correctedText,
                IsFinal = !isFast,
                Confidence = 0.9f, // Whisper.net doesn't provide confidence scores
                DetectedLanguage = "en",
                ProcessingTimeMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"[TranscribeInternalAsync] 音声認識エラー: {ex.GetType().Name} - {ex.Message}");
            LoggerService.LogDebug($"[TranscribeInternalAsync] StackTrace: {ex.StackTrace}");
            return new TranscriptionResult
            {
                SegmentId = segment.Id,
                Text = string.Empty,
                IsFinal = !isFast,
                Confidence = 0f,
                ProcessingTimeMs = 0
            };
        }
        finally
        {
            _transcribeLock.Release();
        }
    }

    public void SetHotwords(IEnumerable<string> hotwords)
    {
        _hotwords = new List<string>(hotwords);
        LogDebug($"[SetHotwords] ホットワード設定: {string.Join(", ", _hotwords)}");
    }

    public void SetInitialPrompt(string prompt)
    {
        _initialPrompt = prompt;
        LogDebug($"[SetInitialPrompt] 初期プロンプト設定: {prompt}");
    }

    public void SetCorrectionDictionary(Dictionary<string, string> dictionary)
    {
        _correctionDictionary = new Dictionary<string, string>(dictionary);
        LogDebug($"[SetCorrectionDictionary] 補正辞書設定: {_correctionDictionary.Count}個のエントリ");
    }

    private string ApplyCorrectionDictionary(string text)
    {
        foreach (var kvp in _correctionDictionary)
        {
            text = text.Replace(kvp.Key, kvp.Value, StringComparison.OrdinalIgnoreCase);
        }
        return text;
    }

    private void LoadModelFromPath(string modelPath)
    {
        try
        {
            LoggerService.LogDebug($"Whisper音声認識モデルの読み込み開始: {modelPath}");

            if (!File.Exists(modelPath))
            {
                throw new FileNotFoundException($"ASR model not found: {modelPath}");
            }

            // GPU を有効にするための環境変数設定（複数オプションをサポート）
            // NVIDIA CUDA をサポート
            Environment.SetEnvironmentVariable("GGML_USE_CUDA", "1");
            LoggerService.LogDebug("GPU (CUDA) support enabled");

            // AMD RADEON をサポート（Vulkan）
            Environment.SetEnvironmentVariable("GGML_USE_VULKAN", "1");
            LoggerService.LogDebug("GPU (Vulkan/RADEON) support enabled");

            // AMD RADEON をサポート（HIP/ROCm）
            Environment.SetEnvironmentVariable("GGML_USE_HIP", "1");
            LoggerService.LogDebug("GPU (HIP/ROCm/RADEON) support enabled");

            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                ModelLabel,
                ModelStatusType.Info,
                "WhisperFactory を作成中..."));

            LoggerService.LogDebug($"Loading ASR model from: {modelPath}");
            _factory = WhisperFactory.FromPath(modelPath);

            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                ModelLabel,
                ModelStatusType.Info,
                "WhisperProcessor を作成中..."));

            var builder = _factory.CreateBuilder()
                .WithThreads(Environment.ProcessorCount);

            _processor = builder.Build();

            LoggerService.LogDebug("Whisper Processor created with GPU support (NVIDIA CUDA + AMD RADEON Vulkan/HIP)");

            _isModelLoaded = true;
            LoggerService.LogInfo("Whisper音声認識モデルの読み込みが完了しました");

            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                ModelLabel,
                ModelStatusType.LoadSucceeded,
                "Whisper音声認識モデルの読み込みが完了しました。"));
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"Whisper音声認識モデル読み込みに失敗: {ex}");
            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                ModelLabel,
                ModelStatusType.LoadFailed,
                "音声認識モデルの読み込みに失敗しました。",
                ex));
            _isModelLoaded = false;
        }
    }

    public void Dispose()
    {
        _processor?.Dispose();
        _factory?.Dispose();
        _transcribeLock.Dispose();
    }
}
