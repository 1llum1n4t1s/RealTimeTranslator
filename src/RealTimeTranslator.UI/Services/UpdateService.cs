using System;
using System.Collections.Generic;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;
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
    private UpdateSettings _settings = new();
    private UpdateInfo? _pendingUpdateInfo;
    private string? _pendingFeedUrl;

    public event EventHandler<UpdateStatusChangedEventArgs>? StatusChanged;
    public event EventHandler<UpdateAvailableEventArgs>? UpdateAvailable;
    public event EventHandler<UpdateReadyEventArgs>? UpdateReady;

    public void UpdateSettings(UpdateSettings settings)
    {
        lock (_syncLock)
        {
            _settings = new UpdateSettings
            {
                Enabled = settings.Enabled,
                FeedUrl = settings.FeedUrl,
                AutoApply = settings.AutoApply
            };
        }

        if (!_settings.Enabled || string.IsNullOrWhiteSpace(_settings.FeedUrl))
        {
            OnStatusChanged(UpdateStatus.Disabled, "更新チェックは無効です。");
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await CheckOnceAsync(cancellationToken);
        using var timer = new PeriodicTimer(UpdateCheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await CheckOnceAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task CheckOnceAsync(CancellationToken cancellationToken)
    {
        await CheckAndDownloadCoreAsync(applyImmediately: false, cancellationToken);
    }

    public async Task<bool> CheckAndApplyStartupAsync(CancellationToken cancellationToken)
    {
        return await CheckAndDownloadCoreAsync(applyImmediately: true, cancellationToken);
    }

    private async Task<bool> CheckAndDownloadCoreAsync(bool applyImmediately, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UpdateSettings snapshot;
        lock (_syncLock)
        {
            snapshot = new UpdateSettings
            {
                Enabled = _settings.Enabled,
                FeedUrl = _settings.FeedUrl,
                AutoApply = _settings.AutoApply
            };
        }

        if (!snapshot.Enabled || string.IsNullOrWhiteSpace(snapshot.FeedUrl))
        {
            OnStatusChanged(UpdateStatus.Disabled, "更新チェックは無効です。");
            return false;
        }

        if (!TryGetValidFeedUri(snapshot.FeedUrl, out var feedUri, out var validationReason))
        {
            LoggerService.LogError($"UpdateService: FeedUrl 検証失敗 — {validationReason}");
            OnStatusChanged(UpdateStatus.Failed, validationReason);
            return false;
        }

        OnStatusChanged(UpdateStatus.Checking, "更新を確認しています...");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = new SimpleWebSource(feedUri);
            var manager = new UpdateManager(source);
            var updateInfo = await manager.CheckForUpdatesAsync();
            if (updateInfo is null)
            {
                OnStatusChanged(UpdateStatus.Idle, "利用可能な更新はありません。");
                return false;
            }

            lock (_syncLock)
            {
                _pendingUpdateInfo = updateInfo;
                _pendingFeedUrl = snapshot.FeedUrl;
            }

            UpdateAvailable?.Invoke(this, new UpdateAvailableEventArgs("更新を検出しました。ダウンロードを開始します。"));
            OnStatusChanged(UpdateStatus.UpdateAvailable, "更新を検出しました。");

            cancellationToken.ThrowIfCancellationRequested();
            await manager.DownloadUpdatesAsync(updateInfo);

            UpdateReady?.Invoke(this, new UpdateReadyEventArgs("更新のダウンロードが完了しました。"));
            OnStatusChanged(UpdateStatus.ReadyToApply, "更新のダウンロードが完了しました。");

            if (applyImmediately)
            {
                manager.ApplyUpdatesAndRestart(updateInfo);
                return true;
            }

            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // スタックトレース / InnerException を永続ログに残す（UI イベントだけでは事後解析できない）。
            LoggerService.LogError($"UpdateService.CheckAndDownloadCoreAsync 失敗 (applyImmediately={applyImmediately}): {ex}");
            OnStatusChanged(UpdateStatus.Failed, $"更新チェックに失敗しました: {ex.Message}");
            return false;
        }
    }

    public Task ApplyUpdateAsync(CancellationToken cancellationToken)
    {
        UpdateInfo? updateInfo;
        string? feedUrl;
        lock (_syncLock)
        {
            updateInfo = _pendingUpdateInfo;
            feedUrl = _pendingFeedUrl;
        }

        if (updateInfo is null || string.IsNullOrWhiteSpace(feedUrl))
        {
            OnStatusChanged(UpdateStatus.Failed, "適用可能な更新がありません。");
            return Task.CompletedTask;
        }

        if (!TryGetValidFeedUri(feedUrl!, out var feedUri, out var validationReason))
        {
            LoggerService.LogError($"UpdateService.ApplyUpdateAsync: FeedUrl 検証失敗 — {validationReason}");
            OnStatusChanged(UpdateStatus.Failed, validationReason);
            return Task.CompletedTask;
        }

        try
        {
            var source = new SimpleWebSource(feedUri);
            var manager = new UpdateManager(source);
            manager.ApplyUpdatesAndRestart(updateInfo);
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"UpdateService.ApplyUpdateAsync 失敗: {ex}");
            OnStatusChanged(UpdateStatus.Failed, $"更新の適用に失敗しました: {ex.Message}");
        }

        return Task.CompletedTask;
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

    public void DismissPendingUpdate()
    {
        lock (_syncLock)
        {
            _pendingUpdateInfo = null;
            _pendingFeedUrl = null;
        }
    }

    private void OnStatusChanged(UpdateStatus status, string message)
    {
        StatusChanged?.Invoke(this, new UpdateStatusChangedEventArgs(status, message));
    }
}
