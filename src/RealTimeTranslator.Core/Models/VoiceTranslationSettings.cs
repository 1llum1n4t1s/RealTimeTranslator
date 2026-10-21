namespace RealTimeTranslator.Core.Models;

/// <summary>字幕翻訳から独立した、マイク音声の送信設定。</summary>
public sealed class VoiceTranslationSettings
{
    public string InputDeviceId { get; set; } = string.Empty;
    public string OutputDeviceId { get; set; } = string.Empty;
    public string OutputLanguage { get; set; } = "en";
    public VoiceSendMode Mode { get; set; } = VoiceSendMode.Translated;
    public bool PushToTalkEnabled { get; set; } = true;
    public int PushToTalkVirtualKey { get; set; } = 0x77;
    public VoiceShortcutModifiers PushToTalkModifiers { get; set; }
    public bool GamePushToTalkEnabled { get; set; }
    public int GamePushToTalkVirtualKey { get; set; } = 0x78;
    public VoiceShortcutModifiers GamePushToTalkModifiers { get; set; }
    public string MonitorOutputDeviceId { get; set; } = string.Empty;
    public float InputGainDb { get; set; }
    public float OutputGainDb { get; set; }
    public bool EnableVad { get; set; } = true;
    public float VadThreshold { get; set; } = 0.8f;
    public int VadPreRollMs { get; set; } = 50;
    public int VadHangoverMs { get; set; } = 200;

    public const float MinimumGainDb = -24f;
    public const float MaximumGainDb = 24f;

    public static float ClampGainDb(float value) => float.IsFinite(value)
        ? Math.Clamp(value, MinimumGainDb, MaximumGainDb) : 0f;

    /// <summary>字幕側の設定へ触れず、マイク VAD の各値を独立して有効範囲へ収める。</summary>
    public bool NormalizeVadSettings()
    {
        float threshold = float.IsFinite(VadThreshold) ? Math.Clamp(VadThreshold, 0f, 1f) : 0.8f;
        int preRoll = Math.Clamp(VadPreRollMs, 0, 2000);
        int hangover = Math.Clamp(VadHangoverMs, 0, 2000);
        bool changed = VadThreshold != threshold || VadPreRollMs != preRoll || VadHangoverMs != hangover;
        VadThreshold = threshold;
        VadPreRollMs = preRoll;
        VadHangoverMs = hangover;
        return changed;
    }

    public VoiceTranslationSettings Clone() => new()
    {
        InputDeviceId = InputDeviceId,
        OutputDeviceId = OutputDeviceId,
        OutputLanguage = OutputLanguage,
        Mode = Mode,
        PushToTalkEnabled = PushToTalkEnabled,
        PushToTalkVirtualKey = PushToTalkVirtualKey,
        PushToTalkModifiers = PushToTalkModifiers,
        GamePushToTalkEnabled = GamePushToTalkEnabled,
        GamePushToTalkVirtualKey = GamePushToTalkVirtualKey,
        GamePushToTalkModifiers = GamePushToTalkModifiers,
        MonitorOutputDeviceId = MonitorOutputDeviceId,
        InputGainDb = InputGainDb,
        OutputGainDb = OutputGainDb,
        EnableVad = EnableVad,
        VadThreshold = VadThreshold,
        VadPreRollMs = VadPreRollMs,
        VadHangoverMs = VadHangoverMs
    };
}

[Flags]
public enum VoiceShortcutModifiers
{
    None = 0,
    Control = 1,
    Shift = 2,
    Alt = 4,
    Windows = 8
}

/// <summary>通常のキー1つと修飾キーで構成する発話キー。</summary>
public static class VoicePushToTalkShortcut
{
    public static bool IsSupportedKey(int key) => key is 0x08 or 0x09 or 0x0D or 0x13 or 0x14
        or >= 0x20 and <= 0x2E or >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A
        or 0x5D or >= 0x60 and <= 0x87 or 0x90 or 0x91
        or >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDF or 0xE2;

    public static bool IsSupportedModifiers(VoiceShortcutModifiers modifiers) =>
        (modifiers & ~(VoiceShortcutModifiers.Control | VoiceShortcutModifiers.Shift |
            VoiceShortcutModifiers.Alt | VoiceShortcutModifiers.Windows)) == 0;
}

public enum VoiceSendMode
{
    Original,
    Translated,
    Muted
}

public enum VoiceTranslationState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Faulted
}
