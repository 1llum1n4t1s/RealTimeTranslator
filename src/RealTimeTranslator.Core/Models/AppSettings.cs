using System.Text.Json;
using System.Text.Json.Serialization;
using RealTimeTranslator.Core.Services;

namespace RealTimeTranslator.Core.Models;

/// <summary>
/// アプリケーション設定
/// </summary>
public class AppSettings
{
    /// <summary>
    /// ASR設定
    /// </summary>
    public ASRSettings ASR { get; set; } = new();

    /// <summary>
    /// 翻訳設定
    /// </summary>
    public TranslationSettings Translation { get; set; } = new();

    /// <summary>
    /// オーバーレイ設定
    /// </summary>
    public OverlaySettings Overlay { get; set; } = new();

    /// <summary>
    /// 音声キャプチャ設定
    /// </summary>
    public AudioCaptureSettings AudioCapture { get; set; } = new();

    /// <summary>
    /// ゲーム別プロファイル
    /// </summary>
    public List<GameProfile> GameProfiles { get; set; } = new();

    /// <summary>
    /// 前回選択したプロセス名
    /// </summary>
    public string LastSelectedProcessName { get; set; } = string.Empty;

    /// <summary>
    /// 更新設定
    /// </summary>
    public UpdateSettings Update { get; set; } = new();

    /// <summary>
    /// 設定をJSONファイルから読み込み
    /// </summary>
    public static AppSettings Load(string filePath)
    {
        if (!File.Exists(filePath))
            return new AppSettings();

        try
        {
            var json = File.ReadAllText(filePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json);

            if (settings == null)
            {
                LoggerService.LogWarning($"Failed to deserialize settings from {filePath}, using defaults");
                return new AppSettings();
            }

            // 設定値の検証
            ValidateSettings(settings);
            return settings;
        }
        catch (JsonException ex)
        {
            LoggerService.LogWarning($"Invalid JSON in settings file {filePath}: {ex.Message}");
            return new AppSettings();
        }
        catch (Exception ex)
        {
            LoggerService.LogWarning($"Error loading settings from {filePath}: {ex.Message}");
            return new AppSettings();
        }
    }

    /// <summary>
    /// 設定値の検証
    /// </summary>
    private static void ValidateSettings(AppSettings settings)
    {
        // サンプルレートの検証
        if (settings.AudioCapture.SampleRate <= 0)
        {
            LoggerService.LogWarning($"Invalid SampleRate {settings.AudioCapture.SampleRate}, using default 16000");
            settings.AudioCapture.SampleRate = 16000;
        }

        // VAD感度の検証
        if (settings.AudioCapture.VADSensitivity < 0 || settings.AudioCapture.VADSensitivity > 1)
        {
            LoggerService.LogWarning($"Invalid VADSensitivity {settings.AudioCapture.VADSensitivity}, using default 0.5");
            settings.AudioCapture.VADSensitivity = 0.5f;
        }

        // 最小発話長の検証
        if (settings.AudioCapture.MinSpeechDuration < 0)
        {
            LoggerService.LogWarning($"Invalid MinSpeechDuration {settings.AudioCapture.MinSpeechDuration}, using default 0.3");
            settings.AudioCapture.MinSpeechDuration = 0.3f;
        }

        // 最大発話長の検証
        if (settings.AudioCapture.MaxSpeechDuration <= settings.AudioCapture.MinSpeechDuration)
        {
            LoggerService.LogWarning($"Invalid MaxSpeechDuration {settings.AudioCapture.MaxSpeechDuration}, using default 6.0");
            settings.AudioCapture.MaxSpeechDuration = 6.0f;
        }

        // フォントサイズの検証
        if (settings.Overlay.FontSize <= 0)
        {
            LoggerService.LogWarning($"Invalid FontSize {settings.Overlay.FontSize}, using default 24");
            settings.Overlay.FontSize = 24;
        }

        // 表示時間の検証
        if (settings.Overlay.DisplayDuration <= 0)
        {
            LoggerService.LogWarning($"Invalid DisplayDuration {settings.Overlay.DisplayDuration}, using default 5.0");
            settings.Overlay.DisplayDuration = 5.0;
        }

        // キャッシュサイズの検証
        if (settings.Translation.CacheSize <= 0)
        {
            LoggerService.LogWarning($"Invalid CacheSize {settings.Translation.CacheSize}, using default 1000");
            settings.Translation.CacheSize = 1000;
        }

        // Beam Sizeの検証
        if (settings.ASR.BeamSize <= 0)
        {
            LoggerService.LogWarning($"Invalid BeamSize {settings.ASR.BeamSize}, using default 5");
            settings.ASR.BeamSize = 5;
        }

        // GPU Device IDの検証
        if (settings.ASR.GPU.DeviceId < 0)
        {
            LoggerService.LogWarning($"Invalid GPU DeviceId {settings.ASR.GPU.DeviceId}, using default 0");
            settings.ASR.GPU.DeviceId = 0;
        }
    }

    /// <summary>
    /// 設定をJSONファイルに保存
    /// </summary>
    public void Save(string filePath)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(this, options);
        File.WriteAllText(filePath, json);
    }
}

/// <summary>
/// 更新設定
/// </summary>
public class UpdateSettings
{
    /// <summary>
    /// 自動更新を有効化
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// 更新フィードURL
    /// </summary>
    public string FeedUrl { get; set; } = string.Empty;

