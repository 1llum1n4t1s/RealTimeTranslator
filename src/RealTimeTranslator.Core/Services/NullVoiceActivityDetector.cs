using RealTimeTranslator.Core.Interfaces;

namespace RealTimeTranslator.Core.Services;

/// <summary>
/// Silero VAD 初期化失敗時の緊急 fallback VAD (rere F-002 対応)。
/// 常に speech_prob=1.0 を返すため、 VAD ゲートは全フレームを speech 扱いし、
/// 全音声を OpenAI に送信する旧素通し動作と等価になる。
///
/// 想定する故障シナリオ:
/// - Velopack 差分更新で `Assets/silero_vad.onnx` を取りこぼした
/// - アンチウイルスが `onnxruntime.dll` を隔離した
/// - VC++ Runtime 不足で ONNX ロードが失敗した
///
/// この fallback により、 「VAD 初期化失敗 → アプリ全体起動不能」の brick を避け、
/// 「VAD 抑制機能だけ無効、 翻訳本体は動く」状態で続行できる。
/// UI 側で BGM 課金抑制が効かない旨を警告バナーで通知する責務は MainViewModel が持つ。
/// </summary>
public sealed class NullVoiceActivityDetector : IVoiceActivityDetector
{
    // Silero VAD 互換: 512 sample / 32ms / 16kHz 固定。 VAD ゲートの frame accumulator が
    // 同じサイズを期待するため、 値を変えると ProcessVadFrame 経路が壊れる。
    public int RequiredFrameSize => 512;
    public int SampleRate => 16000;

    /// <summary>常に 1.0 を返して全フレームを speech 扱いさせる。</summary>
    public float DetectSpeechProb(ReadOnlySpan<float> frame16kHz) => 1.0f;

    /// <summary>state を持たないので no-op。</summary>
    public void Reset() { }

    public void Dispose() { }
}
