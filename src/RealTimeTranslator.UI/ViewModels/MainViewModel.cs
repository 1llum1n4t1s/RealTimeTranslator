using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
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
    private const int MaxAccurateParallelism = 2; // 高精度ASRの最大並列処理数
    private const int MaxLogLines = 1000; // ログの最大行数
    private const int ChannelCapacity = 100; // チャネルバッファサイズ

    private readonly IAudioCaptureService _audioCaptureService;
    private readonly IVADService _vadService;
    private readonly IASRService _asrService;
    private readonly ITranslationService _translationService;
    private readonly OverlayViewModel _overlayViewModel;
    private readonly AppSettings _settings;
    private readonly IUpdateService _updateService;
    private readonly IServiceProvider _serviceProvider;
    private readonly SettingsFilePath _settingsFilePath;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly Queue<string> _logLines = new();
    private string? _lastLogMessage;
    private CancellationTokenSource? _processingCancellation;
    private Channel<SpeechSegmentWorkItem>? _segmentChannel;
    private Channel<SpeechSegmentWorkItem>? _accurateChannel;
    private Task? _fastProcessingTask;
    private Task? _accurateProcessingTask;
    private long _segmentSequence;

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

    /// <summary>
    /// 開始ボタンが有効かどうか
    /// </summary>
    public bool CanStart => SelectedProcess != null && !IsRunning && !IsLoading;

    public MainViewModel(
        IAudioCaptureService audioCaptureService,
        IVADService vadService,
        IASRService asrService,
        ITranslationService translationService,
        OverlayViewModel overlayViewModel,
        AppSettings settings,
        IUpdateService updateService,
        IServiceProvider serviceProvider,
        SettingsFilePath settingsFilePath,
        SettingsViewModel settingsViewModel)
    {
        _audioCaptureService = audioCaptureService;
        _vadService = vadService;
        _asrService = asrService;
        _translationService = translationService;
        _overlayViewModel = overlayViewModel;
        _settings = settings;
        _updateService = updateService;
        _serviceProvider = serviceProvider;
        _settingsFilePath = settingsFilePath;
        _settingsViewModel = settingsViewModel;

        // 音声データ受信時の処理
        _audioCaptureService.AudioDataAvailable += OnAudioDataAvailable;
        _audioCaptureService.CaptureStatusChanged += OnCaptureStatusChanged;
        _settingsViewModel.SettingsSaved += OnSettingsSaved;
        _asrService.ModelDownloadProgress += OnModelDownloadProgress;
        _asrService.ModelStatusChanged += OnModelStatusChanged;
        _translationService.ModelDownloadProgress += OnModelDownloadProgress;
        _translationService.ModelStatusChanged += OnModelStatusChanged;
        _updateService.StatusChanged += OnUpdateStatusChanged;
        _updateService.UpdateAvailable += OnUpdateAvailable;
        _updateService.UpdateReady += OnUpdateReady;
        _updateService.UpdateSettings(_settings.Update);

        // 初期化
        RefreshProcesses();
        RestoreLastSelectedProcess();
        Log("アプリケーションを起動しました");
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

    [RelayCommand]
    private void RefreshProcesses()
    {
        Processes.Clear();

        var activeProcessIds = GetActiveAudioProcessIds();
        var currentProcessId = Environment.ProcessId;
        IEnumerable<ProcessInfo> processes;

        if (activeProcessIds.Count > 0)
        {
            // アクティブなオーディオプロセスのみを表示（自分自身は除外）
            var allProcesses = Process.GetProcesses()
                .Where(p => activeProcessIds.Contains(p.Id) && p.Id != currentProcessId)
                .OrderBy(p => p.ProcessName)
                .ThenBy(p => p.Id);

            var processList = new List<ProcessInfo>();
            var processNames = new Dictionary<string, int>();

            foreach (var p in allProcesses)
            {
                var title = string.IsNullOrWhiteSpace(p.MainWindowTitle) ? p.ProcessName : p.MainWindowTitle;
                var name = p.ProcessName;

                // 同じプロセス名が複数ある場合（例：複数のChromeプロセス）、IDをタイトルに追加
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
            }

            processes = processList;
        }
        else
        {
            // フォールバック：メインウィンドウを持つプロセスを表示（自分自身は除外）
            processes = Process.GetProcesses()
                .Where(p => p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(p.MainWindowTitle) && p.Id != currentProcessId)
                .OrderBy(p => p.ProcessName)
                .ThenBy(p => p.Id)
                .Select(p => new ProcessInfo
                {
                    Id = p.Id,
                    Name = p.ProcessName,
                    Title = p.MainWindowTitle
                });
        }

        foreach (var process in processes)
        {
            Processes.Add(process);
        }

        RestoreLastSelectedProcess();
        Log($"プロセス一覧を更新しました（{Processes.Count}件）");
    }

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

            // ゲームプロファイルを適用
            var profile = _settings.GameProfiles
                .FirstOrDefault(p => p.ProcessName.Equals(SelectedProcess.Name, StringComparison.OrdinalIgnoreCase));

            if (profile != null)
            {
                _asrService.SetHotwords(profile.Hotwords);
                _asrService.SetInitialPrompt(profile.InitialPrompt);
                _asrService.SetCorrectionDictionary(profile.ASRCorrectionDictionary);
                _translationService.SetPreTranslationDictionary(profile.PreTranslationDictionary);
                _translationService.SetPostTranslationDictionary(profile.PostTranslationDictionary);
                Log($"プロファイル '{profile.Name}' を適用しました");
            }

            // モデルのロード状態を確認（アプリ起動時に初期化済み）
            if (!_asrService.IsModelLoaded)
            {
                IsRunning = false;
                StatusText = "ASRモデル未ロード: 音声認識を開始できません。";
                StatusColor = Brushes.Red;
                Log("ASRモデル未ロードのため音声認識を停止しました。モデルのダウンロードが完了するまでお待ちください。");
                return;
            }

            var sourceLanguage = _settings.Translation.SourceLanguage;
            var targetLanguage = _settings.Translation.TargetLanguage;

            if (!_translationService.IsModelLoaded)
            {
                Log($"翻訳モデル未ロードのためタグ付け翻訳にフォールバックします ({sourceLanguage}→{targetLanguage})。");
            }
            else
            {
                Log($"翻訳モデルが準備完了しました ({sourceLanguage}→{targetLanguage})。");
            }

            await StartProcessingPipelinesAsync(_processingCancellation.Token);

            // キャプチャ開始（オーディオセッションが見つかるまで待機）
            StatusText = "音声の再生を待機中...";
            StatusColor = Brushes.Orange;
            Log($"'{SelectedProcess.DisplayName}' (PID: {SelectedProcess.Id}) の音声再生を待機しています...");
            LoggerService.LogDebug($"StartAsync: Starting audio capture for process: {SelectedProcess.Name} (ID: {SelectedProcess.Id}, Title: {SelectedProcess.Title})");

            var captureStarted = await _audioCaptureService.StartCaptureWithRetryAsync(
                SelectedProcess.Id,
                _processingCancellation.Token);

            if (!captureStarted)
            {
                // キャンセルされた場合
                await StopProcessingPipelinesAsync();
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

    [RelayCommand]
    private async Task StopAsync()
    {
        _processingCancellation?.Cancel();
        _processingCancellation?.Dispose();
        _processingCancellation = null;
        await StopProcessingPipelinesAsync();
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

    [RelayCommand]
    private void OpenSettings()
    {
        var window = _serviceProvider.GetRequiredService<SettingsWindow>();
        window.Owner = App.Current.MainWindow;
        window.ShowDialog();
        Log("設定画面を開きました");
    }

    private void OnAudioDataAvailable(object? sender, AudioDataEventArgs e)
    {
        try
        {
            // 状態とチャネルを一度に取得してTOCTOUバグを回避
            var isRunning = IsRunning;
            var cancellation = _processingCancellation;
            var channel = _segmentChannel;

            if (!isRunning || cancellation == null || cancellation.IsCancellationRequested || channel == null)
            {
                return;
            }

            // VADで発話区間を検出
            var segments = _vadService.DetectSpeech(e.AudioData);

            foreach (var segment in segments)
            {
                // キャンセル要求を確認（channelは既にnullチェック済み）
                if (cancellation.IsCancellationRequested)
                {
                    return;
                }

                var sequence = Interlocked.Increment(ref _segmentSequence);
                var workItem = new SpeechSegmentWorkItem(sequence, segment);

                if (!channel.Writer.TryWrite(workItem))
                {
                    Log($"音声セグメントをキューに追加できないため破棄しました (ID: {segment.Id})");
                }
            }
        }
        catch (Exception ex)
        {
            Log($"処理エラー: {ex.Message}\nスタックトレース: {ex.StackTrace}");
        }
    }

    private async Task StartProcessingPipelinesAsync(CancellationToken token)
    {
        await StopProcessingPipelinesAsync();

        _segmentSequence = 0;
        // バウンデッドチャネルを使用してメモリ使用量を制限
        _segmentChannel = Channel.CreateBounded<SpeechSegmentWorkItem>(new BoundedChannelOptions(ChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest // バッファが満杯時は最古のアイテムを削除
        });
        _accurateChannel = Channel.CreateBounded<SpeechSegmentWorkItem>(new BoundedChannelOptions(ChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait // バッファが満杯時はライターが待機
        });

        _fastProcessingTask = Task.Run(() => ProcessFastQueueAsync(_segmentChannel.Reader, _accurateChannel.Writer, token), token);
        _accurateProcessingTask = Task.Run(() => ProcessAccurateQueueAsync(_accurateChannel.Reader, token), token);
    }

    private async Task StopProcessingPipelinesAsync()
    {
        _segmentChannel?.Writer.TryComplete();
        _accurateChannel?.Writer.TryComplete();

        // タスクの完了を待機（タイムアウト付き）
        var tasks = new List<Task>();
        if (_fastProcessingTask != null)
        {
            tasks.Add(_fastProcessingTask);
        }
        if (_accurateProcessingTask != null)
        {
            tasks.Add(_accurateProcessingTask);
        }

        if (tasks.Count > 0)
        {
            try
            {
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                Log("処理パイプラインの停止がタイムアウトしました");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log($"処理パイプライン停止エラー: {ex.Message}");
            }
        }

        _segmentChannel = null;
        _accurateChannel = null;
        _fastProcessingTask = null;
        _accurateProcessingTask = null;
    }

    private async Task ProcessFastQueueAsync(
        ChannelReader<SpeechSegmentWorkItem> reader,
        ChannelWriter<SpeechSegmentWorkItem> accurateWriter,
        CancellationToken token)
    {
        try
        {
            await foreach (var item in reader.ReadAllAsync(token))
            {
                if (token.IsCancellationRequested || !IsRunning)
                {
                    return;
                }

                var fastResult = await _asrService.TranscribeFastAsync(item.Segment);
                if (token.IsCancellationRequested || !IsRunning)
                {
                    return;
                }

                ProcessingLatency = fastResult.ProcessingTimeMs;

                if (!string.IsNullOrWhiteSpace(fastResult.Text))
                {
                    var partialSubtitle = new SubtitleItem
                    {
                        SegmentId = item.Segment.Id,
                        OriginalText = fastResult.Text,
                        IsFinal = false
                    };
                    _overlayViewModel.AddOrUpdateSubtitle(partialSubtitle);
                    Log($"[仮] {fastResult.Text}");
                }

                try
                {
                    await accurateWriter.WriteAsync(item, token);
                }
                catch (ChannelClosedException)
                {
                    if (IsRunning && !token.IsCancellationRequested)
                    {
                        Log($"高精度キューが閉じられたためセグメントを破棄しました (ID: {item.Segment.Id})");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            accurateWriter.TryComplete();
        }
    }

    private async Task ProcessAccurateQueueAsync(ChannelReader<SpeechSegmentWorkItem> reader, CancellationToken token)
    {
        var semaphore = new SemaphoreSlim(MaxAccurateParallelism);
        var pendingTasks = new List<Task>();
        var buffer = new SortedDictionary<long, AccurateOutput>();
        var bufferLock = new object();
        var nextSequence = 1L;

        void EnqueueOutput(AccurateOutput output)
        {
            lock (bufferLock)
            {
                buffer[output.Sequence] = output;

                while (buffer.TryGetValue(nextSequence, out var next))
                {
                    buffer.Remove(nextSequence);
                    if (next.TranslationLatencyMs.HasValue)
                    {
                        TranslationLatency = next.TranslationLatencyMs.Value;
                    }

                    if (next.Subtitle != null)
                    {
                        _overlayViewModel.AddOrUpdateSubtitle(next.Subtitle);
                    }

                    if (!string.IsNullOrWhiteSpace(next.LogMessage))
                    {
                        Log(next.LogMessage);
                    }

                    nextSequence++;
                }
            }
        }

        async Task HandleItemAsync(SpeechSegmentWorkItem item)
        {
            await semaphore.WaitAsync(token);
            try
            {
                if (token.IsCancellationRequested || !IsRunning)
                {
                    EnqueueOutput(new AccurateOutput(item.Sequence, null, null, null));
                    return;
                }

                var accurateResult = await _asrService.TranscribeAccurateAsync(item.Segment);
                if (token.IsCancellationRequested || !IsRunning)
                {
                    EnqueueOutput(new AccurateOutput(item.Sequence, null, null, null));
                    return;
                }

                if (string.IsNullOrWhiteSpace(accurateResult.Text))
                {
                    EnqueueOutput(new AccurateOutput(item.Sequence, null, null, null));
                    return;
                }

                var sourceLanguage = _settings.Translation.SourceLanguage;
                var targetLanguage = _settings.Translation.TargetLanguage;
                LoggerService.LogDebug($"[AccurateProcessingAsync] 翻訳開始: Text={accurateResult.Text}");
                var translationResult = await _translationService.TranslateAsync(accurateResult.Text, sourceLanguage, targetLanguage);
                LoggerService.LogDebug($"[AccurateProcessingAsync] 翻訳完了: Original={accurateResult.Text}, Translated={translationResult.TranslatedText}, FromCache={translationResult.FromCache}, Time={translationResult.ProcessingTimeMs}ms");
                if (token.IsCancellationRequested || !IsRunning)
                {
                    EnqueueOutput(new AccurateOutput(item.Sequence, null, null, null));
                    return;
                }

                var finalSubtitle = new SubtitleItem
                {
                    SegmentId = item.Segment.Id,
                    OriginalText = accurateResult.Text,
                    TranslatedText = translationResult.TranslatedText,
                    IsFinal = true
                };
                var logMessage = $"[確定] ({sourceLanguage}→{targetLanguage}) {accurateResult.Text} → {translationResult.TranslatedText}";
                LoggerService.LogInfo(logMessage);
                EnqueueOutput(new AccurateOutput(item.Sequence, finalSubtitle, logMessage, translationResult.ProcessingTimeMs));
            }
            catch (Exception ex)
            {
                var sourceLanguage = _settings.Translation.SourceLanguage;
                var targetLanguage = _settings.Translation.TargetLanguage;
                var logMessage = $"翻訳エラー ({sourceLanguage}→{targetLanguage}): {ex.Message}";
                EnqueueOutput(new AccurateOutput(item.Sequence, null, logMessage, null));
            }
            finally
            {
                semaphore.Release();
            }
        }

        try
        {
            await foreach (var item in reader.ReadAllAsync(token))
            {
                if (token.IsCancellationRequested || !IsRunning)
                {
                    break;
                }

                var task = Task.Run(() => HandleItemAsync(item), token);
                pendingTasks.Add(task);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            try
            {
                await Task.WhenAll(pendingTasks);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                semaphore.Dispose();
            }
        }
    }

    private void Log(string message, bool suppressDuplicate = false)
    {
        // 重複メッセージの抑制（ダウンロード進捗など）
        if (suppressDuplicate)
        {
            // メッセージのベース部分を抽出（数値部分を除外して比較）
            var baseMessage = ExtractBaseMessage(message);
            var lastBaseMessage = _lastLogMessage != null ? ExtractBaseMessage(_lastLogMessage) : null;

            if (baseMessage == lastBaseMessage)
            {
                // ベース部分が同じ場合はスキップ
                return;
            }
        }

        _lastLogMessage = message;

        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var logLine = $"[{timestamp}] {message}";

        _logLines.Enqueue(logLine);

        // 最新MaxLogLines行のみ保持
        while (_logLines.Count > MaxLogLines)
        {
            _logLines.Dequeue();
        }

        // StringBuilderを使用して効率的に文字列を構築
        var sb = new StringBuilder(_logLines.Count * 50); // 概算容量を事前確保
        // ToArray()でスナップショットを作成し、列挙中のコレクション変更エラーを回避
        foreach (var line in _logLines.ToArray())
        {
            sb.AppendLine(line);
        }
        LogText = sb.ToString();
    }

    /// <summary>
    /// メッセージから数値・パーセント部分を除去してベース部分を抽出
    /// </summary>
    private static string ExtractBaseMessage(string message)
    {
        // 数値とパーセント記号を除去してベース部分を比較
        return System.Text.RegularExpressions.Regex.Replace(message, @"[\d.]+%?", "").Trim();
    }

    private void OnModelDownloadProgress(object? sender, ModelDownloadProgressEventArgs e)
    {
        var progressText = e.ProgressPercentage.HasValue
            ? $"{e.ProgressPercentage.Value:F1}%"
            : "進捗不明";
        StatusText = $"{e.ServiceName} {e.ModelName} ダウンロード中... {progressText}";
        StatusColor = Brushes.Orange;
        // suppressDuplicate: true で連続するダウンロード進捗メッセージを抑制
        Log($"{e.ServiceName} {e.ModelName} ダウンロード進行中: {progressText}", suppressDuplicate: true);
    }

    private void OnModelStatusChanged(object? sender, ModelStatusChangedEventArgs e)
    {
        var message = e.Exception != null
            ? $"{e.Message} ({FormatExceptionMessage(e.Exception)})"
            : e.Message;

        StatusText = $"{e.ServiceName}: {message}";
        StatusColor = e.Status == ModelStatusType.DownloadFailed || e.Status == ModelStatusType.LoadFailed
            ? Brushes.Red
            : Brushes.Orange;
        Log($"{e.ServiceName} {e.ModelName}: {message}");
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
            // 待機中のメッセージは連続で表示しない
            Log(e.Message, suppressDuplicate: e.IsWaiting);
        });
    }

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
    /// <summary>
    /// モデルを初期化（起動時に呼び出される）
    /// ASRと翻訳モデルを並列ダウンロード・読み込み
    /// </summary>
    public async Task InitializeModelsAsync()
    {
        try
        {
            IsLoading = true;
            Log("モデルの初期化を開始します...");

            // ASRと翻訳モデルを並列初期化
            var asrTask = Task.Run(async () =>
            {
                try
                {
                    LoadingMessage = "ASRモデルをダウンロード中...";
                    await _asrService.InitializeAsync();
                    LoggerService.LogInfo("ASR model initialization completed");
                }
                catch (Exception ex)
                {
                    LoggerService.LogError($"ASR initialization error: {ex.Message}");
                    throw;
                }
            });

            var translationTask = Task.Run(async () =>
            {
                try
                {
                    LoadingMessage = "翻訳モデルをダウンロード中...";
                    await _translationService.InitializeAsync();
                    LoggerService.LogInfo("Translation model initialization completed");
                }
                catch (Exception ex)
                {
                    LoggerService.LogError($"Translation initialization error: {ex.Message}");
                    throw;
                }
            });

            // 両方の初期化が完了するまで待機
            await Task.WhenAll(asrTask, translationTask);

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
        _settings.Save(_settingsFilePath.Value);
        Log($"選択プロセス '{process.DisplayName}' を設定ファイルに保存しました");
    }

    /// <summary>
    /// 現在オーディオをアクティブに再生しているプロセスのIDを取得する
    /// </summary>
    /// <returns>アクティブなオーディオプロセスのID一覧</returns>
    private static HashSet<int> GetActiveAudioProcessIds()
    {
        var processIds = new HashSet<int>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var sessionManager = device.AudioSessionManager;
            var sessions = sessionManager.Sessions;

            for (var i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                try
                {
                    // AudioSessionStateActive = 1
                    if ((int)session.State == 1)
                    {
                        // ProcessIDはAudioSessionControlの派生型から取得
                        var processIdProp = session.GetType().GetProperty("ProcessID");
                        if (processIdProp?.GetValue(session) is uint processId && processId > 0)
                        {
                            processIds.Add((int)processId);
                        }
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                {
                    // 個別のセッション取得に失敗しても続行
                    LoggerService.LogError($"Failed to get audio session info: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // AudioSessionの取得に失敗した場合は空のセットを返す
            LoggerService.LogError($"Failed to enumerate audio sessions: {ex.Message}");
        }

        return processIds;
    }

    private sealed record SpeechSegmentWorkItem(long Sequence, SpeechSegment Segment);

    private sealed record AccurateOutput(
        long Sequence,
        SubtitleItem? Subtitle,
        string? LogMessage,
        double? TranslationLatencyMs);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        LoggerService.LogDebug("MainViewModel.Dispose: 開始");

        // 音声キャプチャを停止
        try
        {
            _audioCaptureService.StopCapture();
            LoggerService.LogInfo("MainViewModel.Dispose: 音声キャプチャ停止完了");
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"MainViewModel.Dispose: 音声キャプチャ停止エラー: {ex.Message}");
        }

        // 処理パイプラインの停止
        _processingCancellation?.Cancel();
        _processingCancellation?.Dispose();
        _processingCancellation = null;
        LoggerService.LogInfo("MainViewModel.Dispose: 処理パイプライン停止完了");

        // イベントハンドラの登録解除
        _audioCaptureService.AudioDataAvailable -= OnAudioDataAvailable;
        _audioCaptureService.CaptureStatusChanged -= OnCaptureStatusChanged;
        _settingsViewModel.SettingsSaved -= OnSettingsSaved;
        _asrService.ModelDownloadProgress -= OnModelDownloadProgress;
        _asrService.ModelStatusChanged -= OnModelStatusChanged;
        _translationService.ModelDownloadProgress -= OnModelDownloadProgress;
        _translationService.ModelStatusChanged -= OnModelStatusChanged;
        _updateService.StatusChanged -= OnUpdateStatusChanged;
        _updateService.UpdateAvailable -= OnUpdateAvailable;
        _updateService.UpdateReady -= OnUpdateReady;
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
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string DisplayName => $"{Name} - {Title}";
}
