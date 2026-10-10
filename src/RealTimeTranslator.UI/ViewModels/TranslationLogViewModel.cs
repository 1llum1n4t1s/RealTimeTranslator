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
    // logger への enqueue と表示用の保留を同じ境界で管理する。UI のコレクションはロック外で更新する。
    private readonly object _logStateLock = new();
    private readonly List<TranslationLogEntry> _pendingEntries = new();
    private bool _isHistoryLoaded;
    private int _historyGeneration;

    /// <summary>翻訳ログの全エントリ。 ListBox の ItemsSource にバインドする。</summary>
    [ObservableProperty]
    public partial ObservableCollection<TranslationLogEntry> Entries { get; set; } = new();

    /// <summary>「翻訳ログがまだ無い」状態の placeholder メッセージを出すためのフラグ。</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; set; } = true;

    /// <summary>履歴の読み込み・削除に失敗した場合の案内。</summary>
    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    public TranslationLogViewModel(ITranslationLogger logger)
    {
        _logger = logger;
        // 起動時の履歴読み込みは非同期で別タスクへ。 コンストラクタを await しないため fire-and-forget。
        // 失敗時にも保留している確定字幕を表示し、エラーを翻訳ログタブへ通知する。
        _ = LoadHistoryAsync();
    }

    private async Task LoadHistoryAsync()
    {
        Task cleanup;
        Task<IReadOnlyList<TranslationLogEntry>> history;
        lock (_logStateLock)
        {
            // await より先に両方をキューへ入れる。以後の AddEntry は履歴に含まれず、保留分として表示する。
            cleanup = _logger.PerformRetentionCleanupAsync();
            history = _logger.ReadAllAsync(InitialLoadCount);
        }
        await RefreshHistoryAsync(cleanup, history, 0, isClear: false).ConfigureAwait(false);
    }

    private async Task RefreshHistoryAsync(Task operation, Task<IReadOnlyList<TranslationLogEntry>> history,
                                          int generation, bool isClear)
    {
        string error = string.Empty;
        try { await operation.ConfigureAwait(false); }
        catch (Exception ex)
        {
            LoggerService.LogException(isClear ? "TranslationLogViewModel.ClearAllAsync 失敗" : "TranslationLogViewModel.LoadHistoryAsync 失敗", ex);
            error = isClear
                ? "翻訳ログをすべて削除できませんでした。使用中のファイルを閉じて、もう一度お試しください。"
                : "翻訳ログの保持期間処理に失敗しました。保存フォルダーを確認してください。";
        }

        IReadOnlyList<TranslationLogEntry>? historical = null;
        try { historical = await history.ConfigureAwait(false); }
        catch (Exception ex)
        {
            LoggerService.LogException("TranslationLogViewModel 履歴の読み込み失敗", ex);
            error = isClear
                ? "削除結果を読み込めませんでした。表示を保持しています。使用中のファイルを閉じて、もう一度お試しください。"
                : "翻訳ログの履歴を読み込めませんでした。使用中のファイルを閉じて、再読み込みしてください。";
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            TranslationLogEntry[] pending;
            lock (_logStateLock)
            {
                // 削除前の履歴や表示予約が遅れて届いても、現在の表示へ戻さない。
                if (generation != _historyGeneration) return;
                pending = _pendingEntries.ToArray();
                _pendingEntries.Clear();
                _isHistoryLoaded = true;
            }
            if (historical is not null)
            {
                Entries.Clear();
                foreach (var entry in historical) AddToDisplay(entry);
            }
            foreach (var entry in pending) AddToDisplay(entry);
            IsEmpty = Entries.Count == 0;
            ErrorMessage = error;
        });
    }

    /// <summary>
    /// 新しい翻訳ログを末尾に追加する。 ファイル書き込みも同時に行う (fire-and-forget)。
    /// MainViewModel.OnSubtitleGenerated から呼ばれる想定。
    /// </summary>
    public void AddEntry(TranslationLogEntry entry)
    {
        lock (_logStateLock)
        {
            _logger.Append(entry);
            if (!_isHistoryLoaded)
            {
                _pendingEntries.Add(entry);
                if (_pendingEntries.Count > MaxDisplayEntries) _pendingEntries.RemoveAt(0);
                return;
            }
            int generation = _historyGeneration;
            Dispatcher.UIThread.Post(() =>
            {
                lock (_logStateLock)
                {
                    if (generation != _historyGeneration) return;
                }
                AddToDisplay(entry);
                IsEmpty = false;
            });
        }
    }

    private void AddToDisplay(TranslationLogEntry entry)
    {
        Entries.Add(entry);
        // メモリ上限は履歴・保留・通常追記のすべてに適用する。ファイル上のログは削らない。
        while (Entries.Count > MaxDisplayEntries) Entries.RemoveAt(0);
    }

    /// <summary>読み込み失敗後に、保存済みのログを削除せず再試行する。</summary>
    [RelayCommand]
    private async Task ReloadHistoryAsync()
    {
        Task<IReadOnlyList<TranslationLogEntry>> history;
        int generation;
        lock (_logStateLock)
        {
            generation = ++_historyGeneration;
            _isHistoryLoaded = false;
            _pendingEntries.Clear();
            history = _logger.ReadAllAsync(MaxDisplayEntries);
        }
        await RefreshHistoryAsync(Task.CompletedTask, history, generation, isClear: false).ConfigureAwait(false);
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
    /// 翻訳ログをすべて削除し、部分失敗時は残った履歴を表示する。
    /// </summary>
    [RelayCommand]
    private async Task ClearAllAsync()
    {
        Task clear;
        Task<IReadOnlyList<TranslationLogEntry>> history;
        int generation;
        lock (_logStateLock)
        {
            generation = ++_historyGeneration;
            _isHistoryLoaded = false;
            _pendingEntries.Clear();
            // 再読み込みも同じ境界へ enqueue。削除後の追記は snapshot と重複せず保留分になる。
            clear = _logger.ClearAllAsync();
            history = _logger.ReadAllAsync(MaxDisplayEntries);
        }
        await RefreshHistoryAsync(clear, history, generation, isClear: true).ConfigureAwait(false);
    }
}
