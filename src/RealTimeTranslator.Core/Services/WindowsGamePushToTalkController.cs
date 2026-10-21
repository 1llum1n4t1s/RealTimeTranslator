using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using RealTimeTranslator.Core.Interfaces;
using RealTimeTranslator.Core.Models;

namespace RealTimeTranslator.Core.Services;

/// <summary>専用の低レベルフックで物理入力を区別し、所有するゲーム用キーだけを解放する。</summary>
public sealed partial class WindowsGamePushToTalkController : IGamePushToTalkController
{
    private const int OperationTimeoutMs = 2000;
    private const uint WakeMessage = 0x8001;
    private static int _nextMarker;
    private readonly int[] _keys;
    private readonly int[] _physicalKeys = new int[256];
    private readonly int[] _initialDownKeys = new int[256];
    private readonly bool[] _ownedKeys = new bool[256];
    private readonly ConcurrentQueue<Operation> _operations = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly KeyboardHook _hookCallback;
    private readonly Thread _thread;
    private readonly nuint _marker = (nuint)(0x5254545000000000UL | (uint)Interlocked.Increment(ref _nextMarker));
    private Exception? _failure;
    private uint _threadId;
    private nint _hook;
    private int _stopRequested;
    private int _disposed;
    private bool _pressed;

    public WindowsGamePushToTalkController(int virtualKey, VoiceShortcutModifiers modifiers)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("ゲーム用PTTはWindows専用です。");
        if (!VoicePushToTalkShortcut.IsSupportedKey(virtualKey)) throw new ArgumentOutOfRangeException(nameof(virtualKey));
        if (!VoicePushToTalkShortcut.IsSupportedModifiers(modifiers)) throw new ArgumentOutOfRangeException(nameof(modifiers));

        List<int> keys = [];
        if (modifiers.HasFlag(VoiceShortcutModifiers.Control)) keys.Add(0xA2);
        if (modifiers.HasFlag(VoiceShortcutModifiers.Shift)) keys.Add(0xA0);
        if (modifiers.HasFlag(VoiceShortcutModifiers.Alt)) keys.Add(0xA4);
        if (modifiers.HasFlag(VoiceShortcutModifiers.Windows)) keys.Add(0x5B);
        keys.Add(virtualKey);
        _keys = [.. keys];
        _hookCallback = OnKeyboard;
        _thread = new Thread(Run) { IsBackground = true, Name = "ゲーム用PTTキー監視" };
        _thread.Start();
        if (!_ready.Task.Wait(OperationTimeoutMs))
        {
            var error = new TimeoutException("ゲーム用PTTの物理キー監視を開始できませんでした。");
            Fail(error);
            Wake();
            throw error;
        }

