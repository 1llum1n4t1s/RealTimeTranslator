using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using RealTimeTranslator.Core.Services;
using RealTimeTranslator.UI.ViewModels;

namespace RealTimeTranslator.UI.Views;

/// <summary>
/// オーバーレイウィンドウ
/// 透明・最前面・クリック透過対応
/// </summary>
public partial class OverlayWindow : Window
{
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int GWL_EXSTYLE = -20;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
    {
        return IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLongPtr32(hWnd, nIndex);
    }

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
        return IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : SetWindowLongPtr32(hWnd, nIndex, dwNewLong);
    }

    // ── 位置編集モードのドラッグ状態 (View ローカル状態なので code-behind で完結) ──
    private OverlayViewModel? _vm;
    private Border? _editSample;
    private bool _isDragging;
    private Point _dragStartPointer;     // ウィンドウ座標でのドラッグ開始位置
    private double _dragStartOffsetX;     // ドラッグ開始時の字幕オフセット
    private double _dragStartOffsetY;

    public OverlayWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    public OverlayWindow(OverlayViewModel viewModel) : this()
    {
        DataContext = viewModel;
        _vm = viewModel;
        // 編集モードの ON/OFF に合わせてクリック透過を切替える (編集中だけドラッグ可能にする)。
        viewModel.PositionEditModeChanged += OnPositionEditModeChanged;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        // 透過が有効か確認し、無効ならウィンドウを非表示にして操作不能を防止
        if (ActualTransparencyLevel == WindowTransparencyLevel.None)
        {
            LoggerService.LogWarning("OverlayWindow: 透過レベルが None — ウィンドウを非表示にします（デスクトップ黒画面防止）");
            IsVisible = false;
            return;
        }

        // 背景が透過であることを明示的に保証
        Background = Brushes.Transparent;
        SetClickThrough();

        // 編集用サンプル字幕のドラッグハンドラを結線する。
        _editSample = this.FindControl<Border>("EditSample");
        if (_editSample != null)
        {
            _editSample.PointerPressed += OnEditSamplePointerPressed;
            _editSample.PointerMoved += OnEditSamplePointerMoved;
            _editSample.PointerReleased += OnEditSamplePointerReleased;
        }

        LoggerService.LogInfo($"OverlayWindow: 透過レベル={ActualTransparencyLevel}, クリック透過設定完了");
    }

    // ───────── 位置編集モード ─────────

    private void OnPositionEditModeChanged(object? sender, bool editing)
    {
        if (editing)
        {
            // 編集中はマウスを受け取れるようにクリック透過を解除し、 最前面へ。
            DisableClickThrough();
            Activate();
        }
        else
        {
            _isDragging = false;
            // 編集終了で再びクリック透過 (字幕は背景として振る舞う) に戻す。
            SetClickThrough();
        }
    }

    private void OnEditSamplePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm is null || !_vm.IsPositionEditMode) return;
        _isDragging = true;
        _dragStartPointer = e.GetPosition(this);
        _dragStartOffsetX = _vm.SubtitleOffsetX;
        _dragStartOffsetY = _vm.SubtitleOffsetY;
        e.Pointer.Capture(_editSample);
        e.Handled = true;
    }

    private void OnEditSamplePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging || _vm is null) return;
        var p = e.GetPosition(this);
        var dx = p.X - _dragStartPointer.X;
        var dy = p.Y - _dragStartPointer.Y;
        // ドラッグ中は live 反映のみ (persist=false)。 保存は「確定」ボタンで行う。
        _vm.UpdateSubtitleOffset(_dragStartOffsetX + dx, _dragStartOffsetY + dy, persist: false);
    }

    private void OnEditSamplePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void SetClickThrough()
    {
        var handle = TryGetPlatformHandle();
        if (handle?.Handle == null || handle.Handle == IntPtr.Zero)
        {
            LoggerService.LogWarning("OverlayWindow: ウィンドウハンドルが取得できません — クリック透過を設定できません");
            return;
        }
        var hwnd = handle.Handle;
        var extendedStyle = (int)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)(extendedStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED));
    }

    public void DisableClickThrough()
    {
        var handle = TryGetPlatformHandle();
        if (handle?.Handle == null || handle.Handle == IntPtr.Zero)
            return;
        var hwnd = handle.Handle;
        var extendedStyle = (int)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)(extendedStyle & ~WS_EX_TRANSPARENT));
    }

    public void EnableClickThrough()
    {
        SetClickThrough();
    }
}
