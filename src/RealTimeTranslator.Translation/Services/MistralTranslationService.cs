using System.Diagnostics;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Sampling;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;
using RealTimeTranslator.Translation.Interfaces;

namespace RealTimeTranslator.Translation.Services;

/// <summary>
/// LlamaSharpを使用した翻訳サービス
/// Phi-3, Gemma, Qwen, Mistralなど複数のモデルをサポート
/// </summary>
public class MistralTranslationService : ITranslationService
{
    private const string ServiceName = "翻訳";
    private const int MaxCacheSize = 1000;
    private const string WarmupPrompt = "Test";

    // デフォルトモデル: Phi-3 Mini（高速・高品質）
    private const string DefaultModelFileName = "Phi-3-mini-4k-instruct-q4.gguf";
    private const string DefaultModelDownloadUrl = "https://huggingface.co/microsoft/Phi-3-mini-4k-instruct-gguf/resolve/main/Phi-3-mini-4k-instruct-q4.gguf";

    private readonly TranslationSettings _settings;
    private readonly ModelDownloadService _downloadService;
    private readonly PromptBuilderFactory _promptBuilderFactory;
    private readonly Dictionary<string, string> _cache = new();
    private readonly LinkedList<string> _cacheOrder = new();
    private readonly Dictionary<string, LinkedListNode<string>> _cacheNodeMap = new();
    private readonly object _cacheLock = new();

    private bool _isModelLoaded = false;
    private bool _isWarmupComplete = false;
    private LLamaWeights? _model;
    private ModelParams? _modelParams;
    private StatelessExecutor? _executor;
    private TranslationModelType _detectedModelType = TranslationModelType.Auto;
    private string _modelLabel = "翻訳モデル";
    private IPromptBuilder? _promptBuilder;

    private Dictionary<string, string> _preTranslationDict = new();
    private Dictionary<string, string> _postTranslationDict = new();
    private readonly SemaphoreSlim _translateLock = new(1, 1);
    private readonly SemaphoreSlim _warmupLock = new(1, 1);
    private readonly object _executorLock = new();

    public bool IsModelLoaded => _isModelLoaded;

    public event EventHandler<ModelDownloadProgressEventArgs>? ModelDownloadProgress;
    public event EventHandler<ModelStatusChangedEventArgs>? ModelStatusChanged;

    /// <summary>
    /// MistralTranslationServiceのコンストラクタ
    /// </summary>
    /// <param name="settings">翻訳設定</param>
    /// <param name="downloadService">モデルダウンロードサービス</param>
    /// <param name="promptBuilderFactory">プロンプトビルダーファクトリ</param>
    public MistralTranslationService(TranslationSettings settings, ModelDownloadService downloadService, PromptBuilderFactory promptBuilderFactory)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _downloadService = downloadService ?? throw new ArgumentNullException(nameof(downloadService));
        _promptBuilderFactory = promptBuilderFactory ?? throw new ArgumentNullException(nameof(promptBuilderFactory));

