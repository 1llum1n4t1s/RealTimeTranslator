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
    // デフォルトで有効: UpdateService.TryGetValidFeedUri により HTTPS + github.com /
    // objects.githubusercontent.com ホスト allowlist で安全性確保済み。
    public bool Enabled { get; set; } = true;
    public string FeedUrl { get; set; } = string.Empty;

    // ユーザーが「このバージョンを無視」を選択したタグ名 (例: "v1.0.13")。
    // 起動時の自動チェックで取得した最新タグがこれと一致したら、ダイアログを開かずスキップする。
    // 手動チェック (バージョンタブの「更新の確認」ボタン) では無視タグは適用しない
    // (ユーザーが明示的にチェックしたから、結果は必ず見せる)。
    // Komorebi 互換挙動: VelopackUpdateDialog の VersionIgnored event でここに永続化。
    public string IgnoredTagName { get; set; } = string.Empty;
}


public class OverlaySettings
{
    // 既定フォント: 同梱の IBM Plex Sans JP (avares 解決は OverlayViewModel.EmbeddedFontMap)。
    // 新規 settings.json 生成時 / 旧設定にリスト外フォントが入っている場合 (SettingsViewModel.SanitizeSettings)
    // の両方でここがフォールバック値になる。
    public string FontFamily { get; set; } = "IBM Plex Sans JP";
    public double FontSize { get; set; } = 24;
    // 既定 partial 字幕色は半透明白 (alpha 0x80)。 SettingsViewModel.TextColorOptions の
    // 「白（半透明）」と整合させて ComboBox 未選択状態が起きないようにしている。
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
