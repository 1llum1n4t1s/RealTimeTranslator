using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.Core.Interfaces;

/// <summary>
/// 翻訳パイプラインサービスのインターフェース
/// 音声キャプチャ、VAD、ASR、翻訳の一連のパイプライン処理を管理します
/// </summary>
public interface ITranslationPipelineService : IDisposable
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
    /// <returns>非同期操作のタスク</returns>
    Task StopAsync();
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
