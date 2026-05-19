using System.Threading.Channels;
using Microsoft.Extensions.Options;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.Core.Services;

/// <summary>
/// <see cref="ITranslationLogger"/> の実装。 TSV 形式で日付別ファイルに翻訳ログを永続化する。
///
/// 配置: %APPDATA%/Roaming/RealTimeTranslator/logs/translations/TranslationLog_yyyyMMdd.tsv
/// (Roaming AppData なので Velopack 更新で消えない、 LoggerService と同じ設計)
///
/// 並行性 (Phase 6 レビュー #R-C1 / #R-H1 / #R-H3 対応):
/// - 書き込み系 (<see cref="Append"/> / <see cref="ClearAllAsync"/> / <see cref="PerformRetentionCleanupAsync"/>) は
///   単一ワーカータスクが <see cref="Channel{T}"/> から順次取り出して直列実行する。
///   これで Append の発火順序が TSV ファイル上で保証される + ClearAll 直後の Append が UI と乖離する race も消える。
/// - 読み取り系 (<see cref="ReadAllAsync"/>) は lock を一切取らず並行可能。 起動時の初期読み込みのみで
///   翻訳開始前に完了する想定なので、 書き込みとの厳密な整合性は不要。
/// </summary>
public sealed class TranslationLogService : ITranslationLogger, IAsyncDisposable
{
    private const string FilePrefix = "TranslationLog";
    private const string FileExtension = ".tsv";

    /// <summary>RetentionDays などの設定値を hot-reload で受け取る。</summary>
    private readonly IOptionsMonitor<AppSettings> _settingsMonitor;

    /// <summary>書き込み系操作 (Append / ClearAll / Cleanup) を直列化する Channel。</summary>
    private readonly Channel<Func<Task>> _writeChannel = Channel.CreateUnbounded<Func<Task>>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    /// <summary>Channel から取り出して順次実行するワーカー Task。</summary>
    private readonly Task _writeWorkerTask;

    public string LogDirectory { get; }

    public TranslationLogService(IOptionsMonitor<AppSettings> settingsMonitor)
    {
        _settingsMonitor = settingsMonitor;
        LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RealTimeTranslator",
            "logs",
            "translations");

