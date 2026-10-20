using System.Buffers.Binary;
using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.Core.Services;

public sealed partial class VoiceTranslationService
{
    private float _inputGain = 1f;
    private float _outputGain = 1f;

    public void SetGains(float inputGainDb, float outputGainDb)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Volatile.Write(ref _inputGain, MathF.Pow(10f, VoiceTranslationSettings.ClampGainDb(inputGainDb) / 20f));
        Volatile.Write(ref _outputGain, MathF.Pow(10f, VoiceTranslationSettings.ClampGainDb(outputGainDb) / 20f));
    }

    private byte[] ApplyOutputGain(byte[] pcm)
    {
        var gain = Volatile.Read(ref _outputGain);
        if (gain == 1f) return pcm;
        // 受信元の配列は変更せず、送話と試聴へ同じ調整済みPCMを渡す。
        var output = new byte[pcm.Length];
        for (var i = 0; i < pcm.Length; i += sizeof(short))
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i, sizeof(short)));
            var scaled = Math.Clamp(MathF.Round(sample * gain), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(i, sizeof(short)), (short)scaled);
        }
        return output;
    }
}
