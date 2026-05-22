using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace RealTimeTranslator.Core.Services;

public static class AudioFormatConverter
{
    private const int SourceSampleRate = 16000;
    private const int TargetSampleRate = 24000;
    private static readonly WaveFormat InputFormat = WaveFormat.CreateIeeeFloatWaveFormat(SourceSampleRate, 1);

    public static float[] ResampleTo24kHz(float[] samples16k)
    {
        ArgumentNullException.ThrowIfNull(samples16k);
        if (samples16k.Length == 0) return [];

        var inputProvider = new BufferSampleProvider(samples16k, InputFormat);
        var resampler = new WdlResamplingSampleProvider(inputProvider, TargetSampleRate);

        var outputCount = (int)(samples16k.Length * (double)TargetSampleRate / SourceSampleRate) + 1;
        var output = new float[outputCount];
        var read = resampler.Read(output, 0, outputCount);
        if (read == 0) return [];
        if (read == outputCount) return output; // 全長取れた場合は再確保しない（約 9 割のケース）

        // 端数のみ trim（AsSpan().ToArray() は内部で new float[read] + CopyTo を呼ぶので等価だが明示する）
        var trimmed = new float[read];
        Array.Copy(output, trimmed, read);
        return trimmed;
    }

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

    public static string ToPcm16Base64(float[] samples16k)
    {
        var resampled = ResampleTo24kHz(samples16k);
        var pcm16 = Float32ToPcm16(resampled);
        return Convert.ToBase64String(pcm16);
    }

}

internal sealed class BufferSampleProvider : ISampleProvider
{
    private readonly float[] _buffer;
    private int _position;

    public WaveFormat WaveFormat { get; }

    public BufferSampleProvider(float[] buffer, WaveFormat waveFormat)
    {
        _buffer = buffer;
        WaveFormat = waveFormat;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var available = Math.Min(count, _buffer.Length - _position);
        if (available <= 0) return 0;
        Array.Copy(_buffer, _position, buffer, offset, available);
        _position += available;
        return available;
    }
}

/// <summary>
/// セッションをまたいで状態を保持するストリーミングリサンプラ (16kHz → 24kHz)。
///
/// ⚠️ 重要: <see cref="AudioFormatConverter.ResampleTo24kHz"/> は呼び出しごとに
/// WdlResamplingSampleProvider を新規生成するため、 連続音声を細切れ (例: VAD ゲートの
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
    private const int SourceSampleRate = 16000;
    private const int TargetSampleRate = 24000;
    // WDL の sinc 補間先読みぶん (出力ドメイン)。 各フレームでこのぶんの末尾を次回に保留し、
    // 先読み入力が揃ってから出力することで境界アーティファクトを排除する。
    // ≒2.7ms 相当 (24kHz の 64 サンプル)。 リアルタイム性への影響は無視できる。
    private const int LatencyMarginOut = 64;
    private static readonly WaveFormat InputFormat = WaveFormat.CreateIeeeFloatWaveFormat(SourceSampleRate, 1);

    private readonly QueueSource _source = new(InputFormat);
    private WdlResamplingSampleProvider _resampler;
    // 累積カウンタで出力を駆動する。 「総入力に対応すべき総出力 − 既出力」だけを
    // 要求することで、 WDL が先読み (sinc 補間の未来サンプル) 不足のまま末尾を近似で
    // 埋めてしまうのを防ぐ。 返しきれなかったぶんは次回の入力到着後に出力される。
    private long _totalInSamples;
    private long _totalOutSamples;

    public StreamingResampler()
    {
        _resampler = new WdlResamplingSampleProvider(_source, TargetSampleRate);
    }

    /// <summary>
    /// 16kHz フレームをリサンプルして 24kHz サンプルを返す。 連続して呼ぶことで
    /// フィルタヒストリが引き継がれ、 フレーム境界のアーティファクトが出ない。
    /// 入力 N サンプルに対し出力は約 1.5N だが、 フィルタ遅延 (sinc 補間の先読み) のため
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
        var targetTotalOut = _totalInSamples * TargetSampleRate / SourceSampleRate;
        var wantOut = (int)(targetTotalOut - _totalOutSamples) - LatencyMarginOut;
        if (wantOut <= 0) return [];

        var output = new float[wantOut];
        // 1 回だけ Read する。 WDL が先読み不足で wantOut 未満しか返せない場合、
        // 残りは次回入力が来てから出力される (ループして無理に埋めない)。
        var got = _resampler.Read(output, 0, wantOut);
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
        _resampler = new WdlResamplingSampleProvider(_source, TargetSampleRate);
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

        public int Read(float[] buffer, int offset, int count)
        {
            var available = Math.Min(count, _end - _start);
            if (available <= 0) return 0;
            Array.Copy(_buffer, _start, buffer, offset, available);
            _start += available;
            return available;
        }
    }
}
