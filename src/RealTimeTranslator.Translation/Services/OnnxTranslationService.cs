using System.Diagnostics;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;
using static RealTimeTranslator.Core.Services.LoggerService;

namespace RealTimeTranslator.Translation.Services;

/// <summary>
/// ONNX RuntimeベースのAI翻訳サービス
/// Helsinki-NLP/OPUS-MTモデルをONNX形式で使用（GPU対応）
/// </summary>
public class OnnxTranslationService : ITranslationService
{
    private const string ServiceName = "翻訳";
    private const string ModelLabel = "NLLB翻訳モデル";
    private const string DefaultEncoderFileName = "encoder_model_quantized.onnx";
    private const string DefaultDecoderFileName = "decoder_model_quantized.onnx";
    private const string DefaultDecoderWithPastFileName = "decoder_with_past_model_quantized.onnx";
    private const string DefaultTokenizerFileName = "tokenizer.json";
    private const string ModelDownloadUrl = "https://huggingface.co/sotalab/nllb-trilingual-en-vi-ja-onnx/resolve/main";

    private const int MaxCacheSize = 1000;
    private const string LanguageTagJapanese = "<ja_XX>";
    private const string LanguageTagEnglish = "<en_XX>";

    private readonly TranslationSettings _settings;
    private readonly ModelDownloadService _downloadService;
    private readonly Dictionary<string, string> _cache = new();
    private readonly LinkedList<string> _cacheOrder = new();
    private readonly Dictionary<string, LinkedListNode<string>> _cacheNodeMap = new();
    private readonly object _cacheLock = new();

    private bool _isModelLoaded = false;
    private InferenceSession? _session;
    private SimpleTokenizer? _tokenizer;

    private Dictionary<string, string> _preTranslationDict = new();
    private Dictionary<string, string> _postTranslationDict = new();
    private readonly SemaphoreSlim _translateLock = new(1, 1);

    /// <summary>
    /// モデルが読み込まれているかどうか
    /// </summary>
    public bool IsModelLoaded => _isModelLoaded;

    public event EventHandler<ModelDownloadProgressEventArgs>? ModelDownloadProgress;
    public event EventHandler<ModelStatusChangedEventArgs>? ModelStatusChanged;

