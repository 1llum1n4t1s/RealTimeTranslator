using System.Diagnostics;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Sampling;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;

namespace RealTimeTranslator.Translation.Services;

/// <summary>
/// LlamaSharpとMistral 7B Instruct v0.2を使用した翻訳サービス
/// ローカルで動作するプライベートな翻訳エンジン
/// </summary>
public class MistralTranslationService : ITranslationService
{
    private const string ServiceName = "翻訳";
    private const string ModelLabel = "Mistral翻訳モデル";
    private const string DefaultModelFileName = "mistral-7b-instruct-v0.2.Q4_K_M.gguf";
    private const string DefaultModelDownloadUrl = "https://huggingface.co/TheBloke/Mistral-7B-Instruct-v0.2-GGUF/resolve/main/mistral-7b-instruct-v0.2.Q4_K_M.gguf";
    private const int MaxCacheSize = 1000;

    private readonly TranslationSettings _settings;
    private readonly ModelDownloadService _downloadService;
    private readonly Dictionary<string, string> _cache = new();
    private readonly LinkedList<string> _cacheOrder = new();
    private readonly Dictionary<string, LinkedListNode<string>> _cacheNodeMap = new();
    private readonly object _cacheLock = new();

    private bool _isModelLoaded = false;
    private LLamaWeights? _model;
    private ModelParams? _modelParams;

    private Dictionary<string, string> _preTranslationDict = new();
    private Dictionary<string, string> _postTranslationDict = new();
    private readonly SemaphoreSlim _translateLock = new(1, 1);

    public bool IsModelLoaded => _isModelLoaded;

    public event EventHandler<ModelDownloadProgressEventArgs>? ModelDownloadProgress;
    public event EventHandler<ModelStatusChangedEventArgs>? ModelStatusChanged;

