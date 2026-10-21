using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.Core.Services;

public sealed partial class VoiceTranslationService
{
    // 字幕の padding 設定とは独立して、翻訳音声の末尾を押し出す。
    private const int VoiceSilencePaddingMs = 5000;

    private void InitializeVoiceVad(CancellationToken ct)
    {
        if (!_settings.EnableVad) return;
        var gate = new MicrophoneVadGate(_vadFactory(), _settings);
        try
        {
            lock (_routing)
            {
                ct.ThrowIfCancellationRequested();
                _voiceVad = gate;
            }
        }
        catch
        {
            gate.Dispose();
            throw;
        }
    }

    /// <summary>マイク専用の推論履歴と、同じ入力から分岐する 16 / 24 kHz DSP を所有する。</summary>
    private sealed class MicrophoneVadGate : IDisposable
    {
        private const int VadSampleRate = 16000;
        private const int SendSampleRate = 24000;
        private const int VadFrameSize = 512;
        private const int SendFrameSize = 768;
        private readonly Lock _sync = new();
        private readonly IVoiceActivityDetector _detector;
        private readonly float _threshold;
        private readonly int _preRollSamples;
        private readonly int _hangoverSamples;
        private readonly Queue<float> _vadSamples = new();
        private readonly Queue<float> _sendSamples = new();
        private readonly Queue<byte> _preRoll = new();
        private readonly float[] _vadFrame = new float[VadFrameSize];
        private readonly float[] _sendFrame = new float[SendFrameSize];
        private StreamingResampler? _vadResampler;
        private StreamingResampler? _sendResampler;
        private int _inputRate;
        private int _epoch = -1;
        private int _hangoverRemaining;
        private int _paddingRemaining;
        private int _sentAheadSamples;
        private bool _keyOpen;
        private bool _sendingSpeech;
        private bool _lastFrameWasSpeech;
        private bool _disposed;

        public MicrophoneVadGate(IVoiceActivityDetector detector, VoiceTranslationSettings settings)
        {
            _detector = detector ?? throw new InvalidOperationException("マイク用 VAD を初期化できませんでした。");
            _threshold = settings.VadThreshold;
            _preRollSamples = settings.VadPreRollMs * SendSampleRate / 1000;
            _hangoverSamples = settings.VadHangoverMs * SendSampleRate / 1000;
            try
            {
                if (_detector.SampleRate != VadSampleRate || _detector.RequiredFrameSize != VadFrameSize)
                    throw new InvalidDataException("マイク用 VAD は 16 kHz / 512 samples が必要です。");
                _detector.Reset();
            }
            catch
            {
                _detector.Dispose();
                throw;
            }
        }

