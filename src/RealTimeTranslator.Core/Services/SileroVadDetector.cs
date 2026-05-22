using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RealTimeTranslator.Core.Interfaces;
using SuperLightLogger;

namespace RealTimeTranslator.Core.Services;

/// <summary>
/// Silero VAD v5 (ONNX) を使った <see cref="IVoiceActivityDetector"/> 実装。
/// 16kHz / 512 サンプル (32ms) 固定。 LSTM hidden state を内部で持ち、 連続フレームを
/// 順番に推論する。 推論は ~1ms (Intel i5 級 CPU) なので audio loop 上で問題なく回せる。
///
/// 公式モデル: snakers4/silero-vad (MIT License)。 v5 (16kHz 専用) 仕様:
/// - 入力: input[1, 512] (float32) / state[2, 1, 128] (float32) / sr[1] (int64=16000)
/// - 出力: output[1, 1] (float32 = speech_prob) / stateN[2, 1, 128] (float32)
/// </summary>
public sealed class SileroVadDetector : IVoiceActivityDetector
{
    private const int FrameSizeValue = 512;
    private const int SampleRateValue = 16000;
    private const int StateDim = 128;
    // 公式 silero-vad utils_vad.py の context_size: 16kHz では 64 サンプル。
    // 入力は (context 64 + 現在フレーム 512 = 576) サンプルを batch=1 で渡す必要がある。
    // 推論後、 入力末尾 64 サンプルを次回 context として保持する。
    private const int ContextSize = 64;
    private const int InputWithContextSize = ContextSize + FrameSizeValue; // 576

    private static readonly ILog Logger = LogManager.GetLogger<SileroVadDetector>();

    private readonly InferenceSession _session;
    private readonly float[] _stateBuffer; // [2, 1, 128] = 256 floats
    private readonly float[] _contextBuffer = new float[ContextSize]; // 直前フレームの末尾 64 サンプル
    // VAD ホットパス (32ms ごと) で毎フレーム new していた buffer を field に巻き上げて再利用。
    // OrtValue.CreateTensorValueFromMemory は参照保持なので、 using で破棄したあとは内容書換 OK。
    private readonly float[] _inputBuffer = new float[InputWithContextSize];
    private readonly long[] _srBuffer = [(long)SampleRateValue];
    // shape も毎フレーム new せず static に巻き上げ (alloc 削減 + JIT が定数畳み込み可能)。
    private static readonly long[] s_inputShape = [1, InputWithContextSize];
    private static readonly long[] s_stateShape = [2, 1, StateDim];
    private static readonly long[] s_srShape = []; // scalar (rank 0)
    // VAD ホットパス (32ms ごと) の毎フレーム alloc を排除するため、 入力辞書と RunOptions を
    // field で再利用する (_lock 内でのみ触るので単一スレッドアクセス)。 OrtValue の値は毎フレーム
    // using で作り直して再代入する (CreateTensorValueFromMemory は参照保持だが破棄後の上書きで問題なし)。
    private readonly Dictionary<string, OrtValue> _inputs = new(3);
    private readonly RunOptions _runOptions = new();
    private readonly object _lock = new();
    private bool _disposed;

    public int RequiredFrameSize => FrameSizeValue;
    public int SampleRate => SampleRateValue;

