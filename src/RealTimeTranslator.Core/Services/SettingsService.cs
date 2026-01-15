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
/// 設定の保存を担当するサービスの実装
/// </summary>
public class SettingsService : ISettingsService
{
    /// <summary>
    /// 設定ファイルのパス
    /// </summary>
    private readonly string _settingsPath;

    /// <summary>
    /// SettingsService のコンストラクタ
    /// </summary>
    public SettingsService()
    {
        // 実行ファイルと同じ場所の settings.json をターゲットにする
        _settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
    }

    /// <summary>
    /// 設定をファイルに保存するメソッド
    /// </summary>
    public async Task SaveAsync(AppSettings settings)
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(settings, options);
            await File.WriteAllTextAsync(_settingsPath, json);
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"Failed to save settings: {ex.Message}");
            throw;
        }
    }
}
