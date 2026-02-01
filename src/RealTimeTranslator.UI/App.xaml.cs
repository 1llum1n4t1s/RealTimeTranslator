using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
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

    public App()
    {
        // GPU有効化：アプリケーション起動時に環境変数を設定（ネイティブライブラリ初期化前）
        // 自動検出順序: NVIDIA CUDA > AMD ROCm/HIP > Intel SYCL > Vulkan (汎用) > CPU

        // NVIDIA CUDA（最高性能）
        Environment.SetEnvironmentVariable("GGML_USE_CUDA", "1");
        Environment.SetEnvironmentVariable("GGML_CUDA_NO_PINNED", "1");
        Environment.SetEnvironmentVariable("CUDA_VISIBLE_DEVICES", "0");

        // AMD RADEON対応（ROCm/HIP）
        Environment.SetEnvironmentVariable("GGML_USE_HIP", "1");
        Environment.SetEnvironmentVariable("HSA_OVERRIDE_GFX_VERSION", "11.0.0");  // RDNAアーキテクチャ用

        // Intel Arc / Intel GPU対応（SYCL/oneAPI）
        Environment.SetEnvironmentVariable("GGML_USE_SYCL", "1");
        Environment.SetEnvironmentVariable("SYCL_DEVICE_FILTER", "level_zero:gpu");  // Intel Level Zeroバックエンド優先

        // Vulkan（汎用GPU、NVIDIA/AMD/Intelすべて対応）
        Environment.SetEnvironmentVariable("GGML_USE_VULKAN", "1");

        // Metal（macOS用、Windowsでは無視される）
        Environment.SetEnvironmentVariable("GGML_USE_METAL", "1");

        LoggerService.LogDebug("App constructor: GPU環境変数設定完了 (CUDA/HIP/SYCL/Vulkan/Metal)");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            LoggerService.LogInfo("OnStartup: 起動開始");
            VelopackApp.Build().Run();
            LoggerService.LogInfo("OnStartup: Velopack初期化完了");
            RegisterApplicationInARP();
            LoggerService.LogInfo("OnStartup: ARP登録完了");
            base.OnStartup(e);
            LoggerService.LogInfo("OnStartup: base.OnStartup完了");

            // DIコンテナを構築
            var services = new ServiceCollection();
            ConfigureServices(services);
            _serviceProvider = services.BuildServiceProvider();
            LoggerService.LogInfo("OnStartup: DI構築完了");

            var updateService = _serviceProvider.GetRequiredService<IUpdateService>();
            var optionsSnapshot = _serviceProvider.GetRequiredService<IOptionsSnapshot<AppSettings>>();
            updateService.UpdateSettings(optionsSnapshot.Value.Update);
            _updateCancellation = new CancellationTokenSource();

            // 更新チェックと適用（UIスレッドブロッキングを回避）
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
                Current.Shutdown();
                return;
            }

            // fire-and-forget Task の例外ハンドリングを追加
            _ = updateService.StartAsync(_updateCancellation.Token).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    LoggerService.LogError($"OnStartup: 更新サービスエラー: {t.Exception}");
                }
            }, TaskScheduler.Default);
            LoggerService.LogInfo("OnStartup: 更新サービス開始");

            // オーバーレイウィンドウを表示
            var overlayViewModel = _serviceProvider.GetRequiredService<OverlayViewModel>();
            _overlayWindow = new OverlayWindow(overlayViewModel);
            _overlayWindow.Show();
            LoggerService.LogInfo("OnStartup: オーバーレイウィンドウ表示");

            // メインウィンドウを表示
            var mainViewModel = _serviceProvider.GetRequiredService<MainViewModel>();
            var mainWindow = new MainWindow(mainViewModel);
            mainWindow.Show();
            LoggerService.LogInfo("OnStartup: メインウィンドウ表示");

            MainWindow = mainWindow;

            // モデルをバックグラウンドで初期化（UIスレッドで開始してawaitしない）
            // fire-and-forget Task の例外ハンドリングを追加
            _ = mainViewModel.InitializeModelsAsync().ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    LoggerService.LogError($"OnStartup: モデル初期化エラー: {t.Exception}");
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        MessageBox.Show($"モデルの初期化に失敗しました:\n\n{t.Exception?.GetBaseException().Message}",
                            "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }));
                }
            }, TaskScheduler.Default);
            LoggerService.LogInfo("OnStartup: 起動完了");
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"アプリケーション起動エラー: {ex}");
            MessageBox.Show($"アプリケーション起動に失敗しました:\n\n{ex.Message}\n\n{ex.StackTrace}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            Current.Shutdown(1);
        }
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // 1. 設定ファイルのビルド
        var configurationBuilder = new ConfigurationBuilder()
            .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
            .AddJsonFile("settings.json", optional: true, reloadOnChange: true);

        IConfiguration configuration = configurationBuilder.Build();
        LoggerService.LogInfo("ConfigureServices: 設定ファイルを読み込み完了");

        // 2. IConfiguration 自体を登録（必要な場合）
        services.AddSingleton(configuration);

        // 3. IOptionsパターンの登録
        services.Configure<AppSettings>(configuration);

        // 4. AppSettingsを個別に登録（TranslationPipelineServiceの依存関係解決用）
        services.AddSingleton<AppSettings>(sp =>
        {
            var appSettings = sp.GetRequiredService<IOptionsSnapshot<AppSettings>>().Value;
            return appSettings;
        });

        // 5. TranslationSettingsを個別に登録（WhisperASRService/MistralTranslationServiceの依存関係解決用）
        services.AddSingleton<TranslationSettings>(sp =>
        {
            var appSettings = sp.GetRequiredService<AppSettings>();
            return appSettings.Translation;
        });

        // 6. AudioCaptureSettingsを個別に登録（AudioCaptureServiceの依存関係解決用）
        services.AddSingleton<AudioCaptureSettings>(sp =>
        {
            var appSettings = sp.GetRequiredService<AppSettings>();
            return appSettings.AudioCapture;
        });

        // 7. 設定保存サービスの登録
        services.AddSingleton<ISettingsService, SettingsService>();

        // HttpClient（シングルトン）
        services.AddSingleton<HttpClient>(sp =>
        {
            var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(30); // 大きなファイルのダウンロード用
            return httpClient;
        });

        // モデルダウンロードサービス
        services.AddSingleton<ModelDownloadService>();

        // プロンプトビルダーファクトリ
        services.AddSingleton<PromptBuilderFactory>();

        // サービス
        services.AddSingleton<IAudioCaptureService, AudioCaptureService>();

        // VADService の登録 (依存関係はDIが自動解決)
        services.AddSingleton<IVADService, VADService>();

        services.AddSingleton<IASRService, WhisperASRService>();
        services.AddSingleton<ITranslationService, MistralTranslationService>(); // Mistral Q3_K_S (高速量子化)
        services.AddSingleton<IUpdateService, UpdateService>();

        // 翻訳パイプラインサービス
        services.AddSingleton<ITranslationPipelineService, TranslationPipelineService>();

        // ViewModels
        services.AddSingleton<OverlayViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton(sp =>
            new SettingsViewModel(
                sp.GetRequiredService<IOptionsSnapshot<AppSettings>>(),
                sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<OverlayViewModel>()));

        services.AddTransient<SettingsWindow>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LoggerService.LogInfo("OnExit: アプリケーション終了開始");

        // 更新サービスをキャンセル
        _updateCancellation?.Cancel();
        _updateCancellation?.Dispose();
        LoggerService.LogInfo("OnExit: 更新サービス停止");

        // オーバーレイウィンドウを閉じる
        if (_overlayWindow != null)
        {
            _overlayWindow.Close();
            _overlayWindow = null;
        }
        LoggerService.LogInfo("OnExit: オーバーレイウィンドウ終了");

        // サービスとViewModelを適切に破棄
        if (_serviceProvider != null)
        {
            // MainViewModelのDispose（イベントハンドラ登録解除、キャプチャ停止）
            var mainViewModel = _serviceProvider.GetService<MainViewModel>();
            mainViewModel?.Dispose();
            LoggerService.LogInfo("OnExit: MainViewModel Dispose完了");

            // OverlayViewModelのDispose
            var overlayViewModel = _serviceProvider.GetService<OverlayViewModel>();
            overlayViewModel?.Dispose();
            LoggerService.LogInfo("OnExit: OverlayViewModel Dispose完了");

            // 音声キャプチャサービスをDispose
            var audioCaptureService = _serviceProvider.GetService<IAudioCaptureService>();
            audioCaptureService?.Dispose();
            LoggerService.LogInfo("OnExit: AudioCaptureService Dispose完了");

            // 翻訳サービスをDispose
            var translationService = _serviceProvider.GetService<ITranslationService>();
            translationService?.Dispose();
            LoggerService.LogInfo("OnExit: TranslationService Dispose完了");

            // HttpClientとModelDownloadServiceを適切に破棄
            var httpClient = _serviceProvider.GetService<HttpClient>();
            httpClient?.Dispose();

            var downloadService = _serviceProvider.GetService<ModelDownloadService>();
            downloadService?.Dispose();
            LoggerService.LogInfo("OnExit: DownloadService Dispose完了");

            _serviceProvider.Dispose();
            _serviceProvider = null;
            LoggerService.LogInfo("OnExit: ServiceProvider Dispose完了");
        }

        LoggerService.LogInfo("OnExit: アプリケーション終了完了");
        LoggerService.Shutdown();
        base.OnExit(e);
    }

    /// <summary>
    /// アプリケーションをWindows の「追加と削除」（ARP）に登録
    /// </summary>
    private static void RegisterApplicationInARP()
    {
        try
        {
            var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var exeDir = System.IO.Path.GetDirectoryName(exePath);
            var appVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0.0";

            // レジストリキー: HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall
            var registryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\RealTimeTranslator";

            using (var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(registryPath))
            {
                if (key != null)
                {
                    key.SetValue("DisplayName", "RealTimeTranslator", Microsoft.Win32.RegistryValueKind.String);
                    key.SetValue("DisplayVersion", appVersion, Microsoft.Win32.RegistryValueKind.String);
                    key.SetValue("Publisher", "1llum1n4t1s", Microsoft.Win32.RegistryValueKind.String);
                    if (exeDir != null)
                    {
                        key.SetValue("InstallLocation", exeDir, Microsoft.Win32.RegistryValueKind.String);
                    }
                    key.SetValue("UninstallString", exePath, Microsoft.Win32.RegistryValueKind.String);
                    key.SetValue("DisplayIcon", exePath, Microsoft.Win32.RegistryValueKind.String);
                    key.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
                    key.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
                    LoggerService.LogInfo("RegisterApplicationInARP: ARP登録成功");
                }
            }
        }
        catch (Exception ex)
        {
            LoggerService.LogWarning($"RegisterApplicationInARP: ARP登録エラー: {ex.Message}");
        }
    }
}
