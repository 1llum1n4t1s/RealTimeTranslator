using System.Buffers.Binary;
using RealTimeTranslator.Core.Interfaces;
using SuperLightLogger;

namespace RealTimeTranslator.Core.Services;

/// <summary>
/// OpenAI に送信される PCM16 (24kHz / Mono) を %APPDATA%/RealTimeTranslator/debug/ 配下に
/// WAV ファイルとして書き出すデバッグ録音実装。
/// </summary>
/// <remarks>
/// <para>
/// セッション開始時に WAV ヘッダ (44 bytes) を仮値で書き込み、 1 秒周期 checkpoint で
/// RIFF chunk size と data subchunk size を直近値に書き直す + flush する。 これにより
/// 途中クラッシュで <see cref="StopSession"/> が呼ばれなくても、 直近 1 秒前までの WAV は
/// **再生可能ファイル**として残る (旧設計では data subchunk size=0 の placeholder で再生不能だった)。
/// </para>
/// <para>
/// <see cref="WritePcm16"/> は <c>BufferedStream(_, 64 KB)</c> 経由で書き込み、 OS syscall 頻度を
/// 1 秒あたり ~12 回 → ~1 回に削減 (24kHz Mono PCM16 = 48 KB/sec)。 OneDrive / Windows Defender
/// CFA / ネットワークドライブ配下でも hot path への影響を抑える。
/// </para>
/// <para>
/// スレッドセーフ: <see cref="WritePcm16"/> は 1 つのオーディオ処理タスクから呼ばれる想定だが、
/// Start/Stop は UI スレッドから / checkpoint Timer は ThreadPool から呼ばれるため、 内部の lock で
/// 完全直列化する。
/// </para>
/// </remarks>
public sealed class DebugAudioRecorder : IDebugAudioRecorder, IDisposable
{
    private static readonly ILog Logger = LogManager.GetLogger<DebugAudioRecorder>();

    private const int SampleRate = 24000;
    private const int Channels = 1;
    private const int BitsPerSample = 16;
    private const int WavHeaderSize = 44;
    private const int BufferSize = 65536; // 64 KB = 約 1.3 秒分の PCM (24kHz/Mono/PCM16 = 48 KB/sec)
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(1);
    private static readonly long[] SizeWarningThresholds = { 1_000_000_000L, 2_000_000_000L, 3_000_000_000L };

    private readonly object _lock = new();
    private readonly Timer _checkpointTimer;
    private FileStream? _innerFile;
    private BufferedStream? _file;
    private long _dataBytesWritten;
    private string? _currentFilePath;
    private int _nextSizeWarningIndex;
    private int _disposed;

    public bool IsRecording
    {
        get { lock (_lock) return _file is not null; }
    }

    public event Action<Exception>? WriteFailed;

