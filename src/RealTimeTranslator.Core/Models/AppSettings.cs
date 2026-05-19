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
    /// <summary>フォントの太さ。 "Normal" または "Bold"。 OverlayViewModel が Avalonia.Media.FontWeight に変換する。</summary>
    public string FontWeight { get; set; } = "Normal";
    // 既定 partial 字幕色は半透明白 (alpha 0x80)。 SettingsViewModel.TextColorOptions の
    // 「白（半透明）」と整合させて ComboBox 未選択状態が起きないようにしている。
    public string PartialTextColor { get; set; } = "#80FFFFFF";
    public string FinalTextColor { get; set; } = "#FFFFFFFF";
    // 旧 settings.json との後方互換のため #AARRGGBB を保持する。
    // 新 UI では BackgroundColorBase (#RRGGBB) × BackgroundOpacityPercent (0-100) から合成して
    // BackgroundColor を埋める運用 (SettingsViewModel が autoSave 時に同期)。
    // 既存環境からのマイグレーション: SanitizeSettings で BackgroundColor から
    // BackgroundColorBase / BackgroundOpacityPercent を逆算して埋める。
    public string BackgroundColor { get; set; } = "#80000000";
    /// <summary>背景色の RGB のみ (#RRGGBB)。 透明度は BackgroundOpacityPercent で別管理。</summary>
    public string BackgroundColorBase { get; set; } = "#000000";
    /// <summary>背景の不透明度 (0-100%)。 0/25/50/75/100 の 5 段階を想定 (0% = 完全透明)。</summary>
    public int BackgroundOpacityPercent { get; set; } = 50;
    public double DisplayDuration { get; set; } = 5.0;
    public double FadeOutDuration { get; set; } = 0.5;
    public double BottomMarginPercent { get; set; } = 10;
    public int MaxLines { get; set; } = 3;
}

public class AudioCaptureSettings
{
    public int SampleRate { get; set; } = 16000;

    // ────────── VAD (Voice Activity Detection) ──────────
    // BGM / 効果音だけが鳴っているシーンでの OpenAI 送信を抑制するためのゲート。
    // EnableVad=false の場合は素通し (旧挙動)。

    /// <summary>VAD ゲートを有効にする (default: true)。 BGM/SE 中の token 浪費を防ぐ。</summary>
    public bool EnableVad { get; set; } = true;

    /// <summary>
    /// VAD の感度プリセット。 "Balanced" (推奨) / "PrioritizeEdges" (頭尻尾重視) /
    /// "AggressiveSavings" (節約重視) / "Custom" (下の Threshold/PreRollMs/HangoverMs を直接使用)。
    /// プリセット選択時は SettingsViewModel が Threshold/PreRollMs/HangoverMs を上書きする。
    /// </summary>
    public string VadPreset { get; set; } = "Balanced";

    /// <summary>speech probability のしきい値 (0.0-1.0, default: 0.5 = Silero VAD 公式推奨)。</summary>
    public float VadThreshold { get; set; } = 0.5f;

    /// <summary>
    /// 発話冒頭の取りこぼし防止用にリングバッファに保持する直近音声の長さ (ms)。
    /// Balanced プリセットの推奨値は 600ms (threshold=0.5 維持で頭の子音を確実に拾うバランス値)。
    /// </summary>
    public int VadPreRollMs { get; set; } = 600;

    /// <summary>
    /// 発話末尾の切れ防止用に speech 終了判定後も送信を継続する長さ (ms)。
    /// Balanced プリセットの推奨値は 400ms (語尾の無声子音 / 息継ぎ込みの「、」を取りこぼさない)。
    /// </summary>
    public int VadHangoverMs { get; set; } = 400;

    // ────────── 自動 Pause 保険 ──────────

    /// <summary>
    /// 連続 N 秒間 speech 未検出ならキャプチャを自動停止する (0 = 無効, default: 0)。
    /// 「席を離れた間に token 垂れ流し」事故防止用。 VAD 有効時のみ機能する。
    /// </summary>
    public int AutoPauseOnSilenceSec { get; set; } = 0;
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
