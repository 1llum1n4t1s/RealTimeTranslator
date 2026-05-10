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

    /// <summary>
    /// API設定をキャッシュに反映します。次回の接続時に新しい設定が使われます。
    /// 既にアクティブな接続には影響しません（再接続が必要です）。
    /// </summary>
    /// <param name="settings">適用する API 設定</param>
    /// <param name="cancellationToken">キャンセルトークン</param>
    Task ApplySettingsAsync(OpenAIRealtimeSettings settings, CancellationToken cancellationToken = default);
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
}
