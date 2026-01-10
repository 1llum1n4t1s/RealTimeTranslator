using System;
using System.IO;
using System.Threading;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using RealTimeTranslator.ASR.Services;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Translation.Services;
using RealTimeTranslator.UI.Services;
using RealTimeTranslator.UI.ViewModels;
using RealTimeTranslator.UI.Views;
using Velopack;

namespace RealTimeTranslator.UI;

/// <summary>
/// アプリケーションエントリポイント
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private OverlayWindow? _overlayWindow;
    private CancellationTokenSource? _updateCancellation;

    protected override void OnStartup(StartupEventArgs e)
    {
        VelopackApp.Build().Run();
        base.OnStartup(e);

        // 設定を読み込み
        var settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
        var settings = AppSettings.Load(settingsPath);

        // DIコンテナを構築
        var services = new ServiceCollection();
        ConfigureServices(services, settings, settingsPath);
        _serviceProvider = services.BuildServiceProvider();

        var updateService = _serviceProvider.GetRequiredService<IUpdateService>();
        updateService.UpdateSettings(settings.Update);
        _updateCancellation = new CancellationTokenSource();
        _ = updateService.StartAsync(_updateCancellation.Token);

        // オーバーレイウィンドウを表示
        var overlayViewModel = _serviceProvider.GetRequiredService<OverlayViewModel>();
        _overlayWindow = new OverlayWindow(overlayViewModel);
        _overlayWindow.Show();

        // メインウィンドウを表示
        var mainViewModel = _serviceProvider.GetRequiredService<MainViewModel>();
        var mainWindow = new MainWindow(mainViewModel);
        mainWindow.Show();

        MainWindow = mainWindow;
    }

    private void ConfigureServices(IServiceCollection services, AppSettings settings, string settingsPath)
    {
        // 設定
        services.AddSingleton(settings);
        services.AddSingleton(settings.ASR);
        services.AddSingleton(settings.Translation);
        services.AddSingleton(settings.Overlay);
        services.AddSingleton(settings.AudioCapture);
        services.AddSingleton(new SettingsFilePath(settingsPath));

        // サービス
        services.AddSingleton<IAudioCaptureService, AudioCaptureService>();
        services.AddSingleton<IVADService, VADService>();
        services.AddSingleton<IASRService>(sp =>
        {
            var asrService = new WhisperASRService(sp.GetRequiredService<ASRSettings>());
            // 非同期初期化は別途行う
            return asrService;
        });
        services.AddSingleton<ITranslationService>(sp =>
        {
            var translationService = new LocalTranslationService(sp.GetRequiredService<TranslationSettings>());
            return translationService;
        });
        services.AddSingleton<IUpdateService, UpdateService>();

        // ViewModels
        services.AddSingleton<OverlayViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton(sp =>
            new SettingsViewModel(
                sp.GetRequiredService<AppSettings>(),
                sp.GetRequiredService<SettingsFilePath>().Value,
                sp.GetRequiredService<OverlayViewModel>()));

        services.AddTransient<SettingsWindow>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _updateCancellation?.Cancel();
        _updateCancellation?.Dispose();
        _overlayWindow?.Close();
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
