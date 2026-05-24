using System.Text.Json;
using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.Tests;

/// <summary>
/// UpdateSettings.UpdateBaseUrl は Cloudflare R2 配信元へハードコード固定 ([JsonIgnore] + getter-only)。
/// settings.json 経由で配信元 URL を書き換えられない (悪意ある第三者ホストへの誘導の防御) ことを検証する。
/// </summary>
[TestClass]
public class UpdateSettingsTests
{
    private const string ExpectedBaseUrl = "https://rtt.nephilim.jp";

    [TestMethod]
    public void UpdateBaseUrl_IsHardcodedR2Url()
    {
        var settings = new UpdateSettings();
        Assert.AreEqual(ExpectedBaseUrl, settings.UpdateBaseUrl);
    }

    [TestMethod]
    public void UpdateBaseUrl_IsGetterOnly_NoSetter()
    {
        // setter が物理的に存在しないことを保証 (JSON / リフレクションでの書き換えを構造的に封じる)。
        var prop = typeof(UpdateSettings).GetProperty(nameof(UpdateSettings.UpdateBaseUrl));
        Assert.IsNotNull(prop);
        Assert.IsFalse(prop!.CanWrite, "UpdateBaseUrl は getter-only であるべき");
    }

    [TestMethod]
    public void UpdateBaseUrl_JsonInjection_IgnoredByJsonIgnore()
    {
        // settings.json に悪意ある配信元 URL を仕込んでも [JsonIgnore] で無視され、ハードコード値が維持される。
        // 2026-05-25 で Enabled プロパティ廃止のため、 unknown property として黙殺されることも検証 (互換性)。
        const string json = """{"Enabled": false, "UpdateBaseUrl": "https://evil-attacker.example.com", "IgnoredTagName": "v1.0.13"}""";

        var s = JsonSerializer.Deserialize<UpdateSettings>(json);

        Assert.IsNotNull(s);
        Assert.AreEqual(ExpectedBaseUrl, s!.UpdateBaseUrl);
        Assert.AreEqual("v1.0.13", s.IgnoredTagName, "IgnoredTagName は通常通り JSON から読める");
        // Enabled プロパティは存在しない (System.Text.Json は unknown property を黙殺) → 旧環境 settings.json も読み込み可
    }

    [TestMethod]
    public void Enabled_PropertyRemoved_NoCompileSurface()
    {
        // Enabled プロパティが完全削除されたことをリフレクションで検証 (2026-05-25)。
        // 旧コードへの参照が残っていればコンパイル時にエラーになるが、 念のため定数的なテストとして残す。
        var prop = typeof(UpdateSettings).GetProperty("Enabled");
        Assert.IsNull(prop, "UpdateSettings.Enabled は廃止されたため存在してはならない");
    }

    [TestMethod]
    public void UpdateBaseUrl_NotSerializedToJson()
    {
        // 永続化時に UpdateBaseUrl が settings.json に書き出されない (派生値なので保存不要 + 混乱防止)。
        var settings = new UpdateSettings();
        var json = JsonSerializer.Serialize(settings);
        Assert.IsFalse(json.Contains("UpdateBaseUrl"), "UpdateBaseUrl は [JsonIgnore] でシリアライズされない");
    }
}
