using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
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
    private readonly object _cancellationLock = new();
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
    private IBrush _statusColor = Brushes.Gray;

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
        try
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
        catch (Exception ex)
        {
            LoggerService.LogError($"OnSettingsSaved: Error applying settings: {ex.Message}");
            Log($"設定の適用中にエラーが発生しました: {ex.Message}");
            StatusText = "設定の適用中にエラーが発生しました";
            StatusColor = Brushes.Red;
        }
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
                _ = Services.MessageBoxService.ShowAsync("更新通知", $"更新が見つかりました。\n{e.Message}");
            }
        });
    }

    private async void OnUpdateReady(object? sender, UpdateReadyEventArgs e)
    {
        try
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

                var result = await Services.MessageBoxService.ShowYesNoAsync(
                    "更新適用", "更新のダウンロードが完了しました。今すぐ適用しますか？");

                if (result)
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
        catch (Exception ex)
        {
            LoggerService.LogError($"OnUpdateReady: Error handling update: {ex.Message}");
            Log($"更新の処理中にエラーが発生しました: {ex.Message}");
        }
    }

    private static void RunOnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Invoke(action);
        }
    }

    private static Task RunOnUiThreadAsync(Func<Task> action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return action();
        }
        return Dispatcher.UIThread.InvokeAsync(action);
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
            // オーディオセッションを持つプロセス名を収集（Chrome など親だけセッション持ち・子が実際に再生する場合に同名の子も一覧に出す）
            var activeProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pid in activeProcessIds)
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    if (p.Id != currentProcessId && !string.IsNullOrEmpty(p.ProcessName))
                        activeProcessNames.Add(p.ProcessName);
                }
                catch
                {
                    // プロセス終了等で取得できない場合は無視
                }
            }

            var allRawProcesses = Process.GetProcesses();
            try
            {
                var allProcesses = allRawProcesses
                    .Where(p => p.Id != currentProcessId && (activeProcessIds.Contains(p.Id) || activeProcessNames.Contains(p.ProcessName)))
                    .OrderBy(p => p.ProcessName)
                    .ThenBy(p => p.Id)
                    .ToList();

                LoggerService.LogInfo($"RefreshProcesses: オーディオセッション＋同名プロセスで{allProcesses.Count}個を特定（自分自身を除外）");

                // プロセス名 → セッション所有者の PID（Process Loopback はセッションを持つプロセスを指定する必要がある）
                var sessionOwnerByProcessName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var pid in activeProcessIds)
                {
                    try
                    {
                        using var proc = Process.GetProcessById(pid);
                        if (proc.Id != currentProcessId && !string.IsNullOrEmpty(proc.ProcessName) && !sessionOwnerByProcessName.ContainsKey(proc.ProcessName))
                            sessionOwnerByProcessName[proc.ProcessName] = proc.Id;
                    }
                    catch
                    {
                        // プロセス終了等で取得できない場合は無視
                    }
                }

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

                        var capturePid = activeProcessIds.Contains(p.Id)
                            ? p.Id
                            : (sessionOwnerByProcessName.TryGetValue(name, out var owner) ? owner : p.Id);

                        processList.Add(new ProcessInfo
                        {
                            Id = p.Id,
                            CaptureProcessId = capturePid,
                            Name = name,
                            Title = displayTitle
                        });
                        LoggerService.LogInfo($"RefreshProcesses: プロセス追加 - {name} (PID: {p.Id}, CaptureProcessId: {capturePid}, Title: {displayTitle})");
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                    {
                        LoggerService.LogError($"RefreshProcesses: プロセス情報取得エラー (PID: {p.Id}): {ex.Message}");
                    }
                }

                processes = processList;
            }
            finally
            {
                foreach (var p in allRawProcesses)
                    p.Dispose();
            }
        }
        else
        {
            LoggerService.LogInfo("RefreshProcesses: オーディオセッションを持つプロセスがないため、フォールバック処理を実行（メインウィンドウを持つプロセスを表示）");
            var fallbackRawProcesses = Process.GetProcesses();
            try
            {
                var fallbackProcesses = fallbackRawProcesses
                    .Where(p => p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(p.MainWindowTitle) && p.Id != currentProcessId)
                    .OrderBy(p => p.ProcessName)
                    .ThenBy(p => p.Id)
                    .ToList();

                LoggerService.LogInfo($"RefreshProcesses: フォールバック処理で{fallbackProcesses.Count}個のプロセスを検出");

                // Select を即時評価して ProcessInfo リストを確定させる（Process の Dispose 前にプロパティを読み取る）
                processes = fallbackProcesses.Select(p => new ProcessInfo
                {
                    Id = p.Id,
                    CaptureProcessId = p.Id,
                    Name = p.ProcessName,
                    Title = p.MainWindowTitle
                }).ToList();
            }
            finally
            {
                foreach (var p in fallbackRawProcesses)
                    p.Dispose();
            }
        }

        Dispatcher.UIThread.Invoke(() =>
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
            // Process Loopback は STA（UI）スレッドにバインドするため、キャプチャ開始は必ず UI の SynchronizationContext で実行する。
            // ボタンクリックで呼ばれる想定なので Current は通常 WPF のコンテキスト。null の場合は Dispatcher から取得して確実に UI で実行する。
            var uiContext = SynchronizationContext.Current ?? new AvaloniaSynchronizationContext();
            if (SynchronizationContext.Current == null)
                LoggerService.LogDebug("[Capture] StartAsync: SynchronizationContext.Current was null, using Dispatcher-based context");
            // スレッドセーフにCancellationTokenSourceを置き換え
            lock (_cancellationLock)
            {
                _processingCancellation?.Cancel();
                _processingCancellation?.Dispose();
                _processingCancellation = new CancellationTokenSource();
            }
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
            var selectedPid = SelectedProcess.Id;
            var capturePid = SelectedProcess.CaptureProcessId;
            Log($"'{SelectedProcess.DisplayName}' (PID: {selectedPid}) の音声再生を待機しています...");
            LoggerService.LogInfo($"[キャプチャ開始] 表示PID={selectedPid}, CaptureProcessId={capturePid}, Name={SelectedProcess.Name}, DisplayName={SelectedProcess.DisplayName}");
            // Minimal で実音が取れた条件に合わせ、まず選択行の ProcessId（ウィンドウ／子プロセス）で試す。失敗時のみ CaptureProcessId（セッション所有者）でリトライする。
            var pidForCapture = selectedPid;
            LoggerService.LogDebug($"StartAsync: Starting audio capture for process: {SelectedProcess.Name} (PID: {pidForCapture}, Title: {SelectedProcess.Title})");
            var captureStarted = await _audioCaptureService.StartCaptureWithRetryAsync(
                pidForCapture,
                _processingCancellation.Token,
                uiContext);

            if (!captureStarted && capturePid != selectedPid)
            {
                LoggerService.LogInfo($"[キャプチャ] 選択PID={selectedPid} で開始できなかったため、セッション所有者PID={capturePid} でリトライします");
                captureStarted = await _audioCaptureService.StartCaptureWithRetryAsync(
                    capturePid,
                    _processingCancellation.Token,
                    uiContext);
                if (captureStarted)
                    pidForCapture = capturePid;
            }

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

            // セッション所有者PIDでキャプチャしている場合のみ、持続無音時に選択PIDへフォールバックする監視を開始する。
            if (captureStarted && pidForCapture == capturePid && capturePid != selectedPid)
            {
                var ct = _processingCancellation?.Token ?? CancellationToken.None;
                _ = TryFallbackToWindowPidAfterSilenceAsync(selectedPid, ct);
            }
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
    /// セッション所有者 PID でキャプチャ開始後、一定時間無音なら選択ウィンドウの PID で再キャプチャを試行する。
    /// </summary>
    private async Task TryFallbackToWindowPidAfterSilenceAsync(int windowPid, CancellationToken cancellationToken)
    {
        const int silenceCheckDelayMs = 2500;
        try
        {
            await Task.Delay(silenceCheckDelayMs, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await RunOnUiThreadAsync(async () =>
        {
            if (!IsRunning || cancellationToken.IsCancellationRequested)
                return;
            if (_audioCaptureService.HasReceivedNonSilentDataSinceStart)
                return;
            LoggerService.LogInfo($"[キャプチャ] 持続無音のため選択ウィンドウPID={windowPid} で再キャプチャを試行します");
            Log("音声が検出されないため、別のプロセスで再キャプチャを試行します…");
            _audioCaptureService.StopCapture();
            var uiCtx = SynchronizationContext.Current ?? new AvaloniaSynchronizationContext();
            var started = await _audioCaptureService.StartCaptureWithRetryAsync(
                windowPid,
                cancellationToken,
                uiCtx);
            if (started)
            {
                LoggerService.LogInfo($"[キャプチャ] 選択ウィンドウPID={windowPid} でキャプチャを開始しました");
                Log("再キャプチャを開始しました");
            }
        });
    }

    /// <summary>
    /// 翻訳処理を停止する
    /// </summary>
    [RelayCommand]
    private async Task StopAsync()
    {
        // スレッドセーフにCancellationTokenSourceをクリーンアップ
        lock (_cancellationLock)
        {
            _processingCancellation?.Cancel();
            _processingCancellation?.Dispose();
            _processingCancellation = null;
        }
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
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            _ = window.ShowDialog(desktop.MainWindow);
        }
        else
        {
            window.Show();
        }
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

    private bool _modelsInitialized = false;

    /// <summary>
    /// モデルを初期化（起動時に呼び出される）
    /// </summary>
    public async Task InitializeModelsAsync()
    {
        // 重複初期化を防ぐ
        if (_modelsInitialized)
        {
            LoggerService.LogWarning("InitializeModelsAsync: Already initialized, skipping.");
            return;
        }

        _modelsInitialized = true;

        try
        {
            IsLoading = true;
            Log("モデルの初期化を開始します...");

            var asrService = _serviceProvider.GetRequiredService<IASRService>();
            var translationService = _serviceProvider.GetRequiredService<ITranslationService>();

            // イベントハンドラを登録（重複を避けるため、先に解除してから登録）
            asrService.ModelDownloadProgress -= OnModelDownloadProgress;
            asrService.ModelStatusChanged -= OnModelStatusChanged;
            translationService.ModelDownloadProgress -= OnModelDownloadProgress;
            translationService.ModelStatusChanged -= OnModelStatusChanged;

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
            LoadingMessage = "音声認識モデル読み込み中...";
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
            LoadingMessage = "翻訳モデル読み込み中...";
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
        if (Processes.Count == 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.LastSelectedProcessName))
        {
            SelectFirstProcessIfNeeded();
            return;
        }

        // プロセスIDが保存されていれば、同名が複数ある場合でも正しいプロセス（音声を出している方）を復元する
        if (_settings.LastSelectedProcessId > 0)
        {
            var matchById = Processes.FirstOrDefault(p => p.Id == _settings.LastSelectedProcessId);
            if (matchById != null && !Equals(SelectedProcess, matchById))
            {
                SelectedProcess = matchById;
                Log($"前回選択したプロセス '{matchById.DisplayName}' を復元しました（PID 一致）");
                return;
            }
        }

        var matchByName = Processes.FirstOrDefault(p =>
            p.Name.Equals(_settings.LastSelectedProcessName, StringComparison.OrdinalIgnoreCase));
        if (matchByName != null && !Equals(SelectedProcess, matchByName))
        {
            SelectedProcess = matchByName;
            Log($"前回選択したプロセス '{matchByName.DisplayName}' を復元しました");
            return;
        }

        SelectFirstProcessIfNeeded();
    }

    private void SelectFirstProcessIfNeeded()
    {
        if (SelectedProcess != null || Processes.Count == 0)
        {
            return;
        }

        SelectedProcess = Processes[0];
        Log($"最初のプロセス '{SelectedProcess.DisplayName}' を既定選択しました");
    }

    private void SaveLastSelectedProcess(ProcessInfo? process)
    {
        if (process == null)
        {
            return;
        }

        _settings.LastSelectedProcessName = process.Name;
        _settings.LastSelectedProcessId = process.Id;
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

        // スレッドセーフにCancellationTokenSourceをクリーンアップ
        lock (_cancellationLock)
        {
            _processingCancellation?.Cancel();
            _processingCancellation?.Dispose();
            _processingCancellation = null;
        }
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
    /// プロセスID（表示・選択用）
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Process Loopback キャプチャに使うプロセスID（オーディオセッションを持つプロセス。同名の子プロセスの場合はセッション所有者の PID）
    /// </summary>
    public int CaptureProcessId { get; set; }

    /// <summary>
    /// プロセス名
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// ウィンドウタイトル
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// 表示用の名前（プロセス名 (PID: xxx) - タイトル）
    /// </summary>
    public string DisplayName => $"{Name} (PID: {Id}) - {Title}";
}
