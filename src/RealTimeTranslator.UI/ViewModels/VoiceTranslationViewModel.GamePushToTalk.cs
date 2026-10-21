using CommunityToolkit.Mvvm.ComponentModel;
using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.UI.ViewModels;

public partial class VoiceTranslationViewModel
{
    [ObservableProperty] private bool _isCapturingGamePttKey;
    [ObservableProperty] private string _gamePttShortcutText = "F9";

    public bool GamePushToTalkEnabled
    {
        get => _settings.GamePushToTalkEnabled;
        set
        {
            if (!CanEditSettings || value == _settings.GamePushToTalkEnabled) return;
            _settings.GamePushToTalkEnabled = value;
            if (!value) CancelGamePttCapture();
            OnPropertyChanged();
            SaveSettings();
            UpdateControlState();
        }
    }

    public string GamePttCaptureButtonText => IsCapturingGamePttKey ? "キーを押してください…" : GamePttShortcutText;
    public bool CanCaptureGamePttKey => CanEditSettings && GamePushToTalkEnabled;

    private void InitializeGamePushToTalk()
    {
        if (!VoicePushToTalkShortcut.IsSupportedKey(_settings.GamePushToTalkVirtualKey)
            || !VoicePushToTalkShortcut.IsSupportedModifiers(_settings.GamePushToTalkModifiers))
        {
            _settings.GamePushToTalkVirtualKey = 0x78;
            _settings.GamePushToTalkModifiers = VoiceShortcutModifiers.None;
        }
        GamePttShortcutText = VoiceShortcutKey.Format(_settings.GamePushToTalkVirtualKey, _settings.GamePushToTalkModifiers);
    }

    public void BeginGamePttCapture()
    {
        if (!CanCaptureGamePttKey) return;
        CancelPttCapture();
        ErrorText = string.Empty;
        IsCapturingGamePttKey = true;
        UpdateState();
    }

    public void CancelGamePttCapture()
    {
        if (!IsCapturingGamePttKey) return;
        IsCapturingGamePttKey = false;
        UpdateState();
    }

    public void CaptureGamePttShortcut(int virtualKey, VoiceShortcutModifiers modifiers)
    {
        if (!IsCapturingGamePttKey || !CanCaptureGamePttKey) return;
        if (!VoicePushToTalkShortcut.IsSupportedKey(virtualKey) || !VoicePushToTalkShortcut.IsSupportedModifiers(modifiers))
        {
            ErrorText = "このキーは登録できません。通常のキー1つと、Ctrl・Shift・Alt・Winの組み合わせを押してください。";
            return;
        }
        _settings.GamePushToTalkVirtualKey = virtualKey;
        _settings.GamePushToTalkModifiers = modifiers;
        GamePttShortcutText = VoiceShortcutKey.Format(virtualKey, modifiers);
        IsCapturingGamePttKey = false;
        ErrorText = string.Empty;
        SaveSettings();
        UpdateState();
    }
}
