using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.Core.Services;

/// <summary>選択したマイクと Wave Link 出力を、セッションごとに独立して所有する。</summary>
public sealed class VoiceAudioService : IVoiceAudioService
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private Session? _session;
    private long _sessionId;
    private bool _disposed;

    public long SessionId => Interlocked.Read(ref _sessionId);
    public event Action<VoiceMicrophoneFrame>? MicrophoneFrame;
    public event Action<long, Exception>? ErrorReceived;

    public IReadOnlyList<VoiceAudioDevice> GetMicrophones() => GetDevices(DataFlow.Capture);

    public IReadOnlyList<VoiceAudioDevice> GetOutputs() => GetDevices(DataFlow.Render);

    private static IReadOnlyList<VoiceAudioDevice> GetDevices(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        var result = new List<VoiceAudioDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
            {
                var kind = WaveLinkDeviceIdentity.Classify(device);
                if (flow == DataFlow.Render ? kind == VoiceAudioDeviceKind.WaveLinkOutput : kind == VoiceAudioDeviceKind.Microphone)
                    result.Add(new VoiceAudioDevice(device.ID, device.FriendlyName, kind == VoiceAudioDeviceKind.WaveLinkOutput));
            }
        }
        return result.OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public async Task StartAsync(string microphoneId, string outputDeviceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(microphoneId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDeviceId);
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var current = Volatile.Read(ref _session);
            if (current is { Active: true } && current.MicrophoneId == microphoneId && current.OutputId == outputDeviceId)
                return;

            await StopCoreAsync().ConfigureAwait(false);
            // NAudio のイベントが UI の SynchronizationContext を捕捉しない場所で構築する。
            await Task.Run(async () =>
            {
                var session = new Session(Interlocked.Increment(ref _sessionId), microphoneId, outputDeviceId);
                try
                {
                    session.Initialize();
                    session.Capture!.DataAvailable += (buffer, flags, _, _) => CaptureFrame(session, buffer, flags);
                    session.Capture.RecordingStopped += (_, args) =>
                        Fault(session, args.Exception ?? new IOException("マイク入力が停止しました。"));
                    session.Player!.PlaybackStopped += (_, args) =>
                        Fault(session, args.Exception ?? new IOException("Wave Link への出力が停止しました。"));
                    Volatile.Write(ref _session, session);
                    session.Player.Play();
                    session.Capture.StartRecording();
                    // Starting 中の停止競合を避け、キャプチャスレッドが起動したことを確認する。
                    var timer = Stopwatch.StartNew();
                    while (session.Capture.CaptureState == CaptureState.Starting && timer.ElapsedMilliseconds < 2000)
                        await Task.Delay(10).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (session.Capture.CaptureState != CaptureState.Capturing || !session.Active)
                        throw new IOException("マイク入力を開始できませんでした。");
                }
                catch
                {
                    Interlocked.CompareExchange(ref _session, null, session);
                    session.Deactivate();
                    session.Dispose();
                    throw;
                }
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private void CaptureFrame(Session session, ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags)
    {
        if (!session.Active || buffer.IsEmpty) return;
        try
        {
            // ネイティブバッファを即コピーし、変換後の配列もフレーム専用にする。
            var owned = buffer.ToArray();
            var format = session.CaptureFormat!;
            var frames = owned.Length / format.BlockAlign;
            var mono = new float[frames];
            if ((flags & AudioClientBufferFlags.Silent) == 0)
            {
                using var stream = new RawSourceWaveStream(new MemoryStream(owned, false), format);
                var provider = stream.ToSampleProvider();
                var interleaved = new float[frames * format.Channels];
                var count = provider.Read(interleaved);
                for (var frame = 0; frame < count / format.Channels; frame++)
                {
                    float sum = 0;
                    for (var channel = 0; channel < format.Channels; channel++)
                        sum += interleaved[frame * format.Channels + channel];
                    mono[frame] = sum / format.Channels;
                }
            }
            if (session.Active)
                MicrophoneFrame?.Invoke(new VoiceMicrophoneFrame(mono, format.SampleRate));
        }
        catch (Exception exception)
        {
            Fault(session, exception);
        }
    }

    public void WriteOutput(byte[] pcm16, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(pcm16);
        if (sampleRate is < 8000 or > 192000)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (pcm16.Length % sizeof(short) != 0)
            throw new ArgumentException("PCM16 音声はサンプル境界に揃えてください。", nameof(pcm16));

        var session = Volatile.Read(ref _session);
        if (session is null || !session.Active || pcm16.Length == 0) return;
        try
        {
            session.Output!.Append(pcm16, sampleRate);
        }
        catch (Exception exception)
        {
            Fault(session, exception);
        }
    }

    public void ClearOutput() => Volatile.Read(ref _session)?.Output?.Clear();

    private void Fault(Session session, Exception exception)
    {
        if (!session.Deactivate()) return;
        // キャプチャ／再生スレッド上で Dispose を呼ぶと、自分自身の終了待ちになる。
        _ = Task.Run(async () =>
        {
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(Volatile.Read(ref _session), session))
                    await StopCoreAsync().ConfigureAwait(false);
            }
            catch (Exception stopException)
            {
                exception = new AggregateException("音声入出力の停止処理にも失敗しました。", exception, stopException);
            }
            finally
            {
                _lifecycle.Release();
            }
            try
            {
                ErrorReceived?.Invoke(session.Id, exception);
            }
            catch (Exception handlerException)
            {
                LoggerService.LogException("音声入出力のエラー通知に失敗しました。", handlerException);
            }
        });
    }

    public async Task StopAsync()
    {
        // 停止待ち中も未送出音声を直ちに破棄する。
        Volatile.Read(ref _session)?.Deactivate();
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session is null) return;
        session.Deactivate();
        await Task.Run(session.Dispose).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Volatile.Read(ref _session)?.Deactivate();
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            // 再入する停止要求も安全に完了させるため semaphore は破棄しない。
            _lifecycle.Release();
        }
    }

    private sealed class Session(long id, string microphoneId, string outputId) : IDisposable
    {
        private int _active = 1;
        private MMDevice? _microphone;
        private MMDevice? _outputDevice;
        public long Id { get; } = id;
        public string MicrophoneId { get; } = microphoneId;
        public string OutputId { get; } = outputId;
        public bool Active => Volatile.Read(ref _active) != 0;
        public WasapiRecorder? Capture { get; private set; }
        public WasapiPlayer? Player { get; private set; }
        public WaveFormat? CaptureFormat { get; private set; }
        public OutputBuffer? Output { get; private set; }

        public void Initialize()
        {
            using var enumerator = new MMDeviceEnumerator();
            _microphone = enumerator.GetDevice(MicrophoneId);
            _outputDevice = enumerator.GetDevice(OutputId);
            if (_microphone.State != DeviceState.Active || _microphone.DataFlow != DataFlow.Capture
                || WaveLinkDeviceIdentity.Classify(_microphone) != VoiceAudioDeviceKind.Microphone)
                throw new InvalidOperationException("選択したマイクが使用できません。物理マイクを選び直してください。");
            if (_outputDevice.State != DeviceState.Active || _outputDevice.DataFlow != DataFlow.Render
                || WaveLinkDeviceIdentity.Classify(_outputDevice) != VoiceAudioDeviceKind.WaveLinkOutput)
                throw new InvalidOperationException("選択した出力が使用できません。Wave Link の再生チャンネルを選んでください。");

            Capture = new WasapiRecorderBuilder().WithDevice(_microphone).WithSharedMode().WithBufferLength(20).Build();
            CaptureFormat = Capture.WaveFormat;
            Player = new WasapiPlayerBuilder().WithDevice(_outputDevice).WithSharedMode().WithLatency(20).Build();
            Output = new OutputBuffer(Player.DeviceMixFormat.SampleRate);
            // shared mode の音声エンジンが mono float から endpoint mix format へ適合させる。
            Player.Init(Output);
        }

        public bool Deactivate()
        {
            var wasActive = Interlocked.Exchange(ref _active, 0) != 0;
            Output?.Mute();
            return wasActive;
        }

        public void Dispose()
        {
            Deactivate();
            try
            {
                try
                {
                    Capture?.StopRecording();
                }
                finally
                {
                    Capture?.Dispose();
                }
            }
            finally
            {
                try
                {
                    Player?.Dispose();
                }
                finally
                {
                    _microphone?.Dispose();
                    _outputDevice?.Dispose();
                }
            }
        }
    }

    /// <summary>最大2秒。無音継続読み出しと書き込み・clear の順序を単一ロックで保証する。</summary>
    private sealed class OutputBuffer(int sampleRate) : IWaveProvider
    {
        private readonly object _sync = new();
        private readonly float[] _buffer = new float[sampleRate * 2];
        private int _start;
        private int _count;
        private bool _muted;
        private int _inputSampleRate;
        private StreamingResampler? _resampler;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

        public void Append(byte[] pcm16, int inputSampleRate)
        {
            lock (_sync)
            {
                if (_muted) return;
                // 大きすぎる受信を変換前に拒否し、変換用配列も上限以内に保つ。
                if ((long)pcm16.Length * sampleRate / (sizeof(short) * inputSampleRate) > _buffer.Length - _count)
                    throw new IOException("音声出力が2秒の上限を超えたため、仮想マイクへの出力を停止しました。");
                if (_inputSampleRate != inputSampleRate)
                {
                    if (_count != 0)
                        throw new IOException("再生中に音声のサンプリングレートが変更されました。");
                    _inputSampleRate = inputSampleRate;
                    _resampler = inputSampleRate == sampleRate ? null : new StreamingResampler(inputSampleRate, sampleRate);
                }
                var samples = new float[pcm16.Length / sizeof(short)];
                for (var i = 0; i < samples.Length; i++)
                    samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm16.AsSpan(i * sizeof(short))) / 32768f;
                if (_resampler is not null) samples = _resampler.Resample(samples);
                if (samples.Length > _buffer.Length - _count)
                    throw new IOException("音声出力バッファの上限を超えました。");
                foreach (var sample in samples)
                    _buffer[(_start + _count++) % _buffer.Length] = sample;
            }
        }

        public int Read(Span<byte> buffer)
        {
            lock (_sync)
            {
                buffer.Clear();
                var samples = MemoryMarshal.Cast<byte, float>(buffer);
                var available = Math.Min(samples.Length, _count);
                for (var i = 0; i < available; i++)
                    samples[i] = _buffer[(_start + i) % _buffer.Length];
                _start = (_start + available) % _buffer.Length;
                _count -= available;
                return buffer.Length;
            }
        }

        public void Clear()
        {
            lock (_sync)
            {
                _start = 0;
                _count = 0;
                _inputSampleRate = 0;
                _resampler = null;
            }
        }

        public void Mute()
        {
            lock (_sync)
            {
                _muted = true;
                _start = 0;
                _count = 0;
                _resampler = null;
            }
        }
    }
}