    public MistralTranslationService(TranslationSettings settings, ModelDownloadService downloadService)
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
    /// 翻訳エンジンを初期化
    /// </summary>
    public async Task InitializeAsync()
    {
        OnModelStatusChanged(new ModelStatusChangedEventArgs(
            ServiceName,
            ModelLabel,
            ModelStatusType.Info,
            "Mistral翻訳モデルの初期化を開始しました。"));

        try
        {
            // モデルファイルをダウンロード/確認
            var modelFilePath = await _downloadService.EnsureModelAsync(
                _settings.ModelPath,
                DefaultModelFileName,
                DefaultModelDownloadUrl,
                ServiceName,
                ModelLabel).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(modelFilePath))
            {
                throw new FileNotFoundException($"Failed to ensure model file for: {_settings.ModelPath}");
            }

            // モデルをロード
            LoadModelFromPath(modelFilePath);
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"Mistral translation initialization error: {ex}");
            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                ModelLabel,
                ModelStatusType.LoadFailed,
                "Mistral翻訳モデルの初期化に失敗しました。",
                ex));
            _isModelLoaded = false;
        }
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
    /// Mistral 7B Instruct v0.2を使用して翻訳
    /// </summary>
    private async Task<string> TranslateWithMistralAsync(string text, string sourceLanguage, string targetLanguage)
    {
        if (!_isModelLoaded || _model == null)
        {
            LoggerService.LogError("[MistralTranslation] モデルが読み込まれていません");
            return text;
        }

        try
        {
            // 言語名を取得
            var sourceLangName = GetLanguageName(sourceLanguage);
            var targetLangName = GetLanguageName(targetLanguage);

            // Mistral Instruct形式のプロンプトを作成（シンプルに、ローマ字なし）
            var prompt = $"<s>[INST] Translate to {targetLangName}: {text} [/INST]";

            LoggerService.LogDebug($"[MistralTranslation] プロンプト: {prompt}");

            // 推論パラメータを設定（翻訳タスクに最適化）
            // パフォーマンス最適化: より高速な推論のためパラメータを調整
            var inferenceParams = new InferenceParams
            {
                MaxTokens = 48,  // 翻訳結果には48トークンで十分（さらに削減でスピードアップ）
                AntiPrompts = new List<string> { "</s>", "\n" },  // 改行1つで終了
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = 0.01f,  // 極低温度で決定論的かつ高速に
                    TopP = 0.85f,        // TopPを削減してサンプリング効率化
                    TopK = 10,           // TopKをさらに削減して高速化
                    RepeatPenalty = 1.2f // 繰り返しペナルティを強化
                }
            };

            // 推論を実行（StatelessExecutorを使用 - コンテキストは自動管理）
            var sb = new StringBuilder();
            var executor = new StatelessExecutor(_model, _modelParams);

            await foreach (var outputToken in executor.InferAsync(prompt, inferenceParams))
            {
                sb.Append(outputToken);
            }

            var result = sb.ToString().Trim();

            // 結果のクリーンアップ
            result = CleanupTranslationResult(result);

            LoggerService.LogDebug($"[MistralTranslation] 生成結果: {result}");

            if (string.IsNullOrWhiteSpace(result))
            {
                LoggerService.LogWarning("[MistralTranslation] 空の翻訳結果");
                return text;
            }

            return result;
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"[MistralTranslation] 翻訳エラー: {ex.Message}");
            LoggerService.LogDebug($"[MistralTranslation] StackTrace: {ex.StackTrace}");
            return text;
        }
    }

    /// <summary>
    /// 翻訳結果をクリーンアップ
    /// </summary>
    private string CleanupTranslationResult(string result)
    {
        if (string.IsNullOrWhiteSpace(result))
            return result;

        // 不要なマーカーを削除
        result = result.Replace("</s>", "").Replace("[INST]", "").Replace("[/INST]", "");

        // 括弧内のローマ字読み方を削除（例: "(Wareware ga...)"）
        var parenStartIndex = result.IndexOf('(');
        if (parenStartIndex >= 0)
        {
            var parenEndIndex = result.LastIndexOf(')');
            if (parenEndIndex > parenStartIndex)
            {
                result = result.Substring(0, parenStartIndex).Trim();
            }
        }

        // 改行を削除（1行の翻訳結果を期待）
        result = result.Replace("\n", " ").Replace("\r", "");

        // 複数のスペースを1つに
        while (result.Contains("  "))
        {
            result = result.Replace("  ", " ");
        }

        return result.Trim();
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
            LoggerService.LogDebug($"Mistral翻訳モデルの読み込み開始: {modelPath}");

            if (!File.Exists(modelPath))
            {
                throw new FileNotFoundException($"Translation model not found: {modelPath}");
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
                "Mistralモデルを読み込み中..."));

            // モデルパラメータを設定（翻訳タスクに最適化）
            // パフォーマンス最適化: より高速な推論のためパラメータを調整
            _modelParams = new ModelParams(modelPath)
            {
                ContextSize = 512,   // コンテキストを少し拡張（処理安定性向上）
                BatchSize = 1024,    // バッチサイズを大幅増加で処理速度向上
                GpuLayerCount = 35,  // GPU使用（全レイヤーをGPUで実行）
                Threads = (uint)Math.Max(4, Environment.ProcessorCount / 2)  // CPU並列処理も活用
            };

            LoggerService.LogDebug($"Mistral model parameters: ContextSize=512, BatchSize=1024, GpuLayerCount=35, Threads={_modelParams.Threads}");

            // モデルをロード
            _model = LLamaWeights.LoadFromFile(_modelParams);

            _isModelLoaded = true;
            LoggerService.LogInfo("Mistral翻訳モデルの読み込みが完了しました");
            LoggerService.LogInfo("Mistral GPU support (CUDA/Vulkan/HIP) enabled with GpuLayerCount=35");

            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                ModelLabel,
                ModelStatusType.LoadSucceeded,
                "Mistral翻訳モデルの読み込みが完了しました。"));
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"Mistral翻訳モデル読み込みに失敗: {ex}");
            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                ModelLabel,
                ModelStatusType.LoadFailed,
                "翻訳モデルの読み込みに失敗しました。",
                ex));
            _isModelLoaded = false;
        }
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

    private string ApplyPreTranslation(string text)
    {
        foreach (var kvp in _preTranslationDict)
        {
            text = text.Replace(kvp.Key, kvp.Value, StringComparison.OrdinalIgnoreCase);
        }
        return text;
    }

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

    public void Dispose()
    {
        _model?.Dispose();
        _translateLock.Dispose();
    }
}
