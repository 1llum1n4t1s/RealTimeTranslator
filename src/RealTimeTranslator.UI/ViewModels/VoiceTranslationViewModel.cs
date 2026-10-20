using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.UI.ViewModels;

/// <summary>字幕とは独立してマイク音声の送信を操作する。</summary>
public partial class VoiceTranslationViewModel : ObservableObject, IDisposable
{
    private readonly IVoiceTranslationService _service;
    private readonly IVoiceAudioService _audio;
    private readonly SettingsViewModel _settingsVm;
    private readonly VoiceTranslationSettings _settings;
    private readonly Action<Uri> _openExternalUri;
    private bool _deviceEnumerationSucceeded;
    private bool _initializing = true;
    private bool _disposed;
    private bool _operationPending;
    private bool _stopPending;
    private bool _stopRequested;
    private VoiceTranslationState _state;

    [ObservableProperty] private VoiceAudioDevice? _selectedMicrophone;
    [ObservableProperty] private VoiceAudioDevice? _selectedOutput;
    [ObservableProperty] private OutputLanguageOption? _selectedLanguage;
    [ObservableProperty] private VoiceModeOption? _selectedMode;
    [ObservableProperty] private bool _pushToTalkEnabled;
    [ObservableProperty] private string _statusText = "停止中";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorText = string.Empty;
    [ObservableProperty] private string _transcript = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InputLevelNorm), nameof(InputLevelText))]
    private double _inputLevel;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputLevelNorm), nameof(OutputLevelText))]
    private double _outputLevel;
    [ObservableProperty] private string _deviceStatus = string.Empty;

    public VoiceTranslationViewModel(IVoiceTranslationService service, IVoiceAudioService audio,
        SettingsViewModel settingsVm, Action<Uri>? openExternalUri = null, IVoicePlaybackService? playback = null)
    {
        _service = service;
        _audio = audio;
        _settingsVm = settingsVm;
        _openExternalUri = openExternalUri ?? (uri => Process.Start(new ProcessStartInfo
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true,
        })?.Dispose());
        _settings = settingsVm.Settings.VoiceTranslation.Clone();
        OutputLanguageOptions = settingsVm.OutputLanguageOptions;
        SelectedLanguage = OutputLanguageOptions.FirstOrDefault(x => x.Code == _settings.OutputLanguage)
            ?? OutputLanguageOptions.First(x => x.Code == "en");
        SelectedMode = ModeOptions.FirstOrDefault(x => x.Value == _settings.Mode) ?? ModeOptions[1];
        _settings.OutputLanguage = SelectedLanguage.Code;
        _settings.Mode = SelectedMode.Value;
        PushToTalkEnabled = _settings.PushToTalkEnabled;
        InitializeControls(playback);
        InitializeGains();
        _service.StateChanged += OnStateChanged;
        _service.TranscriptReceived += OnTranscriptReceived;
        _service.LevelsChanged += OnLevelsChanged;
        _service.ErrorReceived += OnErrorReceived;
        _state = service.State;
        RefreshDevices();
        _initializing = false;
        UpdateState();
    }

    public ObservableCollection<VoiceAudioDevice> Microphones { get; } = [];
    public ObservableCollection<VoiceAudioDevice> Outputs { get; } = [];
    public ReadOnlyCollection<OutputLanguageOption> OutputLanguageOptions { get; }
    public ReadOnlyCollection<VoiceModeOption> ModeOptions { get; } = new(new VoiceModeOption[]
    {
        new(VoiceSendMode.Original, "原音を送信"),
        new(VoiceSendMode.Translated, "翻訳音声を送信"),
        new(VoiceSendMode.Muted, "ミュート")
    });

    public bool CanEditSettings => !_operationPending && !_stopPending &&
        _state is VoiceTranslationState.Stopped or VoiceTranslationState.Faulted;
    public bool CanStart => CanEditSettings && !IsCapturingPttKey && SelectedMicrophone is not null && SelectedOutput is not null;
    public bool CanStop => !_stopPending &&
        _state is VoiceTranslationState.Starting or VoiceTranslationState.Running;
    public bool CanChangeMode => !_operationPending && !_stopPending &&
        _state is not (VoiceTranslationState.Starting or VoiceTranslationState.Stopping);
    public bool IsRunning => _state == VoiceTranslationState.Running;
    public bool IsFaulted => _state == VoiceTranslationState.Faulted;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);
    public bool NeedsWaveLinkSetup => _deviceEnumerationSucceeded && Outputs.Count == 0;

    [RelayCommand(CanExecute = nameof(CanEditSettings))]
    private void DownloadWaveLink() => OpenWaveLinkUri("https://www.elgato.com/jp/ja/s/downloads");

    [RelayCommand(CanExecute = nameof(CanEditSettings))]
    private void OpenWaveLinkWebsite() => OpenWaveLinkUri("https://www.elgato.com/jp/ja/explorer/products/wave/wave-link-3-0-software-overview/");

    private void OpenWaveLinkUri(string url)
    {
        try { _openExternalUri(new Uri(url)); }
        catch (Exception ex) { ErrorText = $"Wave Link 3 の公式サイトを開けませんでした: {ex.Message}"; }
    }

    [RelayCommand(CanExecute = nameof(CanEditSettings))]
    private void RefreshDevices()
    {
        bool wasInitializing = _initializing;
        _initializing = true;
        _deviceEnumerationSucceeded = false;
        try
        {
            SelectedMicrophone = null;
            SelectedOutput = null;
            Microphones.Clear();
            Outputs.Clear();
            foreach (var device in _audio.GetMicrophones()) Microphones.Add(device);
            foreach (var device in _audio.GetOutputs().Where(x => x.IsWaveLink)) Outputs.Add(device);
            RefreshMonitorOutputs();
            SelectedMicrophone = Microphones.FirstOrDefault(x => x.Id == _settings.InputDeviceId);
            SelectedOutput = Outputs.FirstOrDefault(x => x.Id == _settings.OutputDeviceId);
            _deviceEnumerationSucceeded = true;
            ErrorText = string.Empty;
        }
        catch (Exception ex) { ErrorText = $"音声機器を取得できませんでした: {ex.Message}"; }
        finally { _initializing = wasInitializing; UpdateState(); }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        _operationPending = true;
        _stopRequested = false;
        ErrorText = string.Empty;
        Transcript = string.Empty;
        UpdateState();
        try
        {
            SaveSettings();
            await _service.StartAsync(_settings.Clone(), _settingsVm.Settings.OpenAIRealtime);
        }
        catch (OperationCanceledException) when (_stopRequested) { }
        catch (Exception ex) { ErrorText = $"ボイス通訳を開始できませんでした: {ex.Message}"; }
        finally { _operationPending = false; _state = _service.State; UpdateState(); }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        if (_stopPending) return;
        _stopPending = true;
        _stopRequested = true;
        UpdateState();
        try { await _service.StopAsync(); }
        catch (Exception ex) { ErrorText = $"ボイス通訳を停止できませんでした: {ex.Message}"; }
        finally { _stopPending = false; UpdateState(); }
    }

    partial void OnSelectedMicrophoneChanged(VoiceAudioDevice? value)
    {
        if (_initializing) return;
        _settings.InputDeviceId = value?.Id ?? string.Empty;
        SaveSettings();
        UpdateState();
    }

    partial void OnSelectedOutputChanged(VoiceAudioDevice? value)
    {
        if (_initializing) return;
        _settings.OutputDeviceId = value?.Id ?? string.Empty;
        SaveSettings();
        UpdateState();
    }

    partial void OnSelectedLanguageChanged(OutputLanguageOption? value)
    {
        if (value is null || _initializing) return;
        _settings.OutputLanguage = value.Code;
        SaveSettings();
    }

    partial void OnPushToTalkEnabledChanged(bool value)
    {
        if (_initializing) return;
        _settings.PushToTalkEnabled = value;
        if (!value) CancelPttCapture();
        SaveSettings();
        UpdateControlState();
    }

    partial void OnSelectedModeChanged(VoiceModeOption? value)
    {
        if (value is null || _initializing) return;
        _ = ApplyModeAsync(value.Value);
    }

    private async Task ApplyModeAsync(VoiceSendMode mode)
    {
        _operationPending = true;
        _stopRequested = false;
        UpdateState();
        try
        {
            await _service.SetModeAsync(mode);
            _settings.Mode = mode;
            SaveSettings();
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException || !_stopRequested)
                ErrorText = $"送信モードを変更できませんでした: {ex.Message}";
            _initializing = true;
            SelectedMode = ModeOptions.First(x => x.Value == _service.Mode);
            _initializing = false;
            _settings.Mode = _service.Mode;
            SaveSettings();
        }
        finally { _operationPending = false; UpdateState(); }
    }

    private void SaveSettings()
    {
        if (!_initializing) _settingsVm.UpdateVoiceTranslation(_settings);
    }

    private void UpdateState()
    {
        _state = _service.State;
        DeviceStatus = !_deviceEnumerationSucceeded ? "音声機器を確認できませんでした。エラーを確認し、機器一覧を更新してください。"
            : Outputs.Count == 0 ? "Wave Link 3 が見つかりません。画面内の「Wave Link 3 の初回設定」から導入できます。"
            : Microphones.Count == 0 ? "利用できるマイクがありません。接続して一覧を更新してください。"
            : SelectedMicrophone is null || SelectedOutput is null
                ? "使用するマイクと Wave Link の翻訳専用チャンネルを選択してください。以前の送信先は再選択が必要です。"
                : "選択した機器へ送信します。";
        StatusText = _stopPending ? "停止処理中…" : _state switch
        {
            VoiceTranslationState.Starting => "接続中…",
            VoiceTranslationState.Running => "送信中",
            VoiceTranslationState.Stopping => "停止処理中…",
            VoiceTranslationState.Faulted => "エラーで停止しました",
            _ => "停止中"
        };
        if (_state is VoiceTranslationState.Stopped or VoiceTranslationState.Faulted)
        {
            InputLevel = OutputLevel = 0;
            IsInputClipping = IsOutputClipping = false;
            _inputClipUntil = _outputClipUntil = 0;
        }
        OnPropertyChanged(nameof(CanEditSettings));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanChangeMode));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsFaulted));
        OnPropertyChanged(nameof(NeedsWaveLinkSetup));
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RefreshDevicesCommand.NotifyCanExecuteChanged();
        DownloadWaveLinkCommand.NotifyCanExecuteChanged();
        OpenWaveLinkWebsiteCommand.NotifyCanExecuteChanged();
        UpdateControlState();
    }

    private void Dispatch(Action action) => Dispatcher.UIThread.Post(() => { if (!_disposed) action(); });
    // 通知が UI キューで待つ間に完了した操作の状態へ巻き戻さない。
    private void OnStateChanged(VoiceTranslationState state) => Dispatch(UpdateState);
    private void OnTranscriptReceived(string text) => Dispatch(() => Transcript = text);
    private void OnLevelsChanged(float input, float output) => Dispatch(() =>
    {
        InputLevel = Math.Clamp(input * 100, 0, 100);
        OutputLevel = Math.Clamp(output * 100, 0, 100);
        UpdateClippingIndicators();
    });
    private void OnErrorReceived(Exception error) => Dispatch(() => ErrorText = error.Message);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _service.StateChanged -= OnStateChanged;
        _service.TranscriptReceived -= OnTranscriptReceived;
        _service.LevelsChanged -= OnLevelsChanged;
        _service.ErrorReceived -= OnErrorReceived;
        _service.MonitoringChanged -= OnMonitoringChanged;
        _service.MonitoringErrorReceived -= OnMonitoringError;
        GC.SuppressFinalize(this);
    }
}

public sealed record VoiceModeOption(VoiceSendMode Value, string Name);