    /// <summary>録音先ディレクトリ (%APPDATA%/RealTimeTranslator/debug/)。 UI の「フォルダを開く」ボタン用に公開。</summary>
    public static string DebugDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RealTimeTranslator",
        "debug");

    public DebugAudioRecorder()
    {
        _checkpointTimer = new Timer(OnCheckpointTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void StartSession(string sessionId)
    {
        lock (_lock)
        {
            StopSessionInternal();

            try
            {
                Directory.CreateDirectory(DebugDirectory);
                var safeSessionId = string.IsNullOrWhiteSpace(sessionId) ? "session" : sessionId;
                var fileName = $"SentAudio_{DateTime.Now:yyyyMMdd_HHmmss}_{safeSessionId}.wav";
                _currentFilePath = Path.Combine(DebugDirectory, fileName);
                _innerFile = new FileStream(_currentFilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
                _file = new BufferedStream(_innerFile, BufferSize);
                WriteWavHeaderPlaceholder(_file);
                _dataBytesWritten = 0;
                _nextSizeWarningIndex = 0;
                _checkpointTimer.Change(CheckpointInterval, CheckpointInterval);
                Logger.Info($"DebugAudioRecorder 開始: {_currentFilePath}");
            }
            catch (Exception ex)
            {
                Logger.Warn("DebugAudioRecorder 開始失敗", ex);
                try { _file?.Dispose(); } catch { }
                try { _innerFile?.Dispose(); } catch { }
                _file = null;
                _innerFile = null;
                _currentFilePath = null;
                _dataBytesWritten = 0;
            }
        }
    }

    public void WritePcm16(ReadOnlySpan<byte> pcm16)
    {
        if (pcm16.IsEmpty) return;
        Exception? failure = null;
        lock (_lock)
        {
            if (_file is null) return;
            try
            {
                _file.Write(pcm16);
                _dataBytesWritten += pcm16.Length;
                CheckSizeWarning();
            }
            catch (Exception ex)
            {
                failure = ex;
                Logger.Warn("DebugAudioRecorder 書き込み失敗、 セッション終了", ex);
                StopSessionInternal();
            }
        }
        if (failure is not null)
        {
            // 購読者の例外は録音側に伝播させない (try-catch でガード)。
            try { WriteFailed?.Invoke(failure); } catch (Exception ex) { Logger.Warn("WriteFailed handler 例外", ex); }
        }
    }

    public void StopSession()
    {
        lock (_lock)
        {
            StopSessionInternal();
        }
    }

    private void StopSessionInternal()
    {
        if (_file is null) return;
        var path = _currentFilePath;
        var bytes = _dataBytesWritten;
        _checkpointTimer.Change(Timeout.Infinite, Timeout.Infinite);
        try
        {
            _file.Flush();
            if (_innerFile is not null) FinalizeWavHeader(_innerFile, bytes);
            _file.Flush();
            Logger.Info($"DebugAudioRecorder 停止: {path} ({bytes:N0} bytes PCM データ)");
        }
        catch (Exception ex)
        {
            Logger.Warn("DebugAudioRecorder 停止中の例外", ex);
        }
        finally
        {
            try { _file.Dispose(); } catch { }
            try { _innerFile?.Dispose(); } catch { }
            _file = null;
            _innerFile = null;
            _currentFilePath = null;
            _dataBytesWritten = 0;
            _nextSizeWarningIndex = 0;
        }
    }

    /// <summary>
    /// 1 秒周期で呼ばれる checkpoint。 BufferedStream を flush して WAV ヘッダを直近値に更新することで、
    /// 途中クラッシュ時も直近 1 秒前までは再生可能ファイルとして残す (C2-001 / B1-004 対応)。
    /// 失敗してもセッションは継続 (致命的ではない、 次回 checkpoint で再試行)。
    /// </summary>
    private void OnCheckpointTimerElapsed(object? state)
    {
        if (Interlocked.CompareExchange(ref _disposed, 0, 0) != 0) return;
        lock (_lock)
        {
            if (_file is null || _innerFile is null) return;
            try
            {
                _file.Flush();
                FinalizeWavHeader(_innerFile, _dataBytesWritten);
                _innerFile.Flush();
                // FinalizeWavHeader が underlying FileStream を末尾に Seek-back するが、
                // BufferedStream の内部 position と underlying の position が乖離する可能性があるため、
                // BufferedStream 側でも明示的に末尾に seek し直しておく (subsequent Write が EOF append であることを保証)。
                _file.Seek(0, SeekOrigin.End);
            }
            catch (Exception ex)
            {
                Logger.Warn("DebugAudioRecorder checkpoint 失敗 (継続)", ex);
            }
        }
    }

    private void CheckSizeWarning()
    {
        while (_nextSizeWarningIndex < SizeWarningThresholds.Length
               && _dataBytesWritten >= SizeWarningThresholds[_nextSizeWarningIndex])
        {
            var threshold = SizeWarningThresholds[_nextSizeWarningIndex];
            Logger.Warn($"DebugAudioRecorder: 録音サイズが {threshold / 1_000_000_000.0:F1} GB を超えました ({_dataBytesWritten:N0} bytes)。 24 時間で WAV format の uint32 chunk size 上限 (4 GB) に達して再生不能になるため、 必要なら録音を停止してください。");
            _nextSizeWarningIndex++;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        StopSession();
        _checkpointTimer.Dispose();
    }

    internal static void WriteWavHeaderPlaceholder(Stream stream)
    {
        Span<byte> header = stackalloc byte[WavHeaderSize];
        // "RIFF"
        header[0] = (byte)'R'; header[1] = (byte)'I'; header[2] = (byte)'F'; header[3] = (byte)'F';
        // ChunkSize (placeholder = 36 = header 44 - 8 + data 0)
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4, 4), 36);
        // "WAVE"
        header[8] = (byte)'W'; header[9] = (byte)'A'; header[10] = (byte)'V'; header[11] = (byte)'E';
        // "fmt "
        header[12] = (byte)'f'; header[13] = (byte)'m'; header[14] = (byte)'t'; header[15] = (byte)' ';
        // Subchunk1Size (PCM = 16)
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(16, 4), 16);
        // AudioFormat (PCM = 1)
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(20, 2), 1);
        // NumChannels
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(22, 2), (ushort)Channels);
        // SampleRate
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(24, 4), SampleRate);
        // ByteRate = SampleRate * Channels * BitsPerSample/8
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(28, 4), (uint)(SampleRate * Channels * BitsPerSample / 8));
        // BlockAlign = Channels * BitsPerSample/8
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(32, 2), (ushort)(Channels * BitsPerSample / 8));
        // BitsPerSample
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(34, 2), BitsPerSample);
        // "data"
        header[36] = (byte)'d'; header[37] = (byte)'a'; header[38] = (byte)'t'; header[39] = (byte)'a';
        // Subchunk2Size (placeholder)
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(40, 4), 0);
        stream.Write(header);
    }

    internal static void FinalizeWavHeader(Stream stream, long dataBytes)
    {
        // WAV は 32bit unsigned size しか持てないので 4GB 超は clamp する
        // (24kHz / Mono / PCM16 で 4GB = 約 24 時間ぶん、 実用上ヒットしない)。
        var riffChunkSize = (uint)Math.Min(uint.MaxValue, 36 + dataBytes);
        var dataChunkSize = (uint)Math.Min(uint.MaxValue, dataBytes);

        Span<byte> buf = stackalloc byte[4];
        stream.Seek(4, SeekOrigin.Begin);
        BinaryPrimitives.WriteUInt32LittleEndian(buf, riffChunkSize);
        stream.Write(buf);

        stream.Seek(40, SeekOrigin.Begin);
        BinaryPrimitives.WriteUInt32LittleEndian(buf, dataChunkSize);
        stream.Write(buf);
        // Seek を末尾に戻しておく (後続 Write が EOF に追記できるように)。
        stream.Seek(0, SeekOrigin.End);
    }
}
