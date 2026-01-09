using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
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
    private readonly StringBuilder _logBuilder = new();

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
        SettingsFilePath settingsFilePath)
    {
        _audioCaptureService = audioCaptureService;
        _vadService = vadService;
        _asrService = asrService;
        _translationService = translationService;
        _overlayViewModel = overlayViewModel;
        _settings = settings;
        _serviceProvider = serviceProvider;
        _settingsFilePath = settingsFilePath;

        // 音声データ受信時の処理
        _audioCaptureService.AudioDataAvailable += OnAudioDataAvailable;

        // 初期化
        RefreshProcesses();
        RestoreLastSelectedProcess();
        Log("アプリケーションを起動しました");
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

        try
        {
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
                _translationService.SetPreTranslationDictionary(profile.PreTranslationDictionary);
                _translationService.SetPostTranslationDictionary(profile.PostTranslationDictionary);
                Log($"プロファイル '{profile.Name}' を適用しました");
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
            // VADで発話区間を検出
            var segments = _vadService.DetectSpeech(e.AudioData);

            foreach (var segment in segments)
            {
                // 低遅延ASR（仮字幕）
                var fastResult = await _asrService.TranscribeFastAsync(segment);
                ProcessingLatency = fastResult.ProcessingTimeMs;

                if (!string.IsNullOrWhiteSpace(fastResult.Text))
                {
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
                _ = ProcessAccurateAsync(segment);
            }
        }
        catch (Exception ex)
        {
            Log($"処理エラー: {ex.Message}");
        }
    }

    private async Task ProcessAccurateAsync(SpeechSegment segment)
    {
        try
        {
            // 高精度ASR
            var accurateResult = await _asrService.TranscribeAccurateAsync(segment);

            if (string.IsNullOrWhiteSpace(accurateResult.Text))
                return;

            // 翻訳
            var translationResult = await _translationService.TranslateAsync(accurateResult.Text);
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
            Log($"[確定] {accurateResult.Text} → {translationResult.TranslatedText}");
        }
        catch (Exception ex)
        {
            Log($"翻訳エラー: {ex.Message}");
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
