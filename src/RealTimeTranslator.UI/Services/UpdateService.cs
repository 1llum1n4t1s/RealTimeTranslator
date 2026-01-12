using System;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using Velopack;
using Velopack.Sources;

namespace RealTimeTranslator.UI.Services;

public class UpdateService : IUpdateService
{
    private const int UpdateCheckIntervalHours = 6; // 更新チェック間隔（時間）

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
            return;
        }

        OnStatusChanged(UpdateStatus.Checking, "更新を確認しています...");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = new SimpleWebSource(new Uri(snapshot.FeedUrl));
            var manager = new UpdateManager(source);
            var updateInfo = await manager.CheckForUpdatesAsync();
            if (updateInfo is null)
            {
                OnStatusChanged(UpdateStatus.Idle, "利用可能な更新はありません。");
                return;
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
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            OnStatusChanged(UpdateStatus.Failed, $"更新チェックに失敗しました: {ex.Message}");
        }
    }

    public async Task<bool> CheckAndApplyStartupAsync(CancellationToken cancellationToken)
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

        OnStatusChanged(UpdateStatus.Checking, "更新を確認しています...");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = new SimpleWebSource(new Uri(snapshot.FeedUrl));
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

            manager.ApplyUpdatesAndRestart(updateInfo);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
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

        try
        {
            var source = new SimpleWebSource(new Uri(feedUrl));
            var manager = new UpdateManager(source);
            manager.ApplyUpdatesAndRestart(updateInfo);
        }
        catch (Exception ex)
        {
            OnStatusChanged(UpdateStatus.Failed, $"更新の適用に失敗しました: {ex.Message}");
        }

        return Task.CompletedTask;
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
