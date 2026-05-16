using System;
using System.Threading;
using System.Threading.Tasks;
using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.Core.Interfaces;

public interface IUpdateService
{
    event EventHandler<UpdateStatusChangedEventArgs>? StatusChanged;

    /// <summary>
    /// 自動チェックで更新が検出されたときに発火する。UI 側は SelfUpdateWindow を開く。
    /// 手動チェックは戻り値経由で結果を受け取るため、このイベントは発火しない。
    /// </summary>
    event EventHandler<UpdateAvailableEventArgs>? UpdateAvailable;

    void UpdateSettings(UpdateSettings settings);

    /// <summary>
    /// 起動直後の 1 回目チェック + 周期チェックを開始する。
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 更新有無を確認する。結果オブジェクト (UI.Models.VelopackUpdate / AlreadyUpToDate / SelfUpdateFailed)
    /// を返す。Komorebi の Check4Update(bool manually) と同じ責務。
    /// </summary>
    /// <param name="manually">
    /// true: 手動チェック。最新時もエラー時も AlreadyUpToDate / SelfUpdateFailed を返してダイアログ表示させる。
    /// false: 自動チェック。VelopackUpdate のときだけ UpdateAvailable を発火し、結果は基本 null を返す。
    /// </param>
    Task<object?> Check4UpdateAsync(bool manually, CancellationToken cancellationToken);
}
