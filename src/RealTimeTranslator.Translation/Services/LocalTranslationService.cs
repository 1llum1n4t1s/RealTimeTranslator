using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Text.RegularExpressions;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.Translation.Services;

/// <summary>
/// ローカル翻訳サービス
/// Argos Translate(.NETバインディング)を使用した翻訳
/// </summary>
public class LocalTranslationService : ITranslationService
{
    private const string DefaultModelFileName = "translate-en_ja.argosmodel";
    private const string DefaultModelDownloadUrl = "https://www.argosopentech.com/argospm/translate-en_ja.argosmodel";
    private readonly TranslationSettings _settings;
    private readonly ConcurrentDictionary<string, string> _cache = new();
    private Dictionary<string, string> _preTranslationDict = new();
    private Dictionary<string, string> _postTranslationDict = new();
    private bool _isModelLoaded = false;
    private object? _translationModel;
    private Func<string, string>? _translateFunc;
    private readonly SemaphoreSlim _translateLock = new(1, 1);

    public bool IsModelLoaded => _isModelLoaded;

    public LocalTranslationService(TranslationSettings? settings = null)
    {
        _settings = settings ?? new TranslationSettings();
    }

    /// <summary>
    /// 翻訳エンジンを初期化
    /// </summary>
    public async Task InitializeAsync()
    {
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
                _isModelLoaded = false;
                return;
            }

            _isModelLoaded = TryLoadArgosModel(modelPath, _settings.SourceLanguage, _settings.TargetLanguage);
            if (!_isModelLoaded)
            {
                Console.WriteLine("Argos Translate model load failed. Running in fallback mode.");
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
        if (_cache.TryGetValue(cacheKey, out var cachedTranslation))
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

            // キャッシュに保存
            if (_cache.Count >= _settings.CacheSize)
            {
                // 古いエントリを削除（簡易LRU）
                var keysToRemove = _cache.Keys.Take(_cache.Count / 4).ToList();
                foreach (var key in keysToRemove)
                {
                    _cache.TryRemove(key, out _);
                }
            }
            _cache[cacheKey] = translatedText;

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

        var targetDirectory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        try
        {
            using var httpClient = new HttpClient();
            using var response = await httpClient.GetAsync(DefaultModelDownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            await using var httpStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await httpStream.CopyToAsync(fileStream);
            Console.WriteLine($"Downloaded translation model to: {targetPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to download translation model: {ex.Message}");
        }
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
            return false;
        }
    }

    /// <summary>
    /// 翻訳前の用語正規化
    /// </summary>
    private string ApplyPreTranslation(string text)
    {
        foreach (var kvp in _preTranslationDict)
        {
            text = Regex.Replace(text, Regex.Escape(kvp.Key), kvp.Value, RegexOptions.IgnoreCase);
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
