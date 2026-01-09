using System.Diagnostics;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using Whisper.net;
using Whisper.net.Ggml;

namespace RealTimeTranslator.ASR.Services;

/// <summary>
/// Whisper.netを使用したASRサービス
/// 二段構え構成：低遅延ASR（仮字幕）と高精度ASR（確定字幕）
/// </summary>
public class WhisperASRService : IASRService
{
    private WhisperProcessor? _fastProcessor;
    private WhisperProcessor? _accurateProcessor;
    private WhisperFactory? _fastFactory;
    private WhisperFactory? _accurateFactory;
    private readonly ASRSettings _settings;
    private readonly List<string> _hotwords = new();
    private string _initialPrompt = string.Empty;
    private bool _isModelLoaded = false;
    private readonly SemaphoreSlim _fastLock = new(1, 1);
    private readonly SemaphoreSlim _accurateLock = new(1, 1);

    public bool IsModelLoaded => _isModelLoaded;

    public WhisperASRService(ASRSettings? settings = null)
    {
        _settings = settings ?? new ASRSettings();
    }

    /// <summary>
    /// モデルを初期化
    /// </summary>
    public async Task InitializeAsync()
    {
        await Task.Run(() =>
        {
            // GPUランタイムの初期化条件:
            // - AMD Vulkan: Whisper.net.Runtime.Vulkan が必要。Vulkan対応ドライバと ggml 系モデルが必須。
            // - NVIDIA CUDA: Whisper.net.Runtime.Cublas が必要。CUDA対応ドライバが必須。
            // - Auto/CPU: 明示的なGPU設定は行わず、Whisper.netに委譲。
            ConfigureGpuRuntime();
            ValidateModelCompatibility();

            // 低遅延モデル（small/medium）の初期化
            if (File.Exists(_settings.FastModelPath))
            {
                _fastFactory = WhisperFactory.FromPath(_settings.FastModelPath);
                _fastProcessor = _fastFactory.CreateBuilder()
                    .WithLanguage(_settings.Language)
                    .WithThreads(4)
                    .Build();
            }

            // 高精度モデル（large系）の初期化
            if (File.Exists(_settings.AccurateModelPath))
            {
                _accurateFactory = WhisperFactory.FromPath(_settings.AccurateModelPath);
                var builder = _accurateFactory.CreateBuilder()
                    .WithLanguage(_settings.Language)
                    .WithThreads(4);

                if (_settings.UseBeamSearch)
                {
                    builder.WithBeamSearchSamplingStrategy();
                }

                _accurateProcessor = builder.Build();
            }

            _isModelLoaded = _fastProcessor != null || _accurateProcessor != null;
        });
    }

    /// <summary>
    /// 低遅延ASRで音声を文字起こし（仮字幕用）
    /// </summary>
    public async Task<TranscriptionResult> TranscribeFastAsync(SpeechSegment segment)
    {
        if (_fastProcessor == null)
        {
            // フォールバック: 高精度モデルを使用
            if (_accurateProcessor != null)
            {
                return await TranscribeAccurateAsync(segment);
            }
            throw new InvalidOperationException("No ASR model loaded");
        }

        await _fastLock.WaitAsync();
        try
        {
            var sw = Stopwatch.StartNew();
            var result = await ProcessAudioAsync(_fastProcessor, segment.AudioData);
            sw.Stop();

            return new TranscriptionResult
            {
                SegmentId = segment.Id,
                Text = ApplyCorrections(result.Text),
                IsFinal = false,
                Confidence = result.Confidence,
                DetectedLanguage = _settings.Language,
                ProcessingTimeMs = sw.ElapsedMilliseconds
            };
        }
        finally
        {
            _fastLock.Release();
        }
    }

