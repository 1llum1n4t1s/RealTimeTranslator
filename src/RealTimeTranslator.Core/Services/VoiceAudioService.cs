using System.Buffers;
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
    private readonly Lock _configuration = new();
    private readonly Func<VoiceTranslationSettings, IGamePushToTalkController> _createGamePtt;
    private VoiceTranslationSettings _gamePttSettings = new();
    private Session? _session;
    private long _sessionId;
    private bool _disposed;

    public VoiceAudioService() : this(settings => new WindowsGamePushToTalkController(
        settings.GamePushToTalkVirtualKey, settings.GamePushToTalkModifiers)) { }

    public VoiceAudioService(Func<VoiceTranslationSettings, IGamePushToTalkController> createGamePtt)
        => _createGamePtt = createGamePtt ?? throw new ArgumentNullException(nameof(createGamePtt));

    public void ConfigureGamePushToTalk(VoiceTranslationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_configuration)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Volatile.Read(ref _session) is { Active: true })
                throw new InvalidOperationException("ゲーム送信用PTTの設定は停止中に変更してください。");
            _gamePttSettings = settings.Clone();
        }
    }

    public bool? GetPhysicalPushToTalkKeyState(int virtualKey)
    {
        var session = Volatile.Read(ref _session);
        return session is { Active: true } ? session.GamePtt?.IsPhysicalKeyDown(virtualKey) : null;
    }

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
                    VoiceTranslationSettings gamePttSettings;
                    lock (_configuration) gamePttSettings = _gamePttSettings.Clone();
                    if (gamePttSettings.GamePushToTalkEnabled)
                        session.ConfigureGamePtt(() => _createGamePtt(gamePttSettings), error =>
                        {
                            if (session.Active) Fault(session, error);
                            else LoggerService.LogException("ゲーム送信用PTTキーの解放に失敗しました。", error);
                        });
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
        byte[]? owned = null;
        try
        {
            // ネイティブバッファを即コピーし、変換後の配列もフレーム専用にする。
            var activeLength = buffer.Length;
            owned = ArrayPool<byte>.Shared.Rent(activeLength);
            buffer.CopyTo(owned);
            var format = session.CaptureFormat!;
            var frames = activeLength / format.BlockAlign;
            var mono = new float[frames];
            if ((flags & AudioClientBufferFlags.Silent) == 0)
            {
                using var stream = new RawSourceWaveStream(new MemoryStream(owned, 0, activeLength, false), format);
                var provider = stream.ToSampleProvider();
                int sampleCount = frames * format.Channels;
                var interleaved = ArrayPool<float>.Shared.Rent(sampleCount);
                try
                {
                    var count = provider.Read(interleaved.AsSpan(0, sampleCount));
                    for (var frame = 0; frame < count / format.Channels; frame++)
                    {
                        float sum = 0;
                        for (var channel = 0; channel < format.Channels; channel++)
                            sum += interleaved[frame * format.Channels + channel];
                        mono[frame] = sum / format.Channels;
                    }
                }
                finally { ArrayPool<float>.Shared.Return(interleaved, clearArray: true); }
            }
            if (session.Active)
                MicrophoneFrame?.Invoke(new VoiceMicrophoneFrame(mono, format.SampleRate));
        }
        catch (Exception exception)
        {
            Fault(session, exception);
        }
        finally
        {
            if (owned is not null) ArrayPool<byte>.Shared.Return(owned);
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
    public void ClearPendingOutput() => Volatile.Read(ref _session)?.Output?.ClearPendingOutput();

    private void Fault(Session session, Exception exception)
    {
        if (!session.Deactivate()) return;
        var outputDiagnostics = exception.Data;
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
                try
                {
                    var details = outputDiagnostics.Contains("OutputQueuedMilliseconds")
                        ? $" queuedMs={outputDiagnostics["OutputQueuedMilliseconds"]:F1} incomingMs={outputDiagnostics["OutputIncomingMilliseconds"]:F1}"
                            + $" queuedNonSilentSamples={outputDiagnostics["OutputQueuedNonSilentSamples"]} queuedPeak={outputDiagnostics["OutputQueuedPeak"]:F6}"
                            + $" pcmPeak={outputDiagnostics["OutputInputPeak"]:F6} silent={outputDiagnostics["OutputInputSilent"]}"
                            + $" readCalls={outputDiagnostics["OutputReadCalls"]} consumedSamples={outputDiagnostics["OutputReadSamples"]}"
                            + $" lastReadAgeMs={outputDiagnostics["OutputLastReadAgeMilliseconds"]:F1}"
                            + $" outputRate={outputDiagnostics["OutputSampleRate"]} inputRate={outputDiagnostics["OutputInputSampleRate"]}"
                        : string.Empty;
                    LoggerService.LogException($"ボイス通訳の音声入出力を停止: session={session.Id}{details}", exception);
                }
                finally
                {
                    // 診断ログが失敗しても、停止状態と利用者へのエラー通知は維持する。
                    ErrorReceived?.Invoke(session.Id, exception);
                }
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
        public IGamePushToTalkController? GamePtt { get; private set; }
        private Func<IGamePushToTalkController>? _createGamePtt;
        private Action<Exception>? _gamePttError;

        public void ConfigureGamePtt(Func<IGamePushToTalkController> createGamePtt, Action<Exception> onError)
        {
            _createGamePtt = createGamePtt;
            _gamePttError = onError;
        }

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

            GamePtt = _createGamePtt?.Invoke();
            Capture = new WasapiRecorderBuilder().WithDevice(_microphone).WithSharedMode().WithBufferLength(20).Build();
            CaptureFormat = Capture.WaveFormat;
            Player = new WasapiPlayerBuilder().WithDevice(_outputDevice).WithSharedMode().WithLatency(20).Build();
            Output = new OutputBuffer(Player.DeviceMixFormat.SampleRate);
            if (GamePtt is not null) Output.ConfigureGamePtt(GamePtt, _gamePttError!);
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
                    try
                    {
                        _microphone?.Dispose();
                        _outputDevice?.Dispose();
                    }
                    finally
                    {
                        try { Output?.DisposeGamePtt(); }
                        finally { GamePtt?.Dispose(); }
                    }
                }
            }
        }
    }

    /// <summary>最大2秒。無音継続読み出しと書き込み・clear の順序を単一ロックで保証する。</summary>
    private sealed class OutputBuffer(int sampleRate) : IWaveProvider
    {
        private GamePushToTalkOutputGate? _gamePtt;
        public void ConfigureGamePtt(IGamePushToTalkController controller, Action<Exception> onError)
            => _gamePtt = new GamePushToTalkOutputGate(controller, sampleRate, onError);
        public void DisposeGamePtt() => _gamePtt?.Dispose();
        private readonly object _sync = new();
        private readonly float[] _buffer = new float[sampleRate * 2];
        private int _start;
        private int _count;
        private int _nonSilentSamples;
        private bool _muted;
        private int _inputSampleRate;
        private StreamingResampler? _resampler;
        private long _readCalls;
        private long _readSamples;
        private long _lastReadTimestamp;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);

        public void Append(byte[] pcm16, int inputSampleRate)
        {
            lock (_sync)
            {
                if (_muted) return;
                // 単一チャンクの上限は変換前に守る。待機分との合計は、無音とフィルタ末尾を判定してから確認する。
                if ((long)pcm16.Length * sampleRate / (sizeof(short) * inputSampleRate) > _buffer.Length)
                    throw CreateOverflowException("音声出力が2秒の上限を超えたため、仮想マイクへの出力を停止しました。", pcm16, inputSampleRate);
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
                // 末尾を押し出すため無音もリサンプルする。実音が残らない無音だけなら Read のゼロ補完へ任せる。
                // 既存キューは消さず、実音に続く間隔とフィルタの残留音は通常どおり保持する。
                if (_nonSilentSamples == 0 && samples.AsSpan().IndexOfAnyExcept(0f) < 0)
                    return;
                if (samples.Length > _buffer.Length - _count)
                    throw CreateOverflowException("音声出力が2秒の上限を超えたため、仮想マイクへの出力を停止しました。", pcm16, inputSampleRate);
                foreach (var sample in samples)
                {
                    _buffer[(_start + _count++) % _buffer.Length] = sample;
                    if (sample != 0f) _nonSilentSamples++;
                }
            }
        }

        // PCM 本文は保存せず、過負荷時だけ無音・受信量・再生の進捗を記録する。
        private IOException CreateOverflowException(string message, byte[] pcm16, int inputSampleRate)
        {
            int peak = 0;
            for (var i = 0; i < pcm16.Length; i += sizeof(short))
                peak = Math.Max(peak, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(pcm16.AsSpan(i, sizeof(short)))));
            var exception = new IOException(message);
            exception.Data["OutputQueuedMilliseconds"] = _count * 1000d / sampleRate;
            exception.Data["OutputQueuedNonSilentSamples"] = _nonSilentSamples;
            float queuedPeak = 0;
            for (var i = 0; i < _count; i++)
                queuedPeak = Math.Max(queuedPeak, Math.Abs(_buffer[(_start + i) % _buffer.Length]));
            exception.Data["OutputQueuedPeak"] = queuedPeak;
            exception.Data["OutputIncomingMilliseconds"] = pcm16.Length * 1000d / (sizeof(short) * inputSampleRate);
            exception.Data["OutputInputPeak"] = peak / 32768d;
            exception.Data["OutputInputSilent"] = peak == 0;
            exception.Data["OutputReadCalls"] = _readCalls;
            exception.Data["OutputReadSamples"] = _readSamples;
            exception.Data["OutputLastReadAgeMilliseconds"] = _lastReadTimestamp == 0
                ? -1d : Stopwatch.GetElapsedTime(_lastReadTimestamp).TotalMilliseconds;
            exception.Data["OutputSampleRate"] = sampleRate;
            exception.Data["OutputInputSampleRate"] = inputSampleRate;
            return exception;
        }

        public int Read(Span<byte> buffer)
        {
            lock (_sync)
            {
                buffer.Clear();
                var samples = MemoryMarshal.Cast<byte, float>(buffer);
                var available = Math.Min(samples.Length, _count);
                if (_gamePtt is not null)
                {
                    int lastSound = available - 1;
                    while (lastSound >= 0 && _buffer[(_start + lastSound) % _buffer.Length] == 0f) lastSound--;
                    if (lastSound >= 0 && !_gamePtt.TryRead(lastSound + 1))
                    {
                        _readCalls++;
                        _lastReadTimestamp = Stopwatch.GetTimestamp();
                        return buffer.Length;
                    }
                }
                for (var i = 0; i < available; i++)
                {
                    samples[i] = _buffer[(_start + i) % _buffer.Length];
                    if (samples[i] != 0f) _nonSilentSamples--;
                }
                _start = (_start + available) % _buffer.Length;
                _count -= available;
                _readCalls++;
                _readSamples += available;
                _lastReadTimestamp = Stopwatch.GetTimestamp();
                return buffer.Length;
            }
        }

        public void Clear() => ClearCore(preservePlayedTail: false);

        public void ClearPendingOutput() => ClearCore(preservePlayedTail: true);

        private void ClearCore(bool preservePlayedTail)
        {
            lock (_sync)
            {
                _start = 0;
                _count = 0;
                _nonSilentSamples = 0;
                _inputSampleRate = 0;
                _resampler = null;
                if (preservePlayedTail) _gamePtt?.ClearPendingOutput();
                else _gamePtt?.Clear();
            }
        }

        public void Mute()
        {
            lock (_sync)
            {
                _muted = true;
                _start = 0;
                _count = 0;
                _nonSilentSamples = 0;
                _resampler = null;
                _gamePtt?.Clear();
            }
        }
    }
}
