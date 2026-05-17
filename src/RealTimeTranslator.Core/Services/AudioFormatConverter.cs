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
