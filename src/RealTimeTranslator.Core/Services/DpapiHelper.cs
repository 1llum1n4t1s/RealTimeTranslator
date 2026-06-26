using System.Security.Cryptography;
using System.Text;
using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.Core.Services;

/// <summary>
/// Windows DPAPI (Data Protection API) を使った文字列の暗号化・復号ヘルパー。
/// CurrentUser スコープ: 暗号化したユーザー / マシンでのみ復号可能。
/// </summary>
/// <remarks>
/// BYOK モデルの API キーを <c>settings.json</c> に平文保存しないための最低限の対策。
/// 形式: <c>"dpapi:" + Convert.ToBase64String(ProtectedData.Protect(UTF8 bytes, null, CurrentUser))</c>
/// 旧 settings.json（prefix なし平文）との互換性のため、<see cref="TryDecrypt"/> は prefix が無ければ
/// 入力をそのまま返す（平文として扱う）。
/// </remarks>
internal static class DpapiHelper
{
    private const string ProtectedPrefix = "dpapi:";

    /// <summary>
    /// 平文文字列を DPAPI で暗号化して <c>"dpapi:" + base64</c> 形式で返す。
    /// 空入力は空のまま返す（暗号化しない）。
    /// </summary>
    public static string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;
        // 既に暗号化済みなら二重暗号化しない（idempotent）
        if (plainText.StartsWith(ProtectedPrefix, StringComparison.Ordinal)) return plainText;

        try
        {
            var bytes = Encoding.UTF8.GetBytes(plainText);
            var protectedBytes = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return ProtectedPrefix + Convert.ToBase64String(protectedBytes);
        }
        catch (Exception ex)
        {
            LoggerService.LogException("DpapiHelper.Encrypt 失敗 — fail-closed で空文字列を返します (settings.json に平文 sk-... を書く事故を防ぐため)", ex);
            // ⚠️ 旧実装は失敗時に plainText を返していたが、 これは settings.json に生 sk-... を
            // 書き込む経路を作るため fail-closed に変更 (rere レビュー P1 #8)。
            // 呼び出し側 (SettingsService.CloneWithEncryptedSecrets) で空が保存され、
            // UI は IsApiKeyConfigured=false 経路で「API キー未設定」を表示する。
            return string.Empty;
        }
    }

    /// <summary>
    /// <c>"dpapi:"</c> プレフィックス付き base64 を復号する。
    /// プレフィックスが無ければ入力をそのまま返す（旧 settings.json 互換）。
    /// 復号失敗時は空文字列を返し（無効なキー扱い）、ログにエラーを記録する。
    /// </summary>
    public static string TryDecrypt(string maybeProtected)
    {
        if (string.IsNullOrEmpty(maybeProtected)) return string.Empty;
        if (!maybeProtected.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
        {
            // 旧形式: 平文として扱う。次回 SaveAsync で暗号化される。
            return maybeProtected;
        }

        try
        {
            var base64 = maybeProtected[ProtectedPrefix.Length..];
            var protectedBytes = Convert.FromBase64String(base64);
            var bytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"DpapiHelper.TryDecrypt 失敗（API キーを無効化）: {ex.GetType().Name}: {ex.Message}");
            return string.Empty;
        }
    }

    /// <summary>
    /// AppSettings 内の機微フィールド（OpenAIRealtime.ApiKey / Gemini.ApiKey）を in-place で復号する。
    /// 設定読み込み直後と <see cref="Microsoft.Extensions.Options.IOptionsMonitor{TOptions}.OnChange"/> 後に
    /// 呼ぶことで、後段のコンシューマは平文を扱える。
    /// </summary>
    public static void DecryptInPlace(AppSettings settings)
    {
        if (settings is null) return;
        settings.OpenAIRealtime.ApiKey = TryDecrypt(settings.OpenAIRealtime.ApiKey);
        settings.Gemini.ApiKey = TryDecrypt(settings.Gemini.ApiKey);
        settings.Soniox.ApiKey = TryDecrypt(settings.Soniox.ApiKey);
        settings.Speechmatics.ApiKey = TryDecrypt(settings.Speechmatics.ApiKey);
        settings.Azure.ApiKey = TryDecrypt(settings.Azure.ApiKey);
    }
}
