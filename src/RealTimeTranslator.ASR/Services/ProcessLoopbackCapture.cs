using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using RealTimeTranslator.Core.Services;

namespace RealTimeTranslator.ASR.Services;

/// <summary>
/// AudioClientActivationParams を用いたプロセス単位のループバックキャプチャ
/// Windows 10 Build 20348以降で利用可能
/// </summary>
internal sealed class ProcessLoopbackCapture : IWaveIn, IDisposable
{
    /// <summary>
    /// Process Loopback 用の仮想オーディオデバイスID
    /// </summary>
    private const string VirtualAudioDeviceProcessLoopback = "{2eef81be-33fa-4800-9670-1cd474972c3f}";
    private const int AudioBufferDurationMs = 100; // オーディオバッファの長さ（ミリ秒）
    private const int CaptureThreadSleepMs = 5; // キャプチャスレッドのスリープ時間（ミリ秒）
    private const long HundredNanosecondsPerSecond = 10000000L; // 1秒あたりの100ナノ秒単位数

    private readonly IAudioClient3 _audioClient;
    private readonly IAudioCaptureClient _captureClient;
    private readonly object _captureLock = new();
    private Thread? _captureThread;
    private bool _isCapturing;
    private readonly int _targetProcessId;
    private bool _isDisposed;

    public event EventHandler<WaveInEventArgs>? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public WaveFormat WaveFormat
    {
        get;
        set;
    }

    /// <summary>
    /// プロセス単位のループバックキャプチャを初期化
    /// </summary>
    /// <param name="targetProcessId">キャプチャ対象のプロセスID</param>
    public ProcessLoopbackCapture(int targetProcessId)
    {
        _targetProcessId = targetProcessId;
        MMDevice? device = null;
        IAudioClient3? audioClient = null;
        IAudioCaptureClient? captureClient = null;
        var formatPointer = IntPtr.Zero;

        try
        {
            device = GetDefaultRenderDevice();
            LoggerService.LogDebug($"ProcessLoopbackCapture: TargetProcessId={targetProcessId}, DefaultDevice={device.FriendlyName}");

            try
            {
                audioClient = ActivateProcessAudioClient(targetProcessId);
            }
            catch (Exception activationEx)
            {
                LoggerService.LogError($"ProcessLoopbackCapture: Audio client activation failed for process {targetProcessId}: {activationEx.GetType().Name} - {activationEx.Message}");
                LoggerService.LogDebug($"ProcessLoopbackCapture: System information - ProcessLoopback may require Windows 10 Build 20348+");
                throw;
            }

            _audioClient = audioClient;

            try
            {
                ThrowOnError(_audioClient.GetMixFormat(out formatPointer));
                WaveFormat = CreateWaveFormat(formatPointer);
                InitializeAudioClient(formatPointer, WaveFormat);
            }
            finally
            {
                if (formatPointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(formatPointer);
                    formatPointer = IntPtr.Zero;
                }
            }

            ThrowOnError(_audioClient.GetBufferSize(out _));
            captureClient = GetCaptureClient(_audioClient);
            _captureClient = captureClient;
        }
        catch
        {
            if (captureClient != null)
            {
                Marshal.ReleaseComObject(captureClient);
            }
            if (audioClient != null)
            {
                Marshal.ReleaseComObject(audioClient);
            }
            device?.Dispose();
            throw;
        }
        finally
        {
            device?.Dispose();
        }
    }

    public void StartRecording()
    {
        ThrowIfDisposed();
        lock (_captureLock)
        {
            if (_isCapturing)
                return;

            ThrowOnError(_audioClient.Start());
            _isCapturing = true;
            _captureThread = new Thread(CaptureThread)
            {
                IsBackground = true,
                Name = $"ProcessLoopbackCapture({_targetProcessId})"
            };
            _captureThread.Start();
        }
    }

    public void StopRecording()
    {
        if (_isDisposed)
            return;

        lock (_captureLock)
        {
            if (!_isCapturing)
                return;

            _isCapturing = false;
        }

        _captureThread?.Join();
        ThrowOnError(_audioClient.Stop());
        RecordingStopped?.Invoke(this, new StoppedEventArgs());
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        StopRecording();
        Marshal.ReleaseComObject(_captureClient);
        Marshal.ReleaseComObject(_audioClient);
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    private static MMDevice GetDefaultRenderDevice()
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        LoggerService.LogDebug($"GetDefaultRenderDevice: Device ID={device.ID}, State={device.State}, FriendlyName={device.FriendlyName}");
        return device;
    }

