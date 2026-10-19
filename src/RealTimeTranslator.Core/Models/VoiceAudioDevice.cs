namespace RealTimeTranslator.Core.Models;

/// <summary>音声翻訳で選択する Windows 音声エンドポイント。</summary>
public sealed record VoiceAudioDevice(string Id, string Name, bool IsWaveLink);
