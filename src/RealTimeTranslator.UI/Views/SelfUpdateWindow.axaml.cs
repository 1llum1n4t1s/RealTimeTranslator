using Avalonia.Controls;
using Avalonia.Interactivity;
using RealTimeTranslator.UI.Models;
using RealTimeTranslator.UI.ViewModels;

namespace RealTimeTranslator.UI.Views;

/// <summary>
/// 自動更新（Velopack）ダイアログ。Komorebi の SelfUpdate と同じレイアウト・挙動。
/// DataContext には SelfUpdateViewModel を渡し、その Data プロパティで 3 パターンを切り替える。
///
/// ⚠️ InitializeComponent / AvaloniaXamlLoader.Load の手動定義は厳禁。
/// Avalonia 12 SDK が `.axaml.g.cs` で自動生成する partial method を上書きしてしまい、
/// XAML 内 `x:DataType` の型解決テーブル (NameScope / XAML compiler 経路) が消えて
/// runtime に "Unable to resolve type vm:SelfUpdateViewModel" 例外になる (v1.0.9 / v1.0.10 で実発生)。
/// MainWindow.axaml.cs と同じく InitializeComponent() のシグネチャだけ呼んで実体は SDK 任せにする。
/// </summary>
public partial class SelfUpdateWindow : Window
{
    public SelfUpdateWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// ウィンドウが閉じられる際の処理。進行中のダウンロードがあればキャンセルする。
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (DataContext is SelfUpdateViewModel vm)
            vm.CancelDownload();
    }

    /// <summary>
    /// 「閉じる」ボタンのハンドラ。
    /// </summary>
    private void CloseWindow(object? _1, RoutedEventArgs _2)
    {
        Close();
    }

    /// <summary>
    /// 「ダウンロード＆インストール」ボタンのハンドラ。ViewModel に処理を委譲する。
    /// </summary>
    private void DownloadAndInstall(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: VelopackUpdate update } &&
            DataContext is SelfUpdateViewModel vm)
        {
            vm.DownloadAndApplyUpdate(update);
        }

        e.Handled = true;
    }
}
