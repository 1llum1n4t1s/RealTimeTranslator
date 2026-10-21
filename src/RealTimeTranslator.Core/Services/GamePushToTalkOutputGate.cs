using System.Diagnostics;
using RealTimeTranslator.Core.Interfaces;

namespace RealTimeTranslator.Core.Services;

/// <summary>送話の実音読み出しに合わせてゲームのキーを保持する。試聴と字幕には接続しない。</summary>
internal sealed class GamePushToTalkOutputGate : IDisposable
{
    private readonly Lock _sync = new();
    private readonly IGamePushToTalkController _controller;
    private readonly int _sampleRate;
    private readonly Action<Exception> _onError;
    private readonly Timer _timer;
    private bool _pressed;
    private bool _hasPlayedAudio;
    private bool _failed;
    private bool _disposed;
    private long _leadUntil;
    private long _releaseAt;

    internal GamePushToTalkOutputGate(IGamePushToTalkController controller, int sampleRate, Action<Exception> onError)
    {
        _controller = controller;
        _sampleRate = sampleRate;
        _onError = onError;
        _timer = new Timer(_ => CheckRelease(), null, 25, 25);
    }

    /// <summary>最後の実音を含む読み出し時間を先に記録し、押下準備中は PCM を消費しない。</summary>
    internal bool TryRead(int samplesThroughLastSound)
    {
        lock (_sync)
        {
            if (_disposed || _failed) return false;
            try
            {
                long now = Stopwatch.GetTimestamp();
                if (!_pressed)
                {
                    _controller.Press();
                    now = Stopwatch.GetTimestamp();
                    _pressed = true;
                    _leadUntil = now + Milliseconds(100);
                    // 再生が進まなくなった場合にもキーを保持し続けない。
                    _releaseAt = _leadUntil + Milliseconds(300);
                }
                if (now < _leadUntil) return false;
                _releaseAt = now + (long)Math.Ceiling((double)samplesThroughLastSound * Stopwatch.Frequency / _sampleRate)
                    + Milliseconds(300);
                _hasPlayedAudio = true;
                return true;
            }
            catch (Exception error)
            {
                Fail(error);
                return false;
            }
        }
    }

    private static long Milliseconds(int milliseconds) => (long)((double)Stopwatch.Frequency * milliseconds / 1000);

    private void CheckRelease()
    {
        lock (_sync)
        {
            if (_disposed || _failed || !_pressed || Stopwatch.GetTimestamp() < _releaseAt) return;
            try { ReleaseCore(); }
            catch (Exception error) { Fail(error); }
        }
    }

    internal void Clear()
    {
        lock (_sync)
        {
            if (_disposed) return;
            // Release は Press の途中失敗で残った所有キーも回収する。
            try { ReleaseCore(); }
            catch (Exception error) { Fail(error); }
        }
    }

    internal void ClearPendingOutput()
    {
        lock (_sync)
        {
            if (_disposed || _hasPlayedAudio) return;
            // 押下準備中に取り消した音声には、再生済みの末尾がない。
            try { ReleaseCore(); }
            catch (Exception error) { Fail(error); }
        }
    }

    private void ReleaseCore()
    {
        _controller.Release();
        _pressed = false;
        _hasPlayedAudio = false;
        _leadUntil = 0;
        _releaseAt = 0;
    }

    private void Fail(Exception error)
    {
        if (_failed) return;
        _failed = true;
        // 音声側の routing → buffer の順序へ再入しない。
        _ = Task.Run(() => _onError(error));
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Dispose();
            ReleaseCore();
        }
    }
}
