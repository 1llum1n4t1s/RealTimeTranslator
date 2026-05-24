using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;
using RealTimeTranslator.UI.ViewModels;

namespace RealTimeTranslator.Tests;

/// <summary>
/// SubtitleDisplayItem (OverlayViewModel 内の字幕表示エンティティ) の表示寿命ロジックを検証する。
///
/// v1.0.24 partial 連結方式の核心バグ修正:
///  - 旧実装は partial / final 問わず DisplayDuration (5秒) 経過で字幕が消えていた
///  - 2026-05-24 ゆろさん観察: 「あれは一生忘れません。」確定後、 partial「そ」が 5 秒で消えてしまう
///  - TranslationPipelineService が最大寿命 45 秒で partial 連結を維持しても、 UI が 5 秒で消すと意味がない
///  - 新実装: partial は永続表示 (次 Update or 確定まで)、 final のみ DisplayDuration カウントダウン
/// </summary>
[TestClass]
public class SubtitleDisplayItemTests
{
    private static OverlaySettings BuildSettings(double displayDurationSec = 5.0, double fadeOutSec = 0.5)
    {
        return new OverlaySettings
        {
            DisplayDuration = displayDurationSec,
            FadeOutDuration = fadeOutSec,
            PartialTextColor = "#FFFFFFFF",
            FinalTextColor = "#FFFFFFFF",
            FontFamily = "Yu Gothic UI",
            FontSize = 24,
        };
    }

    private static SubtitleItem BuildItem(string segmentId, string text, bool isFinal)
    {
        return new SubtitleItem
        {
            SegmentId = segmentId,
            OriginalText = string.Empty,
            TranslatedText = text,
            IsFinal = isFinal,
        };
    }

    /// <summary>
    /// partial (IsFinal=false) は DisplayDuration 経過後も画面に残る (永続表示)。
    /// これが v1.0.24 partial 連結方式の核心: server-side delta gap 中も partial が消えない。
    /// </summary>
    [TestMethod]
    public void Update_PartialSubtitle_PersistsBeyondDisplayDuration()
    {
        var settings = BuildSettings(displayDurationSec: 5.0);
        var item = BuildItem("seg-1", "そ", isFinal: false);
        var display = new SubtitleDisplayItem(item, settings);

        // 10 秒後 (DisplayDuration 5 秒を超過) でも ShouldRemove は false
        var farFuture = DateTime.Now.AddSeconds(10.0);
        Assert.IsFalse(display.ShouldRemove(farFuture),
            "partial 字幕は DisplayDuration を超えても画面に残るはず (永続表示)");

        // さらに極端な未来 (1 時間後) でも残る
        var veryFarFuture = DateTime.Now.AddHours(1.0);
        Assert.IsFalse(display.ShouldRemove(veryFarFuture),
            "partial 字幕は 1 時間後でも画面に残るはず (DateTime.MaxValue 相当)");
    }

    /// <summary>
    /// final (IsFinal=true) は DisplayDuration 経過後に ShouldRemove=true で削除対象になる。
    /// 従来通りの確定字幕の挙動。
    /// </summary>
    [TestMethod]
    public void Update_FinalSubtitle_RemovesAfterDisplayDuration()
    {
        var settings = BuildSettings(displayDurationSec: 1.0, fadeOutSec: 0.5);
        var item = BuildItem("seg-1", "あれは一生忘れません。", isFinal: true);
        var display = new SubtitleDisplayItem(item, settings);

        // DisplayDuration 中は残る
        var withinDuration = DateTime.Now.AddSeconds(0.5);
        Assert.IsFalse(display.ShouldRemove(withinDuration),
            "DisplayDuration 中は残るはず");

        // DisplayDuration 終了直後は fadeOut 中なので False (Opacity が下がる)
        var justAfterDuration = DateTime.Now.AddSeconds(1.1);
        Assert.IsFalse(display.ShouldRemove(justAfterDuration),
            "DisplayDuration 直後は fadeOut 中で残るはず");

        // fadeOut 完了後は削除対象
        var afterFadeOut = DateTime.Now.AddSeconds(2.0);
        Assert.IsTrue(display.ShouldRemove(afterFadeOut),
            "DisplayDuration + FadeOutDuration を超えたら削除されるはず");
    }

    /// <summary>
    /// partial → final 遷移時に DisplayDuration カウントダウン開始する。
    /// 句点で確定したり、 最大寿命で強制確定したりした字幕が、 適切に画面から消える経路を検証。
    /// </summary>
    [TestMethod]
    public void Update_PartialThenFinal_StartsDisplayDurationCountdown()
    {
        var settings = BuildSettings(displayDurationSec: 1.0, fadeOutSec: 0.5);
        var partial = BuildItem("seg-1", "あれは一生忘れません", isFinal: false);
        var display = new SubtitleDisplayItem(partial, settings);

        // partial 状態で 10 秒後でも残る
        Assert.IsFalse(display.ShouldRemove(DateTime.Now.AddSeconds(10.0)),
            "partial 状態では永続表示のはず");

        // final に遷移
        var final = BuildItem("seg-1", "あれは一生忘れません。", isFinal: true);
        display.Update(final, settings);

        // final 遷移後、 DisplayDuration + FadeOutDuration を超えると削除対象
        var afterFadeOut = DateTime.Now.AddSeconds(2.0);
        Assert.IsTrue(display.ShouldRemove(afterFadeOut),
            "final に遷移した字幕は DisplayDuration + FadeOutDuration 経過で削除されるはず");
    }

    /// <summary>
    /// partial の連続 Update でも常に永続表示が維持される。
    /// 「そ → そし → そして → そして、血液検査には...」のように成長する partial の挙動を検証。
    /// </summary>
    [TestMethod]
    public void Update_MultiplePartialUpdates_AlwaysPersists()
    {
        var settings = BuildSettings(displayDurationSec: 5.0);
        var display = new SubtitleDisplayItem(BuildItem("seg-1", "そ", isFinal: false), settings);

        // 複数回 Update (連結方式で成長していく partial)
        foreach (var text in new[] { "そし", "そして", "そして、血液", "そして、血液検査にはESR" })
        {
            display.Update(BuildItem("seg-1", text, isFinal: false), settings);
            Assert.IsFalse(display.ShouldRemove(DateTime.Now.AddSeconds(10.0)),
                $"partial 連続 Update 中 ('{text}') でも永続表示のはず");
        }
    }
}