        public byte[] Process(float[] input, int sampleRate, bool keyOpen, int epoch)
        {
            lock (_sync)
            {
                // mode 切替で detach 済みの処理結果は呼出元の epoch 照合でも破棄される。
                if (_disposed) return [];
                if (_inputRate != sampleRate || _epoch != epoch)
                {
                    ResetSignal(sampleRate);
                    _paddingRemaining = 0;
                    _keyOpen = false;
                    _epoch = epoch;
                }
                if (_keyOpen != keyOpen)
                {
                    // PTT 閉鎖前の実音、特徴量、DSP の先読みを次の押下へ渡さない。
                    bool wasSending = _sendingSpeech;
                    ResetSignal(sampleRate);
                    if (wasSending) _paddingRemaining = VoiceSilencePaddingMs * SendSampleRate / 1000;
                    _keyOpen = keyOpen;
                }
                if (!keyOpen)
                {
                    // 推論に閉鎖中の音声を渡さず、DSP の残留音も含めて厳密な無音を作る。
                    var zeros = _sendResampler!.Resample(input);
                    return TakePadding(zeros.Length);
                }

                // 送信用 24 kHz は 16 kHz を経由せず、入力ゲイン適用後の同じ native 音声から作る。
                foreach (float sample in _vadResampler!.Resample(input)) _vadSamples.Enqueue(sample);
                foreach (float sample in _sendResampler!.Resample(input)) _sendSamples.Enqueue(sample);
                using var output = new MemoryStream();
                while (_vadSamples.Count >= VadFrameSize && _sendSamples.Count >= SendFrameSize)
                {
                    for (int i = 0; i < VadFrameSize; i++) _vadFrame[i] = _vadSamples.Dequeue();
                    for (int i = 0; i < SendFrameSize; i++) _sendFrame[i] = _sendSamples.Dequeue();
                    int sentAhead = Math.Min(_sentAheadSamples, SendFrameSize);
                    _sentAheadSamples -= sentAhead;
                    float probability = _detector.DetectSpeechProb(_vadFrame);
                    if (!float.IsFinite(probability) || probability is < 0f or > 1f)
                        throw new InvalidDataException("マイク用 VAD が不正な発話確率を返しました。");
                    var pcm = AudioFormatConverter.Float32ToPcm16(_sendFrame);
                    _lastFrameWasSpeech = probability >= _threshold;
                    if (_lastFrameWasSpeech)
                    {
                        if (!_sendingSpeech)
                        {
                            // pre-roll と現在音声を一つの送信バッチにまとめて冒頭の drop を避ける。
                            while (_preRoll.TryDequeue(out byte value)) output.WriteByte(value);
                        }
                        output.Write(pcm.AsSpan(sentAhead * sizeof(short)));
                        _sendingSpeech = true;
                        _hangoverRemaining = _hangoverSamples;
                        _paddingRemaining = VoiceSilencePaddingMs * SendSampleRate / 1000;
                    }
                    else
                    {
                        int tailSamples = _sendingSpeech ? Math.Min(_hangoverRemaining, SendFrameSize) : 0;
                        if (tailSamples > sentAhead)
                        {
                            output.Write(pcm.AsSpan(sentAhead * sizeof(short), (tailSamples - sentAhead) * sizeof(short)));
                        }
                        _hangoverRemaining -= tailSamples;
                        if (tailSamples < SendFrameSize)
                        {
                            _sendingSpeech = false;
                            // 判定前に許可して送った部分も pre-roll / padding と重ねない。
                            int silentStart = Math.Max(tailSamples, sentAhead);
                            var silentPart = pcm.AsSpan(silentStart * sizeof(short));
                            RememberPreRoll(silentPart);
                            WritePadding(output, SendFrameSize - silentStart);
                        }
                    }
                }
                if (_sendingSpeech)
                {
                    // 既に開いたゲートの末尾を次の 512 samples 判定まで保留しない。
                    // PTT 解放時には判定待ち queue を消せるよう、KeyOpen のうちに送る。
                    int permitted = _lastFrameWasSpeech ? SendFrameSize : _hangoverRemaining;
                    int sendAhead = Math.Min(_sendSamples.Count, permitted);
                    if (sendAhead > _sentAheadSamples)
                    {
                        var pending = AudioFormatConverter.Float32ToPcm16(_sendSamples.ToArray());
                        output.Write(pending.AsSpan(_sentAheadSamples * sizeof(short),
                            (sendAhead - _sentAheadSamples) * sizeof(short)));
                        _sentAheadSamples = sendAhead;
                    }
                }
                return output.ToArray();
            }
        }

        private void RememberPreRoll(ReadOnlySpan<byte> pcm)
        {
            foreach (byte value in pcm) _preRoll.Enqueue(value);
            while (_preRoll.Count > _preRollSamples * sizeof(short)) _preRoll.Dequeue();
        }

        private void WritePadding(MemoryStream output, int availableSamples)
        {
            int samples = Math.Min(_paddingRemaining, availableSamples);
            _paddingRemaining -= samples;
            // 末尾のゼロ拡張で、一時配列の確保とコピーを省く。
            output.SetLength(output.Length + samples * sizeof(short));
            output.Position = output.Length;
        }

        private byte[] TakePadding(int availableSamples)
        {
            int samples = Math.Min(_paddingRemaining, availableSamples);
            _paddingRemaining -= samples;
            return new byte[samples * sizeof(short)];
        }

        private void ResetSignal(int sampleRate)
        {
            _detector.Reset();
            _vadSamples.Clear();
            _sendSamples.Clear();
            _preRoll.Clear();
            Array.Clear(_vadFrame);
            Array.Clear(_sendFrame);
            _vadResampler = new StreamingResampler(sampleRate, VadSampleRate);
            _sendResampler = new StreamingResampler(sampleRate, SendSampleRate);
            _inputRate = sampleRate;
            _hangoverRemaining = 0;
            _sentAheadSamples = 0;
            _sendingSpeech = false;
            _lastFrameWasSpeech = false;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                _vadSamples.Clear();
                _sendSamples.Clear();
                _preRoll.Clear();
                Array.Clear(_vadFrame);
                Array.Clear(_sendFrame);
                _vadResampler = null;
                _sendResampler = null;
                _detector.Dispose();
            }
        }
    }
}
