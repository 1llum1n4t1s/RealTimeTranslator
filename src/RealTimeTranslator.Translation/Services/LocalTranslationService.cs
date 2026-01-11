using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;

namespace RealTimeTranslator.Translation.Services;

/// <summary>
/// ローカル翻訳サービス
/// Argos Translate(.NETバインディング)を使用した翻訳
/// </summary>
public class LocalTranslationService : ITranslationService
{
    private const string ServiceName = "翻訳";
    private const string ModelLabel = "翻訳モデル";
    private const string DefaultModelFileName = "translate-en_ja.argosmodel";
    private const string DefaultModelDownloadUrl = "https://www.argosopentech.com/argospm/translate-en_ja.argosmodel";
    private readonly TranslationSettings _settings;
    private readonly ModelDownloadService _downloadService;
    private readonly LruCache<string, string> _cache;
    private Dictionary<string, string> _preTranslationDict = new();
    private Dictionary<string, string> _postTranslationDict = new();
    private readonly Dictionary<string, Regex> _compiledPreTranslationRegexes = new();
    private bool _isModelLoaded = false;
    private object? _translationModel;
    private Func<string, string>? _translateFunc;
    private readonly SemaphoreSlim _translateLock = new(1, 1);

    public bool IsModelLoaded => _isModelLoaded;

    public event EventHandler<ModelDownloadProgressEventArgs>? ModelDownloadProgress;
    public event EventHandler<ModelStatusChangedEventArgs>? ModelStatusChanged;

    public LocalTranslationService(TranslationSettings settings, ModelDownloadService downloadService)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _downloadService = downloadService ?? throw new ArgumentNullException(nameof(downloadService));
        _cache = new LruCache<string, string>(_settings.CacheSize);

        // イベントを転送
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

        var modelPath = ResolveModelPath();
        if (modelPath == null)
        {
            await TryDownloadModelAsync();
            modelPath = ResolveModelPath();
        }

        await Task.Run(() =>
        {
            if (modelPath == null)
            {
                Console.WriteLine($"Translation model not found at: {GetModelRootPath()}");
                Console.WriteLine("Running in fallback mode (no actual translation)");
                OnModelStatusChanged(new ModelStatusChangedEventArgs(
                    ServiceName,
                    ModelLabel,
                    ModelStatusType.Fallback,
                    "翻訳モデルが見つからないためタグ付け翻訳で継続します。"));
                _isModelLoaded = false;
                return;
            }

            _isModelLoaded = TryLoadArgosModel(modelPath, _settings.SourceLanguage, _settings.TargetLanguage);
            if (!_isModelLoaded)
            {
                Console.WriteLine("Argos Translate model load failed. Running in fallback mode.");
                OnModelStatusChanged(new ModelStatusChangedEventArgs(
                    ServiceName,
                    ModelLabel,
                    ModelStatusType.Fallback,
                    "翻訳モデルの読み込みに失敗したためタグ付け翻訳で継続します。"));
            }
            else
            {
                OnModelStatusChanged(new ModelStatusChangedEventArgs(
                    ServiceName,
                    ModelLabel,
                    ModelStatusType.LoadSucceeded,
                    "翻訳モデルの読み込みが完了しました。"));
            }
        });
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

        // 翻訳前の正規化
        string normalizedText = ApplyPreTranslation(text);

        // キャッシュチェック
        string cacheKey = $"{sourceLanguage}:{targetLanguage}:{normalizedText}";
        if (_cache.TryGet(cacheKey, out var cachedTranslation))
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
            // 実際の翻訳処理
            string translatedText = await PerformTranslationAsync(normalizedText, sourceLanguage, targetLanguage);

            // 翻訳後の補正
            translatedText = ApplyPostTranslation(translatedText);

            // キャッシュに保存（LRUキャッシュが自動的に古いエントリを削除）
            _cache.Add(cacheKey, translatedText);

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

    private async Task<string> PerformTranslationAsync(string text, string sourceLanguage, string targetLanguage)
    {
        if (!_isModelLoaded || _translateFunc == null)
        {
            return $"[{targetLanguage}] {text}";
        }

        return await Task.Run(() =>
        {
            try
            {
                var translated = _translateFunc(text);
                return string.IsNullOrWhiteSpace(translated) ? $"[{targetLanguage}] {text}" : translated;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Argos Translate translation error: {ex.Message}");
                return $"[{targetLanguage}] {text}";
            }
        });
    }

