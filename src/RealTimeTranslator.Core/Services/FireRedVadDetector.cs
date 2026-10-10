using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RealTimeTranslator.Core.Interfaces;
using SuperLightLogger;

namespace RealTimeTranslator.Core.Services;

/// <summary>
/// FireRedVAD streaming ONNX による音声活動検出。16 kHz／512 samples の既存入力契約を維持し、
/// 内部の 10 ms frame の発話確率の最大値を返す。発話区間の判定は共通の VAD gate に委ねる。
/// </summary>
public sealed class FireRedVadDetector : IVoiceActivityDetector
{
    private const int SampleRateValue = 16000;
    private const int CacheLayers = 8;
    private const int CacheChannels = 128;
    private const int CacheFrames = 19;
    private static readonly ILog Logger = LogManager.GetLogger<FireRedVadDetector>();
    private static readonly long[] s_cacheShape = [CacheLayers, 1, CacheChannels, CacheFrames];
    private static readonly string[] s_outputNames = ["probs", "caches_out"];
    private readonly InferenceSession _session;
    private readonly FireRedFbank _fbank;
    private readonly float[] _cache = new float[CacheLayers * CacheChannels * CacheFrames];
    private readonly float[] _features = new float[FireRedFbank.MaxFramesPerBlock * FireRedFbank.FeatureSize];
    private readonly long[] _featureShape = [1, 1, FireRedFbank.FeatureSize];
    private readonly Dictionary<string, OrtValue> _inputs = new(2);
    private readonly RunOptions _runOptions;
    private readonly System.Threading.Lock _lock = new();
    private bool _disposed;

    public int RequiredFrameSize => FireRedFbank.MaxInputSamples;
    public int SampleRate => SampleRateValue;

    /// <param name="modelPath">省略時は実行ディレクトリの Assets/fireredvad_stream_vad_with_cache.onnx。</param>
    /// <param name="cmvnPath">省略時はモデルと同じディレクトリの fireredvad_cmvn.ark。</param>
    public FireRedVadDetector(string? modelPath = null, string? cmvnPath = null)
    {
        string path = modelPath ?? Path.Combine(AppContext.BaseDirectory, "Assets", "fireredvad_stream_vad_with_cache.onnx");
        if (!File.Exists(path)) throw new FileNotFoundException($"FireRed VAD モデルが見つかりません: {path}", path);
        string statsPath = cmvnPath ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "fireredvad_cmvn.ark");
        _fbank = new FireRedFbank(statsPath);
        // ORT 1.31 の診断送信方式の変更に左右されず、VAD はローカル推論だけを行う。
        OrtEnv.Instance().DisableTelemetryEvents();
        using var options = new SessionOptions
        {
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };
        var session = new InferenceSession(path, options);
        try
        {
            ValidateMetadata(session);
            _runOptions = new RunOptions();
            _session = session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
        Logger.Info($"FireRedVadDetector 初期化完了: {path} (size={new FileInfo(path).Length} bytes)");
    }

    public float DetectSpeechProb(ReadOnlySpan<float> frame16kHz)
    {
        if (frame16kHz.Length != RequiredFrameSize)
        {
            throw new ArgumentException($"フレームサイズは {RequiredFrameSize} samples 固定です (受信: {frame16kHz.Length})。", nameof(frame16kHz));
        }
        lock (_lock)
        {
            if (_disposed) return 0f;
            int frameCount = _fbank.ExtractFrames(frame16kHz, _features);
            _featureShape[1] = frameCount;
            using var features = OrtValue.CreateTensorValueFromMemory<float>(OrtMemoryInfo.DefaultInstance,
                _features.AsMemory(0, frameCount * FireRedFbank.FeatureSize), _featureShape);
            using var cache = OrtValue.CreateTensorValueFromMemory<float>(OrtMemoryInfo.DefaultInstance, _cache, s_cacheShape);
            _inputs["feat"] = features;
            _inputs["caches_in"] = cache;
            using var results = _session.Run(_runOptions, _inputs, s_outputNames);
            ValidateOutput(results[0], [1, frameCount, 1], "probs");
            ValidateOutput(results[1], s_cacheShape, "caches_out");
            var probabilities = results[0].GetTensorDataAsSpan<float>();
            float maximum = 0f;
            foreach (float probability in probabilities)
            {
                if (!float.IsFinite(probability) || probability < 0f || probability > 1f)
                {
                    throw new InvalidDataException("FireRed VAD が 0〜1 の有限値ではない発話確率を返しました。");
                }
                maximum = MathF.Max(maximum, probability);
            }
            var updatedCache = results[1].GetTensorDataAsSpan<float>();
            foreach (float value in updatedCache)
            {
                if (!float.IsFinite(value)) throw new InvalidDataException("FireRed VAD の cache に非有限値が含まれています。");
            }
            updatedCache.CopyTo(_cache);
            return maximum;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _fbank.Reset();
            Array.Clear(_cache);
            Array.Clear(_features);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _inputs.Clear();
            _runOptions.Dispose();
            _session.Dispose();
        }
    }

    private static void ValidateMetadata(InferenceSession session)
    {
        if (session.InputMetadata.Count != 2 || session.OutputMetadata.Count != 2)
        {
            throw new InvalidDataException("FireRed VAD モデルの入出力数が不一致です。");
        }
        ValidateNode(session.InputMetadata, "feat", [1, -1, FireRedFbank.FeatureSize]);
        ValidateNode(session.InputMetadata, "caches_in", [CacheLayers, 1, CacheChannels, CacheFrames]);
        ValidateNode(session.OutputMetadata, "probs", [1, -1, 1]);
        // export 時は cache の末尾長が動的になる。実際の推論出力は必ず 19 と照合する。
        ValidateNode(session.OutputMetadata, "caches_out", [CacheLayers, 1, CacheChannels, -1]);
    }

    private static void ValidateNode(IReadOnlyDictionary<string, NodeMetadata> nodes, string name, ReadOnlySpan<int> shape)
    {
        if (!nodes.TryGetValue(name, out var node) || !node.IsTensor || node.ElementType != typeof(float)
            || node.Dimensions.Length != shape.Length)
        {
            throw new InvalidDataException($"FireRed VAD モデルの '{name}' が float32 tensor／期待 rank と一致しません。");
        }
        for (int i = 0; i < shape.Length; i++)
        {
            if ((shape[i] > 0 && node.Dimensions[i] != shape[i]) || (shape[i] < 0 && node.Dimensions[i] > 0))
            {
                throw new InvalidDataException($"FireRed VAD モデルの '{name}' の shape が不一致です: [{string.Join(",", node.Dimensions)}]");
            }
        }
    }

    private static void ValidateOutput(OrtValue value, ReadOnlySpan<long> shape, string name)
    {
        var info = value.GetTensorTypeAndShape();
        if (info.ElementDataType != TensorElementType.Float || !info.Shape.AsSpan().SequenceEqual(shape))
        {
            throw new InvalidDataException($"FireRed VAD の '{name}' 出力 shape または型が不一致です: [{string.Join(",", info.Shape)}]");
        }
    }
}
