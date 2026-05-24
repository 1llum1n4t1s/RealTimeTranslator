using RealTimeTranslator.Core.Models;
using RealTimeTranslator.Core.Services;
using RealTimeTranslator.UI.ViewModels;

namespace RealTimeTranslator.Tests;

/// <summary>
/// SubtitleDisplayItem (OverlayViewModel 内の字幕表示エンティティ) の表示寿命ロジックを検証する。
///
/// 現行 (v1.0.27 以降): partial / final 問わず DisplayDuration (5 秒) 経過で字幕が消える。
/// 経緯 (歴史):
///  - 〜v1.0.23: partial / final 問わず DisplayDuration 経過で消える (現行と同じ挙動)
///  - v1.0.24-26: partial 連結方式の核心バグ修正で「partial は永続表示」に変更
///    (確定字幕後の partial「そ」が 5 秒で消える UX 違和感への対策)
///  - v1.0.27: 「無音 PCM 5 秒継続送信」戦略 (#D-001) で server gap 自体を解消できるため、
///    partial 連結方式は削除。 旧 v1.0.23 以前と同じ「両方 DisplayDuration で消える」挙動に巻き戻し。
///  - v1.0.28 実機検証で完結文 emit 11 件発生して #D-001 戦略採用確定、 巻き戻しが正解と判明。
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
    /// partial (IsFinal=false) も final (IsFinal=true) も DisplayDuration 経過で削除対象になる (v1.0.27 旧挙動復活)。
    /// v1.0.24-26 では partial 連結方式の前提で partial を永続表示してたが、 v1.0.27 で連結方式廃止 + 無音 PCM 送信に置換 →
    /// partial は次の delta or 句点完結まで数秒以内に置換される前提なので、 DisplayDuration で消える挙動に戻った。
    /// </summary>
    [TestMethod]
    public void Update_PartialAndFinal_BothRemovedAfterDisplayDuration()
    {
        var settings = BuildSettings(displayDurationSec: 1.0, fadeOutSec: 0.5);

        foreach (var isFinal in new[] { false, true })
        {
            var item = BuildItem("seg-1", "テスト", isFinal: isFinal);
            var display = new SubtitleDisplayItem(item, settings);

            // DisplayDuration 中は残る
            Assert.IsFalse(display.ShouldRemove(DateTime.Now.AddSeconds(0.5)),
                $"IsFinal={isFinal}: DisplayDuration 中は残るはず");

            // DisplayDuration + FadeOutDuration 経過後は削除対象
            Assert.IsTrue(display.ShouldRemove(DateTime.Now.AddSeconds(2.0)),
                $"IsFinal={isFinal}: DisplayDuration + FadeOutDuration 経過で削除されるはず (v1.0.27 旧挙動復活)");
        }
    }

    /// <summary>
    /// partial 連続 Update で _displayEndTime が毎回リセットされ、 同じ字幕が画面に残り続けることを検証。
    /// (短時間で次の delta が来る通常ケース)
    /// </summary>
    [TestMethod]
    public void Update_MultiplePartialUpdates_ExtendsDisplayEnd()
    {
        var settings = BuildSettings(displayDurationSec: 1.0);
        var display = new SubtitleDisplayItem(BuildItem("seg-1", "そ", isFinal: false), settings);

        // 500ms 待機相当で Update → _displayEndTime がリセットされる
        foreach (var text in new[] { "そし", "そして", "そして、血液" })
        {
            display.Update(BuildItem("seg-1", text, isFinal: false), settings);
            // Update 直後は DisplayDuration 中
            Assert.IsFalse(display.ShouldRemove(DateTime.Now.AddSeconds(0.5)),
                $"Update 直後 ('{text}') は DisplayDuration 中で残るはず");
        }
    }
}
