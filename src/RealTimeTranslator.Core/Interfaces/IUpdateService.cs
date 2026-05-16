using System;
using System.Threading;
using System.Threading.Tasks;
using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.Core.Interfaces;

public interface IUpdateService
{
    /// <summary>
    /// 更新状態の変化を通知する (Idle / Disabled / Checking / UpdateAvailable / Failed)。
    /// UI 側の StatusText 表示に使う。
    /// </summary>
    event EventHandler<UpdateStatusChangedEventArgs>? StatusChanged;

    void UpdateSettings(UpdateSettings settings);

    /// <summary>
    /// 起動直後の 1 回目チェック + 周期チェックを開始する。
    /// 検出時には自動でアップデートダイアログ (VelopackUpdateDialog) を開く。
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 手動チェック (「更新の確認」ボタン経由)。
    /// 結果に応じてアップデートダイアログを開き、 ユーザーに DL/Apply or 閉じるを選ばせる。
    /// 最新版 / 失敗時もダイアログでフィードバックを返す (Komorebi の手動チェック挙動)。
    /// </summary>
    Task CheckForUpdateAsync(CancellationToken cancellationToken);
}
