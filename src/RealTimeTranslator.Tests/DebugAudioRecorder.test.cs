using System.Buffers.Binary;
using RealTimeTranslator.Core.Services;

namespace RealTimeTranslator.Tests;

/// <summary>
/// DebugAudioRecorder の WAV ヘッダ生成 / 確定書き換えロジックを round-trip で検証する。
///
/// テスト方針:
///  - <see cref="DebugAudioRecorder.WriteWavHeaderPlaceholder"/> が 44 byte の妥当な PCM/24kHz/Mono ヘッダを書く
///  - <see cref="DebugAudioRecorder.FinalizeWavHeader"/> が RIFF chunk size と data subchunk size を実値に書き直す
///  - 4 GB 超 (uint オーバーフロー) は <see cref="uint.MaxValue"/> に clamp される
///  - StartSession → WritePcm16 → StopSession の end-to-end で実 WAV ファイルが作られる
///  - 録音中でない状態での WritePcm16 は no-op (ファイル作成しない / 例外を投げない)
/// </summary>
[TestClass]
public class DebugAudioRecorderTests
{
    private const int WavHeaderSize = 44;
    private const int ExpectedSampleRate = 24000;
    private const int ExpectedBitsPerSample = 16;
    private const int ExpectedChannels = 1;

    [TestMethod]
    public void WriteWavHeaderPlaceholder_WritesCorrectFormat()
    {
        using var ms = new MemoryStream();
        DebugAudioRecorder.WriteWavHeaderPlaceholder(ms);

        Assert.AreEqual(WavHeaderSize, ms.Length, "WAV ヘッダは 44 byte 固定");
        var buf = ms.ToArray();

        // RIFF magic
        Assert.AreEqual('R', (char)buf[0]);
        Assert.AreEqual('I', (char)buf[1]);
        Assert.AreEqual('F', (char)buf[2]);
        Assert.AreEqual('F', (char)buf[3]);

        // ChunkSize placeholder = 36 (= header 44 - 8 + data 0)
        Assert.AreEqual(36u, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4, 4)));

        // WAVE magic
        Assert.AreEqual("WAVE", System.Text.Encoding.ASCII.GetString(buf, 8, 4));

        // fmt  magic
        Assert.AreEqual("fmt ", System.Text.Encoding.ASCII.GetString(buf, 12, 4));

        // Subchunk1Size = 16 (PCM)
        Assert.AreEqual(16u, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(16, 4)));

        // AudioFormat = 1 (PCM)
        Assert.AreEqual((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(20, 2)));

        // NumChannels = 1 (Mono)
        Assert.AreEqual((ushort)ExpectedChannels, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(22, 2)));

        // SampleRate = 24000
        Assert.AreEqual((uint)ExpectedSampleRate, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(24, 4)));

        // ByteRate = 24000 * 1 * 16/8 = 48000
        var expectedByteRate = (uint)(ExpectedSampleRate * ExpectedChannels * ExpectedBitsPerSample / 8);
        Assert.AreEqual(expectedByteRate, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(28, 4)));

        // BlockAlign = 1 * 16/8 = 2
        var expectedBlockAlign = (ushort)(ExpectedChannels * ExpectedBitsPerSample / 8);
        Assert.AreEqual(expectedBlockAlign, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(32, 2)));

        // BitsPerSample = 16
        Assert.AreEqual((ushort)ExpectedBitsPerSample, BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(34, 2)));

        // data magic
        Assert.AreEqual("data", System.Text.Encoding.ASCII.GetString(buf, 36, 4));

        // Subchunk2Size placeholder = 0
        Assert.AreEqual(0u, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(40, 4)));
    }

    [TestMethod]
    public void FinalizeWavHeader_WritesCorrectSizes()
    {
        // header → 1000 byte の dummy PCM → finalize の流れを MemoryStream で再現
        using var ms = new MemoryStream();
        DebugAudioRecorder.WriteWavHeaderPlaceholder(ms);
        ms.Write(new byte[1000]);

        DebugAudioRecorder.FinalizeWavHeader(ms, 1000);

        var buf = ms.ToArray();
        // RIFF chunk size = 36 + 1000 = 1036
        Assert.AreEqual(1036u, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4, 4)));
        // Subchunk2Size = 1000
        Assert.AreEqual(1000u, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(40, 4)));
        // ファイル全体長 = 44 + 1000
        Assert.AreEqual(WavHeaderSize + 1000, buf.Length);
    }

    [TestMethod]
    public void FinalizeWavHeader_ClampsAtUintMaxFor4GBOverflow()
    {
        // 24kHz / Mono / PCM16 で約 24 時間 = 4 GB 相当。 それ以上は uint clamp。
        using var ms = new MemoryStream();
        DebugAudioRecorder.WriteWavHeaderPlaceholder(ms);

        // 実際には書き込まないが、 finalize で渡す dataBytes が uint 超のケースをシミュレート。
        long oversized = (long)uint.MaxValue + 100;
        DebugAudioRecorder.FinalizeWavHeader(ms, oversized);

        var buf = ms.ToArray();
        // RIFF chunk size と Subchunk2Size の両方が uint.MaxValue に clamp される
        Assert.AreEqual(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4, 4)));
        Assert.AreEqual(uint.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(40, 4)));
    }

    [TestMethod]
    public void WritePcm16_BeforeStart_IsNoOp()
    {
        // StartSession を呼ばずに WritePcm16 だけ呼んでも例外を投げず、 IsRecording=false のまま。
        using var recorder = new DebugAudioRecorder();
        Assert.IsFalse(recorder.IsRecording);
        recorder.WritePcm16(new byte[] { 0x00, 0x01, 0x02 });
        Assert.IsFalse(recorder.IsRecording);
    }

    [TestMethod]
    public void StopSession_BeforeStart_IsIdempotent()
    {
        // 録音中でない状態で StopSession を呼んでも例外を投げない (idempotent guarantee)。
        using var recorder = new DebugAudioRecorder();
        recorder.StopSession();
        recorder.StopSession();
        Assert.IsFalse(recorder.IsRecording);
    }

    [TestMethod]
    public void StartSession_WritePcm16_StopSession_ProducesValidWavFile()
    {
        // 実 file system 統合テスト: AppData/RealTimeTranslator/debug 配下に作られたファイルを
        // 検証して即削除する。 1 セッションだけなので CI 環境でも影響軽微。
        using var recorder = new DebugAudioRecorder();
        // ファイル名衝突を避けるため非 ASCII を含まないユニーク ID を使う
        var testId = $"test{Guid.NewGuid():N}"[..12];

        recorder.StartSession(testId);
        Assert.IsTrue(recorder.IsRecording);

        // 100 サンプル分の PCM16 (= 200 byte) を書く
        var pcm = new byte[200];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (byte)(i & 0xFF);
        recorder.WritePcm16(pcm);

        recorder.StopSession();
        Assert.IsFalse(recorder.IsRecording);

        // 作られたファイルを探す
        var files = Directory.GetFiles(DebugAudioRecorder.DebugDirectory, $"SentAudio_*_{testId}.wav");
        Assert.AreEqual(1, files.Length, "テスト ID に対応する WAV ファイルがちょうど 1 つできる");

        try
        {
            var bytes = File.ReadAllBytes(files[0]);
            Assert.AreEqual(WavHeaderSize + 200, bytes.Length, "ヘッダ 44 byte + PCM 200 byte");
            // RIFF chunk size = 36 + 200 = 236
            Assert.AreEqual(236u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)));
            // Subchunk2Size = 200
            Assert.AreEqual(200u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40, 4)));
            // PCM 本体が書いた通りに残っている
            for (int i = 0; i < 200; i++) Assert.AreEqual((byte)(i & 0xFF), bytes[WavHeaderSize + i]);
        }
        finally
        {
            try { File.Delete(files[0]); } catch { }
        }
    }

    [TestMethod]
    public void StartSession_CalledTwice_FirstSessionIsClosed()
    {
        // Start を 2 回連続で呼ぶと、 1 つ目のセッションは閉じられ 2 つ目だけが残る (idempotent guarantee)。
        using var recorder = new DebugAudioRecorder();
        var id1 = $"firstpass{Guid.NewGuid():N}"[..14];
        var id2 = $"secondpas{Guid.NewGuid():N}"[..14];

        recorder.StartSession(id1);
        recorder.WritePcm16(new byte[] { 0x11, 0x22 });
        recorder.StartSession(id2);  // ← 既存セッションを自動で閉じてから 2 つ目を始める
        recorder.WritePcm16(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });
        recorder.StopSession();

        try
        {
            var file1 = Directory.GetFiles(DebugAudioRecorder.DebugDirectory, $"SentAudio_*_{id1}.wav");
            var file2 = Directory.GetFiles(DebugAudioRecorder.DebugDirectory, $"SentAudio_*_{id2}.wav");
            Assert.AreEqual(1, file1.Length, "1 つ目のセッションも閉じられて WAV が残る");
            Assert.AreEqual(1, file2.Length, "2 つ目のセッションが新規 WAV として書かれる");

            var bytes1 = File.ReadAllBytes(file1[0]);
            // 1 つ目には 2 byte だけ書かれて閉じられているはず
            Assert.AreEqual(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes1.AsSpan(40, 4)));

            var bytes2 = File.ReadAllBytes(file2[0]);
            // 2 つ目には 4 byte 書かれている
            Assert.AreEqual(4u, BinaryPrimitives.ReadUInt32LittleEndian(bytes2.AsSpan(40, 4)));
        }
        finally
        {
            foreach (var f in Directory.GetFiles(DebugAudioRecorder.DebugDirectory, $"SentAudio_*_{id1}.wav")) try { File.Delete(f); } catch { }
            foreach (var f in Directory.GetFiles(DebugAudioRecorder.DebugDirectory, $"SentAudio_*_{id2}.wav")) try { File.Delete(f); } catch { }
        }
    }
}
