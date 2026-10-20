using System.Text;
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
/// - 読み書き (<see cref="Append"/> / <see cref="ReadAllAsync"/> / <see cref="ClearAllAsync"/> / <see cref="PerformRetentionCleanupAsync"/>) は
///   単一ワーカータスクが <see cref="Channel{T}"/> から順次取り出して直列実行する。
///   履歴は呼び出し前の追記・削除を反映し、呼び出し後の追記を含めないスナップショットになる。
///
/// /rere 第2R #C2-R2-001 + #F-R2-002 (v1.0.29 候補):
/// - **keep-open StreamWriter**: 旧実装は Append ごとに File.AppendAllTextAsync で open/close を繰り返し、
///   Defender 環境では 1 件 5-15ms × 数千件 = 数十秒の I/O 累積 stall を起こしていた。
///   日付別 StreamWriter を保持して open/close を 1 日 1 回に抑える。
/// - **日次 retention タイマー**: 旧実装は起動時 1 回のみ retention を実行し、 24h+ 連続稼働で
///   保持期間オーバーが起きていた。 1 時間周期で「日付変わったか」を検出して再実行する。
/// </summary>
public sealed class TranslationLogService : ITranslationLogger, IAsyncDisposable
{
    private const string FilePrefix = "TranslationLog";
    private const string FileExtension = ".tsv";

    /// <summary>RetentionDays などの設定値を hot-reload で受け取る。</summary>
    private readonly IOptionsMonitor<AppSettings> _settingsMonitor;

    /// <summary>読み書き操作 (Append / ReadAll / ClearAll / Cleanup) を直列化する Channel。</summary>
    private readonly Channel<Func<Task>> _writeChannel = Channel.CreateUnbounded<Func<Task>>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    /// <summary>Channel から取り出して順次実行するワーカー Task。</summary>
    private readonly Task _writeWorkerTask;

    /// <summary>
    /// /rere 第2R #C2-R2-001 (v1.0.29 候補): 日付別の StreamWriter を保持。
    /// 日付が変わったら旧 writer を Dispose してから新規 open。 これで open/close を 1 日 1 回に抑える。
    /// 単一ワーカー (WriteWorkerLoopAsync) からのみアクセスされるため lock 不要。
    /// </summary>
    private StreamWriter? _currentWriter;
    private DateTime _currentFileDate = DateTime.MinValue;

    /// <summary>
    /// /rere 第2R #F-R2-002 (v1.0.29 候補): 日次 retention タイマー。
    /// 1 時間ごとに「日付が変わったか」を判定し、 変わっていれば retention 再実行する。
    /// 24h+ 連続稼働環境でも保持期間が正しく機能する。
    /// </summary>
    private readonly CancellationTokenSource _retentionTimerCts = new();
    private readonly Task _retentionTimerTask;
    private DateTime _lastRetentionDate = DateTime.MinValue;

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

        // /rere 第2R #F-R2-002: 日次 retention タイマー開始 (1 時間周期で日付変わり検知)。
        _retentionTimerTask = Task.Run(() => DailyRetentionLoopAsync(_retentionTimerCts.Token));
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
        finally
        {
            // ワーカー終了時に保持中の StreamWriter を flush + close する。
            await CloseCurrentWriterAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// /rere 第2R #F-R2-002 (v1.0.29 候補): 1 時間周期で日付変わりを検出し retention を再実行する。
    /// Append 経由で日付変わり時にも実行されるが、 「24h+ 起動しっぱなしで Append が来ない silence 時間」
    /// (例: 配信用 PC で待機中) でも retention が走ることを保証する。
    /// </summary>
    private async Task DailyRetentionLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromHours(1), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }

                var today = DateTime.Now.Date;
                if (today != _lastRetentionDate)
                {
                    _lastRetentionDate = today;
                    try
                    {
                        await PerformRetentionCleanupAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        LoggerService.LogWarning($"TranslationLogService.DailyRetentionLoop: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LoggerService.LogException("TranslationLogService.DailyRetentionLoop が予期せず終了", ex);
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
        var entryDate = entry.Timestamp.Date;

        // /rere 第2R #C2-R2-001 (v1.0.29 候補): keep-open StreamWriter で 1 件あたり 5-15ms の
        // open/close を削減 (Defender 環境)。 日付が変わったら旧 writer を Dispose してから新規 open。
        if (_currentWriter is null || entryDate != _currentFileDate)
        {
            await CloseCurrentWriterAsync().ConfigureAwait(false);
            var filePath = GetFilePathForDate(entry.Timestamp);
            // FileShare.Read で他プロセスから読める (Notepad で開く等)。
            // useAsync: true で WriteLineAsync が真の非同期 I/O を使う。
            var fs = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read,
                                    bufferSize: 4096, useAsync: true);
            _currentWriter = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
            _currentFileDate = entryDate;

            // 日付が変わったタイミングで retention も実行 (DailyRetentionLoop と二重発火するが
            // PerformRetentionCleanupAsync は idempotent なので問題なし)。
            if (entryDate != _lastRetentionDate)
            {
                _lastRetentionDate = entryDate;
                _ = Task.Run(async () =>
                {
                    try { await PerformRetentionCleanupAsync().ConfigureAwait(false); }
                    catch (Exception ex) { LoggerService.LogWarning($"TranslationLogService: 日付変わり retention 失敗: {ex.Message}"); }
                });
            }
        }

        // AutoFlush=true なので WriteLineAsync の戻りでディスク到達済 (耐クラッシュ性維持)。
        await _currentWriter!.WriteLineAsync(entry.ToTsvLine()).ConfigureAwait(false);
    }

    private async Task CloseCurrentWriterAsync()
    {
        if (_currentWriter is not null)
        {
            try { await _currentWriter.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { LoggerService.LogWarning($"TranslationLogService: writer Dispose 失敗: {ex.Message}"); }
            _currentWriter = null;
        }
    }

    public Task<IReadOnlyList<TranslationLogEntry>> ReadAllAsync(int? maxEntries = null)
    {
        // 呼び出し時点でキューへ入れ、前後の Append / ClearAll との境界を固定する。
        var tcs = new TaskCompletionSource<IReadOnlyList<TranslationLogEntry>>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_writeChannel.Writer.TryWrite(async () =>
        {
            try { tcs.SetResult(await ReadAllInternalAsync(maxEntries).ConfigureAwait(false)); }
            catch (Exception ex) { tcs.SetException(ex); }
        }))
        {
            tcs.SetException(new InvalidOperationException("TranslationLogService の書き込みチャネルが閉じています。"));
        }
        return tcs.Task;
    }

    private Task EnqueueAsync(Func<Task> action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_writeChannel.Writer.TryWrite(async () =>
        {
            try { await action().ConfigureAwait(false); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        }))
        {
            tcs.SetException(new InvalidOperationException("TranslationLogService の書き込みチャネルが閉じています。"));
        }
        return tcs.Task;
    }

    private async Task<IReadOnlyList<TranslationLogEntry>> ReadAllInternalAsync(int? maxEntries)
    {
        if (!Directory.Exists(LogDirectory)) return Array.Empty<TranslationLogEntry>();

        // ファイル名 (TranslationLog_yyyyMMdd.tsv) は日付昇順でソートできるため、
        // GetFiles で取得して名前ソート → 古い順から読む。 結果は古い→新しいの順で返す
        // (UI 側で末尾追加 = 最新が下に来る ADV ログ風表示と整合)。
        var files = Directory.GetFiles(LogDirectory, $"{FilePrefix}_*{FileExtension}")
                             .OrderBy(f => f, StringComparer.Ordinal)
                             .ToArray();

        var entries = new List<TranslationLogEntry>();
        // 正の件数制限では最新分だけ保持する。全ファイルの走査と失敗通知は維持する。
        Queue<TranslationLogEntry>? recentEntries = maxEntries is > 0 ? new() : null;
        var failures = new List<Exception>();
        foreach (var file in files)
        {
            try
            {
                // 自分の keep-open writer を閉じずに読める共有モードを使う。
                // 書き込み自体は同じワーカーで直列化済みなので、読み込み中に追記されない。
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                                                        bufferSize: 4096, useAsync: true);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    if (TranslationLogEntry.TryParseTsvLine(line, out var parsed) && parsed is not null)
                    {
                        if (recentEntries is not null)
                        {
                            if (recentEntries.Count == maxEntries) recentEntries.Dequeue();
                            recentEntries.Enqueue(parsed);
                        }
                        else
                        {
                            entries.Add(parsed);
                        }
                    }
                    // パース失敗行は静かに skip (壊れた行が混じっても全体を諦めない設計)。
                }
            }
            catch (Exception ex)
            {
                LoggerService.LogWarning($"TranslationLogService.ReadAllAsync: {Path.GetFileName(file)} を読めませんでした: {ex.Message}");
                failures.Add(ex);
            }
        }