    /// <param name="modelPath">silero_vad.onnx へのフルパス。 null の場合は実行ディレクトリ配下の Assets/silero_vad.onnx を使う。</param>
    public SileroVadDetector(string? modelPath = null)
    {
        var path = modelPath ?? Path.Combine(AppContext.BaseDirectory, "Assets", "silero_vad.onnx");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"silero_vad.onnx が見つかりません: {path}");
        }

        var options = new SessionOptions
        {
            // 推論は ~1ms と軽量なので、 スレッドプールに任せず IntraOp=1 で overhead 削減。
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };

        _session = new InferenceSession(path, options);
        _stateBuffer = new float[2 * 1 * StateDim];

        Logger.Info($"SileroVadDetector 初期化完了: {path} (size={new FileInfo(path).Length} bytes)");

        // ONNX モデルの入出力名 / shape を初期化ログに出す。
        // 「prob 値が動かない」現象の切り分け用 (出力名が想定 'output'/'stateN' と一致するか)。
        foreach (var input in _session.InputMetadata)
        {
            Logger.Info($"  ONNX Input : name='{input.Key}' shape=[{string.Join(",", input.Value.Dimensions)}] type={input.Value.ElementType.Name}");
        }
        foreach (var output in _session.OutputMetadata)
        {
            Logger.Info($"  ONNX Output: name='{output.Key}' shape=[{string.Join(",", output.Value.Dimensions)}] type={output.Value.ElementType.Name}");
        }
    }

    // OrtValue API で渡す時の入出力名 (ONNX モデル定義に合わせる)。
    // 型は string[] (IReadOnlyList<string> 兼用) — InferenceSession.Run の引数互換 + indexer 直接アクセス可。
    private static readonly string[] s_outputNames = ["output", "stateN"];

    public float DetectSpeechProb(ReadOnlySpan<float> frame16kHz)
    {
        if (frame16kHz.Length != FrameSizeValue)
        {
            throw new ArgumentException(
                $"フレームサイズは {FrameSizeValue} サンプル固定です (受信: {frame16kHz.Length})",
                nameof(frame16kHz));
        }

        lock (_lock)
        {
            if (_disposed) return 0f;

            // 公式 silero-vad utils_vad.py 準拠: input = context(64) + 現在フレーム(512) = 576 サンプル。
            // context を連結しないと推論結果が常時 ~0 になる (人声でも speech 判定されない)。
            // _inputBuffer は field 化済み (毎フレーム new を排除)。
            Array.Copy(_contextBuffer, 0, _inputBuffer, 0, ContextSize);
            frame16kHz.CopyTo(new Span<float>(_inputBuffer, ContextSize, FrameSizeValue));

            // OrtValue API を使う: NamedOnnxValue/DenseTensor よりも shape 指定が確実。
            // 特に sr の scalar (shape=[]) は OrtValue で空配列で渡せば確実。
            // shape 配列は static に巻き上げて毎フレーム alloc を排除。
            var memInfo = OrtMemoryInfo.DefaultInstance;
            using var inputOrt = OrtValue.CreateTensorValueFromMemory<float>(memInfo, _inputBuffer, s_inputShape);
            using var stateOrt = OrtValue.CreateTensorValueFromMemory<float>(memInfo, _stateBuffer, s_stateShape);
            using var srOrt = OrtValue.CreateTensorValueFromMemory<long>(memInfo, _srBuffer, s_srShape);

            // field の辞書に値だけ差し替える (毎フレームの Dictionary new を排除)。
            _inputs["input"] = inputOrt;
            _inputs["state"] = stateOrt;
            _inputs["sr"] = srOrt;

            using var results = _session.Run(_runOptions, _inputs, s_outputNames);

            // 出力順序は s_outputNames の指定順 (output, stateN) を InferenceSession.Run が保証する。
            // ElementAt を経由せず index 直アクセスで LINQ enumerator alloc を排除。
            float speechProb = 0f;
            bool stateUpdated = false;

            var probSpan = results[0].GetTensorDataAsSpan<float>();
            if (probSpan.Length > 0) speechProb = probSpan[0];

            var newStateSpan = results[1].GetTensorDataAsSpan<float>();
            if (newStateSpan.Length == _stateBuffer.Length)
            {
                newStateSpan.CopyTo(_stateBuffer);
                stateUpdated = true;
            }

            if (!stateUpdated && !_stateUpdateMissedLogged)
            {
                // state が更新されなかった場合は LSTM 連続性が失われ VAD が永遠に「初期状態」で
                // 推論する原因になる。 初回だけログを出して気づけるようにする。
                _stateUpdateMissedLogged = true;
                Logger.Warn("SileroVadDetector: state 出力テンソル長が不一致 (LSTM state が更新されません)。 ONNX 出力 shape を確認してください。");
            }

            // 次回フレーム用の context として、 今回の入力末尾 64 サンプル (= frame16kHz の末尾) を保持。
            Array.Copy(_inputBuffer, InputWithContextSize - ContextSize, _contextBuffer, 0, ContextSize);

            return speechProb;
        }
    }
    private bool _stateUpdateMissedLogged;

    public void Reset()
    {
        lock (_lock)
        {
            Array.Clear(_stateBuffer, 0, _stateBuffer.Length);
            Array.Clear(_contextBuffer, 0, _contextBuffer.Length);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _runOptions.Dispose();
            _session.Dispose();
        }
    }
}
