using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using NAudio.CoreAudioApi;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;
using RealTimeTranslator.UI.Views;

namespace RealTimeTranslator.UI.ViewModels;

/// <summary>
/// メインウィンドウのViewModel
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly IAudioCaptureService _audioCaptureService;
    private readonly IVADService _vadService;
    private readonly IASRService _asrService;
    private readonly ITranslationService _translationService;
    private readonly OverlayViewModel _overlayViewModel;
    private readonly AppSettings _settings;
    private readonly IServiceProvider _serviceProvider;
    private readonly SettingsFilePath _settingsFilePath;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly StringBuilder _logBuilder = new();
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

    public bool CanStart => SelectedProcess != null && !IsRunning;

    public MainViewModel(
        IAudioCaptureService audioCaptureService,
        IVADService vadService,
        IASRService asrService,
        ITranslationService translationService,
        OverlayViewModel overlayViewModel,
        AppSettings settings,
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
        _serviceProvider = serviceProvider;
        _settingsFilePath = settingsFilePath;
        _settingsViewModel = settingsViewModel;

        // 音声データ受信時の処理
        _audioCaptureService.AudioDataAvailable += OnAudioDataAvailable;
        _settingsViewModel.SettingsSaved += OnSettingsSaved;
        _asrService.ModelDownloadProgress += OnModelDownloadProgress;
        _asrService.ModelStatusChanged += OnModelStatusChanged;
        _translationService.ModelDownloadProgress += OnModelDownloadProgress;
        _translationService.ModelStatusChanged += OnModelStatusChanged;

        // 初期化
        RefreshProcesses();
        RestoreLastSelectedProcess();
        Log("アプリケーションを起動しました");
    }

    private void OnSettingsSaved(object? sender, SettingsSavedEventArgs e)
    {
        _audioCaptureService.ApplySettings(e.Settings.AudioCapture);
        _vadService.ApplySettings(e.Settings.AudioCapture);
        var sourceLanguage = e.Settings.Translation.SourceLanguage;
        var targetLanguage = e.Settings.Translation.TargetLanguage;

        if (IsRunning)
        {
            Stop();
            StatusText = "設定変更のため停止しました。再開時に新しい設定が反映されます。";
            StatusColor = Brushes.Orange;
            Log($"設定変更を検知したため停止しました。再開時に新しい設定が反映されます。翻訳言語: {sourceLanguage}→{targetLanguage}");
            return;
        }

        StatusText = "設定を更新しました。次回開始時に反映されます。";
        StatusColor = Brushes.Gray;
        Log($"設定変更を反映しました（次回開始時に適用）。翻訳言語: {sourceLanguage}→{targetLanguage}");
    }

    [RelayCommand]
    private void RefreshProcesses()
    {
        Processes.Clear();

        var activeProcessIds = GetActiveAudioProcessIds();
        var processes = Process.GetProcesses()
            .Where(p => activeProcessIds.Contains(p.Id))
            .OrderBy(p => p.ProcessName)
            .Select(p =>
            {
                var title = string.IsNullOrWhiteSpace(p.MainWindowTitle) ? p.ProcessName : p.MainWindowTitle;
                return new ProcessInfo
                {
                    Id = p.Id,
                    Name = p.ProcessName,
                    Title = title
                };
            });

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

        void HandleInitializationFailure(string serviceName, Exception ex)
        {
            IsRunning = false;
            var formattedMessage = FormatExceptionMessage(ex);
            StatusText = $"{serviceName}初期化失敗: {formattedMessage}";
            StatusColor = Brushes.Red;
            Log($"{serviceName}初期化エラー: {ex}");
        }

        try
        {
            _processingCancellation?.Cancel();
            _processingCancellation?.Dispose();
            _processingCancellation = new CancellationTokenSource();
            IsRunning = true;
            StatusText = "初期化中...";
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

            StatusText = "ASR初期化中...";
            Log("ASRの初期化を開始しました");
            try
            {
                await _asrService.InitializeAsync();
                Log("ASRの初期化が完了しました");
            }
            catch (Exception ex)
            {
                HandleInitializationFailure("ASR", ex);
                return;
            }

            if (!_asrService.IsModelLoaded)
            {
                IsRunning = false;
                StatusText = "ASRモデル未ロード: 音声認識を開始できません。";
                StatusColor = Brushes.Red;
                Log("ASRモデル未ロードのため音声認識を停止しました。");
                return;
            }

            StatusText = "翻訳初期化中...";
            var sourceLanguage = _settings.Translation.SourceLanguage;
            var targetLanguage = _settings.Translation.TargetLanguage;
            Log($"翻訳の初期化を開始しました ({sourceLanguage}→{targetLanguage})");
            try
            {
                await _translationService.InitializeAsync();
                Log($"翻訳の初期化が完了しました ({sourceLanguage}→{targetLanguage})");
            }
            catch (Exception ex)
            {
                HandleInitializationFailure("翻訳", ex);
                return;
            }

            if (!_translationService.IsModelLoaded)
            {
                Log("翻訳モデル未ロードのためタグ付け翻訳にフォールバックします。");
            }

            // キャプチャ開始
            _audioCaptureService.StartCapture(SelectedProcess.Id);

            StatusText = "実行中";
            StatusColor = Brushes.Green;
            Log($"'{SelectedProcess.DisplayName}' の音声キャプチャを開始しました");
        }
        catch (Exception ex)
        {
            IsRunning = false;
            StatusText = "エラー";
            StatusColor = Brushes.Red;
            Log($"エラー: {ex.Message}");
        }
    }

    [RelayCommand]
    private void Stop()
    {
        _processingCancellation?.Cancel();
        _processingCancellation?.Dispose();
        _processingCancellation = null;
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

    private async void OnAudioDataAvailable(object? sender, AudioDataEventArgs e)
    {
        try
        {
            if (!IsRunning || _processingCancellation == null || _processingCancellation.IsCancellationRequested)
            {
                return;
            }

            // VADで発話区間を検出
            var segments = _vadService.DetectSpeech(e.AudioData);

            foreach (var segment in segments)
            {
                if (!IsRunning || _processingCancellation.IsCancellationRequested)
                {
                    return;
                }

                // 低遅延ASR（仮字幕）
                var fastResult = await _asrService.TranscribeFastAsync(segment);
                if (!IsRunning || _processingCancellation.IsCancellationRequested)
                {
                    return;
                }
                ProcessingLatency = fastResult.ProcessingTimeMs;

                if (!string.IsNullOrWhiteSpace(fastResult.Text))
                {
                    if (!IsRunning || _processingCancellation.IsCancellationRequested)
                    {
                        return;
                    }
                    var partialSubtitle = new SubtitleItem
                    {
                        SegmentId = segment.Id,
                        OriginalText = fastResult.Text,
                        IsFinal = false
                    };
                    _overlayViewModel.AddOrUpdateSubtitle(partialSubtitle);
                    Log($"[仮] {fastResult.Text}");
                }

                // 高精度ASR（確定字幕）+ 翻訳
                _ = ProcessAccurateAsync(segment, _processingCancellation.Token);
            }
        }
        catch (Exception ex)
        {
            Log($"処理エラー: {ex.Message}");
        }
    }

    private async Task ProcessAccurateAsync(SpeechSegment segment, CancellationToken token)
    {
        try
        {
            if (token.IsCancellationRequested || !IsRunning)
            {
                return;
            }

            // 高精度ASR
            var accurateResult = await _asrService.TranscribeAccurateAsync(segment);
            if (token.IsCancellationRequested || !IsRunning)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(accurateResult.Text))
                return;

            if (token.IsCancellationRequested || !IsRunning)
            {
                return;
            }

            // 翻訳
            var sourceLanguage = _settings.Translation.SourceLanguage;
            var targetLanguage = _settings.Translation.TargetLanguage;
            var translationResult = await _translationService.TranslateAsync(accurateResult.Text, sourceLanguage, targetLanguage);
            if (token.IsCancellationRequested || !IsRunning)
            {
                return;
            }
            TranslationLatency = translationResult.ProcessingTimeMs;

            // 確定字幕を表示
            var finalSubtitle = new SubtitleItem
            {
                SegmentId = segment.Id,
                OriginalText = accurateResult.Text,
                TranslatedText = translationResult.TranslatedText,
                IsFinal = true
            };
            _overlayViewModel.AddOrUpdateSubtitle(finalSubtitle);
            Log($"[確定] ({sourceLanguage}→{targetLanguage}) {accurateResult.Text} → {translationResult.TranslatedText}");
        }
        catch (Exception ex)
        {
            var sourceLanguage = _settings.Translation.SourceLanguage;
            var targetLanguage = _settings.Translation.TargetLanguage;
            Log($"翻訳エラー ({sourceLanguage}→{targetLanguage}): {ex.Message}");
        }
    }

    private void Log(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        _logBuilder.AppendLine($"[{timestamp}] {message}");
        
        // 最新1000行のみ保持
        var lines = _logBuilder.ToString().Split('\n');
        if (lines.Length > 1000)
        {
            _logBuilder.Clear();
            _logBuilder.AppendLine(string.Join("\n", lines.TakeLast(1000)));
        }
        
        LogText = _logBuilder.ToString();
    }

    private void OnModelDownloadProgress(object? sender, ModelDownloadProgressEventArgs e)
    {
        var progressText = e.ProgressPercentage.HasValue
            ? $"{e.ProgressPercentage.Value:F1}%"
            : "進捗不明";
        StatusText = $"{e.ServiceName} {e.ModelName} ダウンロード中... {progressText}";
        StatusColor = Brushes.Orange;
        Log($"{e.ServiceName} {e.ModelName} ダウンロード進行中: {progressText}");
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

    private static HashSet<int> GetActiveAudioProcessIds()
    {
        var processIds = new HashSet<int>();
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        using var sessionManager = device.AudioSessionManager;
        using var sessions = sessionManager.Sessions;

        for (var i = 0; i < sessions.Count; i++)
        {
            using var session = sessions[i];
            if (session.State != AudioSessionState.AudioSessionStateActive)
            {
                continue;
            }

            if (session is AudioSessionControl2 session2 && session2.ProcessID > 0)
            {
                processIds.Add((int)session2.ProcessID);
            }
        }

        return processIds;
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
