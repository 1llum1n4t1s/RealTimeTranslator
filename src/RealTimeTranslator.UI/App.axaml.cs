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

        // クラッシュ・未捕捉例外を %LocalAppData% のログに残す。
        // Task.Run(...) 経由の例外や Dispatcher の予期しない例外を取りこぼさないための最後の砦。
        AppDomain.CurrentDomain.UnhandledException += (s, ea) =>
        {
            LoggerService.LogError($"AppDomain.UnhandledException: IsTerminating={ea.IsTerminating}: {ea.ExceptionObject}");
            LoggerService.Shutdown();
        };
        TaskScheduler.UnobservedTaskException += (s, ea) =>
        {
            LoggerService.LogError($"TaskScheduler.UnobservedTaskException: {ea.Exception}");
            ea.SetObserved();
        };
        Dispatcher.UIThread.UnhandledException += (s, ea) =>
        {
            LoggerService.LogError($"Dispatcher.UIThread.UnhandledException: {ea.Exception}");
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

            // 起動時更新チェックは fire-and-forget（UI スレッドを最大 31 秒ブロックしない）。
            // AutoApply=true の時のみ自動再起動。false の時は通知だけ出して、ユーザー操作で適用させる
            //（v1.0.3 で AutoApply 設定を無視して強制 ApplyUpdatesAndRestart していたバグの修正）。
            _ = Task.Run(async () =>
            {
                try
                {
                    if (!updateSettings.AutoApply)
                    {
                        // チェックだけ実行。更新検出時は UpdateReady イベント → MainViewModel.OnUpdateReady で
                        // ユーザーに「再起動して適用しますか？」ダイアログを出す。
                        await updateService.CheckOnceAsync(_updateCancellation.Token).ConfigureAwait(false);
                        return;
                    }

                    var shouldRestart = await updateService.CheckAndApplyStartupAsync(_updateCancellation.Token)
                        .ConfigureAwait(false);
                    if (shouldRestart)
                    {
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            LoggerService.LogInfo("OnStartup: 更新適用のためアプリを再起動します。");
                            desktop.Shutdown(0);
                        });
                    }
                }
                catch (OperationCanceledException) { /* shutdown 中 */ }
                catch (Exception ex)
                {
                    LoggerService.LogError($"OnStartup: 更新チェック失敗: {ex}");
                }
            });

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

        try
        {
            LoggerService.LogInfo("OnExit: アプリケーション終了開始");
            _updateCancellation?.Cancel();
            _updateCancellation?.Dispose();
            if (_overlayWindow != null)
            {
                _overlayWindow.Close();
                _overlayWindow = null;
            }

            // サービス破棄にタイムアウトを設定（無限待ちでハング防止）
            if (_serviceProvider != null)
            {
                using var disposeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    var disposeTask = _serviceProvider.DisposeAsync().AsTask();
                    await disposeTask.WaitAsync(disposeCts.Token);
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
            LoggerService.LogError($"シャットダウン中にエラー: {ex}");
        }
        finally
        {
            // 残存スレッドがあっても確実にプロセスを終了
            Environment.Exit(0);
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
            // settings.json に DPAPI 暗号化済みで保存されている API キーを平文化する。
            SettingsService.DecryptApiKeyInPlace(current);
            return current;
        });
        services.AddSingleton<AudioCaptureSettings>(sp =>
            sp.GetRequiredService<AppSettings>().AudioCapture);
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IAudioCaptureService, AudioCaptureService>();
        services.AddSingleton<IRealtimeTranscriber, OpenAIRealtimeClient>();
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