        ThrowIfUnavailable();
    }

    public bool IsPhysicalKeyDown(int virtualKey)
    {
        ThrowIfUnavailable();
        if ((uint)virtualKey >= _physicalKeys.Length) throw new ArgumentOutOfRangeException(nameof(virtualKey));
        return virtualKey switch
        {
            0x10 => IsPhysical(0xA0) || IsPhysical(0xA1),
            0x11 => IsPhysical(0xA2) || IsPhysical(0xA3),
            0x12 => IsPhysical(0xA4) || IsPhysical(0xA5),
            _ => IsPhysical(virtualKey)
        };
    }

    public void Press() => Invoke(press: true);

    public void Release() => Invoke(press: false);

    private bool IsPhysical(int key) => Volatile.Read(ref _physicalKeys[key]) != 0;

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _failure) is { } error)
            throw new InvalidOperationException("ゲーム用PTTのキー制御を継続できません。", error);
        if (Volatile.Read(ref _stopRequested) != 0 || !_thread.IsAlive)
            throw new InvalidOperationException("ゲーム用PTTのキー監視が停止しています。");
    }

    private void Invoke(bool press)
    {
        ThrowIfUnavailable();
        var operation = new Operation(press);
        _operations.Enqueue(operation);
        if (!Wake())
        {
            Volatile.Write(ref operation.Cancelled, 1);
            var error = new Win32Exception(Marshal.GetLastPInvokeError(), "ゲーム用PTTの操作を通知できませんでした。");
            Fail(error);
            throw error;
        }
        if (!operation.Completion.Task.Wait(OperationTimeoutMs))
        {
            // 待機中の押下は取り消す。実行中でも次の注入前・注入直後に停止を確認して解放する。
            Volatile.Write(ref operation.Cancelled, 1);
            var error = new TimeoutException("ゲーム用PTTのキー操作がタイムアウトしました。");
            Fail(error);
            Wake();
            throw error;
        }

        if (operation.Completion.Task.Result is { } failure)
            throw new InvalidOperationException("ゲーム用PTTのキー操作に失敗しました。", failure);
        ThrowIfUnavailable();
    }

    private bool Wake()
    {
        uint threadId = Volatile.Read(ref _threadId);
        return threadId != 0 && PostThreadMessageW(threadId, WakeMessage, 0, 0);
    }

    private void Fail(Exception error)
    {
        Exception? previous;
        do
        {
            previous = Volatile.Read(ref _failure);
            if (ReferenceEquals(previous, error)) break;
            Exception failure = previous is null ? error : new AggregateException(previous, error);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _failure, failure, previous), previous)) break;
        } while (true);
        Volatile.Write(ref _stopRequested, 1);
    }

    private void Run()
    {
        try
        {
            // PostThreadMessageより先に、このスレッドのメッセージキューを作る。
            PeekMessageW(out _, 0, 0, 0, 0);
            Volatile.Write(ref _threadId, GetCurrentThreadId());
            _hook = SetWindowsHookExW(13, Marshal.GetFunctionPointerForDelegate(_hookCallback), GetModuleHandleW(null), 0);
            if (_hook == 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "物理キー監視のフックを登録できませんでした。");
            for (int key = 1; key < _physicalKeys.Length; key++)
            {
                int down = (GetAsyncKeyState(key) & 0x8000) != 0 ? 1 : 0;
                Volatile.Write(ref _physicalKeys[key], down);
                Volatile.Write(ref _initialDownKeys[key], down);
            }
            _ready.TrySetResult();

            while (Volatile.Read(ref _stopRequested) == 0)
            {
                int result = GetMessageW(out var message, 0, 0, 0);
                if (result == -1) throw new Win32Exception(Marshal.GetLastPInvokeError(), "物理キー監視のメッセージ取得に失敗しました。");
                if (result == 0) throw new InvalidOperationException("物理キー監視が予期せず終了しました。");
                if (Volatile.Read(ref _stopRequested) != 0) break;
                if (message.Message == WakeMessage) DrainOperations();
                else
                {
                    TranslateMessage(in message);
                    DispatchMessageW(in message);
                }
            }
        }
        catch (Exception error)
        {
            Fail(error);
        }
        finally
        {
            Volatile.Write(ref _stopRequested, 1);
            try { ReleaseOwnedKeys(); }
            catch (Exception error) { Fail(error); }
            finally
            {
                // 解放が失敗しても、フックとメッセージスレッドを残さない。
                try
                {
                    if (_hook != 0 && !UnhookWindowsHookEx(_hook))
                        Fail(new Win32Exception(Marshal.GetLastPInvokeError(), "物理キー監視のフックを解除できませんでした。"));
                }
                catch (Exception error) { Fail(error); }
                _hook = 0;
                Volatile.Write(ref _threadId, 0);
                _ready.TrySetResult();
                while (_operations.TryDequeue(out var operation))
                    operation.Completion.TrySetResult(Volatile.Read(ref _failure) ?? new ObjectDisposedException(GetType().Name));
                GC.KeepAlive(_hookCallback);
            }
        }
    }

    private void DrainOperations()
    {
        while (Volatile.Read(ref _stopRequested) == 0 && _operations.TryDequeue(out var operation))
        {
            Exception? failure = null;
            try
            {
                if (Volatile.Read(ref operation.Cancelled) != 0) continue;
                if (operation.Press) PressKeys();
                else ReleaseOwnedKeys();
            }
            catch (Exception error)
            {
                failure = error;
                Fail(error);
            }
            finally { operation.Completion.TrySetResult(failure); }
        }
    }

    private bool IsPhysicalShortcutKeyDown(int key) => key switch
    {
        0xA0 => IsPhysical(0xA0) || IsPhysical(0xA1),
        0xA2 => IsPhysical(0xA2) || IsPhysical(0xA3),
        0xA4 => IsPhysical(0xA4) || IsPhysical(0xA5),
        0x5B => IsPhysical(0x5B) || IsPhysical(0x5C),
        _ => IsPhysical(key)
    };

    private static int ModifierPartner(int key) => key switch
    {
        0xA0 => 0xA1,
        0xA1 => 0xA0,
        0xA2 => 0xA3,
        0xA3 => 0xA2,
        0xA4 => 0xA5,
        0xA5 => 0xA4,
        0x5B => 0x5C,
        0x5C => 0x5B,
        _ => 0
    };

    private bool IsOwnedShortcutKey(int key)
    {
        int partner = ModifierPartner(key);
        return _ownedKeys[key] || (partner != 0 && _ownedKeys[partner]);
    }

    private bool IsLeaseKey(int key)
    {
        int partner = ModifierPartner(key);
        return Array.IndexOf(_keys, key) >= 0 || (partner != 0 && Array.IndexOf(_keys, partner) >= 0);
    }

    private void PressKeys()
    {
        if (_pressed) return;
        // 他キーのSendInput中に、初めから物理保持されていたキーが解放される場合も引き継ぐ。
        _pressed = true;
        try
        {
            foreach (int key in _keys)
            {
                if (Volatile.Read(ref _stopRequested) != 0) throw new OperationCanceledException("ゲーム用PTTの押下を取り消しました。");
                if (IsOwnedShortcutKey(key) || IsPhysicalShortcutKeyDown(key)) continue;
                // フック再入時の物理解放はleaseから引き継ぎ、成功した注入だけを追加所有する。
                SendKey(key, keyUp: false);
                _ownedKeys[key] = true;
            }

            if (Volatile.Read(ref _stopRequested) != 0) throw new OperationCanceledException("ゲーム用PTTの押下を取り消しました。");
        }
        catch (Exception error)
        {
            // 途中まで成功した列も逆順に解放し、挿入失敗を成功扱いしない。
            try { ReleaseOwnedKeys(); }
            catch (Exception rollbackError) { throw new AggregateException(error, rollbackError); }
            throw;
        }
    }

    private void ReleaseOwnedKeys()
    {
        _pressed = false;
        List<Exception>? failures = null;
        for (int index = _keys.Length - 1; index >= 0; index--)
        {
            int key = _keys[index];
            int partner = ModifierPartner(key);
            if (partner != 0) ReleaseOwnedKey(partner, ref failures);
            ReleaseOwnedKey(key, ref failures);
        }

        if (failures is not null) throw new AggregateException("ゲーム用PTTのキーを解放できませんでした。", failures);
    }

    private void ReleaseOwnedKey(int key, ref List<Exception>? failures)
    {
        if (!_ownedKeys[key]) return;
        try
        {
            // 利用者が同じキーを物理的に保持中なら、解放せず所有権を返す。
            if (!IsPhysical(key)) SendKey(key, keyUp: true);
            _ownedKeys[key] = false;
        }
        catch (Exception error) { (failures ??= []).Add(error); }
    }

    private nint OnKeyboard(int code, nuint message, nint data)
    {
        try
        {
            if (code >= 0 && data != 0)
            {
                var input = Marshal.PtrToStructure<LowLevelKeyboardInput>(data);
                bool down = message is 0x0100 or 0x0104;
                bool up = message is 0x0101 or 0x0105;
                int key = (int)input.VirtualKey;
                if (key == 0x10) key = input.ScanCode == 0x36 ? 0xA1 : 0xA0;
                else if (key == 0x11) key = (input.Flags & 1) != 0 ? 0xA3 : 0xA2;
                else if (key == 0x12) key = (input.Flags & 1) != 0 ? 0xA5 : 0xA4;
                if (up && (input.Flags & 0x10) != 0 && (uint)key < _physicalKeys.Length)
                {
                    // 開始前の保持は物理・注入を判別できない。未観測の初期値を注入解放後まで残さない。
                    if (Interlocked.Exchange(ref _initialDownKeys[key], 0) != 0)
                        Volatile.Write(ref _physicalKeys[key], 0);
                    if (input.ExtraInfo != _marker && _pressed && IsLeaseKey(key))
                    {
                        if (!IsPhysical(key)) _ownedKeys[key] = true;
                        return 1;
                    }
                }
                // 解放のSendInput中に物理押下が先行した場合も、その保持を打ち消さない。
                if (up && input.ExtraInfo == _marker && (uint)key < _physicalKeys.Length && IsPhysical(key)) return 1;
                // 他プロセスの注入も、マイクPTTを開く物理入力として数えない。
                if ((input.Flags & 0x10) == 0 && input.ExtraInfo != _marker)
                {
                    if ((down || up) && (uint)key < _physicalKeys.Length)
                    {
                        Volatile.Write(ref _initialDownKeys[key], 0);
                        Volatile.Write(ref _physicalKeys[key], down ? 1 : 0);
                        // 物理解放は記録するが、ゲームの保持はlease解除まで継続する。
                        if (up && (_ownedKeys[key] || (_pressed && IsLeaseKey(key) && !IsOwnedShortcutKey(key))))
                        {
                            _ownedKeys[key] = true;
                            return 1;
                        }
                    }
                }
            }
        }
        catch (Exception error) { Fail(error); Wake(); }
        return CallNextHookEx(0, code, message, data);
    }

    private unsafe void SendKey(int key, bool keyUp)
    {
        uint scan = MapVirtualKeyW((uint)key, 4);
        // PauseのE1列はKEYEVENTF_EXTENDEDKEYのE0指定では表せないため、VKで送る。
        bool useScan = key != 0x13 && (scan & 0xFF) != 0 && (scan & 0xFF00) != 0xE100;
        var input = new NativeInput
        {
            Type = 1,
            Keyboard = new KeyboardInput
            {
                VirtualKey = useScan ? (ushort)0 : (ushort)key,
                ScanCode = useScan ? (ushort)(scan & 0xFF) : (ushort)0,
                Flags = (keyUp ? 2u : 0u) | (useScan ? 8u : 0u) | (useScan && (scan & 0xFF00) == 0xE000 ? 1u : 0u),
                ExtraInfo = _marker
            }
        };
        Marshal.SetLastPInvokeError(0);
        if (SendInput(1, &input, Marshal.SizeOf<NativeInput>()) != 1)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "ゲーム用PTTのキー注入に失敗しました。権限差による拒否はエラー番号から特定できません。");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Volatile.Write(ref _stopRequested, 1);
        Wake();
        if (Thread.CurrentThread == _thread) return;
        if (!_thread.Join(OperationTimeoutMs))
        {
            var error = new TimeoutException("ゲーム用PTTのキー監視を終了できませんでした。");
            Fail(error);
            throw error;
        }

        if (Volatile.Read(ref _failure) is { } failure)
            throw new InvalidOperationException("ゲーム用PTTの終了時にキー制御の失敗を検出しました。", failure);
    }

    private sealed class Operation(bool press)
    {
        public bool Press { get; } = press;
        public int Cancelled;
        public TaskCompletionSource<Exception?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
