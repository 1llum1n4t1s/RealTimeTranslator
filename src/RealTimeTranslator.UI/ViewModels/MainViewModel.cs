using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NAudio.CoreAudioApi;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;
using RealTimeTranslator.UI.Views;

namespace RealTimeTranslator.UI.ViewModels;

/// <summary>
/// メインウィンドウのViewModel
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private bool _disposed;
    private const int MaxLogLines = 1000;

    private readonly ITranslationPipelineService _pipelineService;
    private readonly IAudioCaptureService _audioCaptureService;
    private readonly IVADService _vadService;
    private readonly OverlayViewModel _overlayViewModel;
    private AppSettings _settings;
    private readonly IUpdateService _updateService;
    private readonly IServiceProvider _serviceProvider;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly IDisposable? _settingsChangeSubscription;
    private readonly Queue<string> _logLines = new();
    private readonly object _logLock = new();
    private string? _lastLogMessage;
    private CancellationTokenSource? _processingCancellation;

    [ObservableProperty]
    private ObservableCollection<ProcessInfo> _processes = new();

    [ObservableProperty]
    private ProcessInfo? _selectedProcess;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = "停止中";

    [ObservableProperty]
    private Brush _statusColor = Brushes.Gray;

    [ObservableProperty]
    private double _processingLatency;

    [ObservableProperty]
    private double _translationLatency;

    [ObservableProperty]
    private string _logText = string.Empty;

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private string _loadingMessage = "初期化中...";

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private string _downloadStatus = string.Empty;

    [ObservableProperty]
    private string _downloadReason = string.Empty;

    [ObservableProperty]
    private bool _isDownloading;

    /// <summary>
    /// 開始ボタンが有効かどうか
    /// </summary>
    public bool CanStart => SelectedProcess != null && !IsRunning && !IsLoading;

    /// <summary>
    /// MainViewModel コンストラクタ
    /// </summary>
    public MainViewModel(
        ITranslationPipelineService pipelineService,
        IAudioCaptureService audioCaptureService,
        IVADService vadService,
        OverlayViewModel overlayViewModel,
        IOptionsMonitor<AppSettings> optionsMonitor,
        IUpdateService updateService,
        IServiceProvider serviceProvider,
        SettingsViewModel settingsViewModel)
    {
        _pipelineService = pipelineService;
        _audioCaptureService = audioCaptureService;
        _vadService = vadService;
        _overlayViewModel = overlayViewModel;
        _settings = optionsMonitor.CurrentValue;

        // 設定変更のイベントを購読
        _settingsChangeSubscription = optionsMonitor.OnChange(newSettings =>
        {
            LoggerService.LogInfo("Settings updated detected in MainViewModel.");
            _settings = newSettings;
        });

        _updateService = updateService;
        _serviceProvider = serviceProvider;
        _settingsViewModel = settingsViewModel;

        _pipelineService.SubtitleGenerated += OnSubtitleGenerated;
        _pipelineService.StatsUpdated += OnPipelineStatsUpdated;
        _pipelineService.ErrorOccurred += OnPipelineError;

        _audioCaptureService.CaptureStatusChanged += OnCaptureStatusChanged;

        _settingsViewModel.SettingsSaved += OnSettingsSaved;

        _updateService.StatusChanged += OnUpdateStatusChanged;
        _updateService.UpdateAvailable += OnUpdateAvailable;
        _updateService.UpdateReady += OnUpdateReady;
        _updateService.UpdateSettings(_settings.Update);

        RefreshProcesses();
        RestoreLastSelectedProcess();
        Log("アプリケーションを起動しました");
    }

    /// <summary>
    /// 字幕が生成されたときのハンドラー
    /// </summary>
    private void OnSubtitleGenerated(object? sender, SubtitleItem item)
    {
        RunOnUiThread(() =>
        {
            _overlayViewModel.AddOrUpdateSubtitle(item);
            var sourceLanguage = _settings.Translation.SourceLanguage.ToString();
            var targetLanguage = _settings.Translation.TargetLanguage.ToString();
            var logMessage = $"[確定] {sourceLanguage}→{targetLanguage} {item.OriginalText} → {item.TranslatedText}";
            LoggerService.LogInfo(logMessage);
            Log(logMessage);
        });
    }

    /// <summary>
    /// パイプライン統計が更新されたときのハンドラー
    /// </summary>
    private void OnPipelineStatsUpdated(object? sender, PipelineStatsEventArgs e)
    {
        RunOnUiThread(() =>
        {
            ProcessingLatency = e.ProcessingLatency;
            TranslationLatency = e.TranslationLatency;
        });
    }

    /// <summary>
    /// パイプラインでエラーが発生したときのハンドラー
    /// </summary>
    private void OnPipelineError(object? sender, Exception ex)
    {
        RunOnUiThread(() =>
        {
            Log($"パイプラインエラー: {ex.Message}");
            LoggerService.LogException($"TranslationPipelineService Error: {ex.Message}", ex);
        });
    }

    private async void OnSettingsSaved(object? sender, SettingsSavedEventArgs e)
    {
        _audioCaptureService.ApplySettings(e.Settings.AudioCapture);
        _vadService.ApplySettings(e.Settings.AudioCapture);
        _updateService.UpdateSettings(e.Settings.Update);
        var sourceLanguage = e.Settings.Translation.SourceLanguage;
        var targetLanguage = e.Settings.Translation.TargetLanguage;

        if (IsRunning)
        {
            await StopAsync();
            StatusText = "設定変更のため停止しました。再開時に新しい設定が反映されます。";
            StatusColor = Brushes.Orange;
            Log($"設定変更を検知したため停止しました。再開時に新しい設定が反映されます。翻訳言語: {sourceLanguage}→{targetLanguage}");
            return;
        }

        StatusText = "設定を更新しました。次回開始時に反映されます。";
        StatusColor = Brushes.Gray;
        Log($"設定変更を反映しました（次回開始時に適用）。翻訳言語: {sourceLanguage}→{targetLanguage}");
    }

    private void OnUpdateStatusChanged(object? sender, UpdateStatusChangedEventArgs e)
    {
        RunOnUiThread(() =>
        {
            Log($"更新: {e.Message}");
            if (!IsRunning && e.Status == UpdateStatus.Failed)
            {
                StatusText = "更新エラー";
                StatusColor = Brushes.Red;
            }
        });
    }

    private void OnUpdateAvailable(object? sender, UpdateAvailableEventArgs e)
    {
        RunOnUiThread(() =>
        {
            Log($"更新: {e.Message}");
            if (!_settings.Update.AutoApply)
            {
                MessageBox.Show(
                    $"更新が見つかりました。\n{e.Message}",
                    "更新通知",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        });
    }

    private async void OnUpdateReady(object? sender, UpdateReadyEventArgs e)
    {
        await RunOnUiThreadAsync(async () =>
        {
            Log($"更新: {e.Message}");
            if (_settings.Update.AutoApply)
            {
                Log("更新を自動適用します。");
                await _updateService.ApplyUpdateAsync(CancellationToken.None);
                return;
            }

            var result = MessageBox.Show(
                "更新のダウンロードが完了しました。今すぐ適用しますか？",
                "更新適用",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                await _updateService.ApplyUpdateAsync(CancellationToken.None);
            }
            else
            {
                _updateService.DismissPendingUpdate();
                Log("更新の適用を保留しました。");
            }
        });
    }

    private static void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }

    private static Task RunOnUiThreadAsync(Func<Task> action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            return action();
        }

        return dispatcher.InvokeAsync(action).Task.Unwrap();
    }

    /// <summary>
    /// プロセス一覧を更新する
    /// </summary>
    [RelayCommand]
    private void RefreshProcesses()
    {
        var activeProcessIds = GetActiveAudioProcessIds();
        var currentProcessId = Environment.ProcessId;
        IEnumerable<ProcessInfo> processes;

        LoggerService.LogInfo($"RefreshProcesses: オーディオセッションを持つプロセスID = {activeProcessIds.Count}個");

        if (activeProcessIds.Count > 0)
        {
            var allProcesses = Process.GetProcesses()
                .Where(p => activeProcessIds.Contains(p.Id) && p.Id != currentProcessId)
                .OrderBy(p => p.ProcessName)
                .ThenBy(p => p.Id)
                .ToList();

            LoggerService.LogInfo($"RefreshProcesses: Process.GetProcesses()から{allProcesses.Count}個のプロセスを特定（自分自身を除外）");

            var processList = new List<ProcessInfo>();
            var processNames = new Dictionary<string, int>();

            foreach (var p in allProcesses)
            {
                try
                {
                    var title = string.IsNullOrWhiteSpace(p.MainWindowTitle) ? p.ProcessName : p.MainWindowTitle;
                    var name = p.ProcessName;

                    if (!processNames.ContainsKey(name))
                    {
                        processNames[name] = 0;
                    }
                    processNames[name]++;

                    var displayTitle = processNames[name] > 1
                        ? $"{title} (PID: {p.Id})"
                        : title;

                    processList.Add(new ProcessInfo
                    {
                        Id = p.Id,
                        Name = name,
                        Title = displayTitle
                    });
                    LoggerService.LogInfo($"RefreshProcesses: プロセス追加 - {name} (PID: {p.Id}, Title: {displayTitle})");
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    LoggerService.LogError($"RefreshProcesses: プロセス情報取得エラー (PID: {p.Id}): {ex.Message}");
                }
            }

            processes = processList;
        }
        else
        {
            LoggerService.LogInfo("RefreshProcesses: オーディオセッションを持つプロセスがないため、フォールバック処理を実行（メインウィンドウを持つプロセスを表示）");
            var fallbackProcesses = Process.GetProcesses()
                .Where(p => p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(p.MainWindowTitle) && p.Id != currentProcessId)
                .OrderBy(p => p.ProcessName)
                .ThenBy(p => p.Id)
                .ToList();

            LoggerService.LogInfo($"RefreshProcesses: フォールバック処理で{fallbackProcesses.Count}個のプロセスを検出");

            processes = fallbackProcesses.Select(p => new ProcessInfo
            {
                Id = p.Id,
                Name = p.ProcessName,
                Title = p.MainWindowTitle
            });
        }

        Application.Current.Dispatcher.Invoke(() =>
        {
            Processes.Clear();
            foreach (var process in processes)
            {
                Processes.Add(process);
            }
            RestoreLastSelectedProcess();
        });

        LoggerService.LogInfo($"RefreshProcesses: プロセス一覧を更新しました（{Processes.Count}件）");
        Log($"プロセス一覧を更新しました（{Processes.Count}件）");
    }

    /// <summary>
    /// 翻訳処理を開始する
    /// </summary>
    [RelayCommand]
    private async Task StartAsync()
    {
        if (SelectedProcess == null)
            return;

        try
        {
            _processingCancellation?.Cancel();
            _processingCancellation?.Dispose();
            _processingCancellation = new CancellationTokenSource();
            IsRunning = true;
            StatusText = "起動中...";
            StatusColor = Brushes.Orange;

            var translationService = _serviceProvider.GetRequiredService<ITranslationService>();
            var profile = _settings.GameProfiles
                .FirstOrDefault(p => p.ProcessName.Equals(SelectedProcess.Name, StringComparison.OrdinalIgnoreCase));

            if (profile != null)
            {
                translationService.SetPreTranslationDictionary(profile.PreTranslationDictionary);
                translationService.SetPostTranslationDictionary(profile.PostTranslationDictionary);
                Log($"プロファイル '{profile.Name}' を適用しました");
            }

            if (!translationService.IsModelLoaded)
            {
                IsRunning = false;
                StatusText = "翻訳モデル未ロード: 翻訳を開始できません。";
                StatusColor = Brushes.Red;
                Log("翻訳モデル未ロードのため翻訳を停止しました。モデルのダウンロードが完了するまでお待ちください。");
                return;
            }

            var sourceLanguage = _settings.Translation.SourceLanguage;
            var targetLanguage = _settings.Translation.TargetLanguage;

            Log($"翻訳モデルが準備完了しました ({sourceLanguage}→{targetLanguage})。");

            await _pipelineService.StartAsync(_processingCancellation.Token);

            StatusText = "音声の再生を待機中...";
            StatusColor = Brushes.Orange;
            Log($"'{SelectedProcess.DisplayName}' (PID: {SelectedProcess.Id}) の音声再生を待機しています...");
            LoggerService.LogDebug($"StartAsync: Starting audio capture for process: {SelectedProcess.Name} (ID: {SelectedProcess.Id}, Title: {SelectedProcess.Title})");

            var captureStarted = await _audioCaptureService.StartCaptureWithRetryAsync(
                SelectedProcess.Id,
                _processingCancellation.Token);

            if (!captureStarted)
            {
                await _pipelineService.StopAsync();
                IsRunning = false;
                StatusText = "停止中";
                StatusColor = Brushes.Gray;
                Log("音声キャプチャがキャンセルされました。");
                return;
            }

            StatusText = "実行中";
            StatusColor = Brushes.Green;
            Log($"'{SelectedProcess.DisplayName}' の音声キャプチャを開始しました");
        }
        catch (Exception ex)
        {
            IsRunning = false;
            StatusText = "エラー";
            StatusColor = Brushes.Red;
            Log($"エラー: {ex.GetType().Name}: {ex.Message}");
            LoggerService.LogException($"StartAsync Error: {ex.GetType().FullName}: {ex.Message}", ex);
            if (ex.InnerException != null)
            {
                Log($"内部エラー: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                LoggerService.LogException($"InnerException: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}", ex.InnerException);
            }
        }
    }

    /// <summary>
    /// 翻訳処理を停止する
    /// </summary>
    [RelayCommand]
    private async Task StopAsync()
    {
        _processingCancellation?.Cancel();
        _processingCancellation?.Dispose();
        _processingCancellation = null;
        await _pipelineService.StopAsync();
        var pendingSegment = _vadService.FlushPendingSegment();
        if (pendingSegment != null)
        {
            Log("停止に伴い残留発話を破棄しました");
        }
        _audioCaptureService.StopCapture();
        _overlayViewModel.ClearSubtitles();

        IsRunning = false;
        StatusText = "停止中";
        StatusColor = Brushes.Gray;
        Log("音声キャプチャを停止しました");
    }

    /// <summary>
    /// 設定ウィンドウを開く
    /// </summary>
    [RelayCommand]
    private void OpenSettings()
    {
        var window = _serviceProvider.GetRequiredService<SettingsWindow>();
        window.Owner = App.Current.MainWindow;
        window.ShowDialog();
        Log("設定画面を開きました");
    }

    private void Log(string message, bool suppressDuplicate = false)
    {
        if (suppressDuplicate)
        {
            var baseMessage = ExtractBaseMessage(message);
            var lastBaseMessage = _lastLogMessage != null ? ExtractBaseMessage(_lastLogMessage) : null;

            if (baseMessage == lastBaseMessage)
            {
                return;
            }
        }

        _lastLogMessage = message;

        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var logLine = $"[{timestamp}] {message}";

        lock (_logLock)
        {
            _logLines.Enqueue(logLine);

            while (_logLines.Count > MaxLogLines)
            {
                _logLines.Dequeue();
            }

            var sb = new StringBuilder(_logLines.Count * 50);
            foreach (var line in _logLines)
            {
                sb.AppendLine(line);
            }
            LogText = sb.ToString();
        }
    }

    /// <summary>
    /// メッセージから数値・パーセント部分を除去してベース部分を抽出
    /// </summary>
    private static string ExtractBaseMessage(string message)
    {
        return System.Text.RegularExpressions.Regex.Replace(message, @"[\d.]+%?", "").Trim();
    }

    /// <summary>
    /// バイト数を人間が読みやすい形式にフォーマット
    /// </summary>
    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:F1} {sizes[order]}";
    }

    private void OnModelDownloadProgress(object? sender, ModelDownloadProgressEventArgs e)
    {
        var progressText = e.ProgressPercentage.HasValue
            ? $"{e.ProgressPercentage.Value:F1}%"
            : "進捗不明";

        var totalSize = e.TotalBytes.HasValue
            ? FormatBytes(e.TotalBytes.Value)
            : "不明";
        var downloadedSize = FormatBytes(e.BytesReceived);

        var downloadStatusText = $"{e.ServiceName} {e.ModelName} ダウンロード中... {downloadedSize} / {totalSize} ({progressText})";

        RunOnUiThread(() =>
        {
            IsDownloading = true;
            DownloadProgress = e.ProgressPercentage ?? 0;
            DownloadStatus = downloadStatusText;

            if (!IsLoading)
            {
                StatusText = downloadStatusText;
                StatusColor = Brushes.Orange;
            }
        });
    }

    private void OnModelStatusChanged(object? sender, ModelStatusChangedEventArgs e)
    {
        var message = e.Exception != null
            ? $"{e.Message} ({FormatExceptionMessage(e.Exception)})"
            : e.Message;

        RunOnUiThread(() =>
        {
            StatusText = message;
            StatusColor = e.Status == ModelStatusType.DownloadFailed || e.Status == ModelStatusType.LoadFailed
                ? Brushes.Red
                : Brushes.Orange;

            if (e.Status == ModelStatusType.Info && message.Contains("ダウンロード", StringComparison.Ordinal))
            {
                DownloadReason = message;
            }

            if (e.Status == ModelStatusType.DownloadCompleted || e.Status == ModelStatusType.DownloadFailed)
            {
                IsDownloading = false;
                DownloadProgress = 0;
                DownloadStatus = string.Empty;
                DownloadReason = string.Empty;
            }
        });

        Log(message);
    }

    private void OnCaptureStatusChanged(object? sender, CaptureStatusEventArgs e)
    {
        RunOnUiThread(() =>
        {
            if (e.IsWaiting)
            {
                StatusText = e.Message;
                StatusColor = Brushes.Orange;
            }
            Log(e.Message, suppressDuplicate: e.IsWaiting);
        });
    }

    /// <summary>
    /// 例外メッセージをフォーマット
    /// </summary>
    private static string FormatExceptionMessage(Exception ex)
    {
        var messages = new List<string>();
        Exception? current = ex;
        while (current != null)
        {
            if (!string.IsNullOrWhiteSpace(current.Message))
            {
                messages.Add(current.Message.Trim());
            }
            current = current.InnerException;
        }

        var normalized = messages.Distinct().ToList();
        return normalized.Count > 0 ? string.Join(" / ", normalized) : ex.GetType().Name;
    }

    partial void OnSelectedProcessChanged(ProcessInfo? value)
    {
        OnPropertyChanged(nameof(CanStart));
        SaveLastSelectedProcess(value);
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
    }

    /// <summary>
    /// モデルを初期化（起動時に呼び出される）
    /// </summary>
    public async Task InitializeModelsAsync()
    {
        try
        {
            IsLoading = true;
            Log("モデルの初期化を開始します...");

            var asrService = _serviceProvider.GetRequiredService<IASRService>();
            var translationService = _serviceProvider.GetRequiredService<ITranslationService>();

            asrService.ModelDownloadProgress += OnModelDownloadProgress;
            asrService.ModelStatusChanged += OnModelStatusChanged;
            translationService.ModelDownloadProgress += OnModelDownloadProgress;
            translationService.ModelStatusChanged += OnModelStatusChanged;

            var initTasks = new List<Task>
            {
                InitializeASRModelAsync(asrService),
                InitializeTranslationModelAsync(translationService)
            };

            await Task.WhenAll(initTasks);

            LoadingMessage = "準備完了";
            Log("モデルの初期化が完了しました。");
            LoggerService.LogInfo("All models initialized successfully");
        }
        catch (Exception ex)
        {
            LoadingMessage = $"初期化エラー: {ex.Message}";
            Log($"モデル初期化エラー: {ex.Message}");
            LoggerService.LogError($"モデル初期化エラー: {ex}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// ASRモデルを初期化
    /// </summary>
    private async Task InitializeASRModelAsync(IASRService asrService)
    {
        try
        {
            LoadingMessage = "音声認識モデルをダウンロード中...";
            await asrService.InitializeAsync();
            LoggerService.LogInfo("ASR model initialization completed");
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"ASR initialization error: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 翻訳モデルを初期化
    /// </summary>
    private async Task InitializeTranslationModelAsync(ITranslationService translationService)
    {
        try
        {
            LoadingMessage = "翻訳モデルをダウンロード中...";
            await translationService.InitializeAsync();
            LoggerService.LogInfo("Translation model initialization completed");
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"Translation initialization error: {ex.Message}");
            throw;
        }
    }

    private void RestoreLastSelectedProcess()
    {
        if (string.IsNullOrWhiteSpace(_settings.LastSelectedProcessName))
        {
            return;
        }

        var match = Processes.FirstOrDefault(p =>
            p.Name.Equals(_settings.LastSelectedProcessName, StringComparison.OrdinalIgnoreCase));
        if (match != null && !Equals(SelectedProcess, match))
        {
            SelectedProcess = match;
            Log($"前回選択したプロセス '{match.DisplayName}' を復元しました");
        }
    }

    private void SaveLastSelectedProcess(ProcessInfo? process)
    {
        if (process == null)
        {
            return;
        }

        _settings.LastSelectedProcessName = process.Name;
        Log($"選択プロセス '{process.DisplayName}' を設定に保存しました");
    }

    /// <summary>
    /// 現在オーディオセッションを持つプロセスのIDを取得
    /// </summary>
    private static HashSet<int> GetActiveAudioProcessIds()
    {
        var processIds = new HashSet<int>();
        try
        {
            LoggerService.LogInfo("オーディオセッション列挙を開始");
            using var enumerator = new MMDeviceEnumerator();

            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            LoggerService.LogInfo($"アクティブなオーディオデバイス数: {devices.Count}");

            var deviceIndex = 0;
            foreach (var device in devices)
            {
                try
                {
                    LoggerService.LogInfo($"デバイス[{deviceIndex}]: {device.FriendlyName} (ID: {device.ID})");

                    var sessionManager = device.AudioSessionManager;
                    var sessions = sessionManager.Sessions;
                    LoggerService.LogInfo($"デバイス[{deviceIndex}] オーディオセッション総数: {sessions.Count}");

                    for (var i = 0; i < sessions.Count; i++)
                    {
                        var session = sessions[i];
                        try
                        {
                            var stateValue = (int)session.State;
                            LoggerService.LogInfo($"デバイス[{deviceIndex}] セッション[{i}]: State={stateValue} (0=Inactive, 1=Active, 2=Expired)");

                            if (stateValue != 2)
                            {
                                uint processId = 0;

                                try
                                {
                                    processId = session.GetProcessID;
                                    LoggerService.LogInfo($"デバイス[{deviceIndex}] セッション[{i}]: ProcessID={processId}を取得");
                                }
                                catch (InvalidOperationException ex)
                                {
                                    LoggerService.LogWarning($"デバイス[{deviceIndex}] セッション[{i}]: ProcessIDの取得失敗（IAudioSessionControl2未サポート）: {ex.Message}");
                                }
                                catch (System.Runtime.InteropServices.COMException ex)
                                {
                                    LoggerService.LogWarning($"デバイス[{deviceIndex}] セッション[{i}]: ProcessIDの取得失敗（COMエラー）: HResult=0x{ex.HResult:X}, Message={ex.Message}");
                                }
                                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                                {
                                    LoggerService.LogWarning($"デバイス[{deviceIndex}] セッション[{i}]: ProcessIDの取得失敗: {ex.GetType().Name}: {ex.Message}");
                                }

                                if (processId > 0)
                                {
                                    var stateName = stateValue switch
                                    {
                                        0 => "Inactive",
                                        1 => "Active",
                                        _ => "Unknown"
                                    };

                                    if (processIds.Add((int)processId))
                                    {
                                        LoggerService.LogInfo($"デバイス[{deviceIndex}] セッション[{i}]: ProcessID={processId} (State={stateName}, Device={device.FriendlyName}) を検出");
                                    }
                                    else
                                    {
                                        LoggerService.LogInfo($"デバイス[{deviceIndex}] セッション[{i}]: ProcessID={processId} (State={stateName}) は既に検出済み");
                                    }
                                }
                                else
                                {
                                    LoggerService.LogWarning($"デバイス[{deviceIndex}] セッション[{i}]: ProcessIDが取得できませんでした（State={stateValue}）");
                                }
                            }
                            else
                            {
                                LoggerService.LogInfo($"デバイス[{deviceIndex}] セッション[{i}]: Expired状態のため除外");
                            }
                        }
                        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                        {
                            LoggerService.LogError($"デバイス[{deviceIndex}] セッション[{i}]の情報取得に失敗: {ex.GetType().Name}: {ex.Message}");
                        }
                    }

                    deviceIndex++;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    LoggerService.LogError($"デバイス[{deviceIndex}]の処理に失敗: {ex.GetType().Name}: {ex.Message}");
                    deviceIndex++;
                }
            }

            LoggerService.LogInfo($"オーディオセッションを持つプロセスを{processIds.Count}個検出: [{string.Join(", ", processIds)}]");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            LoggerService.LogError($"オーディオセッション列挙に失敗: {ex.GetType().Name}: {ex.Message}\nStackTrace: {ex.StackTrace}");
        }

        return processIds;
    }

    /// <summary>
    /// MainViewModel のディスポーズ
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        LoggerService.LogDebug("MainViewModel.Dispose: 開始");

        try
        {
            _audioCaptureService.StopCapture();
            LoggerService.LogInfo("MainViewModel.Dispose: 音声キャプチャ停止完了");
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"MainViewModel.Dispose: 音声キャプチャ停止エラー: {ex.Message}");
        }

        _processingCancellation?.Cancel();
        _processingCancellation?.Dispose();
        _processingCancellation = null;
        LoggerService.LogInfo("MainViewModel.Dispose: 処理パイプライン停止完了");

        _settingsChangeSubscription?.Dispose();

        _pipelineService.SubtitleGenerated -= OnSubtitleGenerated;
        _pipelineService.StatsUpdated -= OnPipelineStatsUpdated;
        _pipelineService.ErrorOccurred -= OnPipelineError;
        _audioCaptureService.CaptureStatusChanged -= OnCaptureStatusChanged;
        _settingsViewModel.SettingsSaved -= OnSettingsSaved;
        _updateService.StatusChanged -= OnUpdateStatusChanged;
        _updateService.UpdateAvailable -= OnUpdateAvailable;
        _updateService.UpdateReady -= OnUpdateReady;

        var asrService = _serviceProvider.GetService<IASRService>();
        if (asrService != null)
        {
            asrService.ModelDownloadProgress -= OnModelDownloadProgress;
            asrService.ModelStatusChanged -= OnModelStatusChanged;
        }

        var translationService = _serviceProvider.GetService<ITranslationService>();
        if (translationService != null)
        {
            translationService.ModelDownloadProgress -= OnModelDownloadProgress;
            translationService.ModelStatusChanged -= OnModelStatusChanged;
        }

        LoggerService.LogInfo("MainViewModel.Dispose: イベントハンドラ解除完了");

        _disposed = true;
        GC.SuppressFinalize(this);
        LoggerService.LogInfo("MainViewModel.Dispose: 完了");
    }
}

/// <summary>
/// プロセス情報
/// </summary>
public class ProcessInfo
{
    /// <summary>
    /// プロセスID
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// プロセス名
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// ウィンドウタイトル
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// 表示用の名前（プロセス名 - タイトル）
    /// </summary>
    public string DisplayName => $"{Name} - {Title}";
}
