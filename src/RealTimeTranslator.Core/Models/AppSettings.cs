namespace RealTimeTranslator.Core.Models;

public class AppSettings
{
    public OverlaySettings Overlay { get; set; } = new();
    public AudioCaptureSettings AudioCapture { get; set; } = new();
    public OpenAIRealtimeSettings OpenAIRealtime { get; set; } = new();
    public string LastSelectedProcessName { get; set; } = string.Empty;
    public int LastSelectedProcessId { get; set; }
    public UpdateSettings Update { get; set; } = new();
}

public class UpdateSettings
{
    public bool Enabled { get; set; } = false;
    public string FeedUrl { get; set; } = string.Empty;
    public bool AutoApply { get; set; } = true;
}


public class OverlaySettings
{
    public string FontFamily { get; set; } = "Yu Gothic UI";
    public double FontSize { get; set; } = 24;
    public string PartialTextColor { get; set; } = "#80FFFFFF";
    public string FinalTextColor { get; set; } = "#FFFFFFFF";
    public string BackgroundColor { get; set; } = "#80000000";
    public double DisplayDuration { get; set; } = 5.0;
    public double FadeOutDuration { get; set; } = 0.5;
    public double BottomMarginPercent { get; set; } = 10;
    public int MaxLines { get; set; } = 3;
}

public class AudioCaptureSettings
{
    public int SampleRate { get; set; } = 16000;
}

public class OpenAIRealtimeSettings
{
    public string ApiKey { get; set; } = string.Empty;
    public string OutputLanguage { get; set; } = "ja";
    public string Model { get; set; } = "gpt-realtime-translate";
    public string Endpoint { get; set; } = "wss://api.openai.com/v1/realtime/translations";
    public int ReconnectDelayMs { get; set; } = 3000;
    // モバイル / Wi-Fi の一時切断（数十秒オーダー）でも諦めず、NetworkChange 復帰でもカウンタリセットされる。
    public int MaxReconnectAttempts { get; set; } = 30;
}

// GameProfile / GameProfiles は旧 Whisper+LLM ローカル翻訳時代の設定。
// OpenAI Realtime API 移行（コミット 5de5297）で適用処理が消えたため削除。
// 必要なら将来 OpenAI Realtime API の `instructions` フィールドにマップして復活させる。