    /// <summary>
    /// プロセス単位のオーディオクライアントをアクティベート
    /// </summary>
    private static IAudioClient3 ActivateProcessAudioClient(int targetProcessId)
    {
        var activationParams = new AudioClientActivationParams
        {
            ActivationType = AudioClientActivationType.ProcessLoopback,
            TargetProcessId = (uint)targetProcessId,
            ProcessLoopbackMode = ProcessLoopbackMode.IncludeTargetProcessTree
        };

        var paramsSize = Marshal.SizeOf<AudioClientActivationParams>();
        LoggerService.LogDebug($"ActivateProcessAudioClient: paramsSize={paramsSize}, expected 12");

        if (paramsSize != 12)
        {
            LoggerService.LogError($"ActivateProcessAudioClient: AudioClientActivationParams has invalid size {paramsSize}, expected 12");
        }

        var paramsPtr = Marshal.AllocHGlobal(paramsSize);
        try
        {
            Marshal.StructureToPtr(activationParams, paramsPtr, false);
            var activationParamsPtr = new PropVariant
            {
                vt = 0x41, // VT_BLOB
                blobSize = (uint)paramsSize,
                pointerValue = paramsPtr
            };

            LoggerService.LogDebug($"ActivateProcessAudioClient: PropVariant size={Marshal.SizeOf<PropVariant>()}, vt={activationParamsPtr.vt}, blobSize={activationParamsPtr.blobSize}");

            var iid = typeof(IAudioClient3).GUID;
            ActivateAudioInterface(VirtualAudioDeviceProcessLoopback, ref iid, activationParamsPtr, out var audioClient);
            return audioClient;
        }
        finally
        {
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    /// <summary>
    /// オーディオインターフェースを非同期でアクティベート
    /// </summary>
    private static void ActivateAudioInterface(string deviceInterfacePath, ref Guid iid, PropVariant activationParams, out IAudioClient3 audioClient)
    {
        var completionHandler = new ActivateCompletionHandler();
        try
        {
            var paramsSize = Marshal.SizeOf<AudioClientActivationParams>();
            LoggerService.LogDebug($"ActivateAudioInterfaceAsync: deviceInterfacePath={deviceInterfacePath}, ParamsSize={paramsSize}, ParamsPtrSize={Marshal.SizeOf<PropVariant>()}");

            var hr = ActivateAudioInterfaceAsync(deviceInterfacePath, ref iid, activationParams, completionHandler, out var result);
            if (hr != 0)
            {
                var errorMessage = $"ActivateAudioInterfaceAsync failed: HRESULT=0x{hr:X8}";
                if (hr == unchecked((int)0x80070057)) // E_INVALIDARG
                {
                    errorMessage += " (E_INVALIDARG: Invalid arguments)";
                }
                else if (hr == unchecked((int)0x80004005)) // E_FAIL
                {
                    errorMessage += " (E_FAIL: Unspecified failure)";
                }
                else if (hr == unchecked((int)0x80070005)) // E_ACCESSDENIED
                {
                    errorMessage += " (E_ACCESSDENIED: Access denied)";
                }
                LoggerService.LogError(errorMessage);
                Marshal.ThrowExceptionForHR(hr);
            }

            completionHandler.WaitForCompletion();
            audioClient = completionHandler.GetActivatedInterface();
            Marshal.ReleaseComObject(result);
            LoggerService.LogInfo("ActivateAudioInterfaceAsync: Success");
        }
        catch (TimeoutException tex)
        {
            LoggerService.LogError($"Audio client activation timeout: {tex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"Audio client activation error: {ex.GetType().Name} - {ex.Message}");
            throw;
        }
    }

    private static IAudioCaptureClient GetCaptureClient(IAudioClient3 audioClient)
    {
        var iid = typeof(IAudioCaptureClient).GUID;
        ThrowOnError(audioClient.GetService(ref iid, out var captureClient));
        return (IAudioCaptureClient)captureClient;
    }

    /// <summary>
    /// オーディオクライアントを初期化
    /// Process Loopback API 使用時は Loopback フラグは不要
    /// </summary>
    private void InitializeAudioClient(IntPtr formatPointer, WaveFormat waveFormat)
    {
        // Process Loopback モードでは AudioClientStreamFlags.Loopback は使わない
        var streamFlags = AudioClientStreamFlags.None;
        var bufferDuration = HundredNanosecondsPerSecond * AudioBufferDurationMs / 1000;
        ThrowOnError(_audioClient.Initialize(AudioClientShareMode.Shared, streamFlags, bufferDuration, 0, formatPointer, Guid.Empty));
    }

    private void CaptureThread()
    {
        try
        {
            var frameSize = WaveFormat.BlockAlign;
            while (_isCapturing)
            {
                ThrowOnError(_captureClient.GetNextPacketSize(out var packetFrames));
                while (packetFrames > 0)
                {
                    ThrowOnError(_captureClient.GetBuffer(out var dataPointer, out var numFrames, out var flags, out _, out _));
                    var bytesToRead = (int)(numFrames * (uint)frameSize);
                    var buffer = new byte[bytesToRead];

                    if ((flags & AudioClientBufferFlags.Silent) != 0)
                    {
                        Array.Clear(buffer, 0, buffer.Length);
                    }
                    else
                    {
                        Marshal.Copy(dataPointer, buffer, 0, bytesToRead);
                    }

                    DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, bytesToRead));
                    ThrowOnError(_captureClient.ReleaseBuffer(numFrames));
                    ThrowOnError(_captureClient.GetNextPacketSize(out packetFrames));
                }

                Thread.Sleep(CaptureThreadSleepMs);
            }
        }
        catch (Exception ex)
        {
            RecordingStopped?.Invoke(this, new StoppedEventArgs(ex));
        }
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(ProcessLoopbackCapture));
    }

