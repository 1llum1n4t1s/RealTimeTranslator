using System.Diagnostics;
using System.Management;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;
using Whisper.net;
using Whisper.net.Ggml;

namespace RealTimeTranslator.ASR.Services;

/// <summary>
/// Whisper.netを使用したASRサービス
/// 二段構え構成：低遅延ASR（仮字幕）と高精度ASR（確定字幕）
/// </summary>
public class WhisperASRService : IASRService
{
    private const string ServiceName = "ASR";
    private const string FastModelLabel = "高速ASRモデル";
    private const string AccurateModelLabel = "高精度ASRモデル";
    private const string DefaultFastModelFileName = "ggml-small.bin";
    private const string DefaultAccurateModelFileName = "ggml-large-v3.bin";
    private const string DefaultFastModelDownloadUrl =
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin";
    private const string DefaultAccurateModelDownloadUrl =
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3.bin";
    private WhisperProcessor? _fastProcessor;
    private WhisperProcessor? _accurateProcessor;
    private WhisperFactory? _fastFactory;
    private WhisperFactory? _accurateFactory;
    private readonly ASRSettings _settings;
    private readonly ModelDownloadService _downloadService;
    private readonly List<string> _hotwords = new();
    private Dictionary<string, string> _correctionDictionary = new();
    private readonly Dictionary<string, System.Text.RegularExpressions.Regex> _compiledCorrectionRegexes = new();
    private readonly Dictionary<string, System.Text.RegularExpressions.Regex> _compiledHotwordRegexes = new();
    private string _initialPrompt = string.Empty;
    private bool _isModelLoaded = false;
    private readonly SemaphoreSlim _fastLock = new(1, 1);
    private readonly SemaphoreSlim _accurateLock = new(1, 1);

    public bool IsModelLoaded => _isModelLoaded;

    public event EventHandler<ModelDownloadProgressEventArgs>? ModelDownloadProgress;
    public event EventHandler<ModelStatusChangedEventArgs>? ModelStatusChanged;

    public WhisperASRService(ASRSettings settings, ModelDownloadService downloadService)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _downloadService = downloadService ?? throw new ArgumentNullException(nameof(downloadService));

