using System.Diagnostics;
using System.Text;
using LLama;
using LLama.Common;
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
    private LLamaContext? _context;
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
            await Task.Run(() => LoadModelFromPath(modelFilePath)).ConfigureAwait(false);
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
        if (!_isModelLoaded || _model == null || _context == null)
        {
            LoggerService.LogError("[MistralTranslation] モデルが読み込まれていません");
            return text;
        }

        return await Task.Run(() =>
        {
            try
            {
                // 言語名を取得
                var sourceLangName = GetLanguageName(sourceLanguage);
                var targetLangName = GetLanguageName(targetLanguage);

                // Mistral Instruct形式のプロンプトを作成
                var prompt = $@"<s>[INST] Translate the following {sourceLangName} text to {targetLangName}. Only output the translation, nothing else.

{sourceLangName} text: {text}

{targetLangName} translation: [/INST]";

                LoggerService.LogDebug($"[MistralTranslation] プロンプト: {prompt}");

                // 推論パラメータを設定
                var inferenceParams = new InferenceParams
                {
                    MaxTokens = 256,
                    Temperature = 0.3f,
                    TopP = 0.9f,
                    TopK = 40,
                    RepeatPenalty = 1.1f,
                    AntiPrompts = new List<string> { "</s>", "[INST]", "[/INST]" }
                };

                // 推論を実行
                var sb = new StringBuilder();
                var executor = new InteractiveExecutor(_context);

                foreach (var outputToken in executor.Infer(prompt, inferenceParams))
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
        }).ConfigureAwait(false);
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

            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                ModelLabel,
                ModelStatusType.Info,
                "Mistralモデルを読み込み中..."));

            // モデルパラメータを設定
            _modelParams = new ModelParams(modelPath)
            {
                ContextSize = 2048,
                GpuLayerCount = 35, // GPU使用（調整可能）
                Seed = 1337,
                EmbeddingMode = false
            };

            // モデルをロード
            _model = LLamaWeights.LoadFromFile(_modelParams);
            _context = _model.CreateContext(_modelParams);

            _isModelLoaded = true;
            LoggerService.LogInfo("Mistral翻訳モデルの読み込みが完了しました");

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
        _context?.Dispose();
        _model?.Dispose();
        _translateLock.Dispose();
    }
}
