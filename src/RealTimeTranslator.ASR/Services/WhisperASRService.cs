using System.Diagnostics;
using System.Management;
using System.Net.Http;
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
    private readonly List<string> _hotwords = new();
    private Dictionary<string, string> _correctionDictionary = new();
    private string _initialPrompt = string.Empty;
    private bool _isModelLoaded = false;
    private readonly SemaphoreSlim _fastLock = new(1, 1);
    private readonly SemaphoreSlim _accurateLock = new(1, 1);

    public bool IsModelLoaded => _isModelLoaded;

    public event EventHandler<ModelDownloadProgressEventArgs>? ModelDownloadProgress;
    public event EventHandler<ModelStatusChangedEventArgs>? ModelStatusChanged;

    public WhisperASRService(ASRSettings? settings = null)
    {
        _settings = settings ?? new ASRSettings();
    }

    /// <summary>
    /// モデルを初期化
    /// </summary>
    public async Task InitializeAsync()
    {
        OnModelStatusChanged(new ModelStatusChangedEventArgs(
            ServiceName,
            "ASR",
            ModelStatusType.Info,
            "ASRモデルの初期化を開始しました。"));

        var fastModelPath = await EnsureModelAsync(
            _settings.FastModelPath,
            DefaultFastModelFileName,
            DefaultFastModelDownloadUrl,
            FastModelLabel);
        var accurateModelPath = await EnsureModelAsync(
            _settings.AccurateModelPath,
            DefaultAccurateModelFileName,
            DefaultAccurateModelDownloadUrl,
            AccurateModelLabel);

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
            else
            {
                OnModelStatusChanged(new ModelStatusChangedEventArgs(
                    ServiceName,
                    FastModelLabel,
                    ModelStatusType.LoadFailed,
                    "高速ASRモデルが見つからないため読み込みをスキップしました。"));
            }

            // 高精度モデル（large系）の初期化
            if (!string.IsNullOrWhiteSpace(accurateModelPath) && File.Exists(accurateModelPath))
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
            else
            {
                OnModelStatusChanged(new ModelStatusChangedEventArgs(
                    ServiceName,
                    AccurateModelLabel,
                    ModelStatusType.LoadFailed,
                    "高精度ASRモデルが見つからないため読み込みをスキップしました。"));
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
        var resolvedPath = ResolveModelPath(modelPath, defaultFileName);
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                modelLabel,
                ModelStatusType.LoadFailed,
                "モデルパスが未設定のためダウンロードをスキップしました。"));
            return null;
        }

        if (File.Exists(resolvedPath))
        {
            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                modelLabel,
                ModelStatusType.Info,
                "モデルファイルを検出しました。"));
            return resolvedPath;
        }

        var targetDirectory = Path.GetDirectoryName(resolvedPath);
        if (!string.IsNullOrWhiteSpace(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        try
        {
            using var httpClient = new HttpClient();
            using var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using var httpStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(resolvedPath, FileMode.Create, FileAccess.Write, FileShare.None);
            var totalBytes = response.Content.Headers.ContentLength;
            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;

            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                modelLabel,
                ModelStatusType.Downloading,
                "モデルのダウンロードを開始しました。"));

            while ((bytesRead = await httpStream.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                totalRead += bytesRead;
                var progress = totalBytes.HasValue && totalBytes.Value > 0
                    ? totalRead * 100d / totalBytes.Value
                    : null;
                OnModelDownloadProgress(new ModelDownloadProgressEventArgs(
                    ServiceName,
                    modelLabel,
                    totalRead,
                    totalBytes,
                    progress));
            }
            Console.WriteLine($"Downloaded ASR model to: {resolvedPath}");
            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                modelLabel,
                ModelStatusType.DownloadCompleted,
                "モデルのダウンロードが完了しました。"));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to download ASR model: {ex.Message}");
            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                modelLabel,
                ModelStatusType.DownloadFailed,
                "モデルのダウンロードに失敗しました。",
                ex));
            return null;
        }

        return File.Exists(resolvedPath) ? resolvedPath : null;
    }

    private void OnModelDownloadProgress(ModelDownloadProgressEventArgs args)
    {
        ModelDownloadProgress?.Invoke(this, args);
    }

    private void OnModelStatusChanged(ModelStatusChangedEventArgs args)
    {
        ModelStatusChanged?.Invoke(this, args);
    }

    private static string? ResolveModelPath(string modelPath, string defaultFileName)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            return null;
        }

        var rootPath = Path.IsPathRooted(modelPath)
            ? modelPath
            : Path.Combine(AppContext.BaseDirectory, modelPath);

        if (Directory.Exists(rootPath) || !Path.HasExtension(rootPath))
        {
            return Path.Combine(rootPath, defaultFileName);
        }

        return rootPath;
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
    /// ASR誤変換補正辞書を設定
    /// </summary>
    public void SetCorrectionDictionary(Dictionary<string, string> dictionary)
    {
        _correctionDictionary = new Dictionary<string, string>(dictionary);
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

    private static bool TryInvokeBuilder(object builder, string methodName)
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

    /// <summary>
    /// 誤変換補正を適用
    /// </summary>
    private string ApplyCorrections(string text)
    {
        foreach (var entry in _correctionDictionary)
        {
            text = System.Text.RegularExpressions.Regex.Replace(
                text,
                System.Text.RegularExpressions.Regex.Escape(entry.Key),
                entry.Value,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
        }

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
