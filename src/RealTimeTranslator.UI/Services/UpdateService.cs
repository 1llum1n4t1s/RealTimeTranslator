using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;
using RealTimeTranslator.UI.Models;
using Velopack;
using Velopack.Sources;

namespace RealTimeTranslator.UI.Services;

public class UpdateService : IUpdateService
{
    private const int UpdateCheckIntervalHours = 6; // 更新チェック間隔（時間）

    // FeedUrl で許可するホスト（HTTPS + 完全一致）。
    // Authenticode / RSA 署名は今回見送りのため、最低限のホスト固定でフィッシング feed への接続を防ぐ。
    private static readonly HashSet<string> AllowedFeedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com",
        "objects.githubusercontent.com",
    };

    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(UpdateCheckIntervalHours);
    private readonly object _syncLock = new();
    // Velopack の Check / Download / Apply 操作はプロセス内で直列化する。
    // .velopack_lock ファイルは Velopack 側がプロセス間排他のために確保するが、
    // 同一プロセスから並走させると AcquireLockFailedException が出るので、
    // ここで先に直列化して衝突を防ぐ（起動時の自動チェックと PeriodicTimer の同時発火対策）。
    private static readonly SemaphoreSlim _velopackOpLock = new(1, 1);
    private UpdateSettings _settings = new();

    public event EventHandler<UpdateStatusChangedEventArgs>? StatusChanged;
    public event EventHandler<UpdateAvailableEventArgs>? UpdateAvailable;

    public void UpdateSettings(UpdateSettings settings)
    {
        lock (_syncLock)
        {
            _settings = new UpdateSettings
            {
                Enabled = settings.Enabled,
                FeedUrl = settings.FeedUrl
            };
        }

        if (!_settings.Enabled || string.IsNullOrWhiteSpace(_settings.FeedUrl))
        {
            OnStatusChanged(UpdateStatus.Disabled, "更新チェックは無効です。");
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await Check4UpdateAsync(manually: false, cancellationToken);
        using var timer = new PeriodicTimer(UpdateCheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await Check4UpdateAsync(manually: false, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task<object?> Check4UpdateAsync(bool manually, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UpdateSettings snapshot;
        lock (_syncLock)
        {
            snapshot = new UpdateSettings
            {
                Enabled = _settings.Enabled,
                FeedUrl = _settings.FeedUrl
            };
        }

        if (!snapshot.Enabled || string.IsNullOrWhiteSpace(snapshot.FeedUrl))
        {
            OnStatusChanged(UpdateStatus.Disabled, "更新チェックは無効です。");
            return manually ? new AlreadyUpToDate() : null;
        }

        if (!TryGetValidFeedUri(snapshot.FeedUrl, out var feedUri, out var validationReason))
        {
            LoggerService.LogError($"UpdateService: FeedUrl 検証失敗 — {validationReason}");
            OnStatusChanged(UpdateStatus.Failed, validationReason);
            return manually ? new SelfUpdateFailed(validationReason) : null;
        }

        // 別の Velopack 操作（同プロセス内）が進行中なら:
        //  - 自動チェック: 譲る（次回の Periodic で拾えるので問題ない）
        //  - 手動チェック: 最大 30 秒待つ（ユーザー操作を空振りで終わらせないため）
        var waitTimeout = manually ? TimeSpan.FromSeconds(30) : TimeSpan.Zero;
        if (!await _velopackOpLock.WaitAsync(waitTimeout, cancellationToken).ConfigureAwait(false))
        {
            LoggerService.LogInfo("UpdateService: 別の Velopack 操作が進行中のため、今回のチェックはスキップ");
            return manually
                ? new SelfUpdateFailed("別の更新処理が実行中です。後で再試行してください。")
                : null;
        }

        OnStatusChanged(UpdateStatus.Checking, "更新を確認しています...");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // GithubSource は GitHub Releases API で latest release の assets を解析する。
            // SimpleWebSource は `<FeedUrl>/releases-{channel}.json` を直接 GET するため、
            // GitHub のリポジトリトップ URL (https://github.com/owner/repo) には対応できず 404 になる。
            // Komorebi も GithubSource を使用している。
            var source = new GithubSource(feedUri.AbsoluteUri.TrimEnd('/'), accessToken: string.Empty, prerelease: false);
            var manager = new UpdateManager(source);

            // Velopack でインストールされていない場合（開発環境など）はチェックをスキップする。
            // 手動チェック時のみ「最新版です」と扱ってダイアログを出す（Komorebi と同じ挙動）。
            if (!manager.IsInstalled)
            {
                OnStatusChanged(UpdateStatus.Idle, "Velopack でインストールされていないため、更新確認をスキップします。");
                return manually ? new AlreadyUpToDate() : null;
            }

            var updateInfo = await manager.CheckForUpdatesAsync();
            if (updateInfo is null)
            {
                OnStatusChanged(UpdateStatus.Idle, "利用可能な更新はありません。");
                return manually ? new AlreadyUpToDate() : null;
            }

            var result = new VelopackUpdate(manager, updateInfo);
            OnStatusChanged(UpdateStatus.UpdateAvailable, $"新しいバージョン {result.TagName} が利用できます。");

            // 自動チェックでは MainViewModel に SelfUpdateWindow を開かせるためイベントを発火する。
            // 手動チェックは戻り値経由でダイアログを開くため、二重に開かないようイベントは発火しない。
            if (!manually)
            {
                UpdateAvailable?.Invoke(this, new UpdateAvailableEventArgs(
                    $"新しいバージョン {result.TagName} が利用できます。", result));
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex.GetType().Name == "AcquireLockFailedException")
        {
            // Velopack の .velopack_lock 衝突: 別プロセス（同じインストール先の別バージョン起動など）が
            // 操作中。次回 Periodic で自然に再試行されるのでログだけ残す。
            LoggerService.LogInfo($"UpdateService: Velopack ロック取得失敗（次回チェック時に再試行）— {ex.Message}");
            OnStatusChanged(UpdateStatus.Idle, "別の更新処理が実行中です。後で再試行します。");
            return manually
                ? new SelfUpdateFailed("別の更新処理が実行中です。後で再試行してください。")
                : null;
        }
        catch (Exception ex)
        {
            // スタックトレース / InnerException を永続ログに残す（UI イベントだけでは事後解析できない）。
            LoggerService.LogError($"UpdateService.Check4UpdateAsync 失敗 (manually={manually}): {ex}");
            OnStatusChanged(UpdateStatus.Failed, $"更新チェックに失敗しました: {ex.Message}");
            return manually ? new SelfUpdateFailed(ex) : null;
        }
        finally
        {
            _velopackOpLock.Release();
        }
    }

    /// <summary>
    /// FeedUrl の HTTPS + ホスト許可リスト検証。
    /// 攻撃者が settings.json を書換えて任意の URL に誘導することを防ぐ最低限のガード。
    /// </summary>
    private static bool TryGetValidFeedUri(string feedUrl, out Uri uri, out string reason)
    {
        uri = null!;
        if (!Uri.TryCreate(feedUrl, UriKind.Absolute, out var parsed))
        {
            reason = "FeedUrl が絶対 URI 形式ではありません。";
            return false;
        }
        if (parsed.Scheme != Uri.UriSchemeHttps)
        {
            reason = $"FeedUrl の HTTPS 以外のスキーム（{parsed.Scheme}）は許可されていません。";
            return false;
        }
        if (!AllowedFeedHosts.Contains(parsed.Host))
        {
            reason = $"FeedUrl のホスト '{parsed.Host}' は許可リスト外です（github.com / objects.githubusercontent.com のみ）。";
            return false;
        }
        uri = parsed;
        reason = string.Empty;
        return true;
    }

    private void OnStatusChanged(UpdateStatus status, string message)
    {
        StatusChanged?.Invoke(this, new UpdateStatusChangedEventArgs(status, message));
    }
}