    /// <summary>
    /// 更新を検出したら自動で適用して再起動する
    /// </summary>
    public bool AutoApply { get; set; } = true;
}

/// <summary>
/// ASR設定
/// </summary>
public class ASRSettings
{
    /// <summary>
    /// 低遅延ASRのモデルパス（small/medium）
    /// </summary>
    public string FastModelPath { get; set; } = "models/ggml-small.bin";

    /// <summary>
    /// 高精度ASRのモデルパス（large系）
    /// </summary>
    public string AccurateModelPath { get; set; } = "models/ggml-large-v3.bin";

    /// <summary>
    /// 言語設定（固定: en）
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LanguageType Language { get; set; } = LanguageType.en;

    /// <summary>
    /// Beam Search有効化（高精度ASRのみ）
    /// </summary>
    public bool UseBeamSearch { get; set; } = true;

    /// <summary>
    /// Beam Size
    /// </summary>
    public int BeamSize { get; set; } = 5;

    /// <summary>
    /// GPU使用設定
    /// </summary>
    public GPUSettings GPU { get; set; } = new();
}

/// <summary>
/// GPU設定
/// </summary>
public class GPUSettings
{
    /// <summary>
    /// GPU使用を有効化
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 使用するGPUデバイスID
    /// </summary>
    public int DeviceId { get; set; } = 0;

    /// <summary>
    /// GPUタイプ（自動検出）
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public GPUType Type { get; set; } = GPUType.Auto;
}

/// <summary>
/// GPUタイプ
/// </summary>
public enum GPUType
{
    Auto,
    NVIDIA_CUDA,
    AMD_Vulkan,
    CPU
}

/// <summary>
/// 言語タイプ
/// </summary>
public enum LanguageType
{
    en,
    ja,
    zh
}

/// <summary>
/// 翻訳設定
/// </summary>
public class TranslationSettings
{
    /// <summary>
    /// 翻訳モデルパス
    /// </summary>
    public string ModelPath { get; set; } = "models/translate-en-ja";

    /// <summary>
    /// ソース言語
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LanguageType SourceLanguage { get; set; } = LanguageType.en;

    /// <summary>
    /// ターゲット言語
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LanguageType TargetLanguage { get; set; } = LanguageType.ja;

    /// <summary>
    /// キャッシュサイズ（エントリ数）
    /// </summary>
    public int CacheSize { get; set; } = 1000;
}

/// <summary>
/// オーバーレイ設定
/// </summary>
public class OverlaySettings
{
    /// <summary>
    /// フォントファミリー
    /// </summary>
    public string FontFamily { get; set; } = "Yu Gothic UI";

    /// <summary>
    /// フォントサイズ
    /// </summary>
    public double FontSize { get; set; } = 24;

    /// <summary>
    /// 仮字幕の文字色（ARGB）
    /// </summary>
    public string PartialTextColor { get; set; } = "#80FFFFFF";

    /// <summary>
    /// 確定字幕の文字色（ARGB）
    /// </summary>
    public string FinalTextColor { get; set; } = "#FFFFFFFF";

    /// <summary>
    /// 背景色（ARGB）
    /// </summary>
    public string BackgroundColor { get; set; } = "#80000000";

    /// <summary>
    /// 字幕表示時間（秒）
    /// </summary>
    public double DisplayDuration { get; set; } = 5.0;

    /// <summary>
    /// フェードアウト時間（秒）
    /// </summary>
    public double FadeOutDuration { get; set; } = 0.5;

    /// <summary>
    /// 表示位置（画面下からの距離、%）
    /// </summary>
    public double BottomMarginPercent { get; set; } = 10;

    /// <summary>
    /// 最大表示行数
    /// </summary>
    public int MaxLines { get; set; } = 3;
}

/// <summary>
/// 音声キャプチャ設定
/// </summary>
public class AudioCaptureSettings
{
    /// <summary>
    /// サンプルレート（Hz）
    /// </summary>
    public int SampleRate { get; set; } = 16000;

    /// <summary>
    /// VAD感度（0.0〜1.0）
    /// </summary>
    public float VADSensitivity { get; set; } = 0.6f;

    /// <summary>
    /// 最小発話長（秒）
    /// </summary>
    public float MinSpeechDuration { get; set; } = 0.3f;

    /// <summary>
    /// 最大発話長（秒）
    /// </summary>
    public float MaxSpeechDuration { get; set; } = 6.0f;

    /// <summary>
    /// 無音判定閾値（秒）
    /// </summary>
    public float SilenceThreshold { get; set; } = 0.5f;
}

/// <summary>
/// ゲーム別プロファイル
/// </summary>
public class GameProfile
{
    /// <summary>
    /// プロファイル名
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// 対象プロセス名（拡張子なし）
    /// </summary>
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>
    /// ホットワードリスト（キャラクター名、地名等）
    /// </summary>
    public List<string> Hotwords { get; set; } = new();

    /// <summary>
    /// ASR誤変換補正辞書
    /// </summary>
    public Dictionary<string, string> ASRCorrectionDictionary { get; set; } = new();

    /// <summary>
    /// 翻訳前用語辞書
    /// </summary>
    public Dictionary<string, string> PreTranslationDictionary { get; set; } = new();

    /// <summary>
    /// 翻訳後置換辞書
    /// </summary>
    public Dictionary<string, string> PostTranslationDictionary { get; set; } = new();

    /// <summary>
    /// 初期プロンプト
    /// </summary>
    public string InitialPrompt { get; set; } = string.Empty;
}
