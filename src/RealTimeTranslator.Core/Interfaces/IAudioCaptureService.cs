namespace RealTimeTranslator.Core.Interfaces;

using RealTimeTranslator.Core.Models;

/// <summary>
/// 音声キャプチャサービスのインターフェース
/// プロセス単位のループバックキャプチャを提供
/// </summary>
public interface IAudioCaptureService : IDisposable
{
    /// <summary>
    /// 指定したプロセスIDの音声キャプチャを開始
    /// </summary>
    /// <param name="processId">対象プロセスID</param>
    void StartCapture(int processId);

    /// <summary>
    /// 指定したプロセスIDの音声キャプチャを開始（オーディオセッションが見つかるまで待機）
    /// </summary>
    /// <param name="processId">対象プロセスID</param>
    /// <param name="cancellationToken">キャンセルトークン</param>
    /// <param name="captureCreationContext">指定時は CreateForProcessCaptureAsync をこのコンテキスト（例: UI スレッド）で実行。null の場合は呼び出しスレッドで実行</param>
    /// <returns>キャプチャ開始に成功したかどうか</returns>
    Task<bool> StartCaptureWithRetryAsync(int processId, CancellationToken cancellationToken, SynchronizationContext? captureCreationContext = null);

    /// <summary>
    /// 音声キャプチャを停止
    /// </summary>
    void StopCapture();

    /// <summary>
    /// キャプチャ中かどうか
    /// </summary>
    bool IsCapturing { get; }

    /// <summary>
    /// 今回のキャプチャ開始以降、無音でないデータ（振幅が閾値超）を一度でも受信したか。
    /// 持続無音時の PID 切替判定に利用する。
    /// </summary>
    bool HasReceivedNonSilentDataSinceStart { get; }

    /// <summary>
    /// 設定を再適用
    /// </summary>
    /// <param name="settings">音声キャプチャ設定</param>
    void ApplySettings(AudioCaptureSettings settings);

    /// <summary>
    /// 音声データが利用可能になったときに発火するイベント
    /// </summary>
    event EventHandler<AudioDataEventArgs>? AudioDataAvailable;

    /// <summary>
    /// キャプチャ状態が変化したときに発火するイベント
    /// </summary>
    event EventHandler<CaptureStatusEventArgs>? CaptureStatusChanged;
}

/// <summary>キャプチャ状態変更イベント引数 (Message + IsWaiting)。</summary>
public sealed record CaptureStatusEventArgs(string Message, bool IsWaiting = false);

/// <summary>音声データイベント引数 (16kHz mono float32 + タイムスタンプ)。</summary>
public sealed record AudioDataEventArgs(float[] AudioData, DateTime Timestamp);