    /// <summary>
    /// 高精度ASRで音声を文字起こし（確定字幕用）
    /// </summary>
    public async Task<TranscriptionResult> TranscribeAccurateAsync(SpeechSegment segment)
    {
        if (_accurateProcessor == null)
        {
            // フォールバック: 低遅延モデルを使用
            if (_fastProcessor != null)
            {
                var fallbackResult = await TranscribeFastAsync(segment);
                fallbackResult.IsFinal = true;
                return fallbackResult;
            }
            throw new InvalidOperationException("No ASR model loaded");
        }

        await _accurateLock.WaitAsync();
        try
        {
            var sw = Stopwatch.StartNew();
            var result = await ProcessAudioAsync(_accurateProcessor, segment.AudioData);
            sw.Stop();

            return new TranscriptionResult
            {
                SegmentId = segment.Id,
                Text = ApplyCorrections(result.Text),
                IsFinal = true,
                Confidence = result.Confidence,
                DetectedLanguage = _settings.Language,
                ProcessingTimeMs = sw.ElapsedMilliseconds
            };
        }
        finally
        {
            _accurateLock.Release();
        }
    }

    private async Task<(string Text, float Confidence)> ProcessAudioAsync(WhisperProcessor processor, float[] audioData)
    {
        var segments = new List<string>();
        float totalConfidence = 0;
        int segmentCount = 0;

        await foreach (var segment in processor.ProcessAsync(audioData))
        {
            segments.Add(segment.Text.Trim());
            totalConfidence += segment.Probability;
            segmentCount++;
        }

        string text = string.Join(" ", segments);
        float avgConfidence = segmentCount > 0 ? totalConfidence / segmentCount : 0;

        return (text, avgConfidence);
    }

    private void ConfigureGpuRuntime()
    {
        if (!_settings.GPU.Enabled)
        {
            return;
        }

        switch (_settings.GPU.Type)
        {
            case GPUType.AMD_Vulkan:
                // Vulkan実行時はデバイス番号を環境変数で指定（Whisper.net.Runtime.Vulkan/ggml-vulkan）
                Environment.SetEnvironmentVariable("GGML_VK_DEVICE", _settings.GPU.DeviceId.ToString());
                break;
            case GPUType.NVIDIA_CUDA:
                // CUDA実行時はCUDAデバイスを指定（Whisper.net.Runtime.Cublas）
                Environment.SetEnvironmentVariable("CUDA_VISIBLE_DEVICES", _settings.GPU.DeviceId.ToString());
                break;
            case GPUType.CPU:
            case GPUType.Auto:
            default:
                break;
        }
    }

    private void ValidateModelCompatibility()
    {
        if (!_settings.GPU.Enabled || _settings.GPU.Type != GPUType.AMD_Vulkan)
        {
            return;
        }

        EnsureGgmlModel(_settings.FastModelPath);
        EnsureGgmlModel(_settings.AccurateModelPath);
    }

    private static void EnsureGgmlModel(string modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
        {
            return;
        }

        var fileName = Path.GetFileName(modelPath);
        if (fileName.IndexOf("ggml", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException(
                $"AMD Vulkanランタイムはggml形式モデルのみ対応しています。ggml-*.bin を指定してください: {modelPath}");
        }
    }

    /// <summary>
    /// ホットワードリストを設定
    /// </summary>
    public void SetHotwords(IEnumerable<string> hotwords)
    {
        _hotwords.Clear();
        _hotwords.AddRange(hotwords);
    }

    /// <summary>
    /// 初期プロンプトを設定
    /// </summary>
    public void SetInitialPrompt(string prompt)
    {
        _initialPrompt = prompt;
    }

    /// <summary>
    /// 誤変換補正を適用
    /// </summary>
    private string ApplyCorrections(string text)
    {
        // ホットワードに基づく補正（簡易実装）
        // 実際の実装では、より高度なマッチングアルゴリズムを使用
        foreach (var hotword in _hotwords)
        {
            // 大文字小文字を無視した置換
            text = System.Text.RegularExpressions.Regex.Replace(
                text,
                System.Text.RegularExpressions.Regex.Escape(hotword),
                hotword,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
        }

        return text;
    }

    public void Dispose()
    {
        _fastProcessor?.Dispose();
        _accurateProcessor?.Dispose();
        _fastFactory?.Dispose();
        _accurateFactory?.Dispose();
        _fastLock.Dispose();
        _accurateLock.Dispose();
    }
}
