using Avalonia;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace RealTimeTranslator.UI;

/// <summary>
/// アプリケーションエントリポイント（起動トリガーはここ1か所のみ）
/// </summary>
internal static class Program
{
    private const string SingleInstanceMutexName = "Global\\RealTimeTranslator_SingleInstance_7B3F9E2A";

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    private const int SW_RESTORE = 9;

    [STAThread]
    public static void Main(string[] args)
    {
        var pid = Environment.ProcessId;
        var mutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
        try
        {
            if (!createdNew)
            {
                var acquired = false;
                try
                {
                    acquired = mutex.WaitOne(0, false);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }
                if (!acquired)
                {
                    System.Diagnostics.Trace.WriteLine($"[SingleInstance] PID={pid}: 既存のインスタンスを前面に表示して終了します");
                    BringExistingWindowToFront();
                    return;
                }
            }
            System.Diagnostics.Trace.WriteLine($"[SingleInstance] PID={pid}: Mutex を取得し起動します");
            try
            {
                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            }
            finally
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch (ObjectDisposedException) { }
            }
        }
        finally
        {
            mutex.Dispose();
        }
    }

    private static void BringExistingWindowToFront()
    {
        var currentId = Process.GetCurrentProcess().Id;
        var processName = Process.GetCurrentProcess().ProcessName;
        foreach (var process in Process.GetProcessesByName(processName))
        {
            if (process.Id == currentId)
                continue;
            var hwnd = process.MainWindowHandle;
            if (hwnd == IntPtr.Zero)
                continue;
            if (IsIconic(hwnd))
                ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
            break;
        }
    }

    /// <summary>
    /// Avalonia アプリケーションのビルダー
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}
