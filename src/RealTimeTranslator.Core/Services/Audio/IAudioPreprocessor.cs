namespace RealTimeTranslator.Core.Services.Audio;

/// <summary>
/// 入力プリプロセス DSP の共通インターフェース。
///
/// 全実装は in-place で <see cref="Span{Single}"/> を処理し、 <see cref="Reset"/> でフィルタ
/// 内部状態 (envelope follower / running gain / sum-of-squares 等) を完全クリアする。
///
/// 各 DSP は <see cref="TranslationPipelineService"/> にシングルインスタンスで保持し、
/// 連続音声ストリームを途切れず供給することで境界クリック / 過渡応答リセットを回避する。
/// 詳細な背景は <c>_global/systemPatterns.md</c> の「DSP: ステートフルなリサンプラ/フィルタを
/// フレームごとに新規生成しない」教訓を参照 (RealTimeTranslator v1.0.23 リサンプラ境界
/// クリック事件の対策として確立されたパターン)。
/// </summary>
public interface IAudioPreprocessor
{
    /// <summary>
    /// 有効化状態。 false なら <see cref="Process"/> は呼び出し直後に return して
    /// CPU オーバーヘッドゼロ・配列を変更しない (完全 bypass)。
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// サンプルを in-place で処理する。
    /// 配列長は呼び出しごとに可変 (WASAPI から来る chunk 単位、 典型は 480〜3840 サンプル @ 48kHz)。
    /// </summary>
    void Process(Span<float> samples);

    /// <summary>
    /// フィルタ内部状態を完全クリア。 セッション開始時 (<see cref="TranslationPipelineService"/> の
    /// <c>StartCoreAsync</c>) で <see cref="_vadResampler"/> 等の Reset と同じ位置で呼ぶ。
    /// </summary>
    void Reset();
}
