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
    /// LLMを使用して翻訳
    /// </summary>
    private async Task<string> TranslateWithMistralAsync(string text, string sourceLanguage, string targetLanguage)
    {
        if (!_isModelLoaded || _model == null)
        {
            LoggerService.LogError("[Translation] モデルが読み込まれていません");
            return text;
        }

        try
        {
            // 言語名を取得
            var sourceLangName = GetLanguageName(sourceLanguage);
            var targetLangName = GetLanguageName(targetLanguage);

            // モデルタイプに応じたプロンプトを作成
            var prompt = BuildPrompt(text, sourceLangName, targetLangName);
            var antiPrompts = GetAntiPrompts();

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

            // 結果のクリーンアップ
            result = CleanupTranslationResult(result);

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
    /// モデルタイプに応じたプロンプトを生成
    /// </summary>
    private string BuildPrompt(string text, string sourceLangName, string targetLangName)
    {
        return _detectedModelType switch
        {
            TranslationModelType.Phi3 => BuildPhi3Prompt(text, sourceLangName, targetLangName),
            TranslationModelType.Gemma => BuildGemmaPrompt(text, sourceLangName, targetLangName),
            TranslationModelType.Qwen => BuildQwenPrompt(text, sourceLangName, targetLangName),
            TranslationModelType.Mistral => BuildMistralPrompt(text, sourceLangName, targetLangName),
            _ => BuildPhi3Prompt(text, sourceLangName, targetLangName)
        };
    }

    /// <summary>
    /// Phi-3形式のプロンプト（シンプルで高品質）
    /// </summary>
    private static string BuildPhi3Prompt(string text, string sourceLangName, string targetLangName)
    {
        return $"<|user|>\nTranslate this {sourceLangName} text to {targetLangName}. Reply with ONLY the translation, no explanations.\n\n{text}<|end|>\n<|assistant|>\n";
    }

    /// <summary>
    /// Gemma形式のプロンプト
    /// </summary>
    private static string BuildGemmaPrompt(string text, string sourceLangName, string targetLangName)
    {
        return $"<start_of_turn>user\nTranslate this {sourceLangName} text to {targetLangName}. Reply with ONLY the translation.\n\n{text}<end_of_turn>\n<start_of_turn>model\n";
    }

    /// <summary>
    /// Qwen形式のプロンプト
    /// </summary>
    private static string BuildQwenPrompt(string text, string sourceLangName, string targetLangName)
    {
        return $"<|im_start|>user\nTranslate this {sourceLangName} text to {targetLangName}. Reply with ONLY the translation.\n\n{text}<|im_end|>\n<|im_start|>assistant\n";
    }

    /// <summary>
    /// Mistral形式のプロンプト
    /// </summary>
    private static string BuildMistralPrompt(string text, string sourceLangName, string targetLangName)
    {
        return $"<s>[INST] Translate this {sourceLangName} text to {targetLangName}. Reply with ONLY the translation, no explanations or notes.\n\n{text} [/INST] ";
    }

    /// <summary>
    /// モデルタイプに応じたアンチプロンプト（早期終了用）を取得
    /// </summary>
    private List<string> GetAntiPrompts()
    {
        var common = new List<string> { "\n\n", "Note:", "Explanation:", "Translation:", "Original:" };

        return _detectedModelType switch
        {
            TranslationModelType.Phi3 => new List<string>(common) { "<|end|>", "<|user|>", "<|assistant|>" },
            TranslationModelType.Gemma => new List<string>(common) { "<end_of_turn>", "<start_of_turn>" },
            TranslationModelType.Qwen => new List<string>(common) { "<|im_end|>", "<|im_start|>" },
            TranslationModelType.Mistral => new List<string>(common) { "</s>", "[INST]", "[/INST]" },
            _ => new List<string>(common) { "<|end|>", "</s>" }
        };
    }

    /// <summary>
    /// 生成を早期終了すべきか判定
    /// </summary>
    private static bool ShouldStopGeneration(string currentText, string targetLanguage)
    {
        if (string.IsNullOrEmpty(currentText)) return false;

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
    /// 翻訳結果をクリーンアップ
    /// </summary>
    private string CleanupTranslationResult(string result)
    {
        if (string.IsNullOrWhiteSpace(result))
            return result;

        // モデル固有のトークンを削除
        result = RemoveModelTokens(result);

        result = ExtractTranslationOnly(result);

        // 英語の括弧内を削除（ローマ字や説明が入っていることが多い）
        // ただし日本語の括弧は保持
        result = RemoveEnglishParentheses(result);

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

    /// <summary>
    /// モデル固有のトークンを削除
    /// </summary>
    private static string RemoveModelTokens(string result)
    {
        // 共通のトークン
        result = result.Replace("</s>", "")
                       .Replace("<s>", "")
                       .Replace("<unk>", "")
                       .Replace("<pad>", "");

        // Phi-3トークン
        result = result.Replace("<|end|>", "")
                       .Replace("<|user|>", "")
                       .Replace("<|assistant|>", "")
                       .Replace("<|system|>", "");

        // Gemmaトークン
        result = result.Replace("<start_of_turn>", "")
                       .Replace("<end_of_turn>", "");

        // Qwenトークン
        result = result.Replace("<|im_start|>", "")
                       .Replace("<|im_end|>", "");

        // Mistralトークン
        result = result.Replace("[INST]", "")
                       .Replace("[/INST]", "");

        return result;
    }

    /// <summary>
    /// 英語の括弧内を削除（日本語の括弧は保持）
    /// </summary>
    private static string RemoveEnglishParentheses(string result)
    {
        // 英語の丸括弧内に英字が含まれている場合のみ削除
        while (true)
        {
            var parenStartIndex = result.IndexOf('(');
            if (parenStartIndex < 0)
                break;

            var parenEndIndex = result.IndexOf(')', parenStartIndex);
            if (parenEndIndex <= parenStartIndex)
                break;

            var content = result.Substring(parenStartIndex + 1, parenEndIndex - parenStartIndex - 1);

            // 括弧内に英字が多く含まれている場合は削除
            var englishCount = content.Count(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'));
            if (englishCount > content.Length * 0.5)
            {
                result = (result[..parenStartIndex] + result[(parenEndIndex + 1)..]).Trim();
            }
            else
            {
                // 日本語の括弧は保持するためbreakして次へ
                break;
            }
        }
        return result;
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
            LoggerService.LogDebug($"{_modelLabel}の読み込み開始: {modelPath}");

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
            LoggerService.LogInfo($"{_modelLabel} GPU support (CUDA/Vulkan/HIP) enabled with GpuLayerCount={gpuLayerCount} (完全GPU実行)");

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
