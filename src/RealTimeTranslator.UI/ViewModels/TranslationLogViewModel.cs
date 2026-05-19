using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;

namespace RealTimeTranslator.UI.ViewModels;

/// <summary>
/// 「翻訳ログ」タブの ViewModel。 過去の確定字幕を ADV ゲームの会話ログ風に表示する。
///
/// データフロー:
/// 1. 起動時に <see cref="ITranslationLogger.ReadAllAsync"/> で過去の TSV ファイルを最新 N 件読み込み
/// 2. MainViewModel.OnSubtitleGenerated で確定字幕が生成されるたびに <see cref="AddEntry"/> を呼び出し
/// 3. ObservableCollection で UI バインド (ListBox の ItemsControl)
/// 4. メモリ上限 (MaxDisplayEntries) を超えたら古い側から削除 (ファイルには残る)
/// </summary>
public partial class TranslationLogViewModel : ObservableObject
{
    // メモリ上に保持する最大エントリ数。 これを超えたら古い側を Remove (ファイルには引き続き残る)。
    // ADV ログとしては数千件保持していても Avalonia の VirtualizingStackPanel で快適に動く想定。
    private const int MaxDisplayEntries = 2000;

    // 起動時に読み込む過去ログの最大件数。 Read コスト + 初期表示量のバランスを考えて 1000 件に。
    // RetentionDays=無制限 + 数年運用で TSV が数 MB 超になる可能性があるが、 最新 1000 件で十分。
    private const int InitialLoadCount = 1000;

    private readonly ITranslationLogger _logger;

    /// <summary>
    /// 起動時の履歴ロード完了フラグ (Phase 6 レビュー #R-H2 対応)。
    /// 履歴ロード中に AddEntry が呼ばれた場合、 ReadAllAsync の結果と AddEntry のエントリが
    /// 二重で ObservableCollection に追加される race を防ぐ。
    /// false の間 (起動直後の数百ms) に来た AddEntry はファイルに書くだけで UI 表示は ReadAllAsync 後に反映される。
    /// </summary>
    private volatile bool _isHistoryLoaded;

    /// <summary>翻訳ログの全エントリ。 ListBox の ItemsSource にバインドする。</summary>
    [ObservableProperty]
    public partial ObservableCollection<TranslationLogEntry> Entries { get; set; } = new();

    /// <summary>「翻訳ログがまだ無い」状態の placeholder メッセージを出すためのフラグ。</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; set; } = true;

    public TranslationLogViewModel(ITranslationLogger logger)
    {
        _logger = logger;
        // 起動時の履歴読み込みは非同期で別タスクへ。 コンストラクタを await しないため fire-and-forget。
        // 例外はバックグラウンドで握ってログのみに残し、 翻訳ログタブが空表示になるだけで他には影響させない。
        _ = LoadHistoryAsync();
    }

    private async Task LoadHistoryAsync()
    {
        try
        {
            // 起動時 cleanup (保持期間外の古いファイル削除) を済ませてから読み込む。
            await _logger.PerformRetentionCleanupAsync().ConfigureAwait(false);

            var historical = await _logger.ReadAllAsync(InitialLoadCount).ConfigureAwait(false);

            // UI スレッドで ObservableCollection を更新 + _isHistoryLoaded フラグを立てる。
            // この Post 完了までは AddEntry がファイルにだけ書く (UI には反映しない) ため、 二重追加 race を回避できる。
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var entry in historical)
                {
                    Entries.Add(entry);
                }
                _isHistoryLoaded = true;
                // 履歴 0 件 → IsEmpty=true、 1 件以上 → IsEmpty=false (Phase 6 レビュー #R-H6: 冗長チェック整理)
                IsEmpty = Entries.Count == 0;
            });
        }
        catch (Exception ex)
        {
            LoggerService.LogException("TranslationLogViewModel.LoadHistoryAsync 失敗", ex);
            // 例外時もフラグは立てて、 以後の AddEntry が永久にブロックされないようにする。
            _isHistoryLoaded = true;
        }
    }

    /// <summary>
    /// 新しい翻訳ログを末尾に追加する。 ファイル書き込みも同時に行う (fire-and-forget)。
    /// MainViewModel.OnSubtitleGenerated から呼ばれる想定。
    /// </summary>
    public void AddEntry(TranslationLogEntry entry)
    {
        // ファイルへの書き込みは常時実行 (履歴ロード中でも記録は失わない)。
        // Channel 経由で順序保証されるため、 履歴ロード完了後に来た Append との順序も担保される。
        _logger.Append(entry);

        Dispatcher.UIThread.Post(() =>
        {
            // Phase 6 レビュー #R-H2 対応: 履歴ロード前は UI 追加をスキップして、 ReadAllAsync 結果と二重表示にしない。
            // Append でファイルに書き込まれた entry は次回起動時 (or 履歴ロード完了後) に ObservableCollection に反映される。
            if (!_isHistoryLoaded) return;

            Entries.Add(entry);
            // メモリ上限超過時は古い側から削除 (ファイルには残るので閲覧したい場合はフォルダを開く)。
            while (Entries.Count > MaxDisplayEntries)
            {
                Entries.RemoveAt(0);
            }
            IsEmpty = false;
        });
    }

    /// <summary>
    /// 翻訳ログ保存フォルダをエクスプローラーで開く。
    /// バージョンタブの「ログフォルダを開く」と同じ Process.Start パターン。
    /// </summary>
    [RelayCommand]
    private void OpenTranslationLogsFolder()
    {
        try
        {
            // ディレクトリがまだ作られていない可能性があるので、 開く前に確認 / 作成。
            if (!Directory.Exists(_logger.LogDirectory))
            {
                Directory.CreateDirectory(_logger.LogDirectory);
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = _logger.LogDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            LoggerService.LogException("OpenTranslationLogsFolder 失敗", ex);
        }
    }

    /// <summary>
    /// 翻訳ログをすべて削除する。 ファイル / メモリの両方をクリアする。
    /// </summary>
    [RelayCommand]
    private async Task ClearAllAsync()
    {
        try
        {
            await _logger.ClearAllAsync().ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                Entries.Clear();
                IsEmpty = true;
            });
        }
        catch (Exception ex)
        {
            LoggerService.LogException("TranslationLogViewModel.ClearAllAsync 失敗", ex);
        }
    }
}
