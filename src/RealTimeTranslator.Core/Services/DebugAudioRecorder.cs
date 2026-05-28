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
/// セッション開始時に WAV ヘッダ (44 bytes) を仮値で書き込み、 <see cref="StopSession"/> で
/// RIFF chunk size と data subchunk size を実値に書き直す。 途中クラッシュで Stop が呼ばれなくても、
/// header の placeholder 36 / 0 だけはそのまま残る (多くの WAV プレーヤは data chunk 末尾まで再生してくれる)。
/// </para>
/// <para>
/// スレッドセーフ: <see cref="WritePcm16"/> は 1 つのオーディオ処理タスクから呼ばれる想定だが、
/// Start/Stop は UI スレッドから呼ばれるため、 内部の lock で完全直列化する。
/// </para>
/// </remarks>
public sealed class DebugAudioRecorder : IDebugAudioRecorder, IDisposable
{
    private static readonly ILog Logger = LogManager.GetLogger<DebugAudioRecorder>();

    private const int SampleRate = 24000;
    private const int Channels = 1;
    private const int BitsPerSample = 16;
    private const int WavHeaderSize = 44;

    private readonly object _lock = new();
    private FileStream? _file;
    private long _dataBytesWritten;
    private string? _currentFilePath;

    public bool IsRecording
    {
        get { lock (_lock) return _file is not null; }
    }

    /// <summary>録音先ディレクトリ (%APPDATA%/RealTimeTranslator/debug/)。 UI の「フォルダを開く」ボタン用に公開。</summary>
    public static string DebugDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RealTimeTranslator",
        "debug");

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
                _file = new FileStream(_currentFilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
                WriteWavHeaderPlaceholder(_file);
                _dataBytesWritten = 0;
                Logger.Info($"DebugAudioRecorder 開始: {_currentFilePath}");
            }
            catch (Exception ex)
            {
                Logger.Warn("DebugAudioRecorder 開始失敗", ex);
                try { _file?.Dispose(); } catch { }
                _file = null;
                _currentFilePath = null;
                _dataBytesWritten = 0;
            }
        }
    }

    public void WritePcm16(ReadOnlySpan<byte> pcm16)
    {
        if (pcm16.IsEmpty) return;
        lock (_lock)
        {
            if (_file is null) return;
            try
            {
                _file.Write(pcm16);
                _dataBytesWritten += pcm16.Length;
            }
            catch (Exception ex)
            {
                Logger.Warn("DebugAudioRecorder 書き込み失敗、 セッション終了", ex);
                StopSessionInternal();
            }
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
        try
        {
            FinalizeWavHeader(_file, bytes);
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
            _file = null;
            _currentFilePath = null;
            _dataBytesWritten = 0;
        }
    }

    public void Dispose() => StopSession();

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
