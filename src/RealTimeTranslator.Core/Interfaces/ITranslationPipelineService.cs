using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.Core.Interfaces;

/// <summary>
/// 翻訳パイプラインサービスのインターフェース
/// 音声キャプチャ、VAD、ASR、翻訳の一連のパイプライン処理を管理します
/// </summary>
public interface ITranslationPipelineService : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// 字幕が生成されたときに発火するイベント
    /// </summary>
    event EventHandler<SubtitleItem>? SubtitleGenerated;

    /// <summary>
    /// パイプラインの統計情報が更新されたときに発火するイベント
    /// </summary>
    event EventHandler<PipelineStatsEventArgs>? StatsUpdated;

    /// <summary>
    /// エラーが発生したときに発火するイベント
    /// </summary>
    event EventHandler<Exception>? ErrorOccurred;

    /// <summary>
    /// パイプラインを開始します
    /// </summary>
    /// <param name="token">キャンセルトークン</param>
    /// <returns>非同期操作のタスク</returns>
    Task StartAsync(CancellationToken token);

    /// <summary>
    /// パイプラインを停止します
    /// </summary>
    /// <param name="cancellationToken">キャンセルトークン</param>
    /// <returns>非同期操作のタスク</returns>
    Task StopAsync(CancellationToken cancellationToken = default);

    // rere レビュー P2 B1-007: ApplySettingsAsync は実質 dead code だった
    // (StartAsync 内で _settingsMonitor.CurrentValue から再取得して即上書きされるため)。
    // hot-reload は IOptionsMonitor.OnChange 経由で各サービスに伝わるので不要。 → 削除済み。
    // 将来 hot reconnect 機能を実装するなら IRealtimeTranscriber.ConfigureAsync を経由する設計に。
}

/// <summary>
/// パイプラインの統計情報イベント引数
/// </summary>
public class PipelineStatsEventArgs : EventArgs
{
    /// <summary>
    /// 処理全体のレイテンシ（ミリ秒）
    /// </summary>
    public double ProcessingLatency { get; set; }

    /// <summary>
    /// 翻訳のレイテンシ（ミリ秒）
    /// </summary>
    public double TranslationLatency { get; set; }

    /// <summary>
    /// ステータステキスト
    /// </summary>
    public string StatusText { get; set; } = string.Empty;

    // ───────── token / cost / session 統計 (見える化保険) ─────────

    /// <summary>
    /// セッション開始からの累積 audio input tokens 推定値。
    /// サーバー usage が取得できた場合はその値、 取れない場合は送信秒数からの fallback。
    /// </summary>
    public long InputAudioTokensEstimate { get; set; }

    /// <summary>
    /// セッション開始からの累積推定コスト (USD)。 モデル不明時はフル料金 ($100/1M) で過大評価寄り。
    /// </summary>
    public decimal EstimatedCostUsd { get; set; }

    /// <summary>セッション継続時間 (Start からの経過)。</summary>
    public TimeSpan SessionDuration { get; set; }

    /// <summary>VAD ゲートで OpenAI 送信をスキップした (= token 節約した) 音声秒数の累積。</summary>
    public double SkippedSecondsByVad { get; set; }
}