    private static void ThrowOnError(int hResult)
    {
        if (hResult != 0)
            Marshal.ThrowExceptionForHR(hResult);
    }

    private static WaveFormat CreateWaveFormat(IntPtr formatPointer)
    {
        var format = Marshal.PtrToStructure<WaveFormatEx>(formatPointer);
        if (format.FormatTag == WaveFormatTag.IeeeFloat)
        {
            return WaveFormat.CreateIeeeFloatWaveFormat((int)format.SampleRate, format.Channels);
        }

        if (format.FormatTag == WaveFormatTag.Extensible)
        {
            var extensible = Marshal.PtrToStructure<WaveFormatExtensible>(formatPointer);
            if (extensible.SubFormat == AudioFormatSubType.IeeeFloat)
            {
                return WaveFormat.CreateIeeeFloatWaveFormat((int)extensible.Format.SampleRate, extensible.Format.Channels);
            }
        }

        return new WaveFormat((int)format.SampleRate, format.BitsPerSample, format.Channels);
    }

    [DllImport("Mmdevapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int ActivateAudioInterfaceAsync(
        string deviceInterfacePath,
        ref Guid riid,
        PropVariant activationParams,
        IActivateAudioInterfaceCompletionHandler? completionHandler,
        out IActivateAudioInterfaceAsyncOperation asyncOperation);

    [ComImport]
    [Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    [ComImport]
    [Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport]
    [Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient3
    {
        int Initialize(AudioClientShareMode shareMode, AudioClientStreamFlags streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr format, [In] ref Guid audioSessionGuid);
        int GetBufferSize(out uint bufferSize);
        int GetStreamLatency(out long latency);
        int GetCurrentPadding(out uint currentPadding);
        int IsFormatSupported(AudioClientShareMode shareMode, IntPtr format, out IntPtr closestMatch);
        int GetMixFormat(out IntPtr deviceFormat);
        int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);
        int Start();
        int Stop();
        int Reset();
        int SetEventHandle(IntPtr eventHandle);
        int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        int GetBuffer(out IntPtr data, out uint numFramesToRead, out AudioClientBufferFlags flags, out ulong devicePosition, out ulong qpcPosition);
        int ReleaseBuffer(uint numFramesRead);
        int GetNextPacketSize(out uint numFramesInNextPacket);
    }

    /// <summary>
    /// AUDIOCLIENT_ACTIVATION_PARAMS 構造体
    /// Windows API仕様に準拠したレイアウト：ActivationType(4) + TargetProcessId(4) + ProcessLoopbackMode(4) = 12バイト
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        /// <summary>
        /// アクティベーションタイプ（ProcessLoopback = 1）
        /// </summary>
        public AudioClientActivationType ActivationType;

        /// <summary>
        /// ターゲットプロセスID
        /// </summary>
        public uint TargetProcessId;

        /// <summary>
        /// ループバックモード
        /// </summary>
        public ProcessLoopbackMode ProcessLoopbackMode;
    }

    private enum AudioClientActivationType
    {
        Default = 0,
        ProcessLoopback = 1
    }

    private enum ProcessLoopbackMode
    {
        IncludeTargetProcessTree = 0,
        ExcludeTargetProcessTree = 1
    }

    private enum AudioClientShareMode
    {
        Shared = 0,
        Exclusive = 1
    }

    [Flags]
    private enum AudioClientStreamFlags
    {
        None = 0x0,
        EventCallback = 0x40000,
        Loopback = 0x80000
    }

    [Flags]
    private enum AudioClientBufferFlags
    {
        None = 0x0,
        Silent = 0x2
    }

    /// <summary>
    /// PROPVARIANT 構造体（VT_BLOB用）
    /// Windows APIの標準レイアウトに完全に準拠
    /// sizeof(PROPVARIANT) = 16バイト（32ビット）、24バイト（64ビット）
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        /// <summary>
        /// Variant Type: VT_BLOB = 0x41 = 65
        /// </summary>
        [FieldOffset(0)]
        public ushort vt;

        /// <summary>
        /// Reserved field 1
        /// </summary>
        [FieldOffset(2)]
        public ushort wReserved1;

        /// <summary>
        /// Reserved field 2
        /// </summary>
        [FieldOffset(4)]
        public ushort wReserved2;

        /// <summary>
        /// Reserved field 3
        /// </summary>
        [FieldOffset(6)]
        public ushort wReserved3;

        /// <summary>
        /// BLOB size in bytes (4バイト、offset 8)
        /// </summary>
        [FieldOffset(8)]
        public uint blobSize;

        /// <summary>
        /// Pointer to BLOB data (8バイト、offset 16、アラインメント重要)
        /// </summary>
        [FieldOffset(16)]
        public IntPtr pointerValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public WaveFormatTag FormatTag;
        public short Channels;
        public int SampleRate;
        public int AvgBytesPerSec;
        public short BlockAlign;
        public short BitsPerSample;
        public short Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatExtensible
    {
        public WaveFormatEx Format;
        public short Samples;
        public int ChannelMask;
        public Guid SubFormat;
    }

    private enum WaveFormatTag : short
    {
        Pcm = 1,
        IeeeFloat = 3,
        Extensible = unchecked((short)0xFFFE)
    }

    private static class AudioFormatSubType
    {
        public static readonly Guid IeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");
    }

    private sealed class ActivateCompletionHandler : IActivateAudioInterfaceCompletionHandler
    {
        private const int ActivationTimeoutMs = 5000; // アクティベーションタイムアウト（5秒）
        private readonly ManualResetEventSlim _completedEvent = new(false);
        private int _activateResult;
        private IAudioClient3? _audioClient;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                operation.GetActivateResult(out _activateResult, out var activatedInterface);
                if (_activateResult == 0)
                {
                    _audioClient = (IAudioClient3)activatedInterface;
                }
                else if (activatedInterface != null)
                {
                    Marshal.ReleaseComObject(activatedInterface);
                }
            }
            catch (Exception ex)
            {
                LoggerService.LogError($"ActivateCompleted: Exception during GetActivateResult: {ex.GetType().Name} - {ex.Message}");
            }
            finally
            {
                _completedEvent.Set();
            }
        }

