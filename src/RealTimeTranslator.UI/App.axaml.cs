using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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

        try
        {
            LoggerService.LogInfo("OnStartup: 起動開始");
            RegisterApplicationInARP();
            LoggerService.LogInfo("OnStartup: ARP登録完了");

            var services = new ServiceCollection();
            ConfigureServices(services);
            _serviceProvider = services.BuildServiceProvider();
            LoggerService.LogInfo("OnStartup: DI構築完了");

            var updateService = _serviceProvider.GetRequiredService<IUpdateService>();
            var optionsMonitor = _serviceProvider.GetRequiredService<IOptionsMonitor<AppSettings>>();
            updateService.UpdateSettings(optionsMonitor.CurrentValue.Update);
            _updateCancellation = new CancellationTokenSource();

            var updateCheckTask = Task.Run(async () =>
                await updateService.CheckAndApplyStartupAsync(_updateCancellation.Token));
            var waitTask = Task.Run(() => updateCheckTask.Wait(TimeSpan.FromSeconds(30)));
            if (!waitTask.Wait(TimeSpan.FromSeconds(31)))
            {
                LoggerService.LogWarning("OnStartup: 更新チェックがタイムアウトしました。");
            }
            else if (updateCheckTask.IsCompletedSuccessfully && updateCheckTask.Result)
            {
                LoggerService.LogInfo("OnStartup: 更新適用のためアプリを再起動します。");
                desktop.Shutdown(0);
                return;
            }

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
        var configurationBuilder = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile("settings.json", optional: true, reloadOnChange: true);
        var configuration = configurationBuilder.Build();
        LoggerService.LogInfo("ConfigureServices: 設定ファイルを読み込み完了");
        services.AddSingleton<IConfiguration>(configuration);
        services.Configure<AppSettings>(configuration);
        services.AddSingleton<AppSettings>(sp =>
            sp.GetRequiredService<IOptionsMonitor<AppSettings>>().CurrentValue);
        services.AddSingleton<AudioCaptureSettings>(sp =>
            sp.GetRequiredService<AppSettings>().AudioCapture);
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IAudioCaptureService, AudioCaptureService>();
        services.AddSingleton<OpenAIRealtimeClient>();
        services.AddSingleton<IOpenAIRealtimeClient>(sp => sp.GetRequiredService<OpenAIRealtimeClient>());
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