        // 不完全な履歴を空・成功として表示しない。壊れた TSV 行のスキップは維持する。
        if (failures.Count > 0)
        {
            throw new IOException($"翻訳ログを読み込めませんでした ({failures.Count} ファイル)。", new AggregateException(failures));
        }

        if (recentEntries is not null) return recentEntries.ToArray();

        // null / 0 / 負数の既存契約を維持する。
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
        return EnqueueAsync(PerformRetentionCleanupInternalAsync);
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
                if (!LogFileDateParser.TryParseUnambiguousDate(parts[1], DateTime.Now.Date, out var fileDate))
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
        return EnqueueAsync(ClearAllInternalAsync);
    }

    private async Task ClearAllInternalAsync()
    {
        // /rere 第2R #C2-R2-001 連動: keep-open writer を先に Dispose してファイルロックを解放してから削除。
        await CloseCurrentWriterAsync().ConfigureAwait(false);

        if (!Directory.Exists(LogDirectory)) return;
        var files = Directory.GetFiles(LogDirectory, $"{FilePrefix}_*{FileExtension}");
        var failures = new List<Exception>();
        int deletedCount = 0;
        foreach (var file in files)
        {
            try
            {
                File.Delete(file);
                deletedCount++;
            }
            catch (Exception ex)
            {
                LoggerService.LogWarning($"TranslationLogService.ClearAll: {Path.GetFileName(file)} の削除に失敗: {ex.Message}");
                failures.Add(ex);
            }
        }
        if (failures.Count > 0)
        {
            LoggerService.LogWarning($"TranslationLogService: {deletedCount} ファイルを削除、{failures.Count} ファイルの削除に失敗しました。");
            throw new IOException($"翻訳ログを削除できませんでした ({failures.Count} ファイル)。", new AggregateException(failures));
        }
        LoggerService.LogInfo($"TranslationLogService: 全翻訳ログを削除しました ({deletedCount} ファイル)");
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
        return Path.Combine(LogDirectory, $"{FilePrefix}_{date.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)}{FileExtension}");
    }

    /// <summary>
    /// アプリ終了時にチャネルを完了させ、 ワーカーの残作業を待つ。
    /// Singleton で生きる前提なので通常はプロセス終了時に呼ばれるだけ。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // 日次 retention タイマーを停止 (Task.Delay の CancellationToken でキャンセル)
        _retentionTimerCts.Cancel();

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

        try
        {
            await _retentionTimerTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException) { /* タイマー Cancel 待ちタイムアウト、 致命的でない */ }
        catch (OperationCanceledException) { /* 正常 cancel */ }
        catch (Exception ex) { LoggerService.LogWarning($"TranslationLogService.DisposeAsync (retentionTimer): {ex.Message}"); }

        _retentionTimerCts.Dispose();
    }
}
