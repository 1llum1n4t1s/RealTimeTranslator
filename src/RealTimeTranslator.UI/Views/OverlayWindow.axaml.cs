using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
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

    public OverlayWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
    }

    public OverlayWindow(OverlayViewModel viewModel) : this()
    {
        DataContext = viewModel;
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

        LoggerService.LogInfo($"OverlayWindow: 透過レベル={ActualTransparencyLevel}, クリック透過設定完了");
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
