using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using RealTimeTranslator.ASR.Services;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;
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
        try
        {
            System.Diagnostics.Debug.WriteLine("OnStartup: 起動開始");
            VelopackApp.Build().Run();
            System.Diagnostics.Debug.WriteLine("OnStartup: Velopack初期化完了");
            base.OnStartup(e);
            System.Diagnostics.Debug.WriteLine("OnStartup: base.OnStartup完了");

            // 設定を読み込み
            var settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
            System.Diagnostics.Debug.WriteLine($"OnStartup: settingsPath={settingsPath}");
            var settings = AppSettings.Load(settingsPath);
            System.Diagnostics.Debug.WriteLine("OnStartup: 設定読み込み完了");

            // DIコンテナを構築
            var services = new ServiceCollection();
            ConfigureServices(services, settings, settingsPath);
            _serviceProvider = services.BuildServiceProvider();
            System.Diagnostics.Debug.WriteLine("OnStartup: DI構築完了");

            // モデルをアプリ起動時に初期化（バックグラウンドで実行）
            _ = Task.Run(async () =>
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine("OnStartup: モデル初期化開始");
                    var asrService = _serviceProvider.GetRequiredService<IASRService>();
                    var translationService = _serviceProvider.GetRequiredService<ITranslationService>();

                    // 並列で初期化
                    await Task.WhenAll(
                        asrService.InitializeAsync(),
                        translationService.InitializeAsync()
                    );
                    System.Diagnostics.Debug.WriteLine("OnStartup: モデル初期化完了");
                }
                catch (Exception ex)
                {
                    // エラーをログに記録（UIには表示しない）
                    System.Diagnostics.Debug.WriteLine($"モデル初期化エラー: {ex}");
                }
            });

            var updateService = _serviceProvider.GetRequiredService<IUpdateService>();
            updateService.UpdateSettings(settings.Update);
            _updateCancellation = new CancellationTokenSource();
            _ = updateService.StartAsync(_updateCancellation.Token);
            System.Diagnostics.Debug.WriteLine("OnStartup: 更新サービス開始");

            // オーバーレイウィンドウを表示
            var overlayViewModel = _serviceProvider.GetRequiredService<OverlayViewModel>();
            _overlayWindow = new OverlayWindow(overlayViewModel);
            _overlayWindow.Show();
            System.Diagnostics.Debug.WriteLine("OnStartup: オーバーレイウィンドウ表示");

            // メインウィンドウを表示
            var mainViewModel = _serviceProvider.GetRequiredService<MainViewModel>();
            var mainWindow = new MainWindow(mainViewModel);
            mainWindow.Show();
            System.Diagnostics.Debug.WriteLine("OnStartup: メインウィンドウ表示");

            MainWindow = mainWindow;
            System.Diagnostics.Debug.WriteLine("OnStartup: 起動完了");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"アプリケーション起動エラー: {ex}");
            MessageBox.Show($"アプリケーション起動に失敗しました:\n\n{ex.Message}\n\n{ex.StackTrace}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            Current.Shutdown(1);
        }
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

        // HttpClient（シングルトン）
        services.AddSingleton<HttpClient>(sp =>
        {
            var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(30); // 大きなファイルのダウンロード用
            return httpClient;
        });

        // モデルダウンロードサービス
        services.AddSingleton<ModelDownloadService>();

        // サービス
        services.AddSingleton<IAudioCaptureService, AudioCaptureService>();
        services.AddSingleton<IVADService, VADService>();
        services.AddSingleton<IASRService, WhisperASRService>();
        services.AddSingleton<ITranslationService, LocalTranslationService>();
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

        // サービスとViewModelを適切に破棄
        if (_serviceProvider != null)
        {
            // MainViewModelのDispose（イベントハンドラ登録解除）
            var mainViewModel = _serviceProvider.GetService<MainViewModel>();
            mainViewModel?.Dispose();

            // OverlayViewModelのDispose
            var overlayViewModel = _serviceProvider.GetService<OverlayViewModel>();
            overlayViewModel?.Dispose();

            // HttpClientとModelDownloadServiceを適切に破棄
            var httpClient = _serviceProvider.GetService<HttpClient>();
            httpClient?.Dispose();

            var downloadService = _serviceProvider.GetService<ModelDownloadService>();
            downloadService?.Dispose();

            _serviceProvider.Dispose();
        }

        base.OnExit(e);
    }
}
