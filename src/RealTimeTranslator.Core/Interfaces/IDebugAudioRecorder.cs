namespace RealTimeTranslator.Core.Interfaces;

/// <summary>
/// OpenAI に送信される PCM16 音声 (24kHz / Mono) を WAV ファイルに記録するデバッグ用録音インターフェース。
/// VAD ゲート通過後の音声 + サイレンス padding を、 <see cref="IRealtimeTranscriber.SendAudio"/> の入口で
/// フックして記録する。 ⚠️ DropOldest による「送信前破棄」もこの時点では未発火のため、 厳密には
/// 「送信を試みたバイト列」(= Channel 投入直前) を記録するスーパーセット。
/// </summary>
/// <remarks>
/// セッション開始/終了は <c>TranslationPipelineService.StartCoreAsync</c> /
/// <c>StopCoreAsync</c> から制御する。 <see cref="WritePcm16"/> は recording 中でなければ no-op。
/// </remarks>
public interface IDebugAudioRecorder
{
    /// <summary>現在録音セッションが開いているか。</summary>
    bool IsRecording { get; }

    /// <summary>
    /// <see cref="WritePcm16"/> 内で File I/O 例外 (ディスク full / 権限不足 / AV ブロック等) が起きたときに発火する。
    /// 発火後はセッションが自動終了 (<see cref="IsRecording"/>=false) する。 UI 側で購読してバナー通知に使う想定。
    /// </summary>
    event Action<Exception>? WriteFailed;

    /// <summary>
    /// 録音セッションを開始する。 既存セッションがあれば自動で閉じてから新セッションを始める (idempotent)。
    /// 出力先は <c>%APPDATA%/RealTimeTranslator/debug/SentAudio_yyyyMMdd_HHmmss_{sessionId}.wav</c>。
    /// </summary>
    /// <param name="sessionId">ファイル名に埋め込む短い ID (空なら "session")。</param>
    /// <remarks>
    /// ⚠️ silent-fail 契約: ファイル open 失敗 (ディスク full / 権限不足 / AV ブロック等) のとき、
    /// 例外を呼び出し元に伝播せず、 Logger.Warn にログを残して <see cref="IsRecording"/>=false の
    /// ままに倒れる。 録音開始の成否を判定したい呼び出し元は <see cref="IsRecording"/> を確認すること。
    /// </remarks>
    void StartSession(string sessionId);

    /// <summary>
    /// PCM16 (24kHz / Mono / little-endian) のバイト列を追記する。 録音中でなければ no-op。
    /// 書き込み失敗時は <see cref="WriteFailed"/> イベントが発火しセッションが自動終了する。
    /// </summary>
    void WritePcm16(ReadOnlySpan<byte> pcm16);

    /// <summary>
    /// 録音セッションを閉じて WAV ヘッダのサイズフィールドを確定書き込みする。 idempotent。
    /// </summary>
    void StopSession();
}
