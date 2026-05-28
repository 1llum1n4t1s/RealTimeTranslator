namespace RealTimeTranslator.Core.Interfaces;

/// <summary>
/// OpenAI に送信される PCM16 音声 (24kHz / Mono) を WAV ファイルに記録するデバッグ用録音インターフェース。
/// VAD ゲート通過後の音声 + サイレンス padding を「実送信と完全一致するバイト列」で記録するため、
/// <see cref="IRealtimeTranscriber.SendAudio"/> の入口でフックする想定。
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
    /// 録音セッションを開始する。 既存セッションがあれば自動で閉じてから新セッションを始める (idempotent)。
    /// 出力先は <c>%APPDATA%/RealTimeTranslator/debug/SentAudio_yyyyMMdd_HHmmss_{sessionId}.wav</c>。
    /// </summary>
    /// <param name="sessionId">ファイル名に埋め込む短い ID (空なら "session")。</param>
    void StartSession(string sessionId);

    /// <summary>
    /// PCM16 (24kHz / Mono / little-endian) のバイト列を追記する。 録音中でなければ no-op。
    /// </summary>
    void WritePcm16(ReadOnlySpan<byte> pcm16);

    /// <summary>
    /// 録音セッションを閉じて WAV ヘッダのサイズフィールドを確定書き込みする。 idempotent。
    /// </summary>
    void StopSession();
}
