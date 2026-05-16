using System;
using System.Threading;
using System.Threading.Tasks;
using Velopack;

namespace RealTimeTranslator.UI.Models;

/// <summary>
/// Velopack 更新情報を保持するクラス。SelfUpdateWindow の DataTemplate で
/// 「新バージョン通知 + ProgressBar + ダウンロード＆インストールボタン」表示に分岐させる。
/// </summary>
public class VelopackUpdate
{
    /// <summary>リリースのタグ名（例: v1.0.5）</summary>
    public string TagName => $"v{_updateInfo.TargetFullRelease.Version}";

    /// <summary>バージョン文字列</summary>
    public string VersionString => _updateInfo.TargetFullRelease.Version.ToString();

    public VelopackUpdate(UpdateManager manager, UpdateInfo updateInfo)
    {
        _manager = manager;
        _updateInfo = updateInfo;
    }

    /// <summary>
    /// 更新パッケージを非同期でダウンロードする。進捗 (0-100) は onProgress に通知する。
    /// </summary>
    public async Task DownloadAsync(Action<int> onProgress, CancellationToken token)
    {
        await _manager.DownloadUpdatesAsync(_updateInfo, onProgress, cancelToken: token);
    }

    /// <summary>
    /// ダウンロード済みの更新を適用してアプリケーションを再起動する。
    /// </summary>
    public void ApplyAndRestart()
    {
        _manager.ApplyUpdatesAndRestart(_updateInfo);
    }

    private readonly UpdateManager _manager;
    private readonly UpdateInfo _updateInfo;
}
