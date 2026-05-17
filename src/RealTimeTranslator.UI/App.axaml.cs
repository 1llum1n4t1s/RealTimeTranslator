using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;
using RealTimeTranslator.UI.Services;
using RealTimeTranslator.UI.ViewModels;
using RealTimeTranslator.UI.Views;
using Velopack;

namespace RealTimeTranslator.UI;

/// <summary>
/// アプリケーション
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private OverlayWindow? _overlayWindow;
    private CancellationTokenSource? _updateCancellation;
    private bool _shutdownCleanupDone;

    public App()
    {
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }
        if (Design.IsDesignMode)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        // メインウィンドウを閉じたらアプリ終了（OverlayWindowが残っていても）
        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

        VelopackApp.Build().Run();

        // クラッシュ・未捕捉例外を %APPDATA%/Roaming のログに残す。
        // Task.Run(...) 経由の例外や Dispatcher の予期しない例外を取りこぼさないための最後の砦。
        AppDomain.CurrentDomain.UnhandledException += (s, ea) =>
        {
            LoggerService.LogError($"AppDomain.UnhandledException: IsTerminating={ea.IsTerminating}: {ea.ExceptionObject}");
            LoggerService.Shutdown();
        };
        TaskScheduler.UnobservedTaskException += (s, ea) =>
        {
            LoggerService.LogException("TaskScheduler.UnobservedTaskException", ea.Exception);
            // SuperLightLogger のバッファに残ったログを確実に flush する (rere P1 #15)。
            // Shutdown 後でも Log API は auto re-init するため後続ログは継続可能。
            LoggerService.Shutdown();
            ea.SetObserved();
        };
        Dispatcher.UIThread.UnhandledException += (s, ea) =>
        {
            LoggerService.LogException("Dispatcher.UIThread.UnhandledException", ea.Exception);
            // UI スレッド例外で次の crash が起きる前に flush。 Handled=true で継続するが、
            // この例外時点までのログを必ずディスクに残す (rere P1 #15)。
            LoggerService.Shutdown();
            ea.Handled = true;
        };

        try
        {
            // ログ出力先は Velopack 管理外の %APPDATA%/Roaming/RealTimeTranslator/logs に固定。
            // %LocalAppData%/RealTimeTranslator は Velopack のインストールルートと衝突するため使わない。
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RealTimeTranslator",
                "logs");
            LoggerService.Initialize(new LoggerConfig
            {
                LogDirectory = logDirectory,
                FilePrefix = "RealTimeTranslator"
            });

            // インシデント解析に必須の OS / .NET / CPU / バージョン情報をログ冒頭に必ず残す。
            LoggerService.LogStartup();
            LoggerService.LogInfo("OnStartup: 起動開始");
            RegisterApplicationInARP();
            LoggerService.LogInfo("OnStartup: ARP登録完了");

            var services = new ServiceCollection();
            ConfigureServices(services);
            _serviceProvider = services.BuildServiceProvider();
            LoggerService.LogInfo("OnStartup: DI構築完了");

            var updateService = _serviceProvider.GetRequiredService<IUpdateService>();
            var optionsMonitor = _serviceProvider.GetRequiredService<IOptionsMonitor<AppSettings>>();
            var updateSettings = optionsMonitor.CurrentValue.Update;
            updateService.UpdateSettings(updateSettings);
            _updateCancellation = new CancellationTokenSource();

            // Komorebi 互換: 起動時に 1 回だけ自動チェック (周期チェックなし、 30 秒タイムアウト)。
            // 検出 → DL → Apply → Restart まで VelopackUpdateDialog.Avalonia の UpdateDialogWindow で完結する
            // (v1.0.12 で自前 SelfUpdateWindow を完全置換済み)。
            _ = updateService.StartAsync(_updateCancellation.Token).ContinueWith(t =>
            {
                if (t.IsFaulted)
                    LoggerService.LogError($"OnStartup: 更新サービスエラー: {t.Exception}");
            }, TaskScheduler.Default);
            LoggerService.LogInfo("OnStartup: 更新サービス開始");

            var mainViewModel = _serviceProvider.GetRequiredService<MainViewModel>();
            var mainWindow = new MainWindow(mainViewModel);
            desktop.MainWindow = mainWindow;
            LoggerService.LogInfo("OnStartup: メインウィンドウ表示");

            var overlayViewModel = _serviceProvider.GetRequiredService<OverlayViewModel>();
            _overlayWindow = new OverlayWindow(overlayViewModel);
            _overlayWindow.Show();
            LoggerService.LogInfo("OnStartup: オーバーレイウィンドウ表示");

            LoggerService.LogInfo("OnStartup: 起動完了");
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"アプリケーション起動エラー: {ex}");
            _ = Services.MessageBoxService.ShowAsync("エラー",
                $"アプリケーション起動に失敗しました:\n\n{ex.Message}\n\n{ex.StackTrace}");
            desktop.Shutdown(1);
        }

        desktop.ShutdownRequested += OnShutdownRequested;
        base.OnFrameworkInitializationCompleted();
    }

    private async void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (_shutdownCleanupDone)
            return;

        e.Cancel = true;
        _shutdownCleanupDone = true;

        // どんな経路を通っても最終的にプロセスを強制終了する保険を発動させる。
        // 内側 finally → Environment.Exit(0)、それも効かなければ 5 秒後にこの保険が
        // Process.Kill() で確実にプロセスを落とす（NAudio MMCSS スレッド等が
        // non-background で残ってプロセスが exit しない事故対策）。
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            try { System.Diagnostics.Process.GetCurrentProcess().Kill(); }
            catch { /* best effort */ }
        });

        try
        {
            LoggerService.LogInfo("OnExit: アプリケーション終了開始");
            _updateCancellation?.Cancel();
            _updateCancellation?.Dispose();

            // 明示的に audio capture を停止する（NAudio の WASAPI スレッドを確実に解放）
            try
            {
                _serviceProvider?.GetService<IAudioCaptureService>()?.StopCapture();
            }
            catch (Exception ex) { LoggerService.LogWarning($"OnExit: AudioCapture 停止失敗: {ex.Message}"); }

            if (_overlayWindow != null)
            {
                // OverlayViewModel.Dispose を明示呼び出し。 ServiceProvider.DisposeAsync の
                // 2 秒タイムアウト経路では Singleton Dispose が走らないことがあるため、
                // DispatcherTimer / settings subscription の解放をここで保証する (rere P1 #13)。
                try { (_overlayWindow.DataContext as IDisposable)?.Dispose(); }
                catch (Exception ex) { LoggerService.LogWarning($"OnExit: OverlayViewModel Dispose 失敗: {ex.Message}"); }

                _overlayWindow.Close();
                _overlayWindow = null;
            }

            // サービス破棄にタイムアウトを設定（無限待ちでハング防止、2 秒に短縮）
            if (_serviceProvider != null)
            {
                using var disposeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    var disposeTask = _serviceProvider.DisposeAsync().AsTask();
                    await disposeTask.WaitAsync(disposeCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    LoggerService.LogWarning("OnExit: サービス破棄がタイムアウトしました");
                }
                _serviceProvider = null;
            }

            LoggerService.Shutdown();
        }
        catch (Exception ex)
        {
            try { LoggerService.LogError($"シャットダウン中にエラー: {ex}"); } catch { }
        }
        finally
        {
            // 残存スレッドがあっても確実にプロセスを終了
            try { Environment.Exit(0); }
            catch
            {
                try { System.Diagnostics.Process.GetCurrentProcess().Kill(); } catch { }
            }
        }
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // settings.json の旧パス（exe 隣接）から %LocalAppData%/RealTimeTranslator に移行する。
        // Velopack の `app-x.y.z` フォルダ切り替えで設定が失われる問題を防ぐ。
        SettingsService.MigrateLegacySettingsIfNeeded();

        var configurationBuilder = new ConfigurationBuilder()
            .SetBasePath(SettingsService.SettingsDirectory)
            .AddJsonFile("settings.json", optional: true, reloadOnChange: true);
        var configuration = configurationBuilder.Build();
        LoggerService.LogInfo("ConfigureServices: 設定ファイルを読み込み完了");
        services.AddSingleton<IConfiguration>(configuration);
        services.Configure<AppSettings>(configuration);
        services.AddSingleton<AppSettings>(sp =>
        {
            var current = sp.GetRequiredService<IOptionsMonitor<AppSettings>>().CurrentValue;
            // settings.json に DPAPI 暗号化済みで保存されている API キーを平文化する
            // (rere B1-003 完遂: static 直叩きを ISettingsService DI 経由に統一)。
            sp.GetRequiredService<ISettingsService>().DecryptApiKey(current);
            return current;
        });
        services.AddSingleton<AudioCaptureSettings>(sp =>
            sp.GetRequiredService<AppSettings>().AudioCapture);
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IAudioCaptureService, AudioCaptureService>();
        services.AddSingleton<IRealtimeTranscriber, OpenAIRealtimeClient>();
        // Silero VAD (ONNX 推論セッション)。 onnx ファイルは Assets/silero_vad.onnx に同梱。
        // Singleton にすることで onnx ロード (~10ms) は起動時 1 回のみ。 LSTM state は
        // TranslationPipelineService が Start のたびに Reset を呼んでクリアする。
        services.AddSingleton<IVoiceActivityDetector, SileroVadDetector>();
        services.AddSingleton<IUpdateService, UpdateService>();
        services.AddSingleton<ITranslationPipelineService, TranslationPipelineService>();
        services.AddSingleton<OverlayViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton(sp => new SettingsViewModel(
            sp.GetRequiredService<IOptionsMonitor<AppSettings>>(),
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<OverlayViewModel>()));
    }

    private static void RegisterApplicationInARP()
    {
        try
        {
            var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var exeDir = Path.GetDirectoryName(exePath);
            var appVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0.0";
            var registryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\RealTimeTranslator";
            using var key = Registry.CurrentUser.CreateSubKey(registryPath);
            if (key != null)
            {
                key.SetValue("DisplayName", "RealTimeTranslator", RegistryValueKind.String);
                key.SetValue("DisplayVersion", appVersion, RegistryValueKind.String);
                key.SetValue("Publisher", "1llum1n4t1s", RegistryValueKind.String);
                if (exeDir != null)
                    key.SetValue("InstallLocation", exeDir, RegistryValueKind.String);
                key.SetValue("UninstallString", exePath, RegistryValueKind.String);
                key.SetValue("DisplayIcon", exePath, RegistryValueKind.String);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                LoggerService.LogInfo("RegisterApplicationInARP: ARP登録成功");
            }
        }
        catch (Exception ex)
        {
            LoggerService.LogWarning($"RegisterApplicationInARP: ARP登録エラー: {ex.Message}");
        }
    }
}
