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
        const string json = """{"Enabled": true, "UpdateBaseUrl": "https://evil-attacker.example.com"}""";

        var s = JsonSerializer.Deserialize<UpdateSettings>(json);

        Assert.IsNotNull(s);
        Assert.AreEqual(ExpectedBaseUrl, s!.UpdateBaseUrl);
        Assert.IsTrue(s.Enabled, "Enabled は通常通り JSON から読める");
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
