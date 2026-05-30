using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using RealTimeTranslator.UI.ViewModels;

namespace RealTimeTranslator.UI.Views;

/// <summary>
/// メインウィンドウ
/// </summary>
public partial class MainWindow : Window
{
    // ── 翻訳ログの Discord 風オートスクロール ──
    // スクロール位置 (Offset / Extent / Viewport) は純粋に View 側の状態なので、
    // ViewModel には持たせず code-behind で完結させる (OverlayWindow の Win32 制御と同じ「UI ロジックは code-behind」方針)。

    /// <summary>最下部にいるとみなす許容距離 (px)。末尾からこの範囲内なら追従を継続する。</summary>
    private const double TranslationLogStickyThresholdPx = 48;

    /// <summary>
    /// 翻訳ログを最下部に追従中か。ユーザーが上にスクロールすると false になり、
    /// 最下部付近 (しきい値以内) に戻ると true に復帰する。
    /// 初期 true: タブ初表示・履歴ロード後は最新 (最下部) を表示する。
    /// </summary>
    private bool _translationLogSticky = true;

    // 翻訳ログタブの ScrollViewer / 「↓ 最新へ」ボタンのキャッシュ。
    // ScrollChanged は頻繁に発火するため毎回 FindControl (ツリー線形探索) するのは無駄。
    // TabControl の realize ごとに OnLogScrollViewerLoaded で取り直す (再 realize でインスタンスが変わるため)。
    private ScrollViewer? _logScrollViewer;
    private Button? _jumpToLatestButton;

    public MainWindow()
    {
        InitializeComponent();
    }

    private MainViewModel? _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;

        // 前回保存したウィンドウサイズを復元 (未保存なら axaml の既定 750x800 のまま)。
        var saved = viewModel.GetSavedWindowSize();
        if (saved is { } s)
        {
            Width = s.Width;
            Height = s.Height;
        }

        // ユーザーがリサイズしたら debounce して settings.json に保存する。
        // PropertyChanged で ClientSize/Width/Height を監視 (Avalonia は Window のサイズ変更を Bounds/ClientSize で通知)。
        ClientSizeProperty.Changed.AddClassHandler<MainWindow>((w, _) => w.OnWindowSizeChanged());
    }

    /// <summary>
    /// リサイズ完了の検知。 通常表示 (Normal) のときだけ現在の Width/Height を保存する
    /// (最大化・最小化中のサイズは復元用に保存しない)。
    /// </summary>
    private void OnWindowSizeChanged()
    {
        if (_viewModel is null) return;
        if (WindowState != WindowState.Normal) return;
        // Width/Height は NaN になり得る (未設定時) ため、 確定値の ClientSize ベースではなく
        // 実寸の Bounds を使う。 Bounds はクライアント領域、 Width/Height はウィンドウ全体だが、
        // 復元は同じ Width/Height へ書き戻すので Width/Height をそのまま保存して対称にする。
        var w = double.IsNaN(Width) ? Bounds.Width : Width;
        var h = double.IsNaN(Height) ? Bounds.Height : Height;
        _viewModel.SaveWindowSize(w, h);
    }

    /// <summary>
    /// 翻訳ログタブの ScrollViewer が実体化されたとき (TabControl は選択タブのみ realize するため、
    /// タブを開くたびに発火する)。追従中なら最下部へスクロールして「タブを開くと最新が見える」を実現する。
    /// </summary>
    private void OnLogScrollViewerLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;

        // realize ごとにコントロール参照をキャッシュ (以降 ScrollChanged 等で FindControl しない)。
        _logScrollViewer = sv;
        _jumpToLatestButton = this.FindControl<Button>("JumpToLatestButton");

        if (_translationLogSticky)
        {
            // レイアウト確定後にスクロールしたいので Loaded プライオリティで 1 フレーム遅らせる。
            Dispatcher.UIThread.Post(() =>
            {
                ScrollToBottom(sv);
                UpdateJumpToLatestButton();
            }, DispatcherPriority.Loaded);
        }
        else
        {
            UpdateJumpToLatestButton();
        }
    }

    /// <summary>
    /// ScrollViewer の Offset / Extent / Viewport いずれかが変化したとき。
    /// 新着 (Extent 増加) かつ追従中なら最下部へ追従し、現在位置から追従状態を再判定する。
    /// </summary>
    private void OnLogScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;

        // コンテンツが伸びた (新着 or 履歴ロード) かつ追従中なら最下部へ追従する。
        // 追従が外れている (上にスクロール中) ときは新着が来ても位置を動かさない = Discord 挙動。
        if (e.ExtentDelta.Y > 0 && _translationLogSticky)
        {
            ScrollToBottom(sv);
        }

        // 現在位置から「最下部付近にいるか」を再判定して追従状態とボタン状態を更新する。
        RecomputeSticky(sv);
        UpdateJumpToLatestButton();
    }

    /// <summary>「↓ 最新へ」ボタン: 最下部へジャンプして追従を再開する。</summary>
    private void OnJumpToLatestClick(object? sender, RoutedEventArgs e)
    {
        // ボタンが押せる時点で ScrollViewer は realize 済み (= キャッシュ済み)。
        if (_logScrollViewer is null) return;

        _translationLogSticky = true;
        ScrollToBottom(_logScrollViewer);
        UpdateJumpToLatestButton();
    }

    /// <summary>現在のスクロール位置から追従状態 (<see cref="_translationLogSticky"/>) を再計算する。</summary>
    private void RecomputeSticky(ScrollViewer sv)
    {
        // コンテンツがビューポートに収まる (スクロール不要) ときは常に追従扱い。
        if (sv.Extent.Height <= sv.Viewport.Height)
        {
            _translationLogSticky = true;
            return;
        }

        double distanceFromBottom = sv.Extent.Height - (sv.Offset.Y + sv.Viewport.Height);
        _translationLogSticky = distanceFromBottom <= TranslationLogStickyThresholdPx;
    }

    /// <summary>「↓ 最新へ」ボタンの有効/無効を追従状態に同期する (追従中は押す意味がないので無効)。</summary>
    private void UpdateJumpToLatestButton()
    {
        if (_jumpToLatestButton is null) return;

        _jumpToLatestButton.IsEnabled = !_translationLogSticky;
    }

    /// <summary>ScrollViewer を最下部までスクロールする (Offset を最大値に設定。ScrollViewer 側でクランプされる)。</summary>
    private static void ScrollToBottom(ScrollViewer sv)
    {
        double maxY = Math.Max(0, sv.Extent.Height - sv.Viewport.Height);
        sv.Offset = new Vector(sv.Offset.X, maxY);
    }
}
