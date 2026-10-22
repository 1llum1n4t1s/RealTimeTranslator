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
    private readonly IVoicePlaybackService? _playback;
    private readonly Func<IVoiceActivityDetector> _vadFactory;
    private MicrophoneVadGate? _voiceVad;
    private int _monitoring;
    private long _monitorSessionId;
    private bool _monitorStarting;
    private long _monitorStartBaseline;
    private Exception? _monitorStartError;
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
        Func<IRealtimeSpeechTranslator> createClient, Func<int, bool>? isKeyDown = null,
        IVoicePlaybackService? playback = null, Func<IVoiceActivityDetector>? vadFactory = null)
    {
        _audio = audio;
        _createClient = createClient;
        _isKeyDown = isKeyDown ?? (key => (GetAsyncKeyState(key) & 0x8000) != 0);
        _playback = playback;
        _vadFactory = vadFactory ?? (() => new FireRedVadDetector());
        if (_playback is not null) _playback.ErrorReceived += OnMonitoringError;
        _audio.MicrophoneFrame += OnMicrophoneFrame;
        _audio.ErrorReceived += OnAudioError;
    }

    public VoiceTranslationState State => (VoiceTranslationState)Volatile.Read(ref _state);
    public VoiceSendMode Mode => (VoiceSendMode)Volatile.Read(ref _mode);
    public bool IsMonitoring => Volatile.Read(ref _monitoring) != 0;
    public event Action<VoiceTranslationState>? StateChanged;
    public event Action<string>? TranscriptReceived;
    public event Action<float, float>? LevelsChanged;
    public event Action<Exception>? ErrorReceived;
    public event Action<bool>? MonitoringChanged;
    public event Action<Exception>? MonitoringErrorReceived;

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
            if (settings.PushToTalkEnabled && (!VoicePushToTalkShortcut.IsSupportedKey(settings.PushToTalkVirtualKey)
                || !VoicePushToTalkShortcut.IsSupportedModifiers(settings.PushToTalkModifiers)))
                throw new ArgumentException("発話キーを登録し直してください。", nameof(settings));
            if (settings.GamePushToTalkEnabled)
            {
                if (!VoicePushToTalkShortcut.IsSupportedKey(settings.GamePushToTalkVirtualKey)
                    || !VoicePushToTalkShortcut.IsSupportedModifiers(settings.GamePushToTalkModifiers))
                    throw new ArgumentException("ゲーム送信用PTTキーを登録し直してください。", nameof(settings));
                if (settings.PushToTalkEnabled && settings.PushToTalkVirtualKey == settings.GamePushToTalkVirtualKey)
                    throw new ArgumentException("発話キーとゲーム送信用PTTには、別の主キーを登録してください。", nameof(settings));
            }

            _settings = settings.Clone();
            _settings.NormalizeVadSettings();
            _audio.ConfigureGamePushToTalk(_settings.Clone());
            SetGains(_settings.InputGainDb, _settings.OutputGainDb);
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
            {
                InitializeVoiceVad(_sessionCancellation.Token);
                await ConnectClientAsync(_sessionCancellation.Token).ConfigureAwait(false);
            }
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
        CancellationTokenSource? session = null;
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (State != VoiceTranslationState.Running) return;
            if (Mode == mode) return;
            session = _sessionCancellation;
            var oldClient = MuteRouting();
            await StopMonitoringCoreAsync().ConfigureAwait(false);
            if (oldClient is not null) await oldClient.DisposeAsync().ConfigureAwait(false);
            _transcript.Clear();
            TranscriptReceived?.Invoke(string.Empty);
            if (mode == VoiceSendMode.Translated)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _sessionCancellation!.Token);
                InitializeVoiceVad(linked.Token);
                await ConnectClientAsync(linked.Token).ConfigureAwait(false);
            }
            lock (_routing)
            {
                _sessionCancellation!.Token.ThrowIfCancellationRequested();
                _mode = (int)mode;
                _epoch++;
            }
        }
        catch (Exception ex)
        {
            try
            {
                var failedClient = MuteRouting();
                if (failedClient is not null) await failedClient.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                // 停止操作のキャンセルは正常終了に任せ、切替失敗は通知の有無によらず停止する。
                if (session is not null && !session.IsCancellationRequested) ScheduleFault(ex, session);
            }
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
            ScheduleSessionRefresh(client, ct);
        }
        catch
        {
            bool ownsClient;
            lock (_routing)
            {
                ownsClient = ReferenceEquals(_client, client);
                if (ownsClient) _client = null;
            }
            // 接続待ちを止めた StopAsync が取得済みなら、その呼出元が破棄する。
            if (ownsClient) await client.DisposeAsync().ConfigureAwait(false);
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
            var keyOpen = !_settings.PushToTalkEnabled || IsPushToTalkDown();
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
        int? failedEpoch = null;
        var previousKeyOpen = false;
        var lastMeter = Environment.TickCount64;
        float inputMeterPeak = 0;
        try
        {
            await foreach (var queued in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var epoch = queued.Epoch;
                MicrophoneVadGate? vad;
                lock (_routing)
                {
                    if (epoch != _epoch || failedEpoch == epoch || State != VoiceTranslationState.Running) continue;
                    vad = Mode == VoiceSendMode.Translated ? _voiceVad : null;
                }
                try
                {
                    var frame = queued.Frame;
                    if (frame.SampleRate is < 8000 or > 384000 || frame.Samples.Length > frame.SampleRate * 2)
                        throw new InvalidDataException("マイクの音声形式またはチャンク長が不正です。");
                    if (previousEpoch != epoch || inputRate != frame.SampleRate)
                    {
                        inputRate = frame.SampleRate;
                        resampler = new StreamingResampler(inputRate, 24000);
                        previousEpoch = epoch;
                    }
                    var inputGain = Volatile.Read(ref _inputGain);
                    inputMeterPeak = Math.Max(inputMeterPeak, Math.Min(Peak(frame.Samples) * inputGain, 1f));
                    var keyOpen = queued.KeyOpen;
                    var input = keyOpen ? frame.Samples : new float[frame.Samples.Length];
                    if (keyOpen && inputGain != 1f)
                    {
                        input = (float[])frame.Samples.Clone();
                        for (var i = 0; i < input.Length; i++) input[i] *= inputGain;
                    }
                    var pcm = vad is not null
                        ? vad.Process(input, frame.SampleRate, keyOpen, epoch)
                        : AudioFormatConverter.Float32ToPcm16(resampler!.Resample(input));
                    // リサンプラの先読み履歴にも実音が残るので、閉鎖中の送信は厳密に無音にする。
                    if (!keyOpen) Array.Clear(pcm);
                    lock (_routing)
                    {
                        if (epoch != _epoch || State != VoiceTranslationState.Running) continue;
                        if (Mode == VoiceSendMode.Original)
                        {
                            if (!keyOpen && previousKeyOpen) _audio.ClearPendingOutput();
                            var output = ApplyOutputGain(pcm);
                            _audio.WriteOutput(output, 24000);
                            _outputLevel = Math.Max(_outputLevel, PcmPeak(output));
                        }
                        else if (Mode == VoiceSendMode.Translated)
                        {
                            // VAD 有効時は発話後の padding だけを送り、待機中も接続は保つ。
                            if (pcm.Length > 0) _client?.SendAudio(pcm);
                            if (_client is { DroppedAudioChunkCount: > 0 })
                                throw new IOException("翻訳音声の送信が遅延したため停止しました。再開始してください。");
                        }
                    }
                    previousKeyOpen = keyOpen;
                    if (Environment.TickCount64 - lastMeter >= 100)
                    {
                        lastMeter = Environment.TickCount64;
                        LevelsChanged?.Invoke(inputMeterPeak, Interlocked.Exchange(ref _outputLevel, 0));
                        inputMeterPeak = 0;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    // 失敗した世代だけを閉じ、切替先のフレームを読む処理 loop は残す。
                    failedEpoch = epoch;
                    inputMeterPeak = 0;
                    ScheduleFrameFault(ex, epoch, vad);
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
                var output = ApplyOutputGain(pcm);
                _audio.WriteOutput(output, client.OutputSampleRate);
                if (IsMonitoring && _playback is not null)
                {
                    // 試聴の失敗を、ゲームへの送信や字幕の停止へ波及させない。
                    try { _playback.Write(output, client.OutputSampleRate); }
                    catch (Exception ex) { OnMonitoringError(_monitorSessionId, ex); }
                }
                _outputLevel = Math.Max(_outputLevel, PcmPeak(output));
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

    private void ScheduleFrameFault(Exception error, int epoch, MicrophoneVadGate? vad)
    {
        CancellationTokenSource? session;
        lock (_routing)
        {
            session = _sessionCancellation;
            if (session is null || epoch != _epoch || !ReferenceEquals(vad, _voiceVad)
                || _faultScheduled != 0 || State != VoiceTranslationState.Running) return;
        }
        _ = Task.Run(async () =>
        {
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (_routing)
                {
                    // 予約後の mode 切替も照合し、旧失敗で新モードを停止しない。
                    if (!ReferenceEquals(session, _sessionCancellation) || epoch != _epoch
                        || !ReferenceEquals(vad, _voiceVad) || _faultScheduled != 0
                        || State != VoiceTranslationState.Running) return;
                    _faultScheduled = 1;
                }
                await StopCoreAsync().ConfigureAwait(false);
                SetState(VoiceTranslationState.Faulted);
                ErrorReceived?.Invoke(error);
            }
            finally { _lifecycle.Release(); }
        });
    }

    private IRealtimeSpeechTranslator? MuteRouting(bool preserveMonitoring = false)
    {
        IRealtimeSpeechTranslator? client;
        MicrophoneVadGate? vad;
        lock (_routing)
        {
            _sessionRefreshTimer?.Dispose();
            _sessionRefreshTimer = null;
            _mode = (int)VoiceSendMode.Muted;
            _epoch++;
            _outputLevel = 0;
            _audio.ClearOutput();
            _playback?.Clear();
            if (!preserveMonitoring && Interlocked.Exchange(ref _monitoring, 0) != 0) MonitoringChanged?.Invoke(false);
            while (_frames?.Reader.TryRead(out _) == true) { }
            client = _client;
            _client = null;
            vad = _voiceVad;
            _voiceVad = null;
        }
        // 推論を待つ間も routing を閉じたままにし、旧世代の送受信を先に遮断する。
        vad?.Dispose();
        return client;
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
        try
        {
            await StopMonitoringCoreAsync().ConfigureAwait(false);
            await _audio.StopAsync().ConfigureAwait(false);
        }
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
        return peak;
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
        if (_playback is not null) _playback.ErrorReceived -= OnMonitoringError;
    }
}
