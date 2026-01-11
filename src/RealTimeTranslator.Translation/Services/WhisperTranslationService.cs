using System.Diagnostics;
using System.Text.Json;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;
using Whisper.net;
using Whisper.net.Ggml;
using static RealTimeTranslator.Core.Services.LoggerService;

namespace RealTimeTranslator.Translation.Services;

/// <summary>
/// Whisper.net ベースのGPU翻訳サービス
/// OpenAI Whisper の翻訳機能を使用した高精度翻訳
/// GPU（CUDA/Vulkan/CPU）で高速実行
/// 音声データを直接翻訳して、テキスト翻訳結果を提供
/// </summary>
public class WhisperTranslationService : ITranslationService
{
    private const string ServiceName = "翻訳";
    private const string ModelLabel = "Whisper翻訳モデル";
    private const int MaxCacheSize = 1000;

    private readonly TranslationSettings _settings;
    private readonly ModelDownloadService _downloadService;
    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, string> _cache = new();
    private readonly LinkedList<string> _cacheOrder = new();
    // キャッシュキーから LinkedListNode へのマッピング（O(1) 検索のため）
    private readonly Dictionary<string, LinkedListNode<string>> _cacheNodeMap = new();
    private readonly object _cacheLock = new();

    private bool _isModelLoaded = false;
    private WhisperProcessor? _processor;
    private WhisperFactory? _factory;

    private Dictionary<string, string> _preTranslationDict = new();
    private Dictionary<string, string> _postTranslationDict = new();
    private readonly SemaphoreSlim _translateLock = new(1, 1);

    /// <summary>
    /// モデルが読み込まれているかどうか
    /// </summary>
    public bool IsModelLoaded => _isModelLoaded;

    public event EventHandler<ModelDownloadProgressEventArgs>? ModelDownloadProgress;
    public event EventHandler<ModelStatusChangedEventArgs>? ModelStatusChanged;

    public WhisperTranslationService(TranslationSettings settings, ModelDownloadService downloadService, HttpClient httpClient)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _downloadService = downloadService ?? throw new ArgumentNullException(nameof(downloadService));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

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
            // モデルファイルをダウンロード/確認
            const string defaultModelFileName = "ggml-large-v3.bin";
            const string downloadUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3.bin";

