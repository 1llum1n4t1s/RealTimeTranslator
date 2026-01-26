using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.Core.Services;

/// <summary>
/// Silero VAD (ONNX) を使用した発話区間検出サービス
/// AIモデルにより、ノイズやBGMと人の声を高精度に識別する
/// </summary>
public class VADService : IVADService, IDisposable
{
    private const string ModelFileName = "silero_vad.onnx";
    private const string ModelDownloadUrl = "https://github.com/snakers4/silero-vad/raw/master/src/silero_vad/data/silero_vad.onnx";

    // Silero VAD v4 は以下のチャンクサイズ(16kHz時)をサポート: 512, 1024, 1536
    private const int WindowSizeSamples = 512;
    private const int SampleRate = 16000;
    // 平滑化に使う履歴数
    private const int ProbHistorySize = 12;

    private AudioCaptureSettings _settings;
    private readonly ModelDownloadService _downloadService;
    private readonly IDisposable? _settingsChangeSubscription;
    private readonly object _lock = new();

    // VADの状態
    private InferenceSession? _session;
    private bool _isModelLoaded = false;
    private Task? _modelInitializationTask;
    private bool _modelLoadWarningLogged = false;
    private readonly List<float> _audioBuffer = new();        // 入力バッファ（処理待ちデータ）
    private readonly List<float> _speechBuffer = new();       // 現在の発話データ蓄積用
    private float[] _vadState = new float[2 * 1 * 128];       // VADの内部ステート (2, batch, 128)
    private bool _isSpeaking = false;
    private double _speechStartTimestamp = 0;
    private double _currentTimestamp = 0;
    private double _silenceDuration = 0;
    private readonly Queue<float> _probHistory = new();       // VAD確率の履歴
    private float _probHistorySum = 0f;                       // VAD確率の合計（平均用）
    private int _inferenceCount = 0;                           // 推論回数（ログ用）

    // パラメータ
    private float _threshold = 0.5f;
    // 継続判定の閾値スケール
    private const float ContinueThresholdScale = 0.5f;
    // 継続判定の最小閾値
    private const float MinContinueThreshold = 0.002f;
    // 近接発話判定のスケール（無音カウント抑制）
    private const float NearSpeechScale = 0.5f;

    /// <summary>
    /// VADの感度（0.0～1.0）
    /// 感度が高いほど声を敏感に検出する
    /// </summary>
    public float Sensitivity
    {
        get => 1.0f - _threshold;
        set => _threshold = Math.Clamp(1.0f - value, 0.001f, 0.99f); // 感度が高いほど閾値を下げる
    }

    /// <summary>
    /// 最小発話長（秒）
    /// </summary>
    public float MinSpeechDuration { get; set; }

    /// <summary>
    /// 最大発話長（秒）
    /// </summary>
    public float MaxSpeechDuration { get; set; }

    /// <summary>
    /// VADサービスのコンストラクタ
    /// </summary>
    public VADService(IOptionsMonitor<AppSettings> optionsMonitor, ModelDownloadService downloadService)
    {
        _downloadService = downloadService ?? throw new ArgumentNullException(nameof(downloadService));

        // 現在の設定値を取得
        _settings = optionsMonitor.CurrentValue.AudioCapture;

        // 設定変更（ファイル保存時）のイベントを購読
        _settingsChangeSubscription = optionsMonitor.OnChange(newSettings =>
        {
            LoggerService.LogInfo("Settings updated detected in VADService.");
            ApplySettings(newSettings.AudioCapture);
        });

        ApplySettings(_settings);

        // モデルの準備（非同期で開始）
        _modelInitializationTask = Task.Run(InitializeModelAsync);
    }

