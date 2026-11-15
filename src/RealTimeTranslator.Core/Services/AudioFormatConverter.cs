using System.Buffers.Binary;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace RealTimeTranslator.Core.Services;

/// <summary>
/// float32 と PCM16 (little endian) の相互変換。OpenAI へ送る 24 kHz / mono / PCM16 と、
/// OpenAI から受け取る PCM16 を再生用 float へ戻す経路が共用する。
/// </summary>
public static class AudioFormatConverter
{
    public static byte[] Float32ToPcm16(float[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            // rere A2-005: Math.Clamp は NaN 入力で NaN を返し、(short)NaN は C# 仕様で 0 になるが
            // 実装依存挙動への依存を避けるため明示的に 0 に倒す。 ±Inf は Clamp で -1.0/1.0 に丸まる。
            var raw = samples[i];
            var clamped = float.IsNaN(raw) ? 0f : Math.Clamp(raw, -1.0f, 1.0f);
            var value = (short)(clamped * 32767);
            bytes[i * 2] = (byte)(value & 0xFF);
            bytes[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }
        return bytes;
    }

    /// <summary>
    /// PCM16 (little endian) を -1.0〜1.0 未満の float へ戻す。端数の 1 byte は無視する。
    /// 送話 (仮想マイク) と試聴の出力バッファが同じ変換を使い、両者の音量と波形を一致させる。
    /// </summary>
    public static float[] Pcm16ToFloat32(ReadOnlySpan<byte> pcm16)
    {
        var samples = new float[pcm16.Length / sizeof(short)];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm16.Slice(i * sizeof(short), sizeof(short))) / 32768f;
        return samples;
    }
}

/// <summary>
/// セッションをまたいで状態を保持するストリーミングリサンプラ (任意のレート間。 既定は 16kHz → 24kHz)。
/// 字幕経路の 48k→16k / 48k→24k、 ボイス通訳のマイク→16k/24k、 出力機器向けの変換が使う。
///
/// ⚠️ 重要: 旧実装 (一括リサンプル関数、 撤去済み) は呼び出しごとに
/// WdlResamplingSampleProvider を新規生成していたため、 連続音声を細切れ (例: VAD ゲートの
/// 512 サンプル/32ms フレーム) で渡すと、 各フレーム境界で FIR フィルタのヒストリが
/// ゼロリセットされ、 振幅最大 90% のクリックノイズが規則的に挿入される
/// (2026-05-23 実証: フレーム内部の誤差は 0、 境界だけに誤差が局在)。
/// その結果 OpenAI が文境界を検出できず「区切りがおかしい / 句点が来ない」が起きる。
///
/// このクラスは WdlResamplingSampleProvider を 1 個だけ保持し、 入力フレームを
/// 連続して通すことでフィルタヒストリを引き継ぎ、 境界アーティファクトを構造的に排除する。
///
/// スレッド安全性なし: 単一の audio 処理ループスレッドからのみ呼ぶこと。
/// </summary>
public sealed class StreamingResampler
{
    // WDL の sinc 補間先読みぶん (出力ドメイン)。 各フレームでこのぶんの末尾を次回に保留し、
    // 先読み入力が揃ってから出力することで境界アーティファクトを排除する。
    // ⭐ targetSampleRate に比例させて「時間ぶん」を一定 (4ms) に揃える。
    // これにより複数の StreamingResampler (例: 48k→16k VAD用 + 48k→24k 送信用) を並列に
    // 動かしたとき、 出力の時間遅延が揃って 16k フレームと 24k フレームをペアリングできる。
    // (旧 LatencyMarginOut=64 固定では 16k=4ms / 24k=2.7ms と遅延が違って同期がずれていた。)
    private const int LatencyMarginMs = 4;

    private readonly int _sourceSampleRate;
    private readonly int _targetSampleRate;
    private readonly int _latencyMarginOut; // = _targetSampleRate * LatencyMarginMs / 1000
    private readonly QueueSource _source;
    private WdlResamplingSampleProvider _resampler;
    // 累積カウンタで出力を駆動する。 「総入力に対応すべき総出力 − 既出力」だけを
    // 要求することで、 WDL が先読み (sinc 補間の未来サンプル) 不足のまま末尾を近似で
    // 埋めてしまうのを防ぐ。 返しきれなかったぶんは次回の入力到着後に出力される。
    private long _totalInSamples;
    private long _totalOutSamples;

