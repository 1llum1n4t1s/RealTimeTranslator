using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using RealTimeTranslator.UI.ViewModels;

namespace RealTimeTranslator.UI.Views;

/// <summary>
/// オーバーレイウィンドウ
/// 透明・最前面・クリック透過対応
/// </summary>
public partial class OverlayWindow : Window
{
    // Win32 API for click-through
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int GWL_EXSTYLE = -20;

    // 32bit/64bit互換性のためのAPI呼び出し
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
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, nIndex)
            : GetWindowLongPtr32(hWnd, nIndex);
    }

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : SetWindowLongPtr32(hWnd, nIndex, dwNewLong);
    }

    public OverlayWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public OverlayWindow(OverlayViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // クリック透過を有効化
        SetClickThrough();
    }

    /// <summary>
    /// ウィンドウをクリック透過に設定
    /// </summary>
    private void SetClickThrough()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var extendedStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt32();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(extendedStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED));
    }

    /// <summary>
    /// クリック透過を解除（設定変更時など）
    /// </summary>
    public void DisableClickThrough()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var extendedStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt32();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(extendedStyle & ~WS_EX_TRANSPARENT));
    }

    /// <summary>
    /// クリック透過を再有効化
    /// </summary>
    public void EnableClickThrough()
    {
        SetClickThrough();
    }
}
