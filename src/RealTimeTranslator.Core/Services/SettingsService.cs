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

    /// <summary>
    /// AppSettings 内の機微フィールド (OpenAIRealtime.ApiKey) を DPAPI で in-place 復号する。
    /// 設定読み込み直後と IOptionsMonitor.OnChange 後に呼ぶことで後段の consumer は平文を扱える。
    /// rere レビュー B1-003: TranslationPipelineService が static SettingsService.DecryptApiKeyInPlace を
    /// 直叩きする Service Locator アンチパターンを解消するため interface 経由化。
    /// </summary>
    void DecryptApiKey(AppSettings settings);
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
    // rere I-5: SettingsViewModel の autosave (500ms debounce) と UpdateService の VersionIgnored
    // ハンドラ (fire-and-forget SaveAsync) が並走するシナリオで、 同じ tempPath への File.Replace race を
    // 防ぐためインスタンス内で直列化。 同プロセス内の複数 SaveAsync は順次実行される。
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public async Task SaveAsync(AppSettings settings)
    {
        await _saveLock.WaitAsync().ConfigureAwait(false);
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
        finally
        {
            _saveLock.Release();
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
            // rere v1.0.32 #C2-001: 旧版で SilencePaddingMs / MaxPartialChars がコピー対象に
            // 入っていなかったため、 SaveAsync 経由で settings.json に書き出されるたびに
            // 該当 2 値が default (5000 / 50) に静かにリセットされる致命バグだった。
            // ユーザーが settings.json を手動編集して MaxPartialChars=120 等にしても、
            // UI 設定変更時の autosave で消える経路。 ここに追加して保全する。
            SilencePaddingMs = source.OpenAIRealtime.SilencePaddingMs,
            MaxPartialChars = source.OpenAIRealtime.MaxPartialChars,
        },
        LastSelectedProcessName = source.LastSelectedProcessName,
        LastSelectedProcessId = source.LastSelectedProcessId,
        Update = source.Update,
        TranslationLog = source.TranslationLog,
        // ウィンドウサイズもコピー対象に含める (含めないと autosave のたびに 0 にリセットされる)。
        WindowWidth = source.WindowWidth,
        WindowHeight = source.WindowHeight,
    };

    /// <inheritdoc />
    public void DecryptApiKey(AppSettings settings) => DpapiHelper.DecryptInPlace(settings);
}
