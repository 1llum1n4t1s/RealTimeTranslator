using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.Core.Interfaces;

/// <summary>
/// 翻訳ログを永続化 / 読み戻すサービス。
///
/// 設計方針:
/// - 永続化先は %APPDATA%/Roaming/RealTimeTranslator/logs/translations/TranslationLog_yyyyMMdd.tsv (日付別ローテーション)
/// - Velopack 自動更新で消えない位置 (Roaming AppData) を採用
/// - 既存 LoggerService (SuperLightLogger) とは分離して、 翻訳ログ専用のシンプルな TSV 書き込みに特化
/// - 確定字幕 1 件ごとに <see cref="Append"/> で書き込み (UI スレッドをブロックしない fire-and-forget)
/// - 起動時に <see cref="ReadAllAsync"/> で過去ログを読み込み、 翻訳ログタブで表示
/// - <see cref="PerformRetentionCleanupAsync"/> で保持期間外のファイルを削除
/// </summary>
public interface ITranslationLogger
{
    /// <summary>
    /// 翻訳ログを保存するディレクトリの絶対パス。 「保存フォルダを開く」ボタンの引数に使う。
    /// </summary>
    string LogDirectory { get; }

    /// <summary>
    /// 1 件の翻訳ログを追記する。 ファイル I/O は内部でバックグラウンドにオフロードされるため、
    /// UI スレッド / pipeline スレッドから呼び出してもブロックしない。
    /// </summary>
    void Append(TranslationLogEntry entry);

    /// <summary>
    /// 保存済みの翻訳ログをすべて読み戻す (新しい順 / 古い順は実装次第、 通常は古い順)。
    /// 起動時に翻訳ログタブの ObservableCollection に流し込むのに使う。
    /// </summary>
    /// <param name="maxEntries">最大読込件数。 null なら無制限。 多すぎる場合は古い側から打ち切る。</param>
    Task<IReadOnlyList<TranslationLogEntry>> ReadAllAsync(int? maxEntries = null);

    /// <summary>
    /// AppSettings.TranslationLog.RetentionDays より古いファイルを削除する。
    /// 起動時 / 日付変更時に呼ぶ。 RetentionDays=0 (無制限) なら何もしない。
    /// </summary>
    Task PerformRetentionCleanupAsync();

    /// <summary>
    /// 保存済みの翻訳ログをすべて削除する (翻訳ログタブの「すべて削除」ボタン用)。
    /// メモリ上の表示は呼び出し側で別途クリアすること。
    /// </summary>
    Task ClearAllAsync();
}
