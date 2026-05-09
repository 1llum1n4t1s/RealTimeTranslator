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
        return output.AsSpan(0, read).ToArray();
    }

    public static byte[] Float32ToPcm16(float[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var clamped = Math.Clamp(samples[i], -1.0f, 1.0f);
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
