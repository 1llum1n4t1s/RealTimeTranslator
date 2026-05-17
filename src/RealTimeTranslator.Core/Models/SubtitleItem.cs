namespace RealTimeTranslator.Core.Models;

/// <summary>
/// 字幕アイテム。OpenAI Realtime API の delta（partial）と done（final）の両方を表現する。
/// </summary>
/// <remarks>
/// OriginalText / TranslatedText の意味は IsFinal で異なる:
///   - IsFinal=false (delta 中): OriginalText に partial 翻訳テキストを入れる、TranslatedText は空
///   - IsFinal=true (done):       OriginalText は空、TranslatedText に最終翻訳テキストを入れる
/// これは TranslationPipelineService の挙動と整合させた現実装の前提。命名は将来 Text 1 本に統合予定。
/// </remarks>
public class SubtitleItem
{
    /// <summary>
    /// 発話セグメントを識別する ID。partial と final で同じ ID を使い、UI 側で「同じ字幕の更新」と判定する。
    /// </summary>
    public string SegmentId { get; set; } = string.Empty;

    /// <summary>
    /// IsFinal=false 時の partial 翻訳テキスト。final 確定後は空。
    /// （旧設計の「原文」を保持するフィールドだったが、OpenAI Realtime Translate API は翻訳結果のみ返すため意味が変わった。）
    /// </summary>
    public string OriginalText { get; set; } = string.Empty;

    /// <summary>
    /// IsFinal=true 時の確定翻訳テキスト。partial 中は空。
    /// </summary>
    public string TranslatedText { get; set; } = string.Empty;

    /// <summary>
    /// API の done イベントで確定したかどうか。
    /// true: 確定字幕（response.output_audio_transcript.done 等で受信）
    /// false: ストリーミング途中の partial 字幕（delta イベント由来、TranslationPipelineService.DeltaThrottle で間引き）
    /// </summary>
    public bool IsFinal { get; set; }

    /// <summary>
    /// 表示開始時刻
    /// </summary>
    public DateTime DisplayStartTime { get; set; } = DateTime.Now;

    /// <summary>
    /// 表示終了時刻（フェードアウト開始）
    /// </summary>
    public DateTime DisplayEndTime { get; set; }

    /// <summary>
    /// 表示時間（秒）
    /// </summary>
    public double DisplayDurationSeconds { get; set; } = 5.0;

    /// <summary>
    /// フェードアウト中かどうか
    /// </summary>
    public bool IsFadingOut => DateTime.Now >= DisplayEndTime;

    /// <summary>
    /// 表示すべきテキスト
    /// 確定字幕の場合は翻訳文、仮字幕の場合は原文
    /// </summary>
    public string DisplayText => IsFinal && !string.IsNullOrEmpty(TranslatedText) 
        ? TranslatedText 
        : OriginalText;
}
