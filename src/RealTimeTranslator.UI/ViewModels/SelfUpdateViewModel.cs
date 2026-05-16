using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using RealTimeTranslator.Core.Services;
using RealTimeTranslator.UI.Models;

namespace RealTimeTranslator.UI.ViewModels;

/// <summary>
/// SelfUpdateWindow の ViewModel。Velopack を使ってダウンロード進捗と Apply&Restart を制御する。
/// Komorebi の ViewModels.SelfUpdate と同じ責務。
/// </summary>
public partial class SelfUpdateViewModel : ObservableObject
{
    /// <summary>
    /// アップデート関連のデータ。VelopackUpdate / AlreadyUpToDate / SelfUpdateFailed のいずれかを保持し、
    /// View 側の DataTemplate でテンプレートが切り替わる。
    /// </summary>
    [ObservableProperty]
    private object? _data;

    /// <summary>
    /// アップデートのダウンロード中かどうか。ProgressBar 表示制御に使う。
    /// </summary>
    [ObservableProperty]
    private bool _isDownloading;

    /// <summary>
    /// ダウンロードの進捗率（0-100）。
    /// </summary>
    [ObservableProperty]
    private int _downloadProgress;

    private CancellationTokenSource? _cts;

    /// <summary>
    /// アップデートをダウンロードして適用する。ダウンロード完了後にアプリを再起動する。
    /// 既にダウンロード中の場合は何もしない。
    /// </summary>
    public void DownloadAndApplyUpdate(VelopackUpdate update)
    {
        if (IsDownloading)
            return;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsDownloading = true;
        DownloadProgress = 0;

        Task.Run(async () =>
        {
            try
            {
                await update.DownloadAsync(
                    p => Dispatcher.UIThread.Post(() => DownloadProgress = p),
                    token);

                update.ApplyAndRestart();
            }
            catch (OperationCanceledException)
            {
                Dispatcher.UIThread.Post(() => IsDownloading = false);
            }
            catch (Exception e)
            {
                LoggerService.LogError($"アップデートダウンロード失敗: {e}");
                Dispatcher.UIThread.Post(() =>
                {
                    IsDownloading = false;
                    Data = new Core.Models.SelfUpdateFailed(e);
                });
            }
        });
    }

    /// <summary>
    /// 進行中のダウンロードをキャンセルする。Window の Closing で呼ぶ。
    /// </summary>
    public void CancelDownload()
    {
        _cts?.Cancel();
    }
}
