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

    public VoiceTranslationSettings Clone() => new()
    {
        InputDeviceId = InputDeviceId,
        OutputDeviceId = OutputDeviceId,
        OutputLanguage = OutputLanguage,
        Mode = Mode,
        PushToTalkEnabled = PushToTalkEnabled,
        PushToTalkVirtualKey = PushToTalkVirtualKey
    };
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
