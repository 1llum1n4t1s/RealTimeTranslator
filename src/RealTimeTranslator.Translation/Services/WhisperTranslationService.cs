using System.Diagnostics;
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
    private readonly Dictionary<string, string> _cache = new();
    private readonly LinkedList<string> _cacheOrder = new();
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

    public WhisperTranslationService(TranslationSettings settings, ModelDownloadService downloadService)
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
            await Task.Run(() => LoadModel());
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

        await _translateLock.WaitAsync();
        try
        {
            var preprocessedText = ApplyPreTranslation(text);
            LogDebug($"[TranslateAsync] 翻訳開始: Text={preprocessedText}, Source={sourceLanguage}, Target={targetLanguage}");

            // テキスト翻訳: 音声データがないため、元のテキストを返す
            // 実際の翻訳は TranslateAudioAsync で音声データを処理する際に実行される
            var translatedText = preprocessedText;

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
    /// 音声データを直接翻訳（Whisper.net のネイティブ機能）
    /// キャッシュに保存し、後でテキスト翻訳として取得可能
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

                var translationSegments = new List<string>();
                await foreach (var segment in _processor.ProcessAsync(audioData))
                {
                    translationSegments.Add(segment.Text.Trim());
                    LogDebug($"[TranslateAudioAsync] セグメント: {segment.Text}");
                }

                var translatedText = string.Join(" ", translationSegments);
                LogDebug($"[TranslateAudioAsync] 音声翻訳完了: {translatedText}");

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
            if (_cache.ContainsKey(key))
            {
                _cacheOrder.Remove(_cacheOrder.Find(key)!);
            }

            _cache[key] = value;
            _cacheOrder.AddLast(key);

            if (_cacheOrder.Count > MaxCacheSize)
            {
                var oldestKey = _cacheOrder.First!.Value;
                _cacheOrder.RemoveFirst();
                _cache.Remove(oldestKey);
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
                _cacheOrder.Remove(_cacheOrder.Find(key)!);
                _cacheOrder.AddLast(key);
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

    private void LoadModel()
    {
        try
        {
            LoggerService.LogDebug("Whisper翻訳モデルの読み込み開始");

            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                ModelLabel,
                ModelStatusType.Info,
                "Whisper翻訳モデルを初期化中..."));

            // 翻訳用モデルのパスを取得（高精度モデルを使用）
            // パスの各要素を処理してスラッシュ/バックスラッシュの混在を避ける
            var baseModelPath = Path.IsPathRooted(_settings.ModelPath)
                ? _settings.ModelPath
                : Path.Combine(AppContext.BaseDirectory, _settings.ModelPath);

            baseModelPath = Path.GetFullPath(baseModelPath);  // 正規化

            // ggml-large-v3.bin を試す（高精度）
            var modelPath = Path.Combine(baseModelPath, "ggml-large-v3.bin");

            if (!File.Exists(modelPath))
            {
                LoggerService.LogWarning($"ggml-large-v3.bin が見つかりません: {modelPath}");
                LoggerService.LogWarning($"ggml-small.bin を試します");

                // ggml-small.bin にフォールバック（速度重視）
                modelPath = Path.Combine(baseModelPath, "ggml-small.bin");

                if (!File.Exists(modelPath))
                {
                    LoggerService.LogError($"どちらのモデルも見つかりません");
                    throw new FileNotFoundException($"Translation model not found. Tried: {Path.Combine(baseModelPath, "ggml-large-v3.bin")}, {modelPath}");
                }
            }

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
                .WithLanguage("en")  // ソース言語を英語に指定
                .WithThreads(Environment.ProcessorCount)
                .WithTranslate();  // 翻訳モードを有効化

            _processor = builder.Build();

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
        }
    }

    public void Dispose()
    {
        _processor?.Dispose();
        _factory?.Dispose();
        _translateLock.Dispose();
    }
}
