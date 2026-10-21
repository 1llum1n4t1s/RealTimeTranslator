using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.Core.Services;

public sealed partial class VoiceTranslationService
{
    private bool IsPushToTalkDown()
    {
        if (!IsPhysicalKeyDown(_settings.PushToTalkVirtualKey)) return false;
        var modifiers = _settings.PushToTalkModifiers;
        return (!modifiers.HasFlag(VoiceShortcutModifiers.Control) || IsPhysicalKeyDown(0x11))
            && (!modifiers.HasFlag(VoiceShortcutModifiers.Shift) || IsPhysicalKeyDown(0x10))
            && (!modifiers.HasFlag(VoiceShortcutModifiers.Alt) || IsPhysicalKeyDown(0x12))
            && (!modifiers.HasFlag(VoiceShortcutModifiers.Windows) || IsPhysicalKeyDown(0x5B) || IsPhysicalKeyDown(0x5C));
    }

    private bool IsPhysicalKeyDown(int key) => _audio.GetPhysicalPushToTalkKeyState(key) ?? _isKeyDown(key);

    public async Task SetMonitoringAsync(string? outputDeviceId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopMonitoringCoreAsync().ConfigureAwait(false);
            if (outputDeviceId is null) return;
            if (_playback is null) throw new InvalidOperationException("試聴用の再生サービスがありません。");
            if (State != VoiceTranslationState.Running || Mode != VoiceSendMode.Translated)
                throw new InvalidOperationException("翻訳音声を送信している間に試聴を開始できます。");
            if (string.IsNullOrWhiteSpace(outputDeviceId) || outputDeviceId == _settings.OutputDeviceId)
                throw new ArgumentException("試聴用のヘッドホンやスピーカーを選択してください。", nameof(outputDeviceId));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _sessionCancellation!.Token);
            lock (_routing)
            {
                _monitorStarting = true;
                _monitorStartBaseline = _playback.SessionId;
                _monitorStartError = null;
            }
            await _playback.StartAsync(outputDeviceId, linked.Token).ConfigureAwait(false);
            lock (_routing)
            {
                if (_monitorStartError is { } startError)
                    throw new IOException("試聴の開始中に再生機器が停止しました。", startError);
                if (linked.IsCancellationRequested || State != VoiceTranslationState.Running || Mode != VoiceSendMode.Translated)
                {
                    _playback.Clear();
                    linked.Token.ThrowIfCancellationRequested();
                    throw new InvalidOperationException("ボイス通訳が停止または切り替えられました。");
                }
                _monitorSessionId = _playback.SessionId;
                _monitorStarting = false;
                Volatile.Write(ref _monitoring, 1);
            }
            MonitoringChanged?.Invoke(IsMonitoring);
        }
        catch
        {
            await StopMonitoringCoreAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            lock (_routing)
            {
                _monitorStarting = false;
                _monitorStartError = null;
            }
            _lifecycle.Release();
        }
    }

    private async Task StopMonitoringCoreAsync()
    {
        lock (_routing)
        {
            if (Interlocked.Exchange(ref _monitoring, 0) != 0) MonitoringChanged?.Invoke(false);
            _playback?.Clear();
        }
        if (_playback is null) return;
        try { await _playback.StopAsync().ConfigureAwait(false); }
        catch (Exception ex) { MonitoringErrorReceived?.Invoke(ex); }
    }

    private void OnMonitoringError(long sessionId, Exception error)
    {
        lock (_routing)
        {
            // StartAsync の完了通知より先に機器エラーが届くことがある。
            // 旧セッションを除外し、開始側がオンへ遷移する前に失敗を確定する。
            if (_monitorStarting && sessionId > _monitorStartBaseline && sessionId == _playback?.SessionId)
            {
                _monitorStartError = error;
                return;
            }
            if (!IsMonitoring || sessionId != _monitorSessionId) return;
            Volatile.Write(ref _monitoring, 0);
            _playback?.Clear();
        }
        MonitoringChanged?.Invoke(false);
        MonitoringErrorReceived?.Invoke(error);
        _ = Task.Run(async () =>
        {
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                if (sessionId == _monitorSessionId && !IsMonitoring)
                    await StopMonitoringCoreAsync().ConfigureAwait(false);
            }
            finally { _lifecycle.Release(); }
        });
    }
}
