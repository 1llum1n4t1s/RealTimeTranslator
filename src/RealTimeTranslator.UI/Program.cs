using Avalonia;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using RealTimeTranslator.UI.Services;
using Velopack;

namespace RealTimeTranslator.UI;

/// <summary>
/// アプリケーションエントリポイント（起動トリガーはここ1か所のみ）
/// </summary>
internal static class Program
{
    // 単一インスタンス Mutex はユーザーセッション単位で十分（BringExistingWindowToFront も現セッション内のプロセスしか列挙しないため）。
    // Global\ は別ユーザーからの DoS スプーフィングを許してしまうので Local\ を使う。
    private const string SingleInstanceMutexName = "Local\\RealTimeTranslator_SingleInstance_7B3F9E2A";
    private const string AppUserModelId = "velopack.RealTimeTranslator";

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    private const int SW_RESTORE = 9;

    [STAThread]
    public static void Main(string[] args)
    {
        try { _ = SetCurrentProcessExplicitAppUserModelID(AppUserModelId); }
        catch { /* シェル連携の失敗だけで起動を止めない */ }

        // Velopack のインストール・更新フックは、Avalonia と多重起動ガードより先に処理する。
        var velopackApp = VelopackApp.Build();
        if (OperatingSystem.IsWindows())
        {
            velopackApp
                .OnAfterInstallFastCallback(_ => WindowsLegacyStartMenuShortcutMigrator.MigrateForCurrentUser())
                .OnAfterUpdateFastCallback(_ => WindowsLegacyStartMenuShortcutMigrator.MigrateForCurrentUser());
        }

        velopackApp.Run();

        // 高速フックが一時的なファイルロック等で失敗した場合も、通常起動時に再試行する。
        if (OperatingSystem.IsWindows())
        {
            WindowsLegacyStartMenuShortcutMigrator.MigrateForCurrentUser();
        }

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
        // rere B-1: Avalonia.Fonts.Inter は App.axaml の FontFamily チェーンに含まれず実利用ゼロのため削除。
        // 同梱日本語フォント 5 種 (avares://.../Assets/Fonts/) + システム既定 (Yu Gothic UI 等) で代替可能。
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
    }
}
