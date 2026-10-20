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
    public string MonitorOutputDeviceId { get; set; } = string.Empty;
    public float InputGainDb { get; set; }
    public float OutputGainDb { get; set; }

    public const float MinimumGainDb = -24f;
    public const float MaximumGainDb = 24f;

    public static float ClampGainDb(float value) => float.IsFinite(value)
        ? Math.Clamp(value, MinimumGainDb, MaximumGainDb) : 0f;

    public VoiceTranslationSettings Clone() => new()
    {
        InputDeviceId = InputDeviceId,
        OutputDeviceId = OutputDeviceId,
        OutputLanguage = OutputLanguage,
        Mode = Mode,
        PushToTalkEnabled = PushToTalkEnabled,
        PushToTalkVirtualKey = PushToTalkVirtualKey,
        PushToTalkModifiers = PushToTalkModifiers,
        MonitorOutputDeviceId = MonitorOutputDeviceId,
        InputGainDb = InputGainDb,
        OutputGainDb = OutputGainDb
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