    /// <summary>
    /// モデルのロード完了を待機
    /// </summary>
    public async Task EnsureModelLoadedAsync()
    {
        if (_modelInitializationTask != null)
        {
            await _modelInitializationTask.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 設定を再適用
    /// </summary>
    public void ApplySettings(AudioCaptureSettings settings)
    {
        lock (_lock)
        {
            // 感度(0.0-1.0)を閾値(1.0-0.0)に変換。デフォルト感度0.5なら閾値0.5
            Sensitivity = settings.VADSensitivity;
            MinSpeechDuration = settings.MinSpeechDuration;
            MaxSpeechDuration = settings.MaxSpeechDuration;
        }
    }

    /// <summary>
    /// モデルの非同期初期化
    /// </summary>
    private async Task InitializeModelAsync()
    {
        try
        {
            // モデルファイルのパス解決とダウンロード
            var modelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models", "vad");
            LoggerService.LogDebug($"[VAD] InitializeModelAsync: modelPath={modelPath}");
            
            var resolvedPath = await _downloadService.EnsureModelAsync(
                modelPath,
                ModelFileName,
                ModelDownloadUrl,
                "VAD",
                "Silero VAD Model"
            );

            LoggerService.LogDebug($"[VAD] EnsureModelAsync returned: {resolvedPath ?? "null"}");

            if (!string.IsNullOrEmpty(resolvedPath) && File.Exists(resolvedPath))
            {
                LoggerService.LogDebug($"[VAD] Model file exists, loading session...");
                lock (_lock)
                {
                    var options = new SessionOptions();
                    options.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR;
                    
                    // GPU プロバイダーを追加（CUDA → DirectML → CPU の順でフォールバック）
                    var gpuProviderAdded = TryAddCudaProvider(options) || TryAddDirectMLProvider(options);
                    
                    if (!gpuProviderAdded)
                    {
                        LoggerService.LogInfo("[VAD] No GPU provider available, using CPU execution provider");
                    }
                    
                    LoggerService.LogDebug($"[VAD] Creating InferenceSession with model: {resolvedPath}");
                    try
                    {
                        _session = new InferenceSession(resolvedPath, options);
                    }
                    catch (Exception sessionEx)
                    {
                        LoggerService.LogError($"[VAD] InferenceSession creation failed: {sessionEx.GetType().Name} - {sessionEx.Message}");
                        LoggerService.LogDebug($"[VAD] InferenceSession StackTrace: {sessionEx.StackTrace}");
                        throw;
                    }
                    _isModelLoaded = true;
                    _modelLoadWarningLogged = false;
                    LoggerService.LogInfo("Silero VAD model loaded successfully (GPU acceleration enabled if available).");
                    ResetState();
                }
            }
            else
            {
                LoggerService.LogError($"[VAD] Model file not available. resolvedPath={resolvedPath ?? "null"}, exists={resolvedPath != null && File.Exists(resolvedPath)}");
            }
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"Failed to initialize Silero VAD model: {ex.Message}");
            LoggerService.LogDebug($"[VAD] Exception StackTrace: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// 音声データを処理し、発話区間を検出
    /// </summary>
    public IEnumerable<SpeechSegment> DetectSpeech(float[] audioData)
    {
        if (audioData == null || audioData.Length == 0)
            yield break;

        lock (_lock)
        {
            // モデル未ロードの警告（1回のみ）
            if (!_isModelLoaded && !_modelLoadWarningLogged)
            {
                LoggerService.LogWarning("[VAD] VADモデルがまだロードされていません。音声検出を開始できません。");
                _modelLoadWarningLogged = true;
            }

            if (!_isModelLoaded)
                yield break;

            // 入力データをバッファに追加
            _audioBuffer.AddRange(audioData);

            // WindowSizeSamples (512サンプル) 単位で処理
            while (_audioBuffer.Count >= WindowSizeSamples)
            {
                // バッファから切り出し
                var chunk = _audioBuffer.GetRange(0, WindowSizeSamples).ToArray();
                _audioBuffer.RemoveRange(0, WindowSizeSamples);

                // 推論実行
                float prob = RunInference(chunk);
                float smoothedProb = UpdateSmoothedProbability(prob);

                // 時間更新 (16kHzで512サンプル = 32ms)
                double chunkDuration = (double)WindowSizeSamples / SampleRate;
                _currentTimestamp += chunkDuration;

                // デバッグ：確率値のログ出力（定期的に）
                // 100回に1回、または確率が0.01以上の場合にログ出力
                _inferenceCount++;
                if (_inferenceCount % 100 == 0 || smoothedProb >= 0.01f)
                {
                    // ログ出力用の実効閾値
                    var effectiveThreshold = _isSpeaking ? GetContinueThreshold() : _threshold;
                    LoggerService.LogDebug($"[VAD] Inference #{_inferenceCount}: prob={prob:F3}, smoothed={smoothedProb:F3}, threshold={effectiveThreshold:F3}, speaking={_isSpeaking}");
                }

                // 判定ロジック
                // 発話判定に使う実効閾値
                var speechThreshold = _isSpeaking ? GetContinueThreshold() : _threshold;
                if (smoothedProb >= speechThreshold)
                {
                    // 発話検出
                    if (!_isSpeaking)
                    {
                        _isSpeaking = true;
                        _speechStartTimestamp = _currentTimestamp - chunkDuration;
                        _speechBuffer.Clear();
                        LoggerService.LogDebug($"[VAD] Speech started (Prob: {smoothedProb:F2})");
                    }

                    _speechBuffer.AddRange(chunk);
                    _silenceDuration = 0;
                }
                else
                {
                    // 非発話
                    if (_isSpeaking)
                    {
                        _speechBuffer.AddRange(chunk);
                        if (smoothedProb < speechThreshold * NearSpeechScale)
                        {
                            _silenceDuration += chunkDuration;
                        }

                        // 無音が一定時間続いたら発話終了とみなす
                        if (_silenceDuration > _settings.SilenceThreshold)
                        {
                            var segment = FinalizeSegment();
                            if (segment != null)
                                yield return segment;
                        }
                    }
                }

                // 最大発話長チェック
                if (_isSpeaking)
                {
                    double currentDuration = _currentTimestamp - _speechStartTimestamp;
                    if (currentDuration >= MaxSpeechDuration)
                    {
                        var segment = FinalizeSegment();
                        if (segment != null)
                            yield return segment;
                    }
                }
            }
        }
    }

    /// <summary>
    /// VADの推論を実行
    /// </summary>
    private float RunInference(float[] chunk)
    {
        if (!_isModelLoaded || _session == null)
            return 0f;

        try
        {
            // 音声データの振幅を確認（デバッグ用）
            if (_inferenceCount % 500 == 0)
            {
                float maxAmp = chunk.Max(Math.Abs);
                float avgAmp = chunk.Average(Math.Abs);
                LoggerService.LogDebug($"[VAD] Chunk amplitude check: max={maxAmp:F3}, avg={avgAmp:F3}, samples={chunk.Length}");
            }
            
            // Input tensor: [batch, samples] -> [1, 512]
            var inputTensor = new DenseTensor<float>(new Memory<float>(chunk), new[] { 1, WindowSizeSamples });

            // State tensor: [2, batch, 128] -> [2, 1, 128]
            var stateTensor = new DenseTensor<float>(new Memory<float>(_vadState), new[] { 2, 1, 128 });

            // SR tensor: [1] (Sample Rate)
            var srTensor = new DenseTensor<long>(new Memory<long>(new[] { (long)SampleRate }), new[] { 1 });

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input", inputTensor),
                NamedOnnxValue.CreateFromTensor("state", stateTensor),
                NamedOnnxValue.CreateFromTensor("sr", srTensor)
            };

            using var results = _session.Run(inputs);

            // Output: probability
            var outputTensor = results.First(x => x.Name == "output").AsTensor<float>();
            float probability = outputTensor[0];

            // Update State
            var stateOutput = results.First(x => x.Name == "stateN").AsTensor<float>();
            var stateSpan = stateOutput.ToArray();
            Array.Copy(stateSpan, _vadState, _vadState.Length);

            return probability;
        }
        catch (Exception ex)
        {
            LoggerService.LogDebug($"VAD Inference Error: {ex.Message}");
            return 0f;
        }
    }

    /// <summary>
    /// 現在の発話セグメントを確定して返す
    /// </summary>
    private SpeechSegment? FinalizeSegment()
    {
        _isSpeaking = false;
        _silenceDuration = 0;

        if (_speechBuffer.Count == 0)
            return null;

        double duration = (double)_speechBuffer.Count / SampleRate;

        // 最小発話長チェック
        if (duration < MinSpeechDuration)
        {
            LoggerService.LogDebug($"[VAD] Segment discarded (too short): {duration:F2}s < {MinSpeechDuration:F2}s");
            _speechBuffer.Clear();
            return null;
        }

        LoggerService.LogDebug($"[VAD] Segment finalized: {duration:F2}s");

        var segment = new SpeechSegment
        {
            StartTime = (float)_speechStartTimestamp,
            EndTime = (float)_currentTimestamp,
            AudioData = _speechBuffer.ToArray()
        };

        _speechBuffer.Clear();
        return segment;
    }

    /// <summary>
    /// 継続判定用の閾値を取得
    /// </summary>
    private float GetContinueThreshold()
    {
        return Math.Max(_threshold * ContinueThresholdScale, MinContinueThreshold);
    }

    /// <summary>
    /// VAD確率の移動平均を更新
    /// </summary>
    /// <param name="prob">最新の確率</param>
    /// <returns>平滑化後の確率</returns>
    private float UpdateSmoothedProbability(float prob)
    {
        _probHistory.Enqueue(prob);
        _probHistorySum += prob;
        while (_probHistory.Count > ProbHistorySize)
        {
            _probHistorySum -= _probHistory.Dequeue();
        }
        return _probHistory.Count == 0 ? 0f : _probHistorySum / _probHistory.Count;
    }

    /// <summary>
    /// 残留バッファを確定して返す
    /// </summary>
    public SpeechSegment? FlushPendingSegment()
    {
        lock (_lock)
        {
            return _isSpeaking ? FinalizeSegment() : null;
        }
    }

    /// <summary>
    /// 状態をリセット
    /// </summary>
    private void ResetState()
    {
        Array.Clear(_vadState, 0, _vadState.Length);
        _audioBuffer.Clear();
        _speechBuffer.Clear();
        _probHistory.Clear();
        _probHistorySum = 0f;
        _isSpeaking = false;
        _silenceDuration = 0;
    }

    /// <summary>
    /// CUDA プロバイダーを追加（NVIDIA GPU用）
    /// </summary>
    /// <param name="options">セッションオプション</param>
    /// <returns>追加成功した場合は true</returns>
    private static bool TryAddCudaProvider(SessionOptions options)
    {
        try
        {
            // ONNX Runtime 1.17+ の AppendExecutionProvider を使用
            // プロバイダー名を文字列で指定する方法
            options.AppendExecutionProvider_CUDA(0);
            LoggerService.LogInfo("[VAD] CUDA GPU provider added for Silero VAD (NVIDIA GPU)");
            return true;
        }
        catch (Exception ex)
        {
            LoggerService.LogDebug($"[VAD] CUDA provider not available: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// DirectML プロバイダーを追加（AMD/Intel GPU用）
    /// </summary>
    /// <param name="options">セッションオプション</param>
    /// <returns>追加成功した場合は true</returns>
    private static bool TryAddDirectMLProvider(SessionOptions options)
    {
        try
        {
            // ONNX Runtime 1.17+ の AppendExecutionProvider_DML を使用
            options.AppendExecutionProvider_DML(0);
            LoggerService.LogInfo("[VAD] DirectML GPU provider added for Silero VAD (AMD/Intel GPU)");
            return true;
        }
        catch (Exception ex)
        {
            LoggerService.LogDebug($"[VAD] DirectML provider not available: {ex.Message}");
            return false;
        }
    }

    private bool _disposed = false;

    /// <summary>
    /// リソースを破棄
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            _settingsChangeSubscription?.Dispose();
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"VADService.Dispose: Error disposing settings subscription: {ex.Message}");
        }

        try
        {
            _session?.Dispose();
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"VADService.Dispose: Error disposing ONNX session: {ex.Message}");
        }

        // モデル初期化タスクが完了するまで待機（タイムアウト付き、デッドロック回避）
        if (_modelInitializationTask != null && !_modelInitializationTask.IsCompleted)
        {
            try
            {
                var waitTask = Task.Run(async () => await _modelInitializationTask.ConfigureAwait(false));
                if (!waitTask.Wait(TimeSpan.FromSeconds(5)))
                {
                    LoggerService.LogWarning("VADService.Dispose: Model initialization task did not complete within timeout");
                }
            }
            catch (Exception ex)
            {
                LoggerService.LogError($"VADService.Dispose: Error waiting for model initialization: {ex.Message}");
            }
        }

        GC.SuppressFinalize(this);
    }
}
