using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.Options;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;
using Velopack;
using Velopack.Sources;
using VelopackUpdateDialog;

namespace RealTimeTranslator.UI.Services;

public class UpdateService : IUpdateService
{
    // 配信元 URL は AppSettings.UpdateSettings.UpdateBaseUrl に [JsonIgnore] でハードコード固定されており、
    // settings.json から書き換えできない (Cloudflare R2 へ移行)。攻撃者が任意ホストへ誘導する経路が
    // 構造的に塞がれているため、旧来の host / owner-repo allowlist は不要。TryGetValidFeedUri は
    // HTTPS + 絶対 URI + userinfo 排除の防御チェックのみ残す。

    // Komorebi 互換: 自動チェックは DNS 半切断 / TCP ハングで Velopack 内部ロックが長時間保有される
    // 事故を防ぐため 30 秒で打ち切る。 手動チェックはユーザーがダイアログ前で待てるのでタイムアウト無し。
    private static readonly TimeSpan AutoCheckTimeout = TimeSpan.FromSeconds(30);

    private readonly object _syncLock = new();
    // Velopack の Check / Download / Apply 操作はプロセス内で直列化する。
    // .velopack_lock ファイルは Velopack 側がプロセス間排他のために確保するが、
    // 同一プロセスから並走させると AcquireLockFailedException が出るので、
    // ここで先に直列化して衝突を防ぐ（起動時の自動チェックと手動チェックの同時発火対策）。
    private static readonly SemaphoreSlim _velopackOpLock = new(1, 1);
    private UpdateSettings _settings = new();

    private readonly IOptionsMonitor<AppSettings> _settingsMonitor;
    private readonly ISettingsService _settingsService;

    public event EventHandler<UpdateStatusChangedEventArgs>? StatusChanged;

    public UpdateService(IOptionsMonitor<AppSettings> settingsMonitor, ISettingsService settingsService)
    {
        _settingsMonitor = settingsMonitor;
        _settingsService = settingsService;
    }

    public void UpdateSettings(UpdateSettings settings)
    {
        lock (_syncLock)
        {
            _settings = new UpdateSettings
            {
                Enabled = settings.Enabled,
                IgnoredTagName = settings.IgnoredTagName
            };
        }

        // UpdateBaseUrl は [JsonIgnore] ハードコード固定なので常に非空。実質 Enabled だけで無効判定する。
        if (!_settings.Enabled || string.IsNullOrWhiteSpace(_settings.UpdateBaseUrl))
        {
            OnStatusChanged(UpdateStatus.Disabled, "更新チェックは無効です。");
        }
    }

