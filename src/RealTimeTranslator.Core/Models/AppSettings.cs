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

    // メインウィンドウのサイズ (v1.0.41 新規)。 ユーザーがリサイズした値を保存し、 次回起動時に復元する。
    // 0 = 未保存 (初回起動 or 旧 settings.json) → MainWindow.axaml の既定 (750x800) を使う。
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
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

    // 自動更新は常時有効 (2026-05-25 で Enabled 切替廃止)。
    // 過去 settings.json に残った "Enabled": false は System.Text.Json の unknown property として無視されるため、
    // 旧環境からマイグレートしても自動的に有効化される (別 PC で誤って無効化されていた事例の構造的解消)。
    // ユーザーが「このバージョンを無視」した場合だけ起動時自動チェックがスキップされる (IgnoredTagName 経由)。

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
    // 字幕オーバーレイ窓を画面に表示するか (v1.0.41 新規)。 false にすると画面に重ねる字幕は出ないが、
    // 翻訳処理・翻訳ログ記録は継続する (「翻訳ログ」タブで履歴を読む運用向け。 字幕を画面に出したくない人用)。
    // default true = 従来どおり表示。 旧 settings.json (このキーなし) は System.Text.Json が default true を
    // 採用するので後方互換 (明示マイグレーション不要)。
    public bool ShowSubtitleOverlay { get; set; } = true;

    // 既定フォント: 同梱の IBM Plex Sans JP (avares 解決は OverlayViewModel.EmbeddedFontMap)。
    // 新規 settings.json 生成時 / 旧設定にリスト外フォントが入っている場合 (SettingsViewModel.SanitizeSettings)
    // の両方でここがフォールバック値になる。
    public string FontFamily { get; set; } = "IBM Plex Sans JP";
    public double FontSize { get; set; } = 32;
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
    public string BackgroundColor { get; set; } = "#BF000000";
    /// <summary>背景色の RGB のみ (#RRGGBB)。 透明度は BackgroundOpacityPercent で別管理。</summary>
    public string BackgroundColorBase { get; set; } = "#000000";
    /// <summary>背景の不透明度 (0-100%)。 0/25/50/75/100 の 5 段階を想定 (0% = 完全透明)。 default=75% (0xBF / 255 = 0.749…)。</summary>
    public int BackgroundOpacityPercent { get; set; } = 75;
    public double DisplayDuration { get; set; } = 5.0;
    public double FadeOutDuration { get; set; } = 0.5;
    public double BottomMarginPercent { get; set; } = 10;
    public int MaxLines { get; set; } = 3;

    // ────────── 字幕位置 微調整オフセット (v1.0.41 新規) ──────────
    // 「表示設定 → 翻訳字幕位置調整」の編集モードでサンプル字幕をドラッグした結果を保存する。
    // 基準位置 (BottomMarginPercent による下部中央) からの px オフセット。
    //   SubtitleOffsetX: 正 = 右へ / 負 = 左へ
    //   SubtitleOffsetY: 正 = 下へ / 負 = 上へ
    // 0,0 で従来どおり「下部中央 (BottomMarginPercent 基準)」に表示される。
    // 「位置をリセット」で 0,0 に戻す。
    public double SubtitleOffsetX { get; set; }
    public double SubtitleOffsetY { get; set; }
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

    /// <summary>
    /// speech probability のしきい値 (0.0-1.0, default: 0.4)。
    /// v1.0.30 で 0.5 → 0.3 にシフトしたら、 BGM/SE の継続送信で OpenAI server VAD が
    /// 発話境界を引けなくなり「字幕が句点なしで繋がる」回帰が発生 (実機ログ 23:40 セッションで
    /// 111 partial に対し完結文 emit=1 のみ、 D-7 fallback で 5 文連結を確認)。
    /// v1.0.31 で **0.4** に戻り気味調整。 全プリセットも +0.1 連動シフト
    /// (Balanced 0.4 / PrioritizeEdges 0.3 / AggressiveSavings 0.5)。
    /// 遠距離小音量の声拾いは入力プリプロセス DSP (<see cref="AudioPreprocessingSettings"/>) に任せる方針。
    /// </summary>
    public float VadThreshold { get; set; } = 0.4f;

    /// <summary>
    /// 発話冒頭の取りこぼし防止用にリングバッファに保持する直近音声の長さ (ms)。
    /// Balanced プリセットの推奨値は **800ms** (v1.0.31 で 600 → 800 に拡張)。
    /// 頭の子音 + 立ち上がりを確実に拾うため、 既定をやや厚めに取る方針。
    /// </summary>
    public int VadPreRollMs { get; set; } = 800;

    /// <summary>
    /// 発話末尾の切れ防止用に speech 終了判定後も送信を継続する長さ (ms)。
    /// Balanced プリセットの推奨値は **600ms** (v1.0.31 で 400 → 600 に拡張)。
    /// 語尾の無声子音 / 息継ぎ込みの「、」を取りこぼさないため、 既定をやや厚めに取る方針。
    /// </summary>
    public int VadHangoverMs { get; set; } = 600;

    // ────────── 自動 Pause 保険 ──────────

    /// <summary>
    /// 連続 N 秒間 speech 未検出ならキャプチャを自動停止する (0 = 無効, default: 0)。
    /// 「席を離れた間に token 垂れ流し」事故防止用。 VAD 有効時のみ機能する。
    /// </summary>
    public int AutoPauseOnSilenceSec { get; set; } = 0;

    // ────────── 入力プリプロセス DSP (v1.0.30 新規) ──────────

    /// <summary>
    /// WASAPI capture 直後・リサンプル前に挟まる 2 段プリプロセス DSP の設定 (InputGain → AntiClip)。
    /// VAD パス / API 送信パス両方に同じ前処理が乗る。 すべてのフラグが false かつ
    /// InputGainDb=0 なら完全 bypass で現状動作 (v1.0.29 以前) と一致。
    /// </summary>
    public AudioPreprocessingSettings Preprocessing { get; set; } = new();

    // ────────── デバッグ録音 ──────────

    /// <summary>
    /// OpenAI に送信される PCM16 (24kHz / Mono) を %APPDATA%/RealTimeTranslator/debug/ に
    /// WAV ファイルとして保存する (default: false)。 VAD ゲート通過後・サイレンス padding 含めて
    /// 実送信と完全一致するバイト列を記録する。 セッションごとに 1 ファイル
    /// (SentAudio_yyyyMMdd_HHmmss_{sessionId}.wav)。 token / 容量を消費するので恒常 ON は推奨しない。
    /// </summary>
    public bool DebugRecordSentAudio { get; set; } = false;
}