        _downloadService.DownloadProgress += (sender, e) => ModelDownloadProgress?.Invoke(this, e);
        _downloadService.StatusChanged += (sender, e) => ModelStatusChanged?.Invoke(this, e);
    }

    /// <summary>
    /// モデルステータス変更時のイベント発火
    /// </summary>
    protected virtual void OnModelStatusChanged(ModelStatusChangedEventArgs e)
    {
        ModelStatusChanged?.Invoke(this, e);
    }

    /// <summary>
    /// 翻訳エンジンを初期化
    /// </summary>
    public async Task InitializeAsync()
    {
        OnModelStatusChanged(new ModelStatusChangedEventArgs(
            ServiceName,
            _modelLabel,
            ModelStatusType.Info,
            "翻訳モデルの初期化を開始しました。"));

        try
        {
            // モデルファイルをダウンロード/確認
            var modelFilePath = await _downloadService.EnsureModelAsync(
                _settings.ModelPath,
                DefaultModelFileName,
                DefaultModelDownloadUrl,
                ServiceName,
                _modelLabel).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(modelFilePath))
            {
                throw new FileNotFoundException($"Failed to ensure model file for: {_settings.ModelPath}");
            }

            // モデルタイプを検出
            _detectedModelType = DetectModelType(modelFilePath);
            _modelLabel = GetModelLabel(_detectedModelType);
            _promptBuilder = _promptBuilderFactory.GetBuilder(_detectedModelType);
            LoggerService.LogInfo($"翻訳モデルタイプ検出: {_detectedModelType} ({_modelLabel})");

            // モデルをロード
            LoadModelFromPath(modelFilePath);

            // ウォームアップ推論を非同期実行（初回遅延を短縮）
            _ = Task.Run(WarmupInferenceAsync);
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"Translation initialization error: {ex}");
            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                _modelLabel,
                ModelStatusType.LoadFailed,
                "翻訳モデルの初期化に失敗しました。",
                ex));
            _isModelLoaded = false;
        }
    }

    /// <summary>
    /// モデルファイル名からモデルタイプを検出
    /// </summary>
    private TranslationModelType DetectModelType(string modelFilePath)
    {
        // 設定で明示的に指定されている場合はそれを使用
        if (_settings.ModelType != TranslationModelType.Auto)
        {
            return _settings.ModelType;
        }

        var fileName = Path.GetFileName(modelFilePath).ToLowerInvariant();

        if (fileName.Contains("phi-3") || fileName.Contains("phi3"))
        {
            return TranslationModelType.Phi3;
        }
        if (fileName.Contains("gemma"))
        {
            return TranslationModelType.Gemma;
        }
        if (fileName.Contains("qwen"))
        {
            return TranslationModelType.Qwen;
        }
        if (fileName.Contains("mistral"))
        {
            return TranslationModelType.Mistral;
        }

        // デフォルトはPhi-3形式（最も汎用的）
        return TranslationModelType.Phi3;
    }

    /// <summary>
    /// モデルタイプに応じたラベルを取得
    /// </summary>
    private static string GetModelLabel(TranslationModelType modelType)
    {
        return modelType switch
        {
            TranslationModelType.Phi3 => "Phi-3翻訳モデル",
            TranslationModelType.Gemma => "Gemma翻訳モデル",
            TranslationModelType.Qwen => "Qwen翻訳モデル",
            TranslationModelType.Mistral => "Mistral翻訳モデル",
            _ => "翻訳モデル"
        };
    }

    /// <summary>
    /// テキストを翻訳
    /// </summary>
    public async Task<TranslationResult> TranslateAsync(string text, string sourceLanguage = "en", string targetLanguage = "ja")
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new TranslationResult
            {
                OriginalText = text,
                TranslatedText = text,
                SourceLanguage = sourceLanguage,
                TargetLanguage = targetLanguage,
                FromCache = false,
                ProcessingTimeMs = 0
            };
        }

        var sw = Stopwatch.StartNew();

        var cacheKey = $"{sourceLanguage}:{targetLanguage}:{text}";

        if (TryGetFromCache(cacheKey, out var cachedTranslation) && cachedTranslation != null)
        {
            sw.Stop();
            LoggerService.LogDebug($"[MistralTranslation] キャッシュヒット: {text} -> {cachedTranslation}");
            return new TranslationResult
            {
                OriginalText = text,
                TranslatedText = cachedTranslation,
                SourceLanguage = sourceLanguage,
                TargetLanguage = targetLanguage,
                FromCache = true,
                ProcessingTimeMs = sw.ElapsedMilliseconds
            };
        }

        // ウォームアップが完了するまで待機
        if (!_isWarmupComplete)
        {
            await _warmupLock.WaitAsync().ConfigureAwait(false);
            _warmupLock.Release();
        }

        await _translateLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var preprocessedText = ApplyPreTranslation(text);
            LoggerService.LogDebug($"[MistralTranslation] 翻訳開始: Text={preprocessedText}, Source={sourceLanguage}, Target={targetLanguage}");

            string translatedText;
            if (sourceLanguage.Equals(targetLanguage, StringComparison.OrdinalIgnoreCase))
            {
                translatedText = preprocessedText;
            }
            else
            {
                translatedText = await TranslateWithMistralAsync(preprocessedText, sourceLanguage, targetLanguage).ConfigureAwait(false);
            }

            translatedText = ApplyPostTranslation(translatedText);
            AddToCache(cacheKey, translatedText);

            sw.Stop();
            LoggerService.LogDebug($"[MistralTranslation] 翻訳完了: Result={translatedText}, Time={sw.ElapsedMilliseconds}ms");

            return new TranslationResult
            {
                OriginalText = text,
                TranslatedText = translatedText,
                SourceLanguage = sourceLanguage,
                TargetLanguage = targetLanguage,
                FromCache = false,
                ProcessingTimeMs = sw.ElapsedMilliseconds
            };
        }
        finally
        {
            _translateLock.Release();
        }
    }

    /// <summary>
    /// ウォームアップ推論（GPUメモリ確保と初回遅延短縮）
    /// </summary>
    private async Task WarmupInferenceAsync()
    {
        try
        {
            if (!_isModelLoaded || _model == null)
            {
                return;
            }

            await _warmupLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_isWarmupComplete)
                {
                    return;
                }

                LoggerService.LogDebug("[MistralTranslation] ウォームアップ推論開始");
                var warmupParams = new InferenceParams
                {
                    MaxTokens = 5,
                    AntiPrompts = new List<string> { "</s>", "[INST]" }
                };

                if (_modelParams == null)
                {
                    return;
                }

                // スレッドセーフに_executorを初期化
                if (_executor == null)
                {
                    lock (_executorLock)
                    {
                        if (_executor == null)
                        {
                            _executor = new StatelessExecutor(_model, _modelParams);
                        }
                    }
                }
                var executor = _executor;

                var tokenCount = 0;
                await foreach (var _ in executor.InferAsync(WarmupPrompt, warmupParams))
                {
                    tokenCount++;
                    if (tokenCount >= 3)
                    {
                        break;
                    }
                }

                _isWarmupComplete = true;
                LoggerService.LogDebug("[MistralTranslation] ウォームアップ推論完了");
            }
            finally
            {
                _warmupLock.Release();
            }
        }
        catch (Exception ex)
        {
            LoggerService.LogWarning($"[MistralTranslation] ウォームアップ推論エラー: {ex.Message}");
            _isWarmupComplete = true;
        }
    }

    /// <summary>
    /// LLMを使用して翻訳
    /// </summary>
    private async Task<string> TranslateWithMistralAsync(string text, string sourceLanguage, string targetLanguage)
    {
        if (!_isModelLoaded || _model == null || _promptBuilder == null)
        {
            LoggerService.LogError("[Translation] モデルが読み込まれていません");
            return text;
        }

        try
        {
            // 言語名を取得
            var sourceLangName = GetLanguageName(sourceLanguage);
            var targetLangName = GetLanguageName(targetLanguage);

            // Strategy パターン: プロンプトビルダーがプロンプトを生成
            var prompt = _promptBuilder.BuildPrompt(text, sourceLangName, targetLangName);
            var antiPrompts = _promptBuilder.GetAntiPrompts();

            LoggerService.LogDebug($"[Translation] プロンプト: {prompt}");

            // 推論パラメータを設定（翻訳タスクに最適化）
            // 日本語翻訳では英語よりトークン数が2-3倍必要（文字ごとにトークン化）
            var maxTokens = Math.Clamp(text.Length + 32, 48, 200);  // リアルタイム翻訳用に最適化
            LoggerService.LogDebug($"[Translation] 推論パラメータ: MaxTokens={maxTokens}, Temp=0.1, TopP=0.9, TopK=40");

            var inferenceParams = new InferenceParams
            {
                MaxTokens = maxTokens,
                AntiPrompts = antiPrompts,
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = 0.1f,   // 低温度で確定的な翻訳
                    TopP = 0.9f,          // TopP（品質と多様性のバランス）
                    TopK = 40,            // TopK
                    RepeatPenalty = 1.1f  // 繰り返しペナルティ（軽め）
                }
            };

            // 推論を実行
            LoggerService.LogDebug($"[Translation] InferAsync 開始");
            var inferenceStartTime = Stopwatch.GetTimestamp();

            var sb = new StringBuilder();
            if (_modelParams == null)
            {
                throw new InvalidOperationException("Model parameters are not initialized");
            }

            // スレッドセーフに_executorを初期化
            if (_executor == null)
            {
                lock (_executorLock)
                {
                    if (_executor == null)
                    {
                        _executor = new StatelessExecutor(_model, _modelParams);
                    }
                }
            }
            var executor = _executor;

            var tokenCount = 0;
            var lastLogTime = inferenceStartTime;
            var lastTokenTime = inferenceStartTime;
            var tokenTimes = new List<double>();

            await foreach (var outputToken in executor.InferAsync(prompt, inferenceParams))
            {
                sb.Append(outputToken);
                tokenCount++;

                var currentTime = Stopwatch.GetTimestamp();
                var tokenDuration = (currentTime - lastTokenTime) * 1000.0 / Stopwatch.Frequency;
                tokenTimes.Add(tokenDuration);
                lastTokenTime = currentTime;

                // 10トークンごと、または1000ms経過ごとにログ出力（ログ削減）
                var timeSinceLastLog = (currentTime - lastLogTime) * 1000.0 / Stopwatch.Frequency;
                if (tokenCount % 10 == 0 || timeSinceLastLog >= 1000)
                {
                    var elapsedMs = (currentTime - inferenceStartTime) * 1000.0 / Stopwatch.Frequency;
                    var loggingAvgTokenTime = tokenTimes.Count > 0 ? tokenTimes.Average() : 0;
                    LoggerService.LogDebug($"[Translation] トークン生成中: {tokenCount}トークン, {elapsedMs:F0}ms経過, 平均={loggingAvgTokenTime:F0}ms/token");
                    lastLogTime = currentTime;
                }

                // 早期終了判定（日本語の句点を検出）
                var currentText = sb.ToString();
                if (ShouldStopGeneration(currentText, targetLanguage))
                {
                    break;
                }
            }

            var inferenceEndTime = Stopwatch.GetTimestamp();
            var inferenceDuration = (inferenceEndTime - inferenceStartTime) * 1000.0 / Stopwatch.Frequency;
            var tokensPerSec = tokenCount > 0 ? tokenCount * 1000.0 / inferenceDuration : 0;
            var finalAvgTokenTime = tokenTimes.Count > 0 ? tokenTimes.Average() : 0;
            var firstTokenTime = tokenTimes.Count > 0 ? tokenTimes[0] : 0;
            LoggerService.LogDebug($"[Translation] InferAsync 完了: {tokenCount}トークン生成, {inferenceDuration:F0}ms");
            LoggerService.LogDebug($"[Translation] 速度統計: {tokensPerSec:F2} tokens/sec, 平均={finalAvgTokenTime:F0}ms/token, 初回={firstTokenTime:F0}ms");

            var result = sb.ToString().Trim();

            // Strategy パターン: プロンプトビルダーが結果をクリーンアップ
            result = _promptBuilder.ParseOutput(result);

            LoggerService.LogDebug($"[Translation] 生成結果: {result}");

            if (string.IsNullOrWhiteSpace(result))
            {
                LoggerService.LogWarning("[Translation] 空の翻訳結果");
                return text;
            }

            // 英語が返されている場合は元のテキストを返す
            if (IsEnglishText(result) && targetLanguage == "ja")
            {
                LoggerService.LogWarning($"[Translation] 英語テキストが返されました: {result}");
                return text;
            }

            return result;
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"[Translation] 翻訳エラー: {ex.Message}");
            LoggerService.LogDebug($"[Translation] StackTrace: {ex.StackTrace}");
            return text;
        }
    }

    /// <summary>
    /// 生成を早期終了すべきか判定
    /// </summary>
    private static bool ShouldStopGeneration(string currentText, string targetLanguage)
    {
        if (string.IsNullOrEmpty(currentText))
        {
            return false;
        }

        // 日本語の場合、句点で終了判定
        if (targetLanguage == "ja")
        {
            // 句点（。）、感嘆符、疑問符で終わった場合
            if (currentText.EndsWith("。") || currentText.EndsWith("！") || currentText.EndsWith("？"))
            {
                return true;
            }
        }

        // 改行が2回連続した場合
        if (currentText.Contains("\n\n"))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// テキストが英語かどうかを判定
    /// </summary>
    private static bool IsEnglishText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // 日本語文字（ひらがな・カタカナ・漢字）を含むかチェック
        var japaneseCount = text.Count(c =>
            (c >= 0x3040 && c <= 0x309F) ||  // ひらがな
            (c >= 0x30A0 && c <= 0x30FF) ||  // カタカナ
            (c >= 0x4E00 && c <= 0x9FFF));   // 漢字

        // 日本語文字が1文字以上あれば日本語と判定
        if (japaneseCount > 0)
        {
            return false;
        }

        // 英字の割合をチェック
        var englishCount = text.Count(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'));
        var englishRatio = text.Length > 0 ? (double)englishCount / text.Length : 0;

        // 65%以上が英字なら英語と判定（より厳密）
        return englishRatio > 0.65;
    }

    /// <summary>
    /// 言語コードから言語名を取得
    /// </summary>
    private string GetLanguageName(string languageCode)
    {
        return languageCode.ToLower() switch
        {
            "en" => "English",
            "ja" => "Japanese",
            "zh" => "Chinese",
            "ko" => "Korean",
            "es" => "Spanish",
            "fr" => "French",
            "de" => "German",
            "it" => "Italian",
            "pt" => "Portuguese",
            "ru" => "Russian",
            _ => languageCode
        };
    }

    /// <summary>
    /// モデルをロード
    /// </summary>
    private void LoadModelFromPath(string modelPath)
    {
        try
        {
            LoggerService.LogDebug($"{_modelLabel}の読み込み開始: {modelPath}");

            if (!File.Exists(modelPath))
            {
                throw new FileNotFoundException($"Translation model not found: {modelPath}");
            }

            // GPU環境変数はApp.xaml.csで起動時に設定済み
            // 対応GPU: NVIDIA CUDA, AMD ROCm/HIP, Intel SYCL, Vulkan (汎用)
            LoggerService.LogDebug("GPU support: CUDA/HIP/SYCL/Vulkan (環境変数はApp起動時に設定済み)");

            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                _modelLabel,
                ModelStatusType.Info,
                $"{_modelLabel}を読み込み中..."));

            // モデルタイプに応じたパラメータを設定
            var (contextSize, batchSize, gpuLayerCount) = GetModelParameters();

            _modelParams = new ModelParams(modelPath)
            {
                ContextSize = contextSize,
                BatchSize = batchSize,
                GpuLayerCount = gpuLayerCount,
                Threads = 1  // CPUスレッド最小化（GPU推論中はCPUアイドル）
            };

            LoggerService.LogDebug($"{_modelLabel} parameters: ContextSize={contextSize}, BatchSize={batchSize}, GpuLayerCount={gpuLayerCount}, Threads=1 (完全GPU実行)");

            // モデルをロード
            _model = LLamaWeights.LoadFromFile(_modelParams);
            _executor = new StatelessExecutor(_model, _modelParams);

            _isModelLoaded = true;
            LoggerService.LogInfo($"{_modelLabel}の読み込みが完了しました");
            LoggerService.LogInfo($"{_modelLabel} initialized (GPU: CUDA/HIP/SYCL/Vulkan auto-detect, GpuLayerCount={gpuLayerCount})");

            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                _modelLabel,
                ModelStatusType.LoadSucceeded,
                $"{_modelLabel}の読み込みが完了しました。"));
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"{_modelLabel}読み込みに失敗: {ex}");
            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                _modelLabel,
                ModelStatusType.LoadFailed,
                "翻訳モデルの読み込みに失敗しました。",
                ex));
            _isModelLoaded = false;
        }
    }

    /// <summary>
    /// モデルタイプに応じた最適パラメータを取得
    /// </summary>
    private (uint contextSize, uint batchSize, int gpuLayerCount) GetModelParameters()
    {
        return _detectedModelType switch
        {
            // Phi-3 Mini: 32層、小コンテキストで高速
            TranslationModelType.Phi3 => (512u, 64u, 35),
            // Gemma 2B: 18層、軽量高速
            TranslationModelType.Gemma => (512u, 64u, 20),
            // Qwen: 24層程度
            TranslationModelType.Qwen => (512u, 64u, 28),
            // Mistral 7B: 32層
            TranslationModelType.Mistral => (512u, 64u, 35),
            // デフォルト
            _ => (512u, 64u, 35)
        };
    }

    /// <summary>
    /// キャッシュに追加（LRU キャッシュ戦略）
    /// </summary>
    private void AddToCache(string key, string value)
    {
        lock (_cacheLock)
        {
            if (_cacheNodeMap.TryGetValue(key, out var existingNode))
            {
                _cacheOrder.Remove(existingNode);
                _cacheNodeMap.Remove(key);
            }

            _cache[key] = value;
            var newNode = _cacheOrder.AddLast(key);
            _cacheNodeMap[key] = newNode;

            if (_cacheOrder.Count > MaxCacheSize)
            {
                var oldestNode = _cacheOrder.First!;
                var oldestKey = oldestNode.Value;
                _cacheOrder.RemoveFirst();
                _cache.Remove(oldestKey);
                _cacheNodeMap.Remove(oldestKey);
            }
        }
    }

    /// <summary>
    /// キャッシュから取得
    /// </summary>
    private bool TryGetFromCache(string key, out string? value)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var cachedValue))
            {
                if (_cacheNodeMap.TryGetValue(key, out var node))
                {
                    _cacheOrder.Remove(node);
                    var newNode = _cacheOrder.AddLast(key);
                    _cacheNodeMap[key] = newNode;
                }
                value = cachedValue;
                return true;
            }

            value = null;
            return false;
        }
    }

    /// <summary>
    /// 翻訳前の用語正規化を適用
    /// </summary>
    private string ApplyPreTranslation(string text)
    {
        foreach (var kvp in _preTranslationDict)
        {
            text = text.Replace(kvp.Key, kvp.Value, StringComparison.OrdinalIgnoreCase);
        }
        return text;
    }

    /// <summary>
    /// 翻訳後の補正を適用
    /// </summary>
    private string ApplyPostTranslation(string text)
    {
        foreach (var kvp in _postTranslationDict)
        {
            text = text.Replace(kvp.Key, kvp.Value);
        }
        return text;
    }

    public void SetPreTranslationDictionary(Dictionary<string, string> dictionary)
    {
        _preTranslationDict = new Dictionary<string, string>(dictionary);
    }

    public void SetPostTranslationDictionary(Dictionary<string, string> dictionary)
    {
        _postTranslationDict = new Dictionary<string, string>(dictionary);
    }

    public void ClearCache()
    {
        lock (_cacheLock)
        {
            _cache.Clear();
            _cacheOrder.Clear();
            _cacheNodeMap.Clear();
        }
    }

    private bool _disposed = false;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            _model?.Dispose();
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"MistralTranslationService.Dispose: Error disposing model: {ex.Message}");
        }

        try
        {
            _translateLock.Dispose();
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"MistralTranslationService.Dispose: Error disposing translate lock: {ex.Message}");
        }

        try
        {
            _warmupLock.Dispose();
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"MistralTranslationService.Dispose: Error disposing warmup lock: {ex.Message}");
        }

        GC.SuppressFinalize(this);
    }
}