        /// <summary>
        /// アクティベーション完了を待機（タイムアウト付き）
        /// </summary>
        public void WaitForCompletion()
        {
            if (!_completedEvent.Wait(ActivationTimeoutMs))
            {
                throw new TimeoutException($"Audio client activation timed out after {ActivationTimeoutMs}ms");
            }

            if (_activateResult != 0)
            {
                var errorMessage = $"Audio client activation result: HRESULT=0x{_activateResult:X8}";
                if (_activateResult == unchecked((int)0x80070057)) // E_INVALIDARG
                {
                    errorMessage += " (E_INVALIDARG: Invalid arguments)";
                }
                else if (_activateResult == unchecked((int)0x80004005)) // E_FAIL
                {
                    errorMessage += " (E_FAIL: Unspecified failure)";
                }
                else if (_activateResult == unchecked((int)0x80070005)) // E_ACCESSDENIED
                {
                    errorMessage += " (E_ACCESSDENIED: Access denied)";
                }
                else if (_activateResult == unchecked((int)0x88890001)) // AUDCLNT_E_NOT_INITIALIZED
                {
                    errorMessage += " (AUDCLNT_E_NOT_INITIALIZED: Audio client not initialized)";
                }
                LoggerService.LogError(errorMessage);

                try
                {
                    Marshal.ThrowExceptionForHR(_activateResult);
                }
                catch (ArgumentException aex)
                {
                    // Marshal.ThrowExceptionForHRが無効なHRESULTで ArgumentExceptionを発生させた場合
                    LoggerService.LogError($"Marshal.ThrowExceptionForHR threw ArgumentException: {aex.Message}");
                    throw new COMException($"Audio client activation failed with HRESULT 0x{_activateResult:X8}", _activateResult);
                }
            }
        }

        public IAudioClient3 GetActivatedInterface()
        {
            return _audioClient ?? throw new InvalidOperationException("Audio client activation failed.");
        }
    }
}
