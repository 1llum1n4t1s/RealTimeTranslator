using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.UI.ViewModels;

public partial class VoiceTranslationViewModel
{
    private IVoicePlaybackService? _playback;
    private bool _monitorOperationPending;
    [ObservableProperty] private VoiceAudioDevice? _selectedMonitorOutput;
    [ObservableProperty] private bool _isCapturingPttKey;
    [ObservableProperty] private string _pttShortcutText = "F8";
    [ObservableProperty] private bool _isMonitoring;

    public ObservableCollection<VoiceAudioDevice> MonitorOutputs { get; } = [];
    public string PttCaptureButtonText => IsCapturingPttKey ? "キーを押してください…" : PttShortcutText;
    public bool CanCapturePttKey => CanEditSettings && PushToTalkEnabled;
    public bool CanEditMonitorOutput => !IsMonitoring && !_monitorOperationPending;
    public bool CanToggleMonitoring => !_monitorOperationPending && !_operationPending && !_stopPending &&
        IsRunning && _service.Mode == VoiceSendMode.Translated && (IsMonitoring || SelectedMonitorOutput is not null);
    public string MonitorButtonText => IsMonitoring ? "試聴を停止" : "訳音声を試聴";
    public string MonitorStatusText => IsMonitoring ? "訳音声を試聴先にも同時再生しています。" : "試聴はオフです。ゲームへの送信は通常どおり続きます。";

    private void InitializeControls(IVoicePlaybackService? playback)
    {
        _playback = playback;
        if (!VoicePushToTalkShortcut.IsSupportedKey(_settings.PushToTalkVirtualKey)
            || !VoicePushToTalkShortcut.IsSupportedModifiers(_settings.PushToTalkModifiers))
        {
            _settings.PushToTalkVirtualKey = 0x77;
            _settings.PushToTalkModifiers = VoiceShortcutModifiers.None;
        }
        PttShortcutText = VoiceShortcutKey.Format(_settings.PushToTalkVirtualKey, _settings.PushToTalkModifiers);
        InitializeGamePushToTalk();
        IsMonitoring = _service.IsMonitoring;
        _service.MonitoringChanged += OnMonitoringChanged;
        _service.MonitoringErrorReceived += OnMonitoringError;
    }

    public void BeginPttCapture()
    {
        if (!CanCapturePttKey) return;
        CancelGamePttCapture();
        ErrorText = string.Empty;
        IsCapturingPttKey = true;
        UpdateState();
    }

    public void CancelPttCapture()
    {
        if (!IsCapturingPttKey) return;
        IsCapturingPttKey = false;
        UpdateState();
    }

    public void CapturePttShortcut(int virtualKey, VoiceShortcutModifiers modifiers)
    {
        if (!IsCapturingPttKey || !CanCapturePttKey) return;
        if (!VoicePushToTalkShortcut.IsSupportedKey(virtualKey) || !VoicePushToTalkShortcut.IsSupportedModifiers(modifiers))
        {
            ErrorText = "このキーは登録できません。通常のキー1つと、Ctrl・Shift・Alt・Winの組み合わせを押してください。";
            return;
        }
        _settings.PushToTalkVirtualKey = virtualKey;
        _settings.PushToTalkModifiers = modifiers;
        PttShortcutText = VoiceShortcutKey.Format(virtualKey, modifiers);
        IsCapturingPttKey = false;
        ErrorText = string.Empty;
        SaveSettings();
        UpdateState();
    }

    private void RefreshMonitorOutputs()
    {
        SelectedMonitorOutput = null;
        MonitorOutputs.Clear();
        if (_playback is null) return;
        foreach (var device in _playback.GetOutputs()) MonitorOutputs.Add(device);
        SelectedMonitorOutput = MonitorOutputs.FirstOrDefault(device => device.Id == _settings.MonitorOutputDeviceId);
    }

    partial void OnSelectedMonitorOutputChanged(VoiceAudioDevice? value)
    {
        if (_initializing) return;
        _settings.MonitorOutputDeviceId = value?.Id ?? string.Empty;
        SaveSettings();
        UpdateControlState();
    }

    [RelayCommand(CanExecute = nameof(CanToggleMonitoring))]
    private async Task ToggleMonitoringAsync()
    {
        _monitorOperationPending = true;
        ErrorText = string.Empty;
        UpdateControlState();
        try { await _service.SetMonitoringAsync(_service.IsMonitoring ? null : SelectedMonitorOutput?.Id); }
        catch (OperationCanceledException) when (_stopRequested) { }
        catch (Exception ex) { ErrorText = $"訳音声を試聴できませんでした: {ex.Message}"; }
        finally
        {
            _monitorOperationPending = false;
            IsMonitoring = _service.IsMonitoring;
            UpdateControlState();
        }
    }

    private void OnMonitoringChanged(bool _) => Dispatch(() =>
    {
        IsMonitoring = _service.IsMonitoring;
        UpdateControlState();
    });

    private void OnMonitoringError(Exception error) => Dispatch(() => ErrorText = $"試聴を停止しました: {error.Message}");

    private void UpdateControlState()
    {
        OnPropertyChanged(nameof(PttCaptureButtonText));
        OnPropertyChanged(nameof(CanCapturePttKey));
        OnPropertyChanged(nameof(GamePttCaptureButtonText));
        OnPropertyChanged(nameof(CanCaptureGamePttKey));
        OnPropertyChanged(nameof(CanEditMonitorOutput));
        OnPropertyChanged(nameof(CanToggleMonitoring));
        OnPropertyChanged(nameof(MonitorButtonText));
        OnPropertyChanged(nameof(MonitorStatusText));
        ToggleMonitoringCommand.NotifyCanExecuteChanged();
    }
}