        // イベントを転送
        _downloadService.DownloadProgress += (sender, e) => ModelDownloadProgress?.Invoke(this, e);
        _downloadService.StatusChanged += (sender, e) => ModelStatusChanged?.Invoke(this, e);
    }

    /// <summary>
    /// モデルを初期化
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                "ASR",
                ModelStatusType.Info,
                "ASRモデルの初期化を開始しました。"));

            Debug.WriteLine("InitializeAsync: モデル確認開始");
            var fastModelPath = await EnsureModelAsync(
                _settings.FastModelPath,
                DefaultFastModelFileName,
                DefaultFastModelDownloadUrl,
                FastModelLabel);
            Debug.WriteLine($"InitializeAsync: 高速モデルパス={fastModelPath}");

            var accurateModelPath = await EnsureModelAsync(
                _settings.AccurateModelPath,
                DefaultAccurateModelFileName,
                DefaultAccurateModelDownloadUrl,
                AccurateModelLabel);
            Debug.WriteLine($"InitializeAsync: 高精度モデルパス={accurateModelPath}");

            await Task.Run(() =>
            {
            // GPUランタイムの初期化条件:
            // - AMD Vulkan: Whisper.net.Runtime.Vulkan が必要。Vulkan対応ドライバと ggml 系モデルが必須。
            // - NVIDIA CUDA: Whisper.net.Runtime.Cublas が必要。CUDA対応ドライバが必須。
            // - Auto/CPU: 明示的なGPU設定は行わず、Whisper.netに委譲。
            ConfigureGpuRuntime();
            ValidateModelCompatibility(fastModelPath, accurateModelPath);

            // 低遅延モデル（small/medium）の初期化
            if (!string.IsNullOrWhiteSpace(fastModelPath) && File.Exists(fastModelPath))
            {
                try
                {
                    _fastFactory = WhisperFactory.FromPath(fastModelPath);
                    var fastBuilder = _fastFactory.CreateBuilder()
                        .WithLanguage(_settings.Language)
                        .WithThreads(4);
                    ConfigurePromptAndHotwords(fastBuilder);
                    _fastProcessor = fastBuilder.Build();
                    OnModelStatusChanged(new ModelStatusChangedEventArgs(
                        ServiceName,
                        FastModelLabel,
                        ModelStatusType.LoadSucceeded,
                        "高速ASRモデルの読み込みが完了しました。"));
                }
                catch (Exception ex)
                {
                    OnModelStatusChanged(new ModelStatusChangedEventArgs(
                        ServiceName,
                        FastModelLabel,
                        ModelStatusType.LoadFailed,
                        $"高速ASRモデルの読み込みに失敗しました: {ex.Message}",
                        ex));
                    Debug.WriteLine($"Failed to load fast ASR model: {ex}");
                }
            }
            else
            {
                OnModelStatusChanged(new ModelStatusChangedEventArgs(
                    ServiceName,
                    FastModelLabel,
                    ModelStatusType.LoadFailed,
                    "高速ASRモデルが見つかりません: " + fastModelPath));
            }

            // 高精度モデル（large系）の初期化
            if (!string.IsNullOrWhiteSpace(accurateModelPath) && File.Exists(accurateModelPath))
            {
                try
                {
                    _accurateFactory = WhisperFactory.FromPath(accurateModelPath);
                    var builder = _accurateFactory.CreateBuilder()
                        .WithLanguage(_settings.Language)
                        .WithThreads(4);

                    ApplyBeamSearchSettings(builder);

                    ConfigurePromptAndHotwords(builder);
                    _accurateProcessor = builder.Build();
                    OnModelStatusChanged(new ModelStatusChangedEventArgs(
                        ServiceName,
                        AccurateModelLabel,
                        ModelStatusType.LoadSucceeded,
                        "高精度ASRモデルの読み込みが完了しました。"));
                }
                catch (Exception ex)
                {
                    OnModelStatusChanged(new ModelStatusChangedEventArgs(
                        ServiceName,
                        AccurateModelLabel,
                        ModelStatusType.LoadFailed,
                        $"高精度ASRモデルの読み込みに失敗しました: {ex.Message}",
                        ex));
                    Debug.WriteLine($"Failed to load accurate ASR model: {ex}");
                }
            }
            else
            {
                OnModelStatusChanged(new ModelStatusChangedEventArgs(
                    ServiceName,
                    AccurateModelLabel,
                    ModelStatusType.LoadFailed,
                    "高精度ASRモデルが見つかりません: " + accurateModelPath));
            }

            _isModelLoaded = _fastProcessor != null || _accurateProcessor != null;
        });

            if (!_isModelLoaded)
            {
                OnModelStatusChanged(new ModelStatusChangedEventArgs(
                    ServiceName,
                    "ASR",
                    ModelStatusType.Fallback,
                    "ASRモデルが未ロードのため音声認識は実行できません。"));
            }
        }
        catch (Exception ex)
        {
            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                "ASR",
                ModelStatusType.LoadFailed,
                $"ASR初期化エラー: {ex.Message}",
                ex));
            Debug.WriteLine($"InitializeAsync error: {ex}");
            _isModelLoaded = false;
        }
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
            Environment.SetEnvironmentVariable("GGML_VK_DEVICE", null);
            Environment.SetEnvironmentVariable("CUDA_VISIBLE_DEVICES", null);
            return;
        }

        var effectiveGpuType = _settings.GPU.Type;
        if (effectiveGpuType == GPUType.Auto)
        {
            effectiveGpuType = DetectGpuType();
            _settings.GPU.Type = effectiveGpuType;
            LogGpuDetection(effectiveGpuType);
        }

        switch (effectiveGpuType)
        {
            case GPUType.AMD_Vulkan:
                // Vulkan実行時はデバイス番号を環境変数で指定（Whisper.net.Runtime.Vulkan/ggml-vulkan）
                Environment.SetEnvironmentVariable("GGML_VK_DEVICE", _settings.GPU.DeviceId.ToString());
                Environment.SetEnvironmentVariable("CUDA_VISIBLE_DEVICES", null);
                break;
            case GPUType.NVIDIA_CUDA:
                // CUDA実行時はCUDAデバイスを指定（Whisper.net.Runtime.Cublas）
                Environment.SetEnvironmentVariable("CUDA_VISIBLE_DEVICES", _settings.GPU.DeviceId.ToString());
                Environment.SetEnvironmentVariable("GGML_VK_DEVICE", null);
                break;
            case GPUType.CPU:
            case GPUType.Auto:
            default:
                Environment.SetEnvironmentVariable("GGML_VK_DEVICE", null);
                Environment.SetEnvironmentVariable("CUDA_VISIBLE_DEVICES", null);
                break;
        }
    }

    private static void LogGpuDetection(GPUType gpuType)
    {
        var message = $"検出したGPU種別: {gpuType}";
        Trace.WriteLine(message);
        Debug.WriteLine(message);
    }

    private static GPUType DetectGpuType()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                return GPUType.CPU;
            }

            var hasNvidia = false;
            var hasAmd = false;

            using var searcher = new ManagementObjectSearcher("select Name from Win32_VideoController");
            foreach (var result in searcher.Get())
            {
                if (result is not ManagementObject obj)
                {
                    continue;
                }

                var name = obj["Name"]?.ToString();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                {
                    hasNvidia = true;
                }

                if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
                {
                    hasAmd = true;
                }
            }

            if (hasNvidia)
            {
                return GPUType.NVIDIA_CUDA;
            }

            if (hasAmd)
            {
                return GPUType.AMD_Vulkan;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"GPU検出に失敗しました: {ex.Message}");
            Debug.WriteLine($"GPU検出に失敗しました: {ex.Message}");
        }

        return GPUType.CPU;
    }

    private void ValidateModelCompatibility(string? fastModelPath, string? accurateModelPath)
    {
        if (!_settings.GPU.Enabled || _settings.GPU.Type != GPUType.AMD_Vulkan)
        {
            return;
        }

        EnsureGgmlModel(fastModelPath);
        EnsureGgmlModel(accurateModelPath);
    }

    private static void EnsureGgmlModel(string? modelPath)
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

    private async Task<string?> EnsureModelAsync(
        string modelPath,
        string defaultFileName,
        string downloadUrl,
        string modelLabel)
    {
        return await _downloadService.EnsureModelAsync(
            modelPath,
            defaultFileName,
            downloadUrl,
            ServiceName,
            modelLabel,
            CancellationToken.None);
    }

    private void OnModelStatusChanged(ModelStatusChangedEventArgs args)
    {
        ModelStatusChanged?.Invoke(this, args);
    }

    /// <summary>
    /// ホットワードリストを設定
    /// </summary>
    public void SetHotwords(IEnumerable<string> hotwords)
    {
        _hotwords.Clear();
        _hotwords.AddRange(hotwords);

        // 正規表現を事前コンパイル
        _compiledHotwordRegexes.Clear();
        foreach (var hotword in _hotwords)
        {
            if (!string.IsNullOrWhiteSpace(hotword))
            {
                var pattern = System.Text.RegularExpressions.Regex.Escape(hotword);
                _compiledHotwordRegexes[hotword] = new System.Text.RegularExpressions.Regex(
                    pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
            }
        }
    }

    /// <summary>
    /// 初期プロンプトを設定
    /// </summary>
    public void SetInitialPrompt(string prompt)
    {
        _initialPrompt = prompt;
    }

    /// <summary>
    /// ASR誤変換補正辞書を設定
    /// </summary>
    public void SetCorrectionDictionary(Dictionary<string, string> dictionary)
    {
        _correctionDictionary = new Dictionary<string, string>(dictionary);

        // 正規表現を事前コンパイル
        _compiledCorrectionRegexes.Clear();
        foreach (var entry in _correctionDictionary)
        {
            if (!string.IsNullOrWhiteSpace(entry.Key))
            {
                var pattern = System.Text.RegularExpressions.Regex.Escape(entry.Key);
                _compiledCorrectionRegexes[entry.Key] = new System.Text.RegularExpressions.Regex(
                    pattern,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
            }
        }
    }

    private void ConfigurePromptAndHotwords(object builder)
    {
        var hasHotwords = _hotwords.Count > 0;
        var hasPrompt = !string.IsNullOrWhiteSpace(_initialPrompt);

        var hotwordsApplied = false;
        if (hasHotwords)
        {
            hotwordsApplied = TryInvokeBuilder(builder, "WithHotwords", _hotwords)
                || TryInvokeBuilder(builder, "WithHotwords", string.Join(", ", _hotwords));
        }

        var promptText = hasPrompt ? _initialPrompt : string.Empty;
        if (hasHotwords && !hotwordsApplied)
        {
            promptText = BuildPromptText();
        }

        if (!string.IsNullOrWhiteSpace(promptText))
        {
            TryInvokeBuilder(builder, "WithInitialPrompt", promptText);
            TryInvokeBuilder(builder, "WithPrompt", promptText);
        }
    }

    private string BuildPromptText()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(_initialPrompt))
        {
            parts.Add(_initialPrompt.Trim());
        }

        if (_hotwords.Count > 0)
        {
            parts.Add(string.Join(", ", _hotwords));
        }

        return string.Join(" ", parts).Trim();
    }

    private void ApplyBeamSearchSettings(object builder)
    {
        if (!_settings.UseBeamSearch)
        {
            return;
        }

        var beamSize = Math.Max(1, _settings.BeamSize);
        var appliedWithSize = TryInvokeBuilder(builder, "WithBeamSearchSamplingStrategy", beamSize);
        if (appliedWithSize)
        {
            return;
        }

        var appliedWithoutSize = TryInvokeBuilder(builder, "WithBeamSearchSamplingStrategy");
        if (appliedWithoutSize)
        {
            LogBeamSizeIgnored(beamSize);
            return;
        }

        LogBeamSearchUnsupported(beamSize);
    }

    private static void LogBeamSizeIgnored(int beamSize)
    {
        var message = $"Beam Size指定({beamSize})は未対応のため無視されます。";
        Trace.WriteLine(message);
        Debug.WriteLine(message);
    }

    private static void LogBeamSearchUnsupported(int beamSize)
    {
        var message = $"Beam Search設定が未対応のため、Beam Size({beamSize})は適用されません。";
        Trace.WriteLine(message);
        Debug.WriteLine(message);
    }

    private static bool TryInvokeBuilder(object builder, string methodName, object argument)
    {
        try
        {
            var method = builder.GetType()
                .GetMethods()
                .FirstOrDefault(info =>
                    string.Equals(info.Name, methodName, StringComparison.Ordinal)
                    && info.GetParameters().Length == 1
                    && info.GetParameters()[0].ParameterType.IsAssignableFrom(argument.GetType()));

            if (method == null)
            {
                return false;
            }

            method.Invoke(builder, new[] { argument });
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to invoke {methodName}: {ex.Message}");
            return false;
        }
    }

    private static bool TryInvokeBuilder(object builder, string methodName)
    {
        try
        {
            var method = builder.GetType()
                .GetMethods()
                .FirstOrDefault(info =>
                    string.Equals(info.Name, methodName, StringComparison.Ordinal)
                    && info.GetParameters().Length == 0);

            if (method == null)
            {
                return false;
            }

            method.Invoke(builder, Array.Empty<object>());
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to invoke {methodName}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 誤変換補正を適用
    /// </summary>
    private string ApplyCorrections(string text)
    {
        // 事前コンパイルした正規表現を使用
        foreach (var entry in _correctionDictionary)
        {
            if (_compiledCorrectionRegexes.TryGetValue(entry.Key, out var regex))
            {
                text = regex.Replace(text, entry.Value);
            }
        }

        // ホットワードに基づく補正（簡易実装）
        // 実際の実装では、より高度なマッチングアルゴリズムを使用
        foreach (var hotword in _hotwords)
        {
            if (_compiledHotwordRegexes.TryGetValue(hotword, out var regex))
            {
                // 大文字小文字を無視した置換
                text = regex.Replace(text, hotword);
            }
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
