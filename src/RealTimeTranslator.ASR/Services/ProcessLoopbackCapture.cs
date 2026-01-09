using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace RealTimeTranslator.ASR.Services;

/// <summary>
/// AudioClientActivationParams を用いたプロセス単位のループバックキャプチャ
/// </summary>
internal sealed class ProcessLoopbackCapture : IWaveIn, IDisposable
{
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

    public ProcessLoopbackCapture(int targetProcessId)
    {
        _targetProcessId = targetProcessId;
        var device = GetDefaultRenderDevice();
        var audioClient = ActivateProcessAudioClient(device, targetProcessId);
        _audioClient = audioClient;

        var formatPointer = IntPtr.Zero;
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
            }
        }

        ThrowOnError(_audioClient.GetBufferSize(out _));
        _captureClient = GetCaptureClient(_audioClient);
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
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }

    private static IAudioClient3 ActivateProcessAudioClient(MMDevice device, int targetProcessId)
    {
        var activationParams = new AudioClientActivationParams
        {
            ActivationType = AudioClientActivationType.ProcessLoopback,
            ProcessLoopbackMode = ProcessLoopbackMode.IncludeTargetProcessTree,
            TargetProcessId = (uint)targetProcessId
        };

        var paramsSize = Marshal.SizeOf<AudioClientActivationParams>();
        var paramsPtr = Marshal.AllocHGlobal(paramsSize);
        try
        {
            Marshal.StructureToPtr(activationParams, paramsPtr, false);
            var activationParamsPtr = new PropVariant
            {
                vt = VarEnum.VT_BLOB,
                blobSize = paramsSize,
                pointerValue = paramsPtr
            };

            var iid = typeof(IAudioClient3).GUID;
            ActivateAudioInterface(device, ref iid, activationParamsPtr, out var audioClient);
            return audioClient;
        }
        finally
        {
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    private static void ActivateAudioInterface(MMDevice device, ref Guid iid, PropVariant activationParams, out IAudioClient3 audioClient)
    {
        var completionHandler = new ActivateCompletionHandler();
        var hr = ActivateAudioInterfaceAsync(device.ID, ref iid, activationParams, completionHandler, out var result);
        if (hr != 0)
            Marshal.ThrowExceptionForHR(hr);

        completionHandler.WaitForCompletion();
        audioClient = completionHandler.GetActivatedInterface();
        Marshal.ReleaseComObject(result);
    }

    private static IAudioCaptureClient GetCaptureClient(IAudioClient3 audioClient)
    {
        var iid = typeof(IAudioCaptureClient).GUID;
        ThrowOnError(audioClient.GetService(ref iid, out var captureClient));
        return (IAudioCaptureClient)captureClient;
    }

    private void InitializeAudioClient(IntPtr formatPointer, WaveFormat waveFormat)
    {
        var streamFlags = AudioClientStreamFlags.Loopback;
        var bufferDuration = 10000000L / 10; // 100ms
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

                Thread.Sleep(5);
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

    [DllImport("Mmdevapi.dll", CharSet = CharSet.Unicode)]
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

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        public AudioClientActivationType ActivationType;
        public ProcessLoopbackMode ProcessLoopbackMode;
        public uint TargetProcessId;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public VarEnum vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public int blobSize;
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
        private readonly ManualResetEventSlim _completedEvent = new(false);
        private int _activateResult;
        private IAudioClient3? _audioClient;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
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

            _completedEvent.Set();
        }

        public void WaitForCompletion()
        {
            _completedEvent.Wait();
            if (_activateResult != 0)
            {
                Marshal.ThrowExceptionForHR(_activateResult);
            }
        }

        public IAudioClient3 GetActivatedInterface()
        {
            return _audioClient ?? throw new InvalidOperationException("Audio client activation failed.");
        }
    }
}