            var modelFilePath = await _downloadService.EnsureModelAsync(
                _settings.ModelPath,
                defaultModelFileName,
                downloadUrl,
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
    /// テキストを翻訳
    /// 注：このサービスは主に音声翻訳用です
    /// テキスト翻訳の場合は、音声データが必要なため簡易的な処理のみ実行
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
            LogDebug($"[TranslateAsync] キャッシュヒット: {text} -> {cachedTranslation}");
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
            LogDebug($"[TranslateAsync] 翻訳開始: Text={preprocessedText}, Source={sourceLanguage}, Target={targetLanguage}");

            // MyMemory Translation API を使用（無料、認証不要）
            string translatedText;
            if (sourceLanguage.Equals(targetLanguage, StringComparison.OrdinalIgnoreCase))
            {
                translatedText = preprocessedText;
            }
            else
            {
                translatedText = await TranslateWithMyMemoryAsync(preprocessedText, sourceLanguage, targetLanguage).ConfigureAwait(false);
            }

            translatedText = ApplyPostTranslation(translatedText);
            AddToCache(cacheKey, translatedText);

            sw.Stop();
            LogDebug($"[TranslateAsync] テキスト翻訳完了: Result={translatedText}, Time={sw.ElapsedMilliseconds}ms");

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
    /// 音声データを認識して翻訳（2段階：Whisper ASR → 翻訳）
    /// Whisper で音声を英語に認識し、その後翻訳サービスで目標言語に翻訳
    /// </summary>
    public async Task<string> TranslateAudioAsync(float[] audioData, string sourceLanguage = "en", string targetLanguage = "ja")
    {
        if (!_isModelLoaded || _processor == null)
        {
            LogError($"[TranslateAudioAsync] モデルが読み込まれていません");
            return string.Empty;
        }

        return await Task.Run(async () =>
        {
            try
            {
                LogDebug($"[TranslateAudioAsync] 音声翻訳開始: Source={sourceLanguage}, Target={targetLanguage}, AudioLength={audioData.Length}");

                // ステップ1: Whisper で音声を認識（常に英語）
                var recognizedSegments = new List<string>();
                await foreach (var segment in _processor.ProcessAsync(audioData).ConfigureAwait(false))
                {
                    recognizedSegments.Add(segment.Text.Trim());
                    LogDebug($"[TranslateAudioAsync] 認識セグメント: {segment.Text}");
                }

                var recognizedText = string.Join(" ", recognizedSegments);
                if (string.IsNullOrWhiteSpace(recognizedText))
                {
                    LogDebug($"[TranslateAudioAsync] 音声から認識されたテキストがありません");
                    return string.Empty;
                }

                LogDebug($"[TranslateAudioAsync] 認識完了: {recognizedText}");

                // ステップ2: 認識したテキストを翻訳（英語→目標言語）
                if (targetLanguage.Equals("en", StringComparison.OrdinalIgnoreCase))
                {
                    // ターゲットが英語の場合はそのまま返す
                    LogDebug($"[TranslateAudioAsync] ターゲットが英語のため翻訳をスキップ");
                    return recognizedText;
                }

                var translationResult = await TranslateAsync(recognizedText, "en", targetLanguage).ConfigureAwait(false);
                var translatedText = translationResult.TranslatedText;

                LogDebug($"[TranslateAudioAsync] 翻訳完了: {translatedText}");
                return translatedText;
            }
            catch (Exception ex)
            {
                LoggerService.LogError($"[TranslateAudioAsync] 翻訳エラー: {ex.GetType().Name} - {ex.Message}");
                LoggerService.LogDebug($"[TranslateAudioAsync] StackTrace: {ex.StackTrace}");
                return string.Empty;
            }
        });
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
                // 既存のノードを削除して再追加（パフォーマンス最適化：Find() を避ける）
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
                // ノードマップを使用して O(1) で取得（パフォーマンス最適化：Find() を避ける）
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

    private void LoadModelFromPath(string modelPath)
    {
        try
        {
            LoggerService.LogDebug($"Whisper翻訳モデルの読み込み開始: {modelPath}");

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
                "WhisperFactory を作成中..."));

            LoggerService.LogDebug($"Loading translation model from: {modelPath}");
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
            LoggerService.LogInfo("Whisper翻訳モデルの読み込みが完了しました");

            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                ModelLabel,
                ModelStatusType.LoadSucceeded,
                "Whisper翻訳モデルの読み込みが完了しました。"));
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"Whisper翻訳モデル読み込みに失敗: {ex}");
            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                ModelLabel,
                ModelStatusType.LoadFailed,
                "翻訳モデルの読み込みに失敗しました。",
                ex));
            _isModelLoaded = false;
        }
    }

    private async Task<string> TranslateWithMyMemoryAsync(string text, string sourceLanguage, string targetLanguage)
    {
        try
        {
            // MyMemory Translation API のエンドポイント
            const string baseUrl = "https://api.mymemory.translated.net/get";
            
            // 言語コードを標準化（en, ja など）
            var sourceLang = sourceLanguage.Split('-')[0].ToLower();
            var targetLang = targetLanguage.Split('-')[0].ToLower();
            
            var url = $"{baseUrl}?q={Uri.EscapeDataString(text)}&langpair={sourceLang}|{targetLang}";

            LogDebug($"[TranslateWithMyMemoryAsync] MyMemory API呼び出し: {sourceLang}→{targetLang}");

            var response = await _httpClient.GetAsync(url).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogError($"[TranslateWithMyMemoryAsync] MyMemory API エラー: {response.StatusCode}");
                return text; // 失敗時は元のテキストを返す
            }

            var jsonContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            LogDebug($"[TranslateWithMyMemoryAsync] API応答: {jsonContent}");

            // System.Text.Json を使用して適切に JSON をパース（パフォーマンス最適化）
            try
            {
                using var document = JsonDocument.Parse(jsonContent);
                if (document.RootElement.TryGetProperty("responseData", out var responseData) &&
                    responseData.TryGetProperty("translatedText", out var translatedTextElement))
                {
                    var translated = translatedTextElement.GetString();
                    if (!string.IsNullOrEmpty(translated))
                    {
                        LogDebug($"[TranslateWithMyMemoryAsync] 翻訳結果: {translated}");
                        return translated;
                    }
                }
            }
            catch (JsonException jsonEx)
            {
                LogWarning($"[TranslateWithMyMemoryAsync] JSON パースエラー: {jsonEx.Message}");
            }

            LogWarning($"[TranslateWithMyMemoryAsync] 翻訳結果をパースできません");
            return text;
        }
        catch (Exception ex)
        {
            LogError($"[TranslateWithMyMemoryAsync] エラー: {ex.Message}");
            return text; // 例外時は元のテキストを返す
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

    public void Dispose()
    {
        _processor?.Dispose();
        _factory?.Dispose();
        _translateLock.Dispose();
    }
}
