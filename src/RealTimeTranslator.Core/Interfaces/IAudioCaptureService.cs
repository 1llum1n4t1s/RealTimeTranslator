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
    /// 音声キャプチャを停止
    /// </summary>
    void StopCapture();

    /// <summary>
    /// キャプチャ中かどうか
    /// </summary>
    bool IsCapturing { get; }

    /// <summary>
    /// 設定を再適用
    /// </summary>
    /// <param name="settings">音声キャプチャ設定</param>
    void ApplySettings(AudioCaptureSettings settings);

    /// <summary>
    /// 音声データが利用可能になったときに発火するイベント
    /// </summary>
    event EventHandler<AudioDataEventArgs>? AudioDataAvailable;
}

/// <summary>
/// 音声データイベント引数
/// </summary>
public class AudioDataEventArgs : EventArgs
{
    /// <summary>
    /// 音声データ（16kHz, mono, float32）
    /// </summary>
    public float[] AudioData { get; }

    /// <summary>
    /// タイムスタンプ
    /// </summary>
    public DateTime Timestamp { get; }

    public AudioDataEventArgs(float[] audioData, DateTime timestamp)
    {
        AudioData = audioData;
        Timestamp = timestamp;
    }
}
