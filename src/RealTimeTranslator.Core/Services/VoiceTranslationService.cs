using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.Core.Services;

/// <summary>字幕用の接続や DSP 状態を共有せず、マイクから翻訳音声を送信する。</summary>
public sealed partial class VoiceTranslationService : IVoiceTranslationService
{
    private readonly IVoiceAudioService _audio;
    private readonly Func<IRealtimeSpeechTranslator> _createClient;
    private readonly Func<int, bool> _isKeyDown;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Lock _routing = new();
    private readonly StringBuilder _transcript = new();
    private Channel<QueuedMicrophoneFrame>? _frames;
    private CancellationTokenSource? _sessionCancellation;
    private Task? _processing;
    private IRealtimeSpeechTranslator? _client;
    private VoiceTranslationSettings _settings = new();
    private OpenAIRealtimeSettings _connectionSettings = new();
    private int _state;
    private int _mode = (int)VoiceSendMode.Muted;
    private int _epoch;
    private int _faultScheduled;
    private long _audioSessionBaseline;
    private int _disposed;
    private float _outputLevel;

    public VoiceTranslationService(IVoiceAudioService audio,
        Func<IRealtimeSpeechTranslator> createClient, Func<int, bool>? isKeyDown = null)
    {
        _audio = audio;
        _createClient = createClient;
        _isKeyDown = isKeyDown ?? (key => (GetAsyncKeyState(key) & 0x8000) != 0);
        _audio.MicrophoneFrame += OnMicrophoneFrame;
        _audio.ErrorReceived += OnAudioError;
    }

    public VoiceTranslationState State => (VoiceTranslationState)Volatile.Read(ref _state);
    public VoiceSendMode Mode => (VoiceSendMode)Volatile.Read(ref _mode);
    public event Action<VoiceTranslationState>? StateChanged;
    public event Action<string>? TranscriptReceived;
    public event Action<float, float>? LevelsChanged;
    public event Action<Exception>? ErrorReceived;

