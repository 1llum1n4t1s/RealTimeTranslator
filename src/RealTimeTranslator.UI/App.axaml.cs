using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
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
/// アプリケーション
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private OverlayWindow? _overlayWindow;
    private CancellationTokenSource? _updateCancellation;

    public App()
    {
        Environment.SetEnvironmentVariable("GGML_USE_CUDA", "1");
        Environment.SetEnvironmentVariable("GGML_CUDA_NO_PINNED", "1");
        Environment.SetEnvironmentVariable("CUDA_VISIBLE_DEVICES", "0");
        Environment.SetEnvironmentVariable("GGML_USE_HIP", "1");
        Environment.SetEnvironmentVariable("HSA_OVERRIDE_GFX_VERSION", "11.0.0");
        Environment.SetEnvironmentVariable("GGML_USE_SYCL", "1");
        Environment.SetEnvironmentVariable("SYCL_DEVICE_FILTER", "level_zero:gpu");
        Environment.SetEnvironmentVariable("GGML_USE_VULKAN", "1");
        Environment.SetEnvironmentVariable("GGML_USE_METAL", "1");
        LoggerService.LogDebug("App constructor: GPU環境変数設定完了 (CUDA/HIP/SYCL/Vulkan/Metal)");
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
        DisableAvaloniaDataAnnotationValidation();
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
            var optionsSnapshot = _serviceProvider.GetRequiredService<IOptionsSnapshot<AppSettings>>();
            updateService.UpdateSettings(optionsSnapshot.Value.Update);
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

            _ = mainViewModel.InitializeModelsAsync().ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    LoggerService.LogError($"OnStartup: モデル初期化エラー: {t.Exception}");
                    Dispatcher.UIThread.Post(() =>
                    {
                        _ = Services.MessageBoxService.ShowWindowDialogAsync(mainWindow, "エラー",
                            $"モデルの初期化に失敗しました:\n\n{t.Exception?.GetBaseException().Message}");
                    });
                }
            }, TaskScheduler.Default);
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

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        LoggerService.LogInfo("OnExit: アプリケーション終了開始");
        _updateCancellation?.Cancel();
        _updateCancellation?.Dispose();
        if (_overlayWindow != null)
        {
            _overlayWindow.Close();
            _overlayWindow = null;
        }
        if (_serviceProvider != null)
        {
            _serviceProvider.GetService<MainViewModel>()?.Dispose();
            _serviceProvider.GetService<OverlayViewModel>()?.Dispose();
            _serviceProvider.GetService<IAudioCaptureService>()?.Dispose();
            _serviceProvider.GetService<ITranslationService>()?.Dispose();
            _serviceProvider.GetService<HttpClient>()?.Dispose();
            _serviceProvider.GetService<ModelDownloadService>()?.Dispose();
            _serviceProvider.Dispose();
            _serviceProvider = null;
        }
        LoggerService.Shutdown();
    }

    private static void DisableAvaloniaDataAnnotationValidation()
    {
        var toRemove = BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();
        foreach (var plugin in toRemove)
            BindingPlugins.DataValidators.Remove(plugin);
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
            sp.GetRequiredService<IOptionsSnapshot<AppSettings>>().Value);
        services.AddSingleton<TranslationSettings>(sp =>
            sp.GetRequiredService<AppSettings>().Translation);
        services.AddSingleton<AudioCaptureSettings>(sp =>
            sp.GetRequiredService<AppSettings>().AudioCapture);
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<HttpClient>(sp =>
        {
            var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(30);
            return httpClient;
        });
        services.AddSingleton<ModelDownloadService>();
        services.AddSingleton<PromptBuilderFactory>();
        services.AddSingleton<IAudioCaptureService, AudioCaptureService>();
        services.AddSingleton<IVADService, VADService>();
        services.AddSingleton<IASRService, WhisperASRService>();
        services.AddSingleton<ITranslationService, MistralTranslationService>();
        services.AddSingleton<IUpdateService, UpdateService>();
        services.AddSingleton<ITranslationPipelineService, TranslationPipelineService>();
        services.AddSingleton<OverlayViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton(sp => new SettingsViewModel(
            sp.GetRequiredService<IOptionsSnapshot<AppSettings>>(),
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<OverlayViewModel>()));
        services.AddTransient<SettingsWindow>();
    }

    private static void RegisterApplicationInARP()
    {
        try
        {
            var exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var exeDir = Path.GetDirectoryName(exePath);
            var appVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0.0";
            var registryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\RealTimeTranslator";
            using var key = Registry.LocalMachine.CreateSubKey(registryPath);
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
