using System.Text.Json;
using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.Core.Services;

/// <summary>
/// 設定の保存を担当するサービスのインターフェース
/// </summary>
public interface ISettingsService
{
    /// <summary>
    /// 設定をファイルに保存
    /// </summary>
    Task SaveAsync(AppSettings settings);
}

/// <summary>
/// 設定の保存を担当するサービスの実装。
/// 保存先は %APPDATA%\Roaming\RealTimeTranslator\settings.json。
/// </summary>
public class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions s_writeOptions = new() { WriteIndented = true };

    /// <summary>
    /// 設定ファイルを置くディレクトリ。
    /// 旧来は AppDomain.CurrentDomain.BaseDirectory (exe 隣接) で、その後 %LocalAppData%/RealTimeTranslator
    /// に移行したが、後者は <b>Velopack のインストールルートと衝突</b>するため、Velopack の更新時に
    /// 設定が消失するケースがあった。Velopack の管理外である Roaming AppData に最終的に移行する。
    /// </summary>
    public static string SettingsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RealTimeTranslator");

    /// <summary>
    /// settings.json の絶対パス。
    /// </summary>
    public static string SettingsFilePath { get; } = Path.Combine(SettingsDirectory, "settings.json");

    /// <summary>
    /// 旧 LocalAppData 配下のパス（Velopack インストールルートと衝突するため廃止対象）。
    /// </summary>
    private static readonly string LegacyLocalAppDataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RealTimeTranslator");
    private static readonly string LegacyLocalAppDataSettingsPath = Path.Combine(LegacyLocalAppDataDirectory, "settings.json");

    /// <summary>
    /// 起動時に呼ばれ、旧パス（exe 隣接 / %LocalAppData% / publish dir）から Roaming AppData に
    /// settings.json をマイグレートする。
    /// 既に新パスにファイルがあれば何もしない。
    /// </summary>
    public static void MigrateLegacySettingsIfNeeded()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);

            if (File.Exists(SettingsFilePath))
                return;

            // 1) 旧 %LocalAppData%/RealTimeTranslator/settings.json から移行（v1.0.3〜1.0.6 の保存先）
            if (File.Exists(LegacyLocalAppDataSettingsPath))
            {
                File.Move(LegacyLocalAppDataSettingsPath, SettingsFilePath);
                LoggerService.LogInfo($"settings.json を {LegacyLocalAppDataSettingsPath} から {SettingsFilePath} に移行しました");
                return;
            }

            // 2) exe 隣接の旧 settings.json (v1.0.2 以前) から移行
            var legacyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
            if (File.Exists(legacyPath))
            {
                File.Move(legacyPath, SettingsFilePath);
                LoggerService.LogInfo($"settings.json を {legacyPath} から {SettingsFilePath} に移行しました");
                return;
            }

            // 3) settings.default.json から初期化
            var defaultPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.default.json");
            if (File.Exists(defaultPath))
            {
                File.Copy(defaultPath, SettingsFilePath);
                LoggerService.LogInfo($"settings.default.json を {SettingsFilePath} にコピーしました");
            }
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"settings.json の移行に失敗しました: {ex}");
        }
    }

    /// <summary>
    /// 設定ファイルのパス
    /// </summary>
    private readonly string _settingsPath;

    /// <summary>
    /// SettingsService のコンストラクタ
    /// </summary>
    public SettingsService()
    {
        _settingsPath = SettingsFilePath;
    }

    /// <summary>
    /// 設定をファイルに保存するメソッド
    /// </summary>
    /// <remarks>
    /// 一時ファイルに書き出してから File.Replace / Move でアトミックに置き換えることで、
    /// プロセス中断・電源断時に settings.json が部分書込のまま壊れることを防ぐ。
    /// 機微フィールド（OpenAIRealtime.ApiKey）は DPAPI で暗号化してから JSON 化する。
    /// </remarks>
    public async Task SaveAsync(AppSettings settings)
    {
        var tempPath = _settingsPath + ".tmp";
        try
        {
            // 移行未済の環境でも自動的にディレクトリを用意する
            Directory.CreateDirectory(SettingsDirectory);

            // 元の settings オブジェクトは平文のまま呼び出し側に残し、書き出し用クローンだけ暗号化する。
            var serializable = CloneWithEncryptedSecrets(settings);
            var json = JsonSerializer.Serialize(serializable, s_writeOptions);
            await File.WriteAllTextAsync(tempPath, json).ConfigureAwait(false);

            if (File.Exists(_settingsPath))
            {
                // File.Replace は原子的置換（バックアップなし）
                File.Replace(tempPath, _settingsPath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, _settingsPath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"Failed to save settings: {ex.Message}");
            // 失敗時は temp が残らないように掃除
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>
    /// 機微フィールド（OpenAIRealtime.ApiKey）を DPAPI 暗号化したシャロークローンを返す。
    /// 呼び出し側の元オブジェクトは平文のまま保持される。
    /// </summary>
    private static AppSettings CloneWithEncryptedSecrets(AppSettings source) => new()
    {
        Overlay = source.Overlay,
        AudioCapture = source.AudioCapture,
        OpenAIRealtime = new OpenAIRealtimeSettings
        {
            ApiKey = DpapiHelper.Encrypt(source.OpenAIRealtime.ApiKey),
            OutputLanguage = source.OpenAIRealtime.OutputLanguage,
            Model = source.OpenAIRealtime.Model,
            Endpoint = source.OpenAIRealtime.Endpoint,
            ReconnectDelayMs = source.OpenAIRealtime.ReconnectDelayMs,
            MaxReconnectAttempts = source.OpenAIRealtime.MaxReconnectAttempts,
        },
        LastSelectedProcessName = source.LastSelectedProcessName,
        LastSelectedProcessId = source.LastSelectedProcessId,
        Update = source.Update,
    };

    /// <summary>
    /// 読み込んだ AppSettings の機微フィールドを in-place で復号する。
    /// 起動時の DI シングルトン生成および <see cref="Microsoft.Extensions.Options.IOptionsMonitor{TOptions}.OnChange"/>
    /// ハンドラから呼んで、後段のコンシューマが平文を扱えるようにする。
    /// </summary>
    public static void DecryptApiKeyInPlace(AppSettings settings) => DpapiHelper.DecryptInPlace(settings);
}