    public async Task StartAsync(VoiceTranslationSettings settings, OpenAIRealtimeSettings openAi,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (State == VoiceTranslationState.Running) return;
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentNullException.ThrowIfNull(openAi);
            if (string.IsNullOrWhiteSpace(settings.InputDeviceId)
                || string.IsNullOrWhiteSpace(settings.OutputDeviceId))
                throw new InvalidOperationException("入力マイクと Wave Link の翻訳音声用チャンネルを選択してください。");
            if (!Enum.IsDefined(settings.Mode)) throw new ArgumentException("送信モードが不正です。", nameof(settings));
            if (settings.PushToTalkEnabled && settings.PushToTalkVirtualKey is < 0x75 or > 0x7B)
                throw new ArgumentException("発話キーは F6〜F12 から選択してください。", nameof(settings));

            _settings = settings.Clone();
            _connectionSettings = new OpenAIRealtimeSettings
            {
                ApiKey = openAi.ApiKey,
                Endpoint = openAi.Endpoint,
                Model = openAi.Model,
                OutputLanguage = string.IsNullOrWhiteSpace(settings.OutputLanguage) ? "en" : settings.OutputLanguage,
                // 再接続で保留音声を流さず、障害時は明示的な再開始を要求する。
                MaxReconnectAttempts = 0
            };
            lock (_routing)
            {
                _sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _audioSessionBaseline = _audio.SessionId;
                _faultScheduled = 0;
            }
            _frames = Channel.CreateBounded<QueuedMicrophoneFrame>(new BoundedChannelOptions(24)
            {
                SingleReader = false,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
            _transcript.Clear();
            SetState(VoiceTranslationState.Starting);
            if (_settings.Mode == VoiceSendMode.Translated)
                await ConnectClientAsync(_sessionCancellation.Token).ConfigureAwait(false);
            await _audio.StartAsync(_settings.InputDeviceId, _settings.OutputDeviceId,
                _sessionCancellation.Token).ConfigureAwait(false);
            lock (_routing) _mode = (int)_settings.Mode;
            SetState(VoiceTranslationState.Running);
            var reader = _frames.Reader;
            var token = _sessionCancellation.Token;
            _processing = Task.Run(() => ProcessAsync(reader, token), CancellationToken.None);
        }
        catch
        {
            await StopCoreAsync().ConfigureAwait(false);
            SetState(VoiceTranslationState.Faulted);
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    public async Task SetModeAsync(VoiceSendMode mode, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (State != VoiceTranslationState.Running) return;
            if (Mode == mode) return;
            var oldClient = MuteRouting();
            if (oldClient is not null) await oldClient.DisposeAsync().ConfigureAwait(false);
            _transcript.Clear();
            TranscriptReceived?.Invoke(string.Empty);
            if (mode == VoiceSendMode.Translated)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _sessionCancellation!.Token);
                await ConnectClientAsync(linked.Token).ConfigureAwait(false);
            }
            lock (_routing)
            {
                _sessionCancellation!.Token.ThrowIfCancellationRequested();
                _mode = (int)mode;
                _epoch++;
            }
        }
        catch
        {
            var failedClient = MuteRouting();
            if (failedClient is not null) await failedClient.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync()
    {
        // 接続待ち中でも、停止操作は出力とマイク送信をただちに閉じる。
        var client = MuteRouting();
        CancelSession();
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            SetState(VoiceTranslationState.Stopping);
            if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
            await StopCoreAsync().ConfigureAwait(false);
            SetState(VoiceTranslationState.Stopped);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task ConnectClientAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_connectionSettings.ApiKey))
            throw new InvalidOperationException("API設定タブで OpenAI API キーを入力してください。");
        var client = _createClient();
        client.TranslatedAudioReceived += pcm => OnTranslatedAudio(client, pcm);
        client.TranscriptDeltaReceived += delta => OnTranscript(client, delta, false);
        client.TranscriptCompleted += text => OnTranscript(client, text, true);
        client.ErrorReceived += error => OnClientError(client, error);
        client.StateChanged += state =>
        {
            if (state is ConnectionState.Disconnected or ConnectionState.Failed or ConnectionState.Reconnecting)
                OnClientError(client, new IOException("ボイス通訳の接続が切れました。再開始してください。"));
        };
        try
        {
            lock (_routing) _client = client;
            await client.ConnectAsync(_connectionSettings, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (client.State != ConnectionState.Connected || Volatile.Read(ref _faultScheduled) != 0)
                throw new IOException("ボイス通訳の接続を開始できませんでした。再開始してください。");
        }
        catch
        {
            lock (_routing) { if (ReferenceEquals(_client, client)) _client = null; }
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void OnMicrophoneFrame(VoiceMicrophoneFrame frame)
    {
        var channel = Volatile.Read(ref _frames);
        if (channel is null) return;
        try
        {
            // 処理待ちの間にキーやモードが変わっても、取得時の送信許可を保つ。
            var epoch = Volatile.Read(ref _epoch);
            var keyOpen = !_settings.PushToTalkEnabled || _isKeyDown(_settings.PushToTalkVirtualKey);
            if (!channel.Writer.TryWrite(new QueuedMicrophoneFrame(frame, keyOpen, epoch)))
                ScheduleFault(new IOException("マイクの処理が追いつかないため、音声送信を停止しました。"));
        }
        catch (Exception ex) { ScheduleFault(ex); }
    }

    private async Task ProcessAsync(ChannelReader<QueuedMicrophoneFrame> reader, CancellationToken ct)
    {
        StreamingResampler? resampler = null;
        var inputRate = 0;
        var previousEpoch = -1;
        var previousKeyOpen = false;
        var lastMeter = Environment.TickCount64;
        try
        {
            await foreach (var queued in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var frame = queued.Frame;
                if (frame.SampleRate is < 8000 or > 384000 || frame.Samples.Length > frame.SampleRate * 2)
                    throw new InvalidDataException("マイクの音声形式またはチャンク長が不正です。");
                var epoch = Volatile.Read(ref _epoch);
                if (queued.Epoch != epoch) continue;
                if (previousEpoch != epoch || inputRate != frame.SampleRate)
                {
                    inputRate = frame.SampleRate;
                    resampler = new StreamingResampler(inputRate, 24000);
                    previousEpoch = epoch;
                }
                var inputLevel = Peak(frame.Samples);
                var keyOpen = queued.KeyOpen;
                var input = keyOpen ? frame.Samples : new float[frame.Samples.Length];
                var samples = resampler!.Resample(input);
                var pcm = AudioFormatConverter.Float32ToPcm16(samples);
                // リサンプラの先読み履歴にも実音が残るので、閉鎖中の送信は厳密に無音にする。
                if (!keyOpen) Array.Clear(pcm);
                lock (_routing)
                {
                    if (epoch != _epoch || State != VoiceTranslationState.Running) continue;
                    if (Mode == VoiceSendMode.Original)
                    {
                        if (!keyOpen && previousKeyOpen) _audio.ClearOutput();
                        _audio.WriteOutput(pcm, 24000);
                        _outputLevel = keyOpen ? Peak(samples) : 0;
                    }
                    else if (Mode == VoiceSendMode.Translated)
                    {
                        // 発話キーを離した後も無音を送り、翻訳の末尾を押し出す。
                        _client?.SendAudio(pcm);
                        if (_client is { DroppedAudioChunkCount: > 0 })
                            throw new IOException("翻訳音声の送信が遅延したため停止しました。再開始してください。");
                    }
                }
                previousKeyOpen = keyOpen;
                if (Environment.TickCount64 - lastMeter >= 100)
                {
                    lastMeter = Environment.TickCount64;
                    LevelsChanged?.Invoke(inputLevel, Volatile.Read(ref _outputLevel));
                    Volatile.Write(ref _outputLevel, 0);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { ScheduleFault(ex); }
    }

    private void OnTranslatedAudio(IRealtimeSpeechTranslator client, byte[] pcm)
    {
        try
        {
            lock (_routing)
            {
                if (!ReferenceEquals(_client, client) || Mode != VoiceSendMode.Translated
                    || State != VoiceTranslationState.Running) return;
                _audio.WriteOutput(pcm, client.OutputSampleRate);
                _outputLevel = Math.Max(_outputLevel, PcmPeak(pcm));
            }
        }
        catch (Exception ex) { ScheduleFault(ex); }
    }

    private void OnTranscript(IRealtimeSpeechTranslator client, string text, bool completed)
    {
        string snapshot;
        lock (_routing)
        {
            if (!ReferenceEquals(_client, client) || Mode != VoiceSendMode.Translated) return;
            // response.done の空通知は確定境界だけを表すため、表示中の訳文を保持する。
            if (completed && string.IsNullOrEmpty(text)) return;
            if (completed) _transcript.Clear();
            _transcript.Append(text);
            if (_transcript.Length > 2000) _transcript.Remove(0, _transcript.Length - 2000);
            snapshot = _transcript.ToString();
        }
        TranscriptReceived?.Invoke(snapshot);
    }

    private void OnAudioError(long sessionId, Exception error)
    {
        CancellationTokenSource? session;
        lock (_routing)
        {
            session = _sessionCancellation;
            // 新しい API 接続を待っている間の、前回の機器停止通知も受け付けない。
            if (session is null || sessionId <= _audioSessionBaseline || sessionId != _audio.SessionId) return;
        }
        ScheduleFault(error, session);
    }

    private void OnClientError(IRealtimeSpeechTranslator client, Exception error)
    {
        CancellationTokenSource? session;
        lock (_routing)
        {
            if (!ReferenceEquals(_client, client)) return;
            session = _sessionCancellation;
        }
        if (session is not null) ScheduleFault(error, session);
    }

    private void ScheduleFault(Exception error, CancellationTokenSource? expectedSession = null)
    {
        CancellationTokenSource? session;
        lock (_routing)
        {
            session = expectedSession ?? _sessionCancellation;
            if (session is null || !ReferenceEquals(session, _sessionCancellation) || _faultScheduled != 0) return;
            _faultScheduled = 1;
        }
        _ = Task.Run(async () =>
        {
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(session, _sessionCancellation)) return;
                await StopCoreAsync().ConfigureAwait(false);
                SetState(VoiceTranslationState.Faulted);
                ErrorReceived?.Invoke(error);
            }
            finally { _lifecycle.Release(); }
        });
    }

    private IRealtimeSpeechTranslator? MuteRouting()
    {
        lock (_routing)
        {
            _mode = (int)VoiceSendMode.Muted;
            _epoch++;
            _outputLevel = 0;
            _audio.ClearOutput();
            while (_frames?.Reader.TryRead(out _) == true) { }
            var client = _client;
            _client = null;
            return client;
        }
    }

    private void CancelSession()
    {
        try { Volatile.Read(ref _sessionCancellation)?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task StopCoreAsync()
    {
        var client = MuteRouting();
        CancelSession();
        var channel = Interlocked.Exchange(ref _frames, null);
        channel?.Writer.TryComplete();
        try { await _audio.StopAsync().ConfigureAwait(false); }
        finally
        {
            if (_processing is not null) await _processing.ConfigureAwait(false);
            _processing = null;
            if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
            CancellationTokenSource? ended;
            lock (_routing) { ended = _sessionCancellation; _sessionCancellation = null; }
            ended?.Dispose();
            LevelsChanged?.Invoke(0, 0);
        }
    }

    private void SetState(VoiceTranslationState state)
    {
        Volatile.Write(ref _state, (int)state);
        StateChanged?.Invoke(state);
    }

    private static float Peak(float[] samples)
    {
        float peak = 0;
        foreach (var sample in samples) if (float.IsFinite(sample)) peak = Math.Max(peak, Math.Abs(sample));
        return Math.Min(peak, 1);
    }

    private static float PcmPeak(byte[] pcm)
    {
        float peak = 0;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
            peak = Math.Max(peak, Math.Abs(System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i, 2)) / 32768f));
        return peak;
    }

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int virtualKey);

    private readonly record struct QueuedMicrophoneFrame(VoiceMicrophoneFrame Frame, bool KeyOpen, int Epoch);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopAsync().ConfigureAwait(false);
        _audio.MicrophoneFrame -= OnMicrophoneFrame;
        _audio.ErrorReceived -= OnAudioError;
    }
}
