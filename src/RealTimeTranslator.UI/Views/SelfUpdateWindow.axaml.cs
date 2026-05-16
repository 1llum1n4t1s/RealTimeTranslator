using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using RealTimeTranslator.UI.Models;
using RealTimeTranslator.UI.ViewModels;

namespace RealTimeTranslator.UI.Views;

/// <summary>
/// 自動更新（Velopack）ダイアログ。Komorebi の SelfUpdate と同じレイアウト・挙動。
/// DataContext には SelfUpdateViewModel を渡し、その Data プロパティで 3 パターンを切り替える。
/// </summary>
public partial class SelfUpdateWindow : Window
{
    public SelfUpdateWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
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