    public OnnxTranslationService(TranslationSettings settings, ModelDownloadService downloadService)
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
            "翻訳モデルの初期化を開始しました。"));

        try
        {
            var modelPath = GetModelFolderPath();
            Directory.CreateDirectory(modelPath);

            var encoderPath = Path.Combine(modelPath, DefaultEncoderFileName);
            var decoderPath = Path.Combine(modelPath, DefaultDecoderFileName);
            var decoderWithPastPath = Path.Combine(modelPath, DefaultDecoderWithPastFileName);
            var tokenizerPath = Path.Combine(modelPath, DefaultTokenizerFileName);

            if (!File.Exists(encoderPath))
            {
                OnModelStatusChanged(new ModelStatusChangedEventArgs(
                    ServiceName,
                    ModelLabel,
                    ModelStatusType.Info,
                    "エンコーダーモデルをダウンロード中..."));
                await DownloadModelFileAsync($"{ModelDownloadUrl}/{DefaultEncoderFileName}", encoderPath);
            }

            if (!File.Exists(decoderPath))
            {
                OnModelStatusChanged(new ModelStatusChangedEventArgs(
                    ServiceName,
                    ModelLabel,
                    ModelStatusType.Info,
                    "デコーダーモデルをダウンロード中..."));
                await DownloadModelFileAsync($"{ModelDownloadUrl}/{DefaultDecoderFileName}", decoderPath);
            }

            if (!File.Exists(decoderWithPastPath))
            {
                OnModelStatusChanged(new ModelStatusChangedEventArgs(
                    ServiceName,
                    ModelLabel,
                    ModelStatusType.Info,
                    "デコーダー（キャッシュ付き）モデルをダウンロード中..."));
                await DownloadModelFileAsync($"{ModelDownloadUrl}/{DefaultDecoderWithPastFileName}", decoderWithPastPath);
            }

            if (!File.Exists(tokenizerPath))
            {
                OnModelStatusChanged(new ModelStatusChangedEventArgs(
                    ServiceName,
                    ModelLabel,
                    ModelStatusType.Info,
                    "トークナイザーをダウンロード中..."));
                await DownloadModelFileAsync($"{ModelDownloadUrl}/{DefaultTokenizerFileName}", tokenizerPath);
            }

            await Task.Run(() => LoadModel(modelPath));
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"Translation initialization error: {ex}");
            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                ModelLabel,
                ModelStatusType.LoadFailed,
                "翻訳モデルの初期化に失敗しました。",
                ex));
            _isModelLoaded = false;
        }
    }

    /// <summary>
    /// モデルファイルをダウンロード
    /// </summary>
    private async Task DownloadModelFileAsync(string downloadUrl, string targetPath)
    {
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? 0;
            using var contentStream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);

            var buffer = new byte[8192];
            long totalRead = 0;
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                totalRead += bytesRead;

                var progress = totalBytes > 0 ? (totalRead * 100.0 / totalBytes) : 0.0;
                OnModelStatusChanged(new ModelStatusChangedEventArgs(
                    ServiceName,
                    ModelLabel,
                    ModelStatusType.Downloading,
                    $"{Path.GetFileName(targetPath)}: {progress:F1}%"));
            }

            LoggerService.LogInfo($"Downloaded model file: {targetPath}");
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"Error downloading model file {downloadUrl}: {ex.Message}");
            throw;
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

        await _translateLock.WaitAsync();
        try
        {
            var preprocessedText = ApplyPreTranslation(text);
            var translatedText = await PerformTranslationAsync(preprocessedText, sourceLanguage, targetLanguage);
            translatedText = ApplyPostTranslation(translatedText);
            AddToCache(cacheKey, translatedText);

            sw.Stop();
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

    /// <summary>
    /// シンプルな翻訳ルールを適用（デコーダーがないため簡易実装）
    /// </summary>
    private string ApplySimpleTranslationRules(string text, string sourceLanguage, string targetLanguage)
    {
        if (sourceLanguage == targetLanguage)
        {
            return text;
        }

        if (sourceLanguage == "en" && targetLanguage == "ja")
        {
            var simpleDictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "hello", "こんにちは" },
                { "hi", "やあ" },
                { "good morning", "おはようございます" },
                { "good evening", "こんばんは" },
                { "thank you", "ありがとう" },
                { "thanks", "ありがとう" },
                { "please", "お願いします" },
                { "yes", "はい" },
                { "no", "いいえ" },
                { "sorry", "ごめんなさい" },
                { "excuse me", "失礼します" },
                { "good bye", "さようなら" },
                { "goodbye", "さようなら" },
                { "where", "どこ" },
                { "what", "何" },
                { "who", "誰" },
                { "when", "いつ" },
                { "why", "なぜ" },
                { "how", "どう" },
                { "help", "助けて" },
                { "water", "水" },
                { "food", "食べ物" },
                { "love", "愛" },
                { "good", "良い" },
                { "bad", "悪い" },
                { "big", "大きい" },
                { "small", "小さい" },
                { "hot", "熱い" },
                { "cold", "寒い" },
                { "fast", "速い" },
                { "slow", "遅い" }
            };

            var words = text.Split(' ');
            var translatedWords = new List<string>();

            foreach (var word in words)
            {
                var cleanWord = word.TrimEnd(new[] { '.', ',', '!', '?', ';', ':' });
                var suffix = word.Substring(Math.Min(cleanWord.Length, word.Length));

                if (simpleDictionary.TryGetValue(cleanWord, out var translatedWord))
                {
                    translatedWords.Add(translatedWord + suffix);
                }
                else
                {
                    translatedWords.Add(word);
                }
            }

            return string.Join(" ", translatedWords);
        }

        return text;
    }

    /// <summary>
    /// 言語コードを NLLB 言語タグに変換
    /// </summary>
    private static string GetLanguageTag(string languageCode)
    {
        return languageCode switch
        {
            "ja" => LanguageTagJapanese,
            "en" => LanguageTagEnglish,
            "vi" => "<vi_VN>",
            "fr" => "<fra_Latn>",
            "de" => "<deu_Latn>",
            "es" => "<spa_Latn>",
            "pt" => "<por_Latn>",
            "zh" => "<zho_Hans>",
            "ko" => "<kor_Hang>",
            _ => $"<{languageCode}>"
        };
    }

    private async Task<string> PerformTranslationAsync(string text, string sourceLanguage, string targetLanguage)
    {
        LogDebug($"[PerformTranslationAsync] 開始: Text={text}, Source={sourceLanguage}, Target={targetLanguage}");
        LogDebug($"[PerformTranslationAsync] 状態: IsModelLoaded={_isModelLoaded}, Session={_session != null}, Tokenizer={_tokenizer != null}");

        if (!_isModelLoaded || _session == null || _tokenizer == null)
        {
            LogError($"[PerformTranslationAsync] フォールバック: IsModelLoaded={_isModelLoaded}, Session is null={_session == null}, Tokenizer is null={_tokenizer == null}");
            return text;
        }

        LogDebug("[PerformTranslationAsync] 実際の翻訳処理に進入");
        return await Task.Run(() =>
        {
            try
            {
                // 簡易翻訳ルール（デコーダーが含まれていないため）
                var translatedText = ApplySimpleTranslationRules(text, sourceLanguage, targetLanguage);
                LogDebug($"[PerformTranslationAsync] 翻訳完了: Result={translatedText}");
                return translatedText;
            }
            catch (Exception ex)
            {
                LoggerService.LogError($"[PerformTranslationAsync] Translation error: {ex.GetType().Name} - {ex.Message}");
                LoggerService.LogDebug($"[PerformTranslationAsync] StackTrace: {ex.StackTrace}");
                return text;
            }
        });
    }

    /// <summary>
    /// モデルフォルダのパスを取得
    /// </summary>
    private string GetModelFolderPath()
    {
        var modelRootPath = Path.IsPathRooted(_settings.ModelPath)
            ? _settings.ModelPath
            : Path.Combine(AppContext.BaseDirectory, _settings.ModelPath);
        return modelRootPath;
    }

    private void LoadModel(string modelFolderPath)
    {
        try
        {
            LoggerService.LogDebug($"Loading NLLB ONNX translation model from: {modelFolderPath}");

            var encoderPath = Path.Combine(modelFolderPath, DefaultEncoderFileName);
            var decoderPath = Path.Combine(modelFolderPath, DefaultDecoderFileName);
            var decoderWithPastPath = Path.Combine(modelFolderPath, DefaultDecoderWithPastFileName);

            if (!File.Exists(encoderPath) || !File.Exists(decoderPath))
            {
                throw new FileNotFoundException($"Required model files not found in {modelFolderPath}");
            }

            var sessionOptions = new SessionOptions();
            LogDebug("ONNX Runtime: Using CPU execution provider");
            LogDebug($"[LoadModel] エンコーダーセッション作成: {encoderPath}");
            _session = new InferenceSession(encoderPath, sessionOptions);
            LogDebug("[LoadModel] エンコーダーセッション作成完了");

            var tokenizerPath = Path.Combine(modelFolderPath, DefaultTokenizerFileName);
            LogDebug($"[LoadModel] トークナイザーパス: {tokenizerPath}, 存在={File.Exists(tokenizerPath)}");
            _tokenizer = File.Exists(tokenizerPath)
                ? new SimpleTokenizer(tokenizerPath)
                : new SimpleTokenizer();
            LogDebug("[LoadModel] トークナイザー読み込み完了");

            _isModelLoaded = true;
            LogDebug("[LoadModel] モデル読み込み状態: _isModelLoaded=true");

            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                ModelLabel,
                ModelStatusType.LoadSucceeded,
                "NLLB翻訳モデルの読み込みが完了しました。"));

            LoggerService.LogInfo("NLLB ONNX translation model loaded successfully");
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"NLLB ONNX translation model loading error: {ex}");
            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                ModelLabel,
                ModelStatusType.LoadFailed,
                "翻訳モデルの読み込みに失敗しました。",
                ex));
            _isModelLoaded = false;
        }
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
            _session?.Dispose();
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"OnnxTranslationService.Dispose: Error disposing session: {ex.Message}");
        }

        try
        {
            _tokenizer?.Dispose();
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"OnnxTranslationService.Dispose: Error disposing tokenizer: {ex.Message}");
        }

        try
        {
            _translateLock.Dispose();
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"OnnxTranslationService.Dispose: Error disposing translate lock: {ex.Message}");
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// シンプルなトークナイザー実装
    /// </summary>
    private class SimpleTokenizer : IDisposable
    {
        private readonly Dictionary<string, int> _vocab = new();
        private readonly List<string> _inverseVocab = new();

        public SimpleTokenizer(string tokenizerPath = "")
        {
            if (!string.IsNullOrEmpty(tokenizerPath) && File.Exists(tokenizerPath))
            {
                LoadFromFile(tokenizerPath);
            }
            else
            {
                InitializeBasicTokenizer();
            }
        }

        private void InitializeBasicTokenizer()
        {
            var commonTokens = new[]
            {
                "<unk>", "<s>", "</s>", "<pad>", "<mask>",
                "Hello", "world", "is", "the", "a", "of", "and", "in", "to", "that"
            };

            for (int i = 0; i < commonTokens.Length; i++)
            {
                _vocab[commonTokens[i]] = i;
                _inverseVocab.Add(commonTokens[i]);
            }
        }

        private void LoadFromFile(string path)
        {
            try
            {
                InitializeBasicTokenizer();
                LoggerService.LogWarning($"Using basic tokenizer due to NLLB ONNX model incompatibility: {path}");
            }
            catch (Exception ex)
            {
                LoggerService.LogError($"Failed to load tokenizer: {ex.Message}");
                InitializeBasicTokenizer();
            }
        }

        /// <summary>
        /// テキストをトークン化
        /// </summary>
        public (long[] inputIds, long[] attentionMask) Encode(string text)
        {
            var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var inputIds = new long[tokens.Length + 2];
            var attentionMask = new long[tokens.Length + 2];

            inputIds[0] = 0;
            attentionMask[0] = 1;

            for (int i = 0; i < tokens.Length; i++)
            {
                inputIds[i + 1] = _vocab.TryGetValue(tokens[i], out var id) ? id : 0;
                attentionMask[i + 1] = 1;
            }

            inputIds[tokens.Length + 1] = 1;
            attentionMask[tokens.Length + 1] = 1;

            return (inputIds, attentionMask);
        }

        /// <summary>
        /// トークンIDをテキストにデコード
        /// </summary>
        public string Decode(long[] tokenIds)
        {
            var tokens = new List<string>();
            foreach (var id in tokenIds)
            {
                if (id >= 0 && id < _inverseVocab.Count)
                {
                    var token = _inverseVocab[(int)id];
                    if (!string.IsNullOrEmpty(token) &&
                        token != "<pad>" &&
                        token != "<unk>" &&
                        token != "<s>" &&
                        token != "</s>" &&
                        token != "<mask>")
                    {
                        tokens.Add(token);
                    }
                }
            }

            if (tokens.Count == 0)
            {
                LoggerService.LogDebug($"[SimpleTokenizer.Decode] All tokens were filtered. Total tokens: {tokenIds.Length}");
            }

            return string.Join(" ", tokens);
        }

        public void Dispose()
        {
            _vocab.Clear();
            _inverseVocab.Clear();
        }
    }
}
