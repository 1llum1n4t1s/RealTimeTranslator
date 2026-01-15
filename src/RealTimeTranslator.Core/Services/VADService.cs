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
    private const string ModelDownloadUrl = "https://github.com/snakers4/silero-vad/raw/master/files/silero_vad.onnx";

    // Silero VAD v4 は以下のチャンクサイズ(16kHz時)をサポート: 512, 1024, 1536
    private const int WindowSizeSamples = 512;
    private const int SampleRate = 16000;

    private readonly AudioCaptureSettings _settings;
    private readonly ModelDownloadService _downloadService;
    private readonly object _lock = new();

    // VADの状態
    private InferenceSession? _session;
    private bool _isModelLoaded = false;
    private readonly List<float> _audioBuffer = new();        // 入力バッファ（処理待ちデータ）
    private readonly List<float> _speechBuffer = new();       // 現在の発話データ蓄積用
    private float[] _vadState = new float[2 * 1 * 128];       // VADの内部ステート (2, batch, 128)
    private bool _isSpeaking = false;
    private float _speechStartTimestamp = 0;
    private float _currentTimestamp = 0;
    private float _silenceDuration = 0;

    // パラメータ
    private float _threshold = 0.5f;

    /// <summary>
    /// VADの感度（0.0～1.0）
    /// 感度が高いほど声を敏感に検出する
    /// </summary>
    public float Sensitivity
    {
        get => _threshold;
        set => _threshold = Math.Clamp(1.0f - value, 0.1f, 0.9f); // 感度が高いほど閾値を下げる
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
    public VADService(AudioCaptureSettings settings, ModelDownloadService downloadService)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _downloadService = downloadService ?? throw new ArgumentNullException(nameof(downloadService));
        ApplySettings(_settings);

        // モデルの準備（非同期で開始）
        Task.Run(InitializeModelAsync);
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
            var resolvedPath = await _downloadService.EnsureModelAsync(
                modelPath,
                ModelFileName,
                ModelDownloadUrl,
                "VAD",
                "Silero VAD Model"
            );

            if (!string.IsNullOrEmpty(resolvedPath) && File.Exists(resolvedPath))
            {
                lock (_lock)
                {
                    var options = new SessionOptions();
                    options.RegisterOrtExtensions(); // 必要に応じて
                    options.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR;
                    _session = new InferenceSession(resolvedPath, options);
                    _isModelLoaded = true;
                    LoggerService.LogInfo("Silero VAD model loaded successfully.");
                    ResetState();
                }
            }
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"Failed to initialize Silero VAD model: {ex.Message}");
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

                // 時間更新 (16kHzで512サンプル = 32ms)
                float chunkDuration = (float)WindowSizeSamples / SampleRate;
                _currentTimestamp += chunkDuration;

                // 判定ロジック
                if (prob >= _threshold)
                {
                    // 発話検出
                    if (!_isSpeaking)
                    {
                        _isSpeaking = true;
                        _speechStartTimestamp = _currentTimestamp - chunkDuration;
                        _speechBuffer.Clear();
                        LoggerService.LogDebug($"[VAD] Speech started (Prob: {prob:F2})");
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
                        _silenceDuration += chunkDuration;

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
                    float currentDuration = _currentTimestamp - _speechStartTimestamp;
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

        float duration = (float)_speechBuffer.Count / SampleRate;

        // 最小発話長チェック
        if (duration < MinSpeechDuration)
        {
            _speechBuffer.Clear();
            return null;
        }

        LoggerService.LogDebug($"[VAD] Segment finalized: {duration:F2}s");

        var segment = new SpeechSegment
        {
            StartTime = _speechStartTimestamp,
            EndTime = _currentTimestamp,
            AudioData = _speechBuffer.ToArray()
        };

        _speechBuffer.Clear();
        return segment;
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
        _isSpeaking = false;
        _silenceDuration = 0;
    }

    /// <summary>
    /// リソースを破棄
    /// </summary>
    public void Dispose()
    {
        _session?.Dispose();
    }
}
