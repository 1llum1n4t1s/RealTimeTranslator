using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.Core.Interfaces;

/// <summary>
/// 「開始」を押す前でも、 ドロップダウンで選択中のプロセスの音量をリアルタイムに把握するための
/// プレビュー専用レベルモニタ。 翻訳パイプライン (<see cref="ITranslationPipelineService"/>) とは独立して
/// 動き、 OpenAI には一切音声を送らない (= 課金ゼロ)。
///
/// 内部で専用の <see cref="IAudioCaptureService"/> を 1 つ持ち、 WASAPI でキャプチャした音声に
/// 入力ゲインを適用した後のピークを <see cref="LevelUpdated"/> (約 50ms 間隔) で通知する。
/// 翻訳開始時は呼び出し側 (MainViewModel) がこのモニタを停止し、 本番パイプラインのメーターに切り替える
/// (同一プロセスを二重キャプチャしないため)。 停止時にプレビューを再開する。
/// </summary>
public interface IAudioLevelMonitor : IDisposable
{
    /// <summary>
    /// 入力ゲイン (dB)。 メーターは「ゲイン適用後ピーク」を表示するため、 本番パイプラインと同じ値を反映する。
    /// 走行中に変更すると次フレームから新ゲインで計測する。
    /// </summary>
    float GainDb { get; set; }

    /// <summary>現在プレビュー計測中か。</summary>
    bool IsMonitoring { get; }

    /// <summary>
    /// 指定プロセスのプレビュー計測を開始する。 既存セッションがあれば自動で停止してから開始する (idempotent)。
    /// キャプチャ開始に失敗しても例外は投げず、 <see cref="IsMonitoring"/>=false のままに倒れる (silent-fail)。
    /// </summary>
    /// <param name="processId">対象プロセス ID。</param>
    /// <param name="captureCreationContext">
    /// WASAPI Process Loopback は STA (UI) スレッドにバインドするため、 UI の SynchronizationContext を渡す。
    /// null の場合は呼び出しスレッドで生成する (本番パイプライン StartAsync と同じ作法)。
    /// </param>
    /// <param name="cancellationToken">キャンセルトークン。</param>
    Task StartAsync(int processId, SynchronizationContext? captureCreationContext = null, CancellationToken cancellationToken = default);

    /// <summary>プレビュー計測を停止する (idempotent)。 停止後はレベル 0 相当を 1 回通知する。</summary>
    void Stop();

    /// <summary>
    /// ゲイン適用後のピークレベル更新 (dBFS、 約 50ms 間隔)。 UI のメーター表示用。
    /// 停止時には無音相当 (<see cref="AudioLevelEventArgs.PeakDb"/> = 床値) を 1 回発火する。
    /// </summary>
    event EventHandler<AudioLevelEventArgs>? LevelUpdated;
}