    /// <param name="sourceSampleRate">入力サンプリングレート (既定 16000)</param>
    /// <param name="targetSampleRate">出力サンプリングレート (既定 24000)</param>
    public StreamingResampler(int sourceSampleRate = 16000, int targetSampleRate = 24000)
    {
        _sourceSampleRate = sourceSampleRate;
        _targetSampleRate = targetSampleRate;
        _latencyMarginOut = targetSampleRate * LatencyMarginMs / 1000;
        _source = new QueueSource(WaveFormat.CreateIeeeFloatWaveFormat(sourceSampleRate, 1));
        _resampler = new WdlResamplingSampleProvider(_source, targetSampleRate);
    }

    /// <summary>
    /// 入力フレームを targetSampleRate へリサンプルして返す。 連続して呼ぶことで
    /// フィルタヒストリが引き継がれ、 フレーム境界のアーティファクトが出ない。
    /// 入力 N サンプルに対し出力は約 N × target/source だが、 フィルタ遅延 (sinc 補間の先読み) のため
    /// 各呼び出しの出力数は変動する (トータルでは帳尻が合う)。
    /// </summary>
    public float[] Resample(float[] input16k)
    {
        ArgumentNullException.ThrowIfNull(input16k);
        if (input16k.Length == 0) return [];

        // 追記型キューに入力を足す。 WDL が sinc 補間の先読みで消費しきれなかった
        // 未消費サンプルは _source 内に保持され、 次回の入力と連続して供給される。
        _source.Append(input16k);
        _totalInSamples += input16k.Length;

        // これまでの総入力に対応すべき総出力数 − 既に出力した数 = 今回出すべき数。
        // ただし末尾 LatencyMarginOut サンプルは保留して次回に回す。 こうしないと WDL が
        // source 枯渇時に sinc 先読み不足のまま末尾を近似で埋めてしまい、 フレーム境界に
        // 誤差が残る。 保留ぶんは次フレーム到着後 (先読み入力が揃ってから) 正確に出力される。
        var targetTotalOut = _totalInSamples * _targetSampleRate / _sourceSampleRate;
        var wantOut = (int)(targetTotalOut - _totalOutSamples) - _latencyMarginOut;
        if (wantOut <= 0) return [];

        var output = new float[wantOut];
        // 1 回だけ Read する。 WDL が先読み不足で wantOut 未満しか返せない場合、
        // 残りは次回入力が来てから出力される (ループして無理に埋めない)。
        var got = _resampler.Read(output);
        if (got <= 0) return [];
        _totalOutSamples += got;

        if (got == output.Length) return output;

        var trimmed = new float[got];
        Array.Copy(output, trimmed, got);
        return trimmed;
    }

    /// <summary>
    /// セッション開始時にフィルタヒストリと未消費入力を完全クリアする。
    /// WdlResamplingSampleProvider.Reset() の有無に依存しないよう、 インスタンスごと作り直す。
    /// </summary>
    public void Reset()
    {
        _source.Clear();
        _totalInSamples = 0;
        _totalOutSamples = 0;
        _resampler = new WdlResamplingSampleProvider(_source, _targetSampleRate);
    }

    /// <summary>
    /// 追記型の ISampleProvider。 WDL が読んだぶんだけ先頭を進め、 未消費ぶんは保持する。
    /// これにより同一の WdlResamplingSampleProvider に連続フレームを途切れなく供給できる
    /// (sinc 補間の先読みで残った入力が次フレームと正しく連結される)。
    /// </summary>
    private sealed class QueueSource : ISampleProvider
    {
        private float[] _buffer = new float[8192];
        private int _start; // 未消費の先頭インデックス
        private int _end;   // 書き込み済み末尾インデックス

        public WaveFormat WaveFormat { get; }

        public QueueSource(WaveFormat waveFormat) => WaveFormat = waveFormat;

        public void Append(float[] samples)
        {
            // 未消費ぶんを先頭に詰める (コンパクション)。 残量は通常数十サンプルなので軽量。
            if (_start > 0)
            {
                var remaining = _end - _start;
                if (remaining > 0)
                    Array.Copy(_buffer, _start, _buffer, 0, remaining);
                _start = 0;
                _end = remaining;
            }

            var required = _end + samples.Length;
            if (required > _buffer.Length)
            {
                var newSize = _buffer.Length * 2;
                while (newSize < required) newSize *= 2;
                Array.Resize(ref _buffer, newSize);
            }

            Array.Copy(samples, 0, _buffer, _end, samples.Length);
            _end += samples.Length;
        }

        public void Clear()
        {
            _start = 0;
            _end = 0;
        }

        public int Read(Span<float> buffer)
        {
            var available = Math.Min(buffer.Length, _end - _start);
            if (available <= 0) return 0;
            _buffer.AsSpan(_start, available).CopyTo(buffer);
            _start += available;
            return available;
        }
    }
}