        // Channel ワーカーを起動。 Singleton 寿命と一致するため Stop 不要 (DisposeAsync で Complete する)。
        _writeWorkerTask = Task.Run(WriteWorkerLoopAsync);
    }

    private async Task WriteWorkerLoopAsync()
    {
        try
        {
            await foreach (var action in _writeChannel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    await action().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LoggerService.LogError($"TranslationLogService.WriteWorker: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            // ReadAllAsync 自体は通常例外を投げないが、 念のため握ってアプリ全体を止めない。
            LoggerService.LogException("TranslationLogService.WriteWorker が予期せず終了", ex);
        }
    }

    public void Append(TranslationLogEntry entry)
    {
        // Channel への enqueue は同期 + 順序保証 (SingleReader なので必ず enqueue 順で取り出される)。
        // TryWrite 失敗は Channel が Complete 済 (= 終了処理中) のときのみ。
        if (!_writeChannel.Writer.TryWrite(() => AppendInternalAsync(entry)))
        {
            LoggerService.LogWarning("TranslationLogService.Append: 書き込みチャネルが閉じています、 ログを破棄しました。");
        }
    }

    private async Task AppendInternalAsync(TranslationLogEntry entry)
    {
        EnsureDirectory();
        var filePath = GetFilePathForDate(entry.Timestamp);
        // File.AppendAllTextAsync は内部で UTF-8 で追記する。
        // 翻訳テキストの \t / \n は ToTsvLine で除去済みなので 1 行 1 エントリが保証される。
        await File.AppendAllTextAsync(filePath, entry.ToTsvLine() + Environment.NewLine,
                                      System.Text.Encoding.UTF8).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TranslationLogEntry>> ReadAllAsync(int? maxEntries = null)
    {
        // Phase 6 レビュー #R-H1 対応: lock を取らず読み取り専用で動作。
        // 起動時の初期ロードのみで翻訳開始前に完了する想定 (= 書き込みは発生しない)。
        if (!Directory.Exists(LogDirectory)) return Array.Empty<TranslationLogEntry>();

        // ファイル名 (TranslationLog_yyyyMMdd.tsv) は日付昇順でソートできるため、
        // GetFiles で取得して名前ソート → 古い順から読む。 結果は古い→新しいの順で返す
        // (UI 側で末尾追加 = 最新が下に来る ADV ログ風表示と整合)。
        var files = Directory.GetFiles(LogDirectory, $"{FilePrefix}_*{FileExtension}")
                             .OrderBy(f => f, StringComparer.Ordinal)
                             .ToArray();

        var entries = new List<TranslationLogEntry>();
        foreach (var file in files)
        {
            try
            {
                var lines = await File.ReadAllLinesAsync(file, System.Text.Encoding.UTF8).ConfigureAwait(false);
                foreach (var line in lines)
                {
                    if (TranslationLogEntry.TryParseTsvLine(line, out var parsed) && parsed is not null)
                    {
                        entries.Add(parsed);
                    }
                    // パース失敗行は静かに skip (壊れた行が混じっても全体を諦めない設計)。
                }
            }
            catch (Exception ex)
            {
                LoggerService.LogWarning($"TranslationLogService.ReadAllAsync: {Path.GetFileName(file)} を読めませんでした: {ex.Message}");
            }
        }

        // 件数制限: 古い側を切り捨てて新しい maxEntries 件を返す (ADV ログとして直近を見せたい想定)。
        if (maxEntries is int max && entries.Count > max)
        {
            return entries.Skip(entries.Count - max).ToList();
        }
        return entries;
    }

    public Task PerformRetentionCleanupAsync()
    {
        // Phase 6 レビュー #R-C1 対応: Cleanup を Channel 経由で直列化することで、
        // Append/ClearAll との実行順序が保証される。 await した完了通知も含めて返す。
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_writeChannel.Writer.TryWrite(async () =>
        {
            try { await PerformRetentionCleanupInternalAsync().ConfigureAwait(false); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        }))
        {
            tcs.SetException(new InvalidOperationException("TranslationLogService の書き込みチャネルが閉じています。"));
        }
        return tcs.Task;
    }

    private async Task PerformRetentionCleanupInternalAsync()
    {
        int retentionDays = _settingsMonitor.CurrentValue.TranslationLog.RetentionDays;
        if (retentionDays <= 0) return; // 0 = 無制限、 削除しない
        if (!Directory.Exists(LogDirectory)) return;

        // Phase 6 レビュー #R-H4 対応: 「N 日保持」= 「今日含む N 日分残す」を意図する仕様に揃える。
        // cutoffDate = 今日 - (N-1) 日。 fileDate < cutoffDate なら削除。
        // 例: RetentionDays=7、 今日が 5/19 → cutoff = 5/13 → 5/12 (8 日前) のファイルは削除、 5/13〜5/19 (7 日分) は残る。
        var cutoffDate = DateTime.Now.Date.AddDays(-(retentionDays - 1));
        var files = Directory.GetFiles(LogDirectory, $"{FilePrefix}_*{FileExtension}");

        foreach (var file in files)
        {
            try
            {
                // ファイル名から日付を抽出 (例: TranslationLog_20260519.tsv → 2026-05-19)
                var name = Path.GetFileNameWithoutExtension(file);
                var parts = name.Split('_');
                if (parts.Length < 2 || parts[1].Length != 8) continue;
                if (!DateTime.TryParseExact(parts[1], "yyyyMMdd", null,
                                            System.Globalization.DateTimeStyles.None, out var fileDate))
                {
                    continue;
                }

                if (fileDate < cutoffDate)
                {
                    File.Delete(file);
                    LoggerService.LogDebug($"TranslationLogService: 古いログを削除 — {Path.GetFileName(file)} (cutoff={cutoffDate:yyyy-MM-dd})");
                }
            }
            catch (Exception ex)
            {
                LoggerService.LogWarning($"TranslationLogService.PerformRetentionCleanup: {Path.GetFileName(file)} の削除に失敗: {ex.Message}");
            }
        }

        await Task.CompletedTask;
    }

    public Task ClearAllAsync()
    {
        // Phase 6 レビュー #R-C1 対応: ClearAll を Channel 経由で直列化することで、
        // 「ClearAll 完了 → 後続 Append」の順序が確実になり、 削除直後の Append が消えるエントリの「先に書き込まれて消されない」事故も無くなる。
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_writeChannel.Writer.TryWrite(async () =>
        {
            try { await ClearAllInternalAsync().ConfigureAwait(false); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        }))
        {
            tcs.SetException(new InvalidOperationException("TranslationLogService の書き込みチャネルが閉じています。"));
        }
        return tcs.Task;
    }

    private Task ClearAllInternalAsync()
    {
        if (!Directory.Exists(LogDirectory)) return Task.CompletedTask;
        var files = Directory.GetFiles(LogDirectory, $"{FilePrefix}_*{FileExtension}");
        foreach (var file in files)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex)
            {
                LoggerService.LogWarning($"TranslationLogService.ClearAll: {Path.GetFileName(file)} の削除に失敗: {ex.Message}");
            }
        }
        LoggerService.LogInfo($"TranslationLogService: 全翻訳ログを削除しました ({files.Length} ファイル)");
        return Task.CompletedTask;
    }

    private void EnsureDirectory()
    {
        if (!Directory.Exists(LogDirectory))
        {
            Directory.CreateDirectory(LogDirectory);
        }
    }

    private string GetFilePathForDate(DateTime date)
    {
        // 日付別ファイル: TranslationLog_yyyyMMdd.tsv
        return Path.Combine(LogDirectory, $"{FilePrefix}_{date:yyyyMMdd}{FileExtension}");
    }

    /// <summary>
    /// アプリ終了時にチャネルを完了させ、 ワーカーの残作業を待つ。
    /// Singleton で生きる前提なので通常はプロセス終了時に呼ばれるだけ。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _writeChannel.Writer.TryComplete();
        try
        {
            await _writeWorkerTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            LoggerService.LogWarning("TranslationLogService.DisposeAsync: ワーカー停止がタイムアウト");
        }
        catch (Exception ex)
        {
            LoggerService.LogWarning($"TranslationLogService.DisposeAsync: {ex.Message}");
        }
    }
}
