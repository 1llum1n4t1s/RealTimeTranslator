using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.Core.Services;

/// <summary>訳音声を明示的に選択した通常の再生機器で試聴する。</summary>
public sealed class VoicePlaybackService : IVoicePlaybackService
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private Session? _session;
    private long _sessionId;
    private bool _disposed;

    public long SessionId => Interlocked.Read(ref _sessionId);
    public event Action<long, Exception>? ErrorReceived;

    public IReadOnlyList<VoiceAudioDevice> GetOutputs()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = new List<VoiceAudioDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                if (IsPlaybackDevice(device))
                    devices.Add(new VoiceAudioDevice(device.ID, device.FriendlyName, false));
            }
        }
        return devices.OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static bool IsPlaybackDevice(MMDevice device) =>
        device.State == DeviceState.Active && device.DataFlow == DataFlow.Render
        && !WaveLinkDeviceIdentity.IsWaveLinkAdapter(device);

    public async Task StartAsync(string deviceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var current = Volatile.Read(ref _session);
            if (current is { Active: true } && current.DeviceId == deviceId)
                return;

            await StopCoreAsync().ConfigureAwait(false);
            // WASAPI の停止通知が UI の SynchronizationContext を捕捉しない場所で構築する。
            await Task.Run(() =>
            {
                var session = new Session(Interlocked.Increment(ref _sessionId), deviceId);
                try
                {
                    session.Initialize();
                    session.Player!.PlaybackStopped += (_, args) =>
                        Fault(session, args.Exception ?? new IOException("訳音声の試聴が停止しました。"));
                    Volatile.Write(ref _session, session);
                    ct.ThrowIfCancellationRequested();
                    session.Player.Play();
                    ct.ThrowIfCancellationRequested();
                    if (!session.Active)
                        throw new IOException("訳音声の試聴を開始できませんでした。");
                }
                catch
                {
                    Interlocked.CompareExchange(ref _session, null, session);
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

    public void Write(byte[] pcm16, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(pcm16);
        if (sampleRate is < 8000 or > 192000)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (pcm16.Length % sizeof(short) != 0)
            throw new ArgumentException("PCM16 音声はサンプル境界に揃えてください。", nameof(pcm16));

        var session = Volatile.Read(ref _session);
        if (session is null || !session.Active || pcm16.Length == 0)
            return;
        try
        {
            session.Output!.Append(pcm16, sampleRate);
        }
        catch (Exception exception)
        {
            Fault(session, exception);
        }
    }

    public void Clear() => Volatile.Read(ref _session)?.Output?.Clear();

    private void Fault(Session session, Exception exception)
    {
        if (!session.Deactivate())
            return;
        // 再生スレッド上で破棄すると自身を待つため、停止と通知は別タスクで行う。
        _ = Task.Run(async () =>
        {
            var notify = false;
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(Volatile.Read(ref _session), session))
                {
                    notify = true;
                    await StopCoreAsync().ConfigureAwait(false);
                }
            }
            catch (Exception stopException)
            {
                exception = new AggregateException("試聴の停止処理にも失敗しました。", exception, stopException);
            }
            finally
            {
                _lifecycle.Release();
            }
            // 通知の購読者はこの識別子を確認し、新しい試聴セッションへ適用しない。
            if (!notify || Volatile.Read(ref _disposed) || SessionId != session.Id)
                return;
            try
            {
                ErrorReceived?.Invoke(session.Id, exception);
            }
            catch (Exception handlerException)
            {
                LoggerService.LogException("試聴のエラー通知に失敗しました。", handlerException);
            }
        });
    }

    public async Task StopAsync()
    {
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
        if (session is null)
            return;
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
            // 通知の購読者からの Stop / Dispose 再入も安全に完了させる。
            _lifecycle.Release();
        }
    }

    private sealed class Session(long id, string deviceId) : IDisposable
    {
        private int _active = 1;
        private MMDevice? _device;
        public long Id { get; } = id;
        public string DeviceId { get; } = deviceId;
        public bool Active => Volatile.Read(ref _active) != 0;
        public WasapiPlayer? Player { get; private set; }
        public OutputBuffer? Output { get; private set; }

        public void Initialize()
        {
            using var enumerator = new MMDeviceEnumerator();
            _device = enumerator.GetDevice(DeviceId);
            if (!IsPlaybackDevice(_device))
                throw new InvalidOperationException("選択した試聴機器が使用できません。ヘッドホンまたはスピーカーを選び直してください。");
            Player = new WasapiPlayerBuilder().WithDevice(_device).WithSharedMode().WithLatency(20).Build();
            Output = new OutputBuffer(Player.DeviceMixFormat.SampleRate);
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
                Player?.Dispose();
            }
            finally
            {
                _device?.Dispose();
            }
        }
    }

    /// <summary>PCM16 mono を機器の周波数へ変換し、最大2秒だけ保持する。</summary>
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
                if (_muted)
                    return;
                if ((long)pcm16.Length * sampleRate / (sizeof(short) * inputSampleRate) > _buffer.Length - _count)
                    throw new IOException("試聴音声が2秒の上限を超えたため、試聴を停止しました。");
                if (_inputSampleRate != inputSampleRate)
                {
                    if (_count != 0)
                        throw new IOException("試聴中に音声のサンプリングレートが変更されました。");
                    _inputSampleRate = inputSampleRate;
                    _resampler = inputSampleRate == sampleRate ? null : new StreamingResampler(inputSampleRate, sampleRate);
                }
                var samples = new float[pcm16.Length / sizeof(short)];
                for (var i = 0; i < samples.Length; i++)
                    samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm16.AsSpan(i * sizeof(short))) / 32768f;
                if (_resampler is not null)
                    samples = _resampler.Resample(samples);
                if (samples.Length > _buffer.Length - _count)
                    throw new IOException("試聴音声バッファの上限を超えました。");
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
