namespace RealTimeTranslator.Core.Models;

/// <summary>呼び出し側が所有する mono float 音声と、マイクの実サンプリングレート。</summary>
public sealed record VoiceMicrophoneFrame(float[] Samples, int SampleRate);