/// <summary>
/// 入力プリプロセス DSP の設定 (v1.0.30 新規、 v1.0.32 で LoudnessNormalizer 削除、 v1.0.36 で NightModeCompressor 削除)。
///
/// 信号フロー (有効化されたものだけ実効):
/// <code>
/// WASAPI 48kHz mono float32
///   → [InputGainStage] → [AntiClipLimiter]
///   → 既存の 48k→16k リサンプル (VAD 判定 + 24k リサンプル → OpenAI 送信)
/// </code>
///
/// パラメータ値は WebRestrictionRemoval (Chrome 拡張音量ブースター) で動画運用実証済みのものを移植。
/// 詳細な根拠は各 DSP クラスの XML doc を参照。
///
/// 削除履歴:
/// - v1.0.32: LoudnessNormalizer を削除。 NightModeCompressor (DRC) と機能重複のため。
/// - v1.0.36: NightModeCompressor を削除。 ON 時に server VAD が句点を返さなくなる経路を誘発しやすく、
///   多層防御パラメータ相互依存 (DSP → VAD threshold → D-7 fallback → 類似抑制) を増やすデメリットが大きかった。
///   遠距離小音量の声拾いは InputGainStage で底上げする方針に統一。
/// </summary>
public class AudioPreprocessingSettings
{

    /// <summary>
    /// ユーザー手動の入力ゲイン (dB)。 範囲 -24〜+24 (UI 側で制約)、 default 0 (= no-op)。
    /// 0 dB ピッタリ (差 ±0.01dB 以内) のときは <see cref="Services.Audio.InputGainStage"/> が
    /// 完全 bypass する (CPU オーバーヘッドゼロ)。
    /// </summary>
    public float InputGainDb { get; set; } = 0f;
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
    //
    // 変更履歴:
    //   v1.0.33: 5000 → 8000 に延長 (ゆろさん要望「翻訳が遅れているときに 5 秒だと足りない感じ」)。
    //   v1.0.36: 8000 → 5000 に戻し (ゆろさん判断、 token 消費を抑えて短い沈黙でカット)。
    // 1 silence 区間 = N 秒以上の沈黙場面では padding 終了後に Silero VAD が次発話を検知するまで完全停止する。
    public int SilencePaddingMs { get; set; } = 5000;

    // ⭐ D-7 fallback: 句点なし partial の最大累積文字数 (v1.0.28 復活)。
    //
    // 背景 (2026-05-24 v1.0.27 実機ログから事実確証):
    //   OpenAI Realtime Translate API は句読点 (`。！？.!?`) を必ず入れるとは限らず、
    //   ARC Raiders のセリフ「また良いその場所について実際に気にかけてくれるとは...」が
    //   1 分 25 秒・127 文字に渡って **句点ゼロで partial だけ伸び続け、 完結文 emit=0** の状態を観測。
    //   v1.0.24 で導入された D-7 fallback (100 文字超で「、」強制分割) を v1.0.27 棚卸しで削除した
    //   ことが裏目に出た。 「無音 PCM 送信で server が句点入れる」前提は破綻していた。
    //
    // 動作:
    //   _accumulatedText.Length >= MaxPartialChars に到達したら、 OnTranscriptDelta 内で:
    //     1. 末尾 30 文字以内の「、」「,」を探して切る (自然な節目優先)
    //     2. なければ末尾 30 文字以内の半角/全角空白で切る
    //     3. なければ閾値位置で強制切断 (最終手段)
    //   切った部分を完結文として emit + 新 SegmentId 発行。
    //
    // 値が 0 以下なら機能を無効化する (= 句点が来るまで永遠に partial が伸びる、 v1.0.27 と同じ挙動)。
    // v1.0.31 で 80 → **50** に短縮。 v1.0.30 の VAD threshold 0.3 で BGM 連続送信時に
    // 「複数文が句点なしで繋がる」現象 (実機 23:40 セッションで 5 文連結を確認) が顕在化した対策。
    // 50 文字なら字幕として 2 行に収まる読みやすい長さで、 連結バグの可視被害も最小化する。
    public int MaxPartialChars { get; set; } = 50;
}

// GameProfile / GameProfiles は旧 Whisper+LLM ローカル翻訳時代の設定。
// OpenAI Realtime API 移行（コミット 5de5297）で適用処理が消えたため削除。
// 必要なら将来 OpenAI Realtime API の `instructions` フィールドにマップして復活させる。