    /// <summary>
    /// Komorebi 互換: 起動時に 1 回だけ自動チェック (PeriodicTimer による周期チェックは廃止)。
    /// 長時間起動アプリでも 6 時間ごとにダイアログが立ち上がる UX を避ける。
    /// 周期的に新版を取りたい場合はユーザーが「更新の確認」ボタン (手動) を押す運用。
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        return ShowUpdateDialogAsync(manualCheck: false, cancellationToken);
    }

    public Task CheckForUpdateAsync(CancellationToken cancellationToken)
    {
        return ShowUpdateDialogAsync(manualCheck: true, cancellationToken);
    }

    /// <summary>
    /// VelopackUpdateDialog.Avalonia の UpdateDialogWindow を表示して更新フロー全体を委譲する。
    /// 検出 → DL → 適用 → 再起動までダイアログ側で完結する。
    ///
    /// Komorebi 互換フロー:
    ///  - manualCheck=false: ViewModel.CheckAsync で 30 秒以内に Available 判定 → 無視タグ照合 → Window 表示
    ///                      UpToDate / Failed / 無視タグ一致 / タイムアウトのいずれも Window を開かない
    ///  - manualCheck=true: ShowAsync 便利メソッドに丸投げ (即 Window 表示、 タイムアウトなし)
    /// </summary>
    private async Task ShowUpdateDialogAsync(bool manualCheck, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        UpdateSettings snapshot;
        lock (_syncLock)
        {
            snapshot = new UpdateSettings
            {
                Enabled = _settings.Enabled,
                IgnoredTagName = _settings.IgnoredTagName
            };
        }

        if (!snapshot.Enabled || string.IsNullOrWhiteSpace(snapshot.UpdateBaseUrl))
        {
            OnStatusChanged(UpdateStatus.Disabled, "更新チェックは無効です。");
            return;
        }

        if (!TryGetValidFeedUri(snapshot.UpdateBaseUrl, out var feedUri, out var validationReason))
        {
            LoggerService.LogError($"UpdateService: 配信元 URL 検証失敗 — {validationReason}");
            OnStatusChanged(UpdateStatus.Failed, validationReason);
            return;
        }

        // 別の Velopack 操作（同プロセス内）が進行中なら:
        //  - 自動チェック: 譲る（次回起動時に拾えるので問題ない）
        //  - 手動チェック: 最大 30 秒待つ（ユーザー操作を空振りで終わらせないため）
        var waitTimeout = manualCheck ? TimeSpan.FromSeconds(30) : TimeSpan.Zero;
        if (!await _velopackOpLock.WaitAsync(waitTimeout, cancellationToken).ConfigureAwait(false))
        {
            LoggerService.LogInfo("UpdateService: 別の Velopack 操作が進行中のため、 今回のチェックはスキップ");
            return;
        }

        OnStatusChanged(UpdateStatus.Checking, "更新を確認しています...");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // SimpleWebSource: Cloudflare R2 (静的 HTTP ホスティング) から `<UpdateBaseUrl>/releases.{channel}.json`
            // を直接 GET し、同ディレクトリの nupkg をダウンロードする。channel はインストール時の値を
            // Velopack が内部記憶して自動再利用するため、明示指定は不要 (win / win-arm64)。
            var source = new SimpleWebSource(feedUri.AbsoluteUri.TrimEnd('/'));
            var manager = new UpdateManager(source);

            // Velopack でインストールされていない場合（開発環境など）はチェックをスキップする。
            // 手動チェック時のみ「最新版です」と扱ってダイアログを出す（Komorebi と同じ挙動）。
            if (!manager.IsInstalled)
            {
                OnStatusChanged(UpdateStatus.Idle, "Velopack でインストールされていないため、 更新確認をスキップします。");
                if (manualCheck)
                {
                    await ShowDialogOnUiThreadAsync(manager, manualCheck, snapshot).ConfigureAwait(false);
                }
                return;
            }

            await ShowDialogOnUiThreadAsync(manager, manualCheck, snapshot).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex.GetType().Name == "AcquireLockFailedException"
                                   && ex.GetType().Namespace?.StartsWith("Velopack", StringComparison.Ordinal) == true)
        {
            // Velopack の .velopack_lock 衝突: 別プロセス（同じインストール先の別バージョン起動など）が
            // 操作中。 次回起動時に自然に再試行されるのでログだけ残す。
            // rere A1-002: 型名文字列マッチに加えて namespace ガードを追加。 別 assembly の同名例外
            // (DLL Hijacking 等) を誤判定しないため。
            LoggerService.LogInfo($"UpdateService: Velopack ロック取得失敗（次回起動時に再試行）— {ex.Message}");
            OnStatusChanged(UpdateStatus.Idle, "別の更新処理が実行中です。後で再試行します。");
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"UpdateService.ShowUpdateDialogAsync 失敗 (manualCheck={manualCheck}): {ex}");
            OnStatusChanged(UpdateStatus.Failed, $"更新チェックに失敗しました: {ex.Message}");
        }
        finally
        {
            _velopackOpLock.Release();
        }
    }

    /// <summary>
    /// Komorebi 互換フローで UpdateDialogWindow を表示する。
    /// 自動チェック時は ViewModel.CheckAsync を 30 秒タイムアウト付きで先回り実行し、 Available 判定
    /// + 無視タグ照合をホスト側で済ませてから Window を開く (UpToDate / Failed / 無視タグなら Window 開かず)。
    /// 手動チェック時は ShowAsync 便利メソッドに丸投げで Window を即表示。
    /// </summary>
    private async Task ShowDialogOnUiThreadAsync(UpdateManager manager, bool manualCheck, UpdateSettings snapshot)
    {
        var options = new UpdateDialogOptions
        {
            ChromeMode = WindowChromeMode.Custom,
            ResizeMode = WindowResizeMode.Fixed,
            AccentBrush = Brushes.DodgerBlue,
            // アプリ全体が日本語 UI なので、 ダイアログ文言も日本語化する (タイトル/ボタン/メッセージ全 8 種)。
            Strings = JapaneseUpdateDialogStrings.Instance,
            // Komorebi 互換: 「このバージョンを無視」ボタンを出して、 押されたらタグを保存。
            AllowIgnoreVersion = true,
            AllowCloseDuringDownload = true,
            // 自動チェックでは「最新版です」ダイアログを出さない (UI を邪魔しない)。
            SuppressUpToDateOnAutoCheck = true,
            // 自動チェック時に「既に無視済みのタグ」をパッケージ側にも伝えて、 Window が開かれないようにする。
            // ホスト側でも先回り判定するが、 二重ガードで確実性を上げる。
            IgnoredTagName = manualCheck ? string.Empty : snapshot.IgnoredTagName,
        };

        // ユーザーが「このバージョンを無視」を押した時、 タグを settings.json に永続化する。
        // 次回起動時の自動チェックで同タグが返ったら、 ホスト側で先回り判定してダイアログを開かない。
        options.VersionIgnored += tag =>
        {
            try
            {
                var current = _settingsMonitor.CurrentValue;
                current.Update.IgnoredTagName = tag ?? string.Empty;
                lock (_syncLock)
                {
                    _settings.IgnoredTagName = tag ?? string.Empty;
                }
                _ = _settingsService.SaveAsync(current);
                LoggerService.LogInfo($"UpdateDialog: ユーザーがバージョン '{tag}' を無視に設定 (settings.json に保存)");
            }
            catch (Exception ex)
            {
                LoggerService.LogException("UpdateDialog: 無視タグの永続化に失敗", ex);
            }
        };

        options.ErrorOccurred += ex => LoggerService.LogException("UpdateDialog エラー", ex);

        if (manualCheck)
        {
            // 手動チェック: パッケージ側の便利メソッドに丸投げ (即 Window 表示 + 並行チェック)。
            // ユーザーがダイアログ前で待てるのでタイムアウトは入れない。
            UpdateDialogResult? result = null;
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var parent = (Application.Current?.ApplicationLifetime
                    as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                result = await UpdateDialogWindow.ShowAsync(parent, manager, options, manualCheck: true).ConfigureAwait(true);
            }).ConfigureAwait(false);

            HandleManualResult(result);
            return;
        }

        // 自動チェック: Komorebi 流フロー (ViewModel.CheckAsync で 30 秒以内に判定 → Available なら Window)。
        // パッケージ側 SuppressUpToDateOnAutoCheck=true + IgnoredTagName でも二重ガードしているが、
        // ホスト側で先回り判定することで「DNS 半切断時の長時間ロック保有」も予防する。
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var parent = (Application.Current?.ApplicationLifetime
                as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

            using var cts = new CancellationTokenSource(AutoCheckTimeout);
            var vm = new UpdateDialogViewModel(manager, options);
            try
            {
                await vm.CheckAsync(manualCheck: false, cts.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                LoggerService.LogInfo($"UpdateService: 自動更新チェックがタイムアウトしました ({AutoCheckTimeout.TotalSeconds:F0} 秒)");
                OnStatusChanged(UpdateStatus.Idle, "更新チェックがタイムアウトしました。");
                return;
            }

            if (vm.State != UpdateState.Available)
            {
                // UpToDate / Failed / Downloading 等: Window を開かずに静かに終了。
                OnStatusChanged(UpdateStatus.Idle, "利用可能な更新はありません。");
                return;
            }

            // 無視タグ一致なら Window を開かない (Komorebi 互換)。
            if (!string.IsNullOrEmpty(snapshot.IgnoredTagName) &&
                string.Equals(vm.AvailableTagName, snapshot.IgnoredTagName, StringComparison.Ordinal))
            {
                LoggerService.LogInfo($"UpdateService: 自動チェックで '{vm.AvailableTagName}' を検出したが、 無視タグと一致するためダイアログをスキップ");
                OnStatusChanged(UpdateStatus.Idle, "このバージョンを無視中です。");
                return;
            }

            // Available かつ無視タグ非一致: Window を表示する。
            var window = new UpdateDialogWindow(vm);
            if (parent != null)
            {
                await window.ShowDialog(parent).ConfigureAwait(true);
            }
            else
            {
                window.Show();
            }
            OnStatusChanged(UpdateStatus.UpdateAvailable, $"更新があります: {vm.AvailableTagName}");
        }).ConfigureAwait(false);
    }

    private void HandleManualResult(UpdateDialogResult? result)
    {
        if (result == null) return;

        switch (result.Outcome)
        {
            case UpdateOutcome.Updated:
                LoggerService.LogInfo("UpdateDialog: 更新完了 (再起動指示済み)");
                OnStatusChanged(UpdateStatus.UpdateAvailable, "更新を適用しました。再起動します...");
                break;
            case UpdateOutcome.UpToDate:
                OnStatusChanged(UpdateStatus.Idle, "利用可能な更新はありません。");
                break;
            case UpdateOutcome.Ignored:
                LoggerService.LogInfo("UpdateDialog: ユーザーが「このバージョンを無視」を選択");
                OnStatusChanged(UpdateStatus.Idle, "このバージョンを無視しました。");
                break;
            case UpdateOutcome.Cancelled:
                LoggerService.LogInfo("UpdateDialog: ダウンロードがキャンセルされました");
                OnStatusChanged(UpdateStatus.Idle, "ダウンロードをキャンセルしました。");
                break;
            case UpdateOutcome.Failed:
                LoggerService.LogError($"UpdateDialog: 失敗 — {result.Error}");
                OnStatusChanged(UpdateStatus.Failed, $"更新に失敗しました: {result.Error?.Message}");
                break;
            case UpdateOutcome.Closed:
                OnStatusChanged(UpdateStatus.Idle, "更新ダイアログを閉じました。");
                break;
        }
    }

    /// <summary>
    /// 配信元 URL (UpdateBaseUrl) の HTTPS + 絶対 URI 形式の防御チェック。
    /// 配信元は <see cref="UpdateSettings.UpdateBaseUrl"/> に [JsonIgnore] でハードコード固定されており
    /// settings.json から書き換え不可だが、万一の不正値を弾く防御として最低限の検証を残す。
    /// rere A2-003: userinfo (user:pass@host) 経由の host spoofing を拒否。
    /// </summary>
    private static bool TryGetValidFeedUri(string feedUrl, out Uri uri, out string reason)
    {
        uri = null!;
        if (!Uri.TryCreate(feedUrl, UriKind.Absolute, out var parsed))
        {
            reason = "配信元 URL が絶対 URI 形式ではありません。";
            return false;
        }
        if (parsed.Scheme != Uri.UriSchemeHttps)
        {
            reason = $"配信元 URL の HTTPS 以外のスキーム（{parsed.Scheme}）は許可されていません。";
            return false;
        }
        // userinfo (user:pass@host) を含む URL を拒否。 host spoofing 防止
        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            reason = "配信元 URL に user-info (user:pass@) を含めることはできません。";
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
