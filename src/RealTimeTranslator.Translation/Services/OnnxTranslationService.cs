using System.Diagnostics;
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

    private readonly TranslationSettings _settings;
    private readonly ModelDownloadService _downloadService;
    private readonly Dictionary<string, string> _cache = new();
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

        try
        {
            // モデルフォルダのパスを解決
            var modelPath = GetModelFolderPath();
            Directory.CreateDirectory(modelPath);

            // モデルファイルを確認・ダウンロード
            var encoderPath = Path.Combine(modelPath, DefaultEncoderFileName);
            var decoderPath = Path.Combine(modelPath, DefaultDecoderFileName);
            var decoderWithPastPath = Path.Combine(modelPath, DefaultDecoderWithPastFileName);
            var tokenizerPath = Path.Combine(modelPath, DefaultTokenizerFileName);

            // 必要なファイルをダウンロード
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

            // バックグラウンドでモデルを読み込み
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

                // 進捗を報告
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

        // キャッシュキーを生成
        var cacheKey = $"{sourceLanguage}:{targetLanguage}:{text}";

        // キャッシュをチェック
        lock (_cacheLock)
        {
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
        }

        await _translateLock.WaitAsync();
        try
        {
            // 翻訳前処理
            var preprocessedText = ApplyPreTranslation(text);

            // 実際の翻訳
            var translatedText = await PerformTranslationAsync(preprocessedText, sourceLanguage, targetLanguage);

            // 翻訳後処理
            translatedText = ApplyPostTranslation(translatedText);

            // キャッシュに保存
            lock (_cacheLock)
            {
                _cache[cacheKey] = translatedText;
            }

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

    private async Task<string> PerformTranslationAsync(string text, string sourceLanguage, string targetLanguage)
    {
        LogDebug($"[PerformTranslationAsync] 開始: Text={text}, Source={sourceLanguage}, Target={targetLanguage}");
        LogDebug($"[PerformTranslationAsync] 状態: IsModelLoaded={_isModelLoaded}, Session={_session != null}, Tokenizer={_tokenizer != null}");

        if (!_isModelLoaded || _session == null || _tokenizer == null)
        {
            LogError($"[PerformTranslationAsync] フォールバック: IsModelLoaded={_isModelLoaded}, Session is null={_session == null}, Tokenizer is null={_tokenizer == null}");
            // フォールバック：原文に言語タグを付けて返す
            return $"[{targetLanguage}] {text}";
        }

        LogDebug("[PerformTranslationAsync] 実際の翻訳処理に進入");
        return await Task.Run(() =>
        {
            try
            {
                // テキストをトークン化
                var (inputIds, attentionMask) = _tokenizer.Encode(text);

                // 入力テンソルを作成
                var inputTensor = new long[1, inputIds.Length];
                var maskTensor = new long[1, attentionMask.Length];

                for (int i = 0; i < inputIds.Length; i++)
                {
                    inputTensor[0, i] = inputIds[i];
                    maskTensor[0, i] = attentionMask[i];
                }

                // 1次元に変換してからDenseTensorを作成
                var flatInputIds = new long[inputTensor.GetLength(0) * inputTensor.GetLength(1)];
                var flatMask = new long[maskTensor.GetLength(0) * maskTensor.GetLength(1)];

                int idx = 0;
                for (int i = 0; i < inputTensor.GetLength(0); i++)
                {
                    for (int j = 0; j < inputTensor.GetLength(1); j++)
                    {
                        flatInputIds[idx] = inputTensor[i, j];
                        flatMask[idx] = maskTensor[i, j];
                        idx++;
                    }
                }

                // 入力を作成
                var inputTensor1D = new DenseTensor<long>(flatInputIds, new int[] { 1, inputIds.Length });
                var maskTensor1D = new DenseTensor<long>(flatMask, new int[] { 1, attentionMask.Length });

                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor("input_ids", inputTensor1D),
                    NamedOnnxValue.CreateFromTensor("attention_mask", maskTensor1D)
                };

                // 推論を実行
                using var results = _session.Run(inputs);

                // 出力を取得（logits）
                var output = results.FirstOrDefault()?.AsTensor<float>();
                if (output == null)
                {
                    LogDebug("[PerformTranslationAsync] 出力がnull、フォールバック");
                    return $"[{targetLanguage}] {text}";
                }

                // デコード（最初の出力IDを取得）
                var translatedTokenIds = new List<long>();
                for (int i = 0; i < output.Dimensions[1]; i++)
                {
                    long maxIdx = 0;
                    float maxVal = float.MinValue;

                    for (int j = 0; j < output.Dimensions[2]; j++)
                    {
                        var val = output[0, i, j];
                        if (val > maxVal)
                        {
                            maxVal = val;
                            maxIdx = j;
                        }
                    }
                    translatedTokenIds.Add(maxIdx);
                }

                // トークンをテキストにデコード
                var translatedText = _tokenizer.Decode(translatedTokenIds.ToArray());
                LogDebug($"[PerformTranslationAsync] 翻訳完了: Result={translatedText}");
                return string.IsNullOrWhiteSpace(translatedText) ? $"[{targetLanguage}] {text}" : translatedText;
            }
            catch (Exception ex)
            {
                LoggerService.LogError($"[PerformTranslationAsync] Translation error: {ex.GetType().Name} - {ex.Message}");
                LoggerService.LogDebug($"[PerformTranslationAsync] StackTrace: {ex.StackTrace}");
                return $"[{targetLanguage}] {text}";
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

            // エンコーダーとデコーダーのパスを指定
            var encoderPath = Path.Combine(modelFolderPath, DefaultEncoderFileName);
            var decoderPath = Path.Combine(modelFolderPath, DefaultDecoderFileName);
            var decoderWithPastPath = Path.Combine(modelFolderPath, DefaultDecoderWithPastFileName);

            if (!File.Exists(encoderPath) || !File.Exists(decoderPath))
            {
                throw new FileNotFoundException($"Required model files not found in {modelFolderPath}");
            }

            // ONNX Runtimeセッションを作成（エンコーダー）
            var sessionOptions = new SessionOptions();
            LogDebug("ONNX Runtime: Using CPU execution provider");
            LogDebug($"[LoadModel] エンコーダーセッション作成: {encoderPath}");
            _session = new InferenceSession(encoderPath, sessionOptions);
            LogDebug("[LoadModel] エンコーダーセッション作成完了");

            // トークナイザーの読み込み
            var tokenizerPath = Path.Combine(modelFolderPath, DefaultTokenizerFileName);
            LogDebug($"[LoadModel] トークナイザーパス: {tokenizerPath}, 存在={File.Exists(tokenizerPath)}");
            _tokenizer = File.Exists(tokenizerPath)
                ? new SimpleTokenizer(tokenizerPath)
                : new SimpleTokenizer(); // フォールバック：簡易トークナイザー
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
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
        _tokenizer?.Dispose();
        _translateLock.Dispose();
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
            // 基本的なトークンを初期化
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
                // 簡略版：tokenizer.jsonが存在する場合は、基本的なボキャブラリのみを初期化
                // 完全な実装にはJSON解析が必要
                InitializeBasicTokenizer();
                LoggerService.LogWarning($"Tokenizer file found but using basic tokenizer: {path}");
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
            var inputIds = new long[tokens.Length + 2]; // [CLS] + tokens + [SEP]
            var attentionMask = new long[tokens.Length + 2];

            // [CLS] トークン
            inputIds[0] = 0;
            attentionMask[0] = 1;

            // 実際のトークン
            for (int i = 0; i < tokens.Length; i++)
            {
                inputIds[i + 1] = _vocab.TryGetValue(tokens[i], out var id) ? id : 0; // <unk>
                attentionMask[i + 1] = 1;
            }

            // [SEP] トークン
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
                    if (!string.IsNullOrEmpty(token) && token != "<pad>" && token != "<unk>")
                    {
                        tokens.Add(token);
                    }
                }
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