    private string? ResolveModelPath()
    {
        var modelRootPath = GetModelRootPath();

        if (File.Exists(modelRootPath))
        {
            return modelRootPath;
        }

        if (Directory.Exists(modelRootPath))
        {
            var modelFile = Directory.GetFiles(modelRootPath, "*.argosmodel", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
            return modelFile;
        }

        return null;
    }

    private string GetModelRootPath()
    {
        if (Path.IsPathRooted(_settings.ModelPath))
        {
            return _settings.ModelPath;
        }

        return Path.Combine(AppContext.BaseDirectory, _settings.ModelPath);
    }

    private async Task TryDownloadModelAsync()
    {
        var modelRootPath = GetModelRootPath();
        string targetPath = Path.HasExtension(modelRootPath)
            ? modelRootPath
            : Path.Combine(modelRootPath, DefaultModelFileName);

        if (File.Exists(targetPath))
        {
            return;
        }

        await _downloadService.EnsureModelAsync(
            _settings.ModelPath,
            DefaultModelFileName,
            DefaultModelDownloadUrl,
            ServiceName,
            ModelLabel,
            CancellationToken.None);
    }

    private bool TryLoadArgosModel(string modelPath, string sourceLanguage, string targetLanguage)
    {
        try
        {
            var packageType = Type.GetType("ArgosTranslate.Models.Package, ArgosTranslate.NET");
            if (packageType == null)
            {
                return false;
            }

            var loadFromMethod = packageType.GetMethod("LoadFrom", new[] { typeof(string) });
            if (loadFromMethod == null)
            {
                return false;
            }

            var package = loadFromMethod.Invoke(null, new object[] { modelPath });
            if (package == null)
            {
                return false;
            }

            var installMethod = packageType.GetMethod("Install", Type.EmptyTypes);
            installMethod?.Invoke(package, null);

            var getTranslationMethod = packageType.GetMethod("GetTranslation", new[] { typeof(string), typeof(string) });
            _translationModel = getTranslationMethod != null
                ? getTranslationMethod.Invoke(package, new object[] { sourceLanguage, targetLanguage })
                : package;

            if (_translationModel == null)
            {
                return false;
            }

            var translateMethod = _translationModel.GetType().GetMethod("Translate", new[] { typeof(string) });
            if (translateMethod == null)
            {
                return false;
            }

            _translateFunc = (Func<string, string>)translateMethod.CreateDelegate(
                typeof(Func<string, string>),
                _translationModel);

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Argos Translate initialization error: {ex.Message}");
            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                ModelLabel,
                ModelStatusType.LoadFailed,
                "翻訳モデルの初期化に失敗しました。",
                ex));
            return false;
        }
    }

    /// <summary>
    /// 翻訳前の用語正規化
    /// </summary>
    private string ApplyPreTranslation(string text)
    {
        // 事前コンパイルした正規表現を使用
        foreach (var kvp in _preTranslationDict)
        {
            if (_compiledPreTranslationRegexes.TryGetValue(kvp.Key, out var regex))
            {
                text = regex.Replace(text, kvp.Value);
            }
        }
        return text;
    }

    /// <summary>
    /// 翻訳後の補正
    /// </summary>
    private string ApplyPostTranslation(string text)
    {
        foreach (var kvp in _postTranslationDict)
        {
            text = text.Replace(kvp.Key, kvp.Value);
        }
        return text;
    }

    /// <summary>
    /// 翻訳前用語辞書を設定
    /// </summary>
    public void SetPreTranslationDictionary(Dictionary<string, string> dictionary)
    {
        _preTranslationDict = new Dictionary<string, string>(dictionary);

        // 正規表現を事前コンパイル
        _compiledPreTranslationRegexes.Clear();
        foreach (var entry in _preTranslationDict)
        {
            if (!string.IsNullOrWhiteSpace(entry.Key))
            {
                var pattern = Regex.Escape(entry.Key);
                _compiledPreTranslationRegexes[entry.Key] = new Regex(
                    pattern,
                    RegexOptions.IgnoreCase | RegexOptions.Compiled);
            }
        }
    }

    /// <summary>
    /// 翻訳後置換辞書を設定
    /// </summary>
    public void SetPostTranslationDictionary(Dictionary<string, string> dictionary)
    {
        _postTranslationDict = new Dictionary<string, string>(dictionary);
    }

    /// <summary>
    /// キャッシュをクリア
    /// </summary>
    public void ClearCache()
    {
        _cache.Clear();
    }

    public void Dispose()
    {
        _translateLock.Dispose();
    }
}

/// <summary>
/// スレッドセーフなLRUキャッシュ実装
/// </summary>
internal class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<CacheItem>> _cacheMap;
    private readonly LinkedList<CacheItem> _lruList;
    private readonly object _lock = new();

    public LruCache(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be greater than 0");
        }

        _capacity = capacity;
        _cacheMap = new Dictionary<TKey, LinkedListNode<CacheItem>>(capacity);
        _lruList = new LinkedList<CacheItem>();
    }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_lock)
        {
            if (_cacheMap.TryGetValue(key, out var node))
            {
                // ノードをリストの先頭に移動（最近使用されたマーク）
                _lruList.Remove(node);
                _lruList.AddFirst(node);
                value = node.Value.Value;
                return true;
            }

            value = default!;
            return false;
        }
    }

    public void Add(TKey key, TValue value)
    {
        lock (_lock)
        {
            if (_cacheMap.TryGetValue(key, out var existingNode))
            {
                // 既存のエントリを更新
                _lruList.Remove(existingNode);
                existingNode.Value = new CacheItem(key, value);
                _lruList.AddFirst(existingNode);
            }
            else
            {
                // 容量を超えている場合、最も古いエントリを削除
                if (_cacheMap.Count >= _capacity)
                {
                    var lastNode = _lruList.Last;
                    if (lastNode != null)
                    {
                        _lruList.RemoveLast();
                        _cacheMap.Remove(lastNode.Value.Key);
                    }
                }

                // 新しいエントリを追加
                var newNode = new LinkedListNode<CacheItem>(new CacheItem(key, value));
                _lruList.AddFirst(newNode);
                _cacheMap[key] = newNode;
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _cacheMap.Clear();
            _lruList.Clear();
        }
    }

    private record struct CacheItem(TKey Key, TValue Value);
}
