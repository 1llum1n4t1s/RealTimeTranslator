using VelopackUpdateDialog;

namespace RealTimeTranslator.UI.Services;

/// <summary>
/// VelopackUpdateDialog.Avalonia の自動更新ダイアログを日本語表示にするための翻訳セット。
/// `UpdateDialogOptions.Strings` プロパティに渡して使う (UpdateService 参照)。
///
/// 既定 (英語) の表示文字列:
///   Title              = "Self Update"
///   AvailableHeader    = "New version available!"
///   DownloadAndInstall = "Download and install"
///   IgnoreThisVersion  = "Ignore this version"
///   UpToDateMessage    = "You're using the latest version."
///   ErrorHeader        = "Self update failed"
///   Close              = "Close"
///   CheckingMessage    = "Checking for updates..."
///
/// アプリ全体が日本語固定 UI のため、 ダイアログ文言も常に日本語で返す。
/// </summary>
public sealed class JapaneseUpdateDialogStrings : IUpdateDialogStrings
{
    /// <summary>共有シングルトン。 アプリ全体で 1 インスタンスを使い回す (DefaultStrings.Instance と同じパターン)。</summary>
    public static readonly JapaneseUpdateDialogStrings Instance = new();

    private JapaneseUpdateDialogStrings() { }

    public string Title => "更新の確認";
    public string AvailableHeader => "新しいバージョンがあります";
    public string DownloadAndInstall => "ダウンロードしてインストール";
    public string IgnoreThisVersion => "このバージョンを無視";
    public string UpToDateMessage => "現在のバージョンが最新版です。";
    public string ErrorHeader => "更新に失敗しました";
    public string Close => "閉じる";
    public string CheckingMessage => "更新を確認中…";
}
