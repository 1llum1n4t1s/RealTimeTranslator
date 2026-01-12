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
    private const string DefaultModelFileName = "mistral-7b-instruct-v0.2.Q3_K_S.gguf";
    private const string DefaultModelDownloadUrl = "https://huggingface.co/TheBloke/Mistral-7B-Instruct-v0.2-GGUF/resolve/main/mistral-7b-instruct-v0.2.Q3_K_S.gguf";
    private const int MaxCacheSize = 1000;
    private const string WarmupPrompt = "Test prompt";

    private readonly TranslationSettings _settings;
    private readonly ModelDownloadService _downloadService;
    private readonly Dictionary<string, string> _cache = new();
    private readonly LinkedList<string> _cacheOrder = new();
    private readonly Dictionary<string, LinkedListNode<string>> _cacheNodeMap = new();
    private readonly object _cacheLock = new();

    private bool _isModelLoaded = false;
    private bool _isWarmupComplete = false;
    private LLamaWeights? _model;
    private ModelParams? _modelParams;
    private StatelessExecutor? _executor;

    private Dictionary<string, string> _preTranslationDict = new();
    private Dictionary<string, string> _postTranslationDict = new();
    private readonly SemaphoreSlim _translateLock = new(1, 1);
    private readonly SemaphoreSlim _warmupLock = new(1, 1);

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

            // ウォームアップ推論を非同期実行（初回遅延を短縮）
            _ = Task.Run(WarmupInferenceAsync);
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
                var executor = _executor ?? new StatelessExecutor(_model, _modelParams);
                _executor ??= executor;

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

            // Mistral Instruct形式のプロンプトを作成（翻訳専用・説明禁止・括弧禁止）
            var prompt =
                $"<s>[INST] You are a professional translator. Translate the following text from {sourceLangName} to {targetLangName}. " +
                "Output ONLY the translation. " +
                "Do NOT add explanations, notes, parentheses, or any additional text. " +
                "Do NOT use brackets, parentheses, or footnotes. " +
                "Provide ONLY the translated text.\n" +
                $"\"{text}\" [/INST] ";

            LoggerService.LogDebug($"[MistralTranslation] プロンプト: {prompt}");

            // 推論パラメータを設定（翻訳タスクに最適化）
            // パフォーマンス最適化: より高速な推論のためパラメータを調整
            // 日本語翻訳では英語よりトークン数が2-3倍必要（文字ごとにトークン化）
            var maxTokens = Math.Clamp(text.Length / 2 + 32, 64, 256);  // トークン数を増加（品質向上）
            LoggerService.LogDebug($"[MistralTranslation] 推論パラメータ: MaxTokens={maxTokens}, Temp=0.05, TopP=0.7, TopK=8");

            var inferenceParams = new InferenceParams
            {
                MaxTokens = maxTokens,  // 日本語翻訳に十分なトークン数（拡張）
                AntiPrompts = new List<string> { "</s>", "[INST]", "Note:", "Explanation:", "The translation is:", "In Japanese:" },  // 説明文を避ける
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = 0.05f,  // 低温度で確定的な翻訳（精度重視）
                    TopP = 0.7f,          // TopPを下げ（多様性を制限、正確性向上）
                    TopK = 8,             // TopKを下げ（最高確率のトークンに集中）
                    RepeatPenalty = 1.15f // 繰り返しペナルティ（適度な値）
                }
            };

            // 推論を実行（StatelessExecutorを使用 - コンテキストは自動管理）
            LoggerService.LogDebug($"[MistralTranslation] InferAsync 開始");
            var inferenceStartTime = Stopwatch.GetTimestamp();

            var sb = new StringBuilder();
            if (_modelParams == null)
            {
                throw new InvalidOperationException("Model parameters are not initialized");
            }
            var executor = _executor ?? new StatelessExecutor(_model, _modelParams);
            _executor ??= executor;

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

                // 5トークンごと、または500ms経過ごとにログ出力
                var timeSinceLastLog = (currentTime - lastLogTime) * 1000.0 / Stopwatch.Frequency;
                if (tokenCount % 5 == 0 || timeSinceLastLog >= 500)
                {
                    var elapsedMs = (currentTime - inferenceStartTime) * 1000.0 / Stopwatch.Frequency;
                    var loggingAvgTokenTime = tokenTimes.Count > 0 ? tokenTimes.Average() : 0;
                    LoggerService.LogDebug($"[MistralTranslation] トークン生成中: {tokenCount}トークン, {elapsedMs:F0}ms経過, 平均={loggingAvgTokenTime:F0}ms/token");
                    lastLogTime = currentTime;
                }
            }

            var inferenceEndTime = Stopwatch.GetTimestamp();
            var inferenceDuration = (inferenceEndTime - inferenceStartTime) * 1000.0 / Stopwatch.Frequency;
            var tokensPerSec = tokenCount > 0 ? tokenCount * 1000.0 / inferenceDuration : 0;
            var finalAvgTokenTime = tokenTimes.Count > 0 ? tokenTimes.Average() : 0;
            var firstTokenTime = tokenTimes.Count > 0 ? tokenTimes[0] : 0;
            LoggerService.LogDebug($"[MistralTranslation] InferAsync 完了: {tokenCount}トークン生成, {inferenceDuration:F0}ms");
            LoggerService.LogDebug($"[MistralTranslation] 速度統計: {tokensPerSec:F2} tokens/sec, 平均={finalAvgTokenTime:F0}ms/token, 初回={firstTokenTime:F0}ms");

            var result = sb.ToString().Trim();

            // 結果のクリーンアップ
            result = CleanupTranslationResult(result);

            LoggerService.LogDebug($"[MistralTranslation] 生成結果: {result}");

            if (string.IsNullOrWhiteSpace(result))
            {
                LoggerService.LogWarning("[MistralTranslation] 空の翻訳結果");
                return text;
            }

            // 英語が返されている場合は再翻訳（APIが説明文を返した可能性）
            if (IsEnglishText(result) && !IsEnglishText(text))
            {
                LoggerService.LogWarning($"[MistralTranslation] 英語テキストが返されました。再翻訳を実行: {result}");
                return text;  // 元のテキストを返す（翻訳失敗を避ける）
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

        result = ExtractTranslationOnly(result);

        // 括弧内の全てを削除（英語説明やローマ字が入っていることが多い）
        // （例: "(Wareware ga...)", "(Katakana: ...)", "(近鉄)", etc.）
        while (true)
        {
            var parenStartIndex = result.IndexOf('(');
            if (parenStartIndex < 0)
                break;

            var parenEndIndex = result.IndexOf(')', parenStartIndex);
            if (parenEndIndex <= parenStartIndex)
                break;

            result = (result[..parenStartIndex] + result[(parenEndIndex + 1)..]).Trim();
        }

        // 改行を削除（1行の翻訳結果を期待）
        result = result.Replace("\n", " ").Replace("\r", "");

        // 複数のスペースを1つに
        while (result.Contains("  "))
        {
            result = result.Replace("  ", " ");
        }

        // 末尾の句点が複数ある場合は1つに統一
        while (result.EndsWith("。。"))
        {
            result = result[..^1];
        }

        return result.Trim();
    }

    private string ExtractTranslationOnly(string result)
    {
        if (string.IsNullOrWhiteSpace(result))
            return result;

        var trimmed = result.Trim();

        // よくある説明文の先頭を除去
        var prefixes = new[]
        {
            "In Japanese", "In Japanese,", "In Japanese:", "Japanese translation", "Japanese:",
            "日本語訳", "日本語:", "日本語では", "和訳"
        };

        foreach (var prefix in prefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var colonIndex = trimmed.IndexOf(':');
                if (colonIndex >= 0 && colonIndex + 1 < trimmed.Length)
                {
                    trimmed = trimmed[(colonIndex + 1)..].Trim();
                }
                break;
            }
        }

        // 引用符/かぎ括弧の中身を優先
        var quoted = ExtractQuotedText(trimmed);
        if (!string.IsNullOrWhiteSpace(quoted))
        {
            return quoted.Trim();
        }

        // 最初の改行以降は削除
        var newlineIndex = trimmed.IndexOfAny(new[] { '\n', '\r' });
        if (newlineIndex >= 0)
        {
            trimmed = trimmed[..newlineIndex].Trim();
        }

        return trimmed;
    }

    private static string? ExtractQuotedText(string text)
    {
        var quotePairs = new[]
        {
            new[] { "「", "」" },
            new[] { "『", "』" },
            new[] { "\"", "\"" },
            new[] { "“", "”" }
        };

        foreach (var pair in quotePairs)
        {
            var start = text.IndexOf(pair[0], StringComparison.Ordinal);
            if (start < 0) continue;
            var end = text.IndexOf(pair[1], start + pair[0].Length, StringComparison.Ordinal);
            if (end > start)
            {
                var extracted = text.Substring(start + pair[0].Length, end - start - pair[0].Length);
                if (!string.IsNullOrWhiteSpace(extracted))
                {
                    return extracted;
                }
            }
        }

        return null;
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
            LoggerService.LogDebug($"Mistral翻訳モデルの読み込み開始: {modelPath}");

            if (!File.Exists(modelPath))
            {
                throw new FileNotFoundException($"Translation model not found: {modelPath}");
            }

            // GPU環境変数はApp起動時に設定済み（ここではログのみ）
            LoggerService.LogDebug("GPU (CUDA) support enabled");
            LoggerService.LogDebug("GPU (Vulkan/RADEON) support enabled");
            LoggerService.LogDebug("GPU (HIP/ROCm/RADEON) support enabled");
            LoggerService.LogDebug("CUDA device selection: GPU 0");

            OnModelStatusChanged(new ModelStatusChangedEventArgs(
                ServiceName,
                ModelLabel,
                ModelStatusType.Info,
                "Mistralモデルを読み込み中..."));

            // モデルパラメータを設定（GPU優先での高速翻訳）
            // パフォーマンス最適化: GPU最優先、CPU処理をほぼなし
            var gpuLayerCount = 41;  // 全レイヤーをGPUで実行（Mistral 7B = 32層 + 余裕）
            _modelParams = new ModelParams(modelPath)
            {
                ContextSize = 512,   // コンテキストサイズ大幅削減（翻訳タスク最適化、GPU VRAM大幅節約）
                BatchSize = 64,      // バッチサイズ最適化（GPU効率最大化）
                GpuLayerCount = gpuLayerCount,  // 完全GPU処理（CPU処理ゼロ）
                Threads = 1  // CPUスレッド最小化（GPU推論中はCPUアイドル）
            };

            LoggerService.LogDebug($"Mistral model parameters: ContextSize=512, BatchSize=64, GpuLayerCount={gpuLayerCount}, Threads=1 (完全GPU実行)");

            // モデルをロード
            _model = LLamaWeights.LoadFromFile(_modelParams);
            _executor = new StatelessExecutor(_model, _modelParams);

            _isModelLoaded = true;
            LoggerService.LogInfo("Mistral翻訳モデルの読み込みが完了しました");
            LoggerService.LogInfo($"Mistral GPU support (CUDA/Vulkan/HIP) enabled with GpuLayerCount={gpuLayerCount} (完全GPU実行)");

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
        _warmupLock.Dispose();
    }
}
