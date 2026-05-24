using System.Text.Json.Serialization;

namespace RealTimeTranslator.Core.Models;

public class AppSettings
{
    public OverlaySettings Overlay { get; set; } = new();
    public AudioCaptureSettings AudioCapture { get; set; } = new();
    public OpenAIRealtimeSettings OpenAIRealtime { get; set; } = new();
    public string LastSelectedProcessName { get; set; } = string.Empty;
    public int LastSelectedProcessId { get; set; }
    public UpdateSettings Update { get; set; } = new();
    public TranslationLogSettings TranslationLog { get; set; } = new();
}

/// <summary>
/// 翻訳ログ機能の設定。 確定字幕を `%APPDATA%/Roaming/RealTimeTranslator/logs/translations/` に
/// TSV 形式で永続化する機能の挙動を制御する。
/// </summary>
public class TranslationLogSettings
{
    /// <summary>
    /// 翻訳ログの保持日数。 0 = 無制限 (自動削除しない、 Windows ごみ箱と同等の挙動)。
    /// UI は ComboBox 6 段階 (0/7/30/90/180/365 日)。 デフォルトは 0 (無制限) でユーザーが手動で消すまで残す。
    /// </summary>
    public int RetentionDays { get; set; } = 0;
}

public class UpdateSettings
{
    /// <summary>
    /// 自動更新で許可する R2 配信元の正規 URL（悪意ある誘導を防ぐためハードコード固定）。
    /// Velopack の <see cref="Velopack.Sources.SimpleWebSource"/> がこの base URL + <c>/releases.{channel}.json</c>
    /// を取得しに行く。末尾の "/" は付けない（Velopack 内部で正規化される）。
    /// 旧 GitHub Releases (github.com/1llum1n4t1s/RealTimeTranslator) からは Cloudflare R2 へ移行。
    /// 配信元は中立ドメイン rtt.nephilim.jp を使う (1llum1n4t1.com 系はクラウド/企業 egress の
    /// SNI フィルタで false positive を起こすため中立ドメインに統一。Lhamiel の lhamiel.nephilim.jp と同方針)。
    /// 超旧 GithubSource クライアント救済のため、GitHub Releases に R2 対応版を「踏み台」として 1 つ publish する
    /// （GithubSource は最新版を選ぶので、それ経由で更新 → 再起動後に R2 を見るようになる）。
    /// </summary>
    internal const string CanonicalUpdateBaseUrl = "https://rtt.nephilim.jp";

    // デフォルトで有効: UpdateBaseUrl は [JsonIgnore] ハードコード固定で外部誘導を防止済み。
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 自動更新の配信元 base URL（Cloudflare R2 でホスティング）。
    /// セキュリティ上の理由でハードコード固定（<see cref="CanonicalUpdateBaseUrl"/>）。
    /// settings.json から書き換えても反映されない（悪意ある第三者ホストへの誘導を防ぐため）。
    /// </summary>
    [JsonIgnore]
    public string UpdateBaseUrl => CanonicalUpdateBaseUrl;

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

    // ⭐ VAD Silence 中の「無音 PCM 継続送信」最大時間 (v1.0.27 設計)。
    //
    // 背景 (v1.0.26 ログから事実確証):
    //   OpenAI Realtime Translate API は continuous streaming model 前提で、 入力音声が来ない区間は
    //   「次の音声が続きか別発話か」を判断できず、 **続きの音声が来るまで delta 出力を保留する**。
    //   ARC Raiders で 2 分間の delta gap → 次の音声入力で「続き」を吐き出す挙動を観測
    //   (2026-05-24 「繰...」「り返す、10分」分断事件)。
    //   ゆろさん仮説: VAD OFF で BGM が押し出してるから途切れない = 無音 PCM 送信で同じ効果が得られる。
    //
    // v1.0.26 戦略 (VAD Silence で client 強制確定 + commit 送信) は失敗:
    //   - 戦略 A: 分断を引き起こした (server 保留中の続き delta が別 SegmentId で出てくる)
    //   - 戦略 B: `session.input_audio_buffer.commit` が API に拒否された (BadRequest 大量発生)
    //
    // v1.0.27 戦略:
    //   - VAD Hangover → Silence 遷移後、 この時間 (ミリ秒) 内は **無音 PCM (ゼロ埋め PCM16)** を継続送信
    //   - server に「入力継続中」をアピール → 保留してた delta を吐かせる
    //   - 時間超過したら送信停止 (token 節約、 Silero VAD が次の発話を検知するまで完全停止)
    //   - Silence → InSpeech 再遷移でカウントリセット
    // 値が 0 以下なら機能を無効化する (= VAD Silence 中は完全に送信停止、 v1.0.25 以前と同じ)。
    public int SilencePaddingMs { get; set; } = 5000;
}

// GameProfile / GameProfiles は旧 Whisper+LLM ローカル翻訳時代の設定。
// OpenAI Realtime API 移行（コミット 5de5297）で適用処理が消えたため削除。
// 必要なら将来 OpenAI Realtime API の `instructions` フィールドにマップして復活させる。
