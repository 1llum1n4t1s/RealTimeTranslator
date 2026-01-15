using System.Buffers;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using RealTimeTranslator.Core.Services;

namespace RealTimeTranslator.Core.Services;

/// <summary>
/// AudioClientActivationParams を用いたプロセス単位のループバックキャプチャ
/// Windows 10 Build 20348以降で利用可能
/// 
/// 注記:
/// - CsWin32パッケージ (Microsoft.Windows.CsWin32) を導入済み
/// - AllowUnsafeBlocks が有効化済み (unsafe コンテキスト対応)
/// - 将来的に手動定義のP/Invokeは CsWin32 生成コードに段階的に置き換え可能
/// </summary>
internal sealed class ProcessLoopbackCapture : IWaveIn, IDisposable
{
    /// <summary>
    /// Process Loopback 用のデバイスインターフェースパス GUID
    /// 完全なパスは \\?\SWD#MMDEVAPI#{GUID} 形式
    /// </summary>
    internal const string ProcessLoopbackDeviceInterfaceGuid = "{2eef81be-33fa-4800-9670-1cd474972c3f}";
    /// <summary>
    /// Process Loopback 用の仮想オーディオデバイスID
    /// </summary>
    private const string VirtualAudioDeviceProcessLoopback = "{2eef81be-33fa-4800-9670-1cd474972c3f}";
    private const int AudioBufferDurationMs = 100; // オーディオバッファの長さ（ミリ秒）
    private const int CaptureThreadSleepMs = 5; // キャプチャスレッドのスリープ時間（ミリ秒）
    private const long HundredNanosecondsPerSecond = 10000000L; // 1秒あたりの100ナノ秒単位数

    // CLSCTX constants
    private static class CLSCTX
    {
        public const int CLSCTX_INPROC_SERVER = 0x1;
        public const int CLSCTX_INPROC_HANDLER = 0x2;
        public const int CLSCTX_LOCAL_SERVER = 0x4;
        public const int CLSCTX_INPROC_SERVER16 = 0x8;
        public const int CLSCTX_REMOTE_SERVER = 0x10;
        public const int CLSCTX_INPROC_HANDLER16 = 0x20;
        public const int CLSCTX_RESERVED1 = 0x40;
        public const int CLSCTX_RESERVED2 = 0x80;
        public const int CLSCTX_RESERVED3 = 0x100;
        public const int CLSCTX_RESERVED4 = 0x200;
        public const int CLSCTX_NO_CODE_DOWNLOAD = 0x400;
        public const int CLSCTX_RESERVED5 = 0x800;
        public const int CLSCTX_NO_CUSTOM_MARSHAL = 0x1000;
        public const int CLSCTX_ENABLE_CODE_DOWNLOAD = 0x2000;
        public const int CLSCTX_NO_FAILURE_LOG = 0x4000;
        public const int CLSCTX_DISABLE_AAA = 0x8000;
        public const int CLSCTX_ENABLE_AAA = 0x10000;
        public const int CLSCTX_FROM_DEFAULT_CONTEXT = 0x20000;
        public const int CLSCTX_ACTIVATE_X86_SERVER = 0x40000;
        public const int CLSCTX_ACTIVATE_64_BIT_SERVER = 0x80000;
        public const int CLSCTX_ENABLE_CLOAKING = 0x100000;
        public const int CLSCTX_APPCONTAINER = 0x400000;
        public const int CLSCTX_ACTIVATE_AAA_AS_IU = 0x800000;
        public const int CLSCTX_RESERVED6 = 0x1000000;
        public const int CLSCTX_ACTIVATE_ARM32_SERVER = 0x2000000;
        public const uint CLSCTX_PS_DLL = 0x80000000;
        public const int CLSCTX_ALL = CLSCTX_INPROC_SERVER | CLSCTX_INPROC_HANDLER | CLSCTX_LOCAL_SERVER | CLSCTX_REMOTE_SERVER;
    }

    // AUDCLNT_SHAREMODE
    private enum AUDCLNT_SHAREMODE
    {
        AUDCLNT_SHAREMODE_SHARED = 0,
        AUDCLNT_SHAREMODE_EXCLUSIVE = 1
    }

    // AUDCLNT_STREAMFLAGS
    [Flags]
    private enum AUDCLNT_STREAMFLAGS : uint
    {
        AUDCLNT_STREAMFLAGS_CROSSPROCESS = 0x00010000,
        AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000,
        AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000,
        AUDCLNT_STREAMFLAGS_NOPERSIST = 0x00080000,
        AUDCLNT_STREAMFLAGS_RATEADJUST = 0x00100000,
        AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000,
        AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000
    }

    // AudioClientActivationType
    private enum AudioClientActivationType : uint
    {
        Default = 0,
        ProcessLoopback = 1
    }

    // ProcessLoopbackMode
    private enum ProcessLoopbackMode : uint
    {
        IncludeTargetProcessTree = 0,
        ExcludeTargetProcessTree = 1
    }

    // AudioClientProcessLoopbackParams
    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientProcessLoopbackParams
    {
        public uint TargetProcessId;
        public ProcessLoopbackMode ProcessLoopbackMode;
    }


    private readonly IAudioClient _audioClient;
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
        IAudioClient? audioClient = null;
        IAudioCaptureClient? captureClient = null;
        var formatPointer = IntPtr.Zero;

        try
        {
            device = GetDefaultRenderDevice();
            LoggerService.LogDebug($"ProcessLoopbackCapture: TargetProcessId={targetProcessId}, DefaultDevice={device.FriendlyName}");

            try
            {
                audioClient = ActivateProcessAudioClient(device, targetProcessId);
            }
            catch (Exception activationEx)
            {
                LoggerService.LogError($"ProcessLoopbackCapture: Audio client activation failed for process {targetProcessId}: {activationEx.GetType().Name} - {activationEx.Message}");
                LoggerService.LogDebug($"ProcessLoopbackCapture: System information - ProcessLoopback may require Windows 10 Build 20348+");
                throw;
            }

            _audioClient = audioClient;
            LoggerService.LogDebug($"ProcessLoopbackCapture: audioClient assigned to _audioClient, Type={audioClient?.GetType().Name}");

            try
            {
                LoggerService.LogDebug($"ProcessLoopbackCapture: Calling GetMixFormat on IAudioClient");
                ThrowOnError(_audioClient.GetMixFormat(out formatPointer));
                LoggerService.LogDebug($"ProcessLoopbackCapture: GetMixFormat succeeded, formatPointer=0x{formatPointer:X}");
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
    /// 重要：このメソッドは COM 初期化されたスレッド（通常は UI スレッド）から呼ぶ必要がある
    /// </summary>
    private static unsafe IAudioClient ActivateProcessAudioClient(MMDevice device, int targetProcessId)
    {
        LoggerService.LogDebug($"ActivateProcessAudioClient: Activating IAudioClient for device {device.ID}");

        // Create process loopback parameters
        var paramsSize = sizeof(AudioClientActivationParams);
        LoggerService.LogDebug($"ActivateProcessAudioClient: paramsSize={paramsSize}, expected 12");

        var activationParams = new AudioClientActivationParams
        {
            ActivationType = AudioClientActivationType.ProcessLoopback,
            ProcessLoopbackParams = new AudioClientProcessLoopbackParams
            {
                TargetProcessId = (uint)targetProcessId,
                ProcessLoopbackMode = ProcessLoopbackMode.IncludeTargetProcessTree
            }
        };

        LoggerService.LogDebug($"ActivateProcessAudioClient: ActivationType=ProcessLoopback ({(int)activationParams.ActivationType}), TargetProcessId={targetProcessId}, ProcessLoopbackMode=IncludeTargetProcessTree ({(int)activationParams.ProcessLoopbackParams.ProcessLoopbackMode})");

        // Marshal the parameters to unmanaged memory
        var paramsPtr = Marshal.AllocHGlobal(paramsSize);
        try
        {
            Marshal.StructureToPtr(activationParams, paramsPtr, false);
            LoggerService.LogDebug($"ActivateProcessAudioClient: AudioClientActivationParams marshaled successfully");

            // Dump memory for debugging
            var paramsBytes = new byte[paramsSize];
            Marshal.Copy(paramsPtr, paramsBytes, 0, paramsSize);
            LoggerService.LogDebug($"ActivateProcessAudioClient: Params memory dump = {BitConverter.ToString(paramsBytes).Replace("-", "")}");

            // Create PROPVARIANT with the parameters
            Span<byte> propVariantSpan = stackalloc byte[24];
            fixed (byte* propVariantPtr = propVariantSpan)
            {
                for (var i = 0; i < 24; i++)
                {
                    propVariantPtr[i] = 0;
                }

                *(ushort*)propVariantPtr = 0x41; // VT_BLOB
                *(uint*)(propVariantPtr + 8) = (uint)paramsSize; // cbSize
                *(IntPtr*)(propVariantPtr + 16) = paramsPtr; // pBlobData (aligned for x64)

                LoggerService.LogDebug($"ActivateProcessAudioClient: PROPVARIANT Layout:");
                LoggerService.LogDebug($"  [0-1]   vt=0x{(*(ushort*)propVariantPtr):X4} (VT_BLOB)");
                LoggerService.LogDebug($"  [2-3]   reserved1=0x{(*(ushort*)(propVariantPtr + 2)):X4}");
                LoggerService.LogDebug($"  [4-5]   reserved2=0x{(*(ushort*)(propVariantPtr + 4)):X4}");
                LoggerService.LogDebug($"  [6-7]   reserved3=0x{(*(ushort*)(propVariantPtr + 6)):X4}");
                LoggerService.LogDebug($"  [8-11]  BLOB.cbSize={(uint)paramsSize}");
                LoggerService.LogDebug($"  [12-15] padding=0x{(*(uint*)(propVariantPtr + 12)):X8}");
                LoggerService.LogDebug($"  [16-23] BLOB.pBlobData=0x{(*(IntPtr*)(propVariantPtr + 16)):X}");

                var propVariantHex = BitConverter.ToString(propVariantSpan.ToArray()).Replace("-", "");
                LoggerService.LogDebug($"ActivateProcessAudioClient: PROPVARIANT hex dump = {propVariantHex}");

                var iid = IID_IAudioClient;
                LoggerService.LogDebug($"ActivateProcessAudioClient: Using IID_IAudioClient={iid:B}");
                LoggerService.LogDebug($"ActivateProcessAudioClient: Device ID={device.ID}");

                // Process Loopback 用の特別なデバイスインターフェースパスを使用する
                // 定義値: VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK (L"VAD\\Process_Loopback")
                var deviceInterfacePath = "VAD\\Process_Loopback";

                return ActivateAudioInterface(deviceInterfacePath, ref iid, (IntPtr)propVariantPtr, out var audioClient);
            }
        }
        finally
        {
            if (paramsPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(paramsPtr);
            }
        }
    }

    /// <summary>
    /// オーディオインターフェースをアクティベート
    /// </summary>
    private static IAudioClient ActivateAudioInterface(string deviceInterfacePath, ref Guid iid, IntPtr activationParams, out IAudioClient audioClient)
    {
        LoggerService.LogDebug($"ActivateAudioInterface: Called with deviceInterfacePath={deviceInterfacePath}, iid={iid:B}, activationParams=0x{activationParams:X}");

        // Create completion handler
        var completionHandler = new ActivateCompletionHandler();
        var gcHandle = GCHandle.Alloc(completionHandler);
        try
        {
            var completionHandlerPtr = Marshal.GetComInterfaceForObject(completionHandler, typeof(IActivateAudioInterfaceCompletionHandler));
            LoggerService.LogDebug($"ActivateAudioInterface: GetComInterfaceForObject returned ptr=0x{completionHandlerPtr:X}");

            try
            {
                LoggerService.LogDebug($"ActivateAudioInterface: Calling ActivateAudioInterfaceAsync P/Invoke");
                LoggerService.LogDebug($"  deviceInterfacePath={deviceInterfacePath}");
                LoggerService.LogDebug($"  riid={iid:B}");
                LoggerService.LogDebug($"  activationParams=0x{activationParams:X}");
                LoggerService.LogDebug($"  completionHandlerPtr=0x{completionHandlerPtr:X}");

                var hr = ActivateAudioInterfaceAsync(deviceInterfacePath, ref iid, activationParams, completionHandlerPtr, out var resultPtr);
                LoggerService.LogDebug($"ActivateAudioInterface: P/Invoke returned HRESULT=0x{hr:X8}, resultPtr=0x{resultPtr:X}");

                if (hr != 0)
                {
                    var ex = new COMException("ActivateAudioInterfaceAsync failed", hr);
                    LoggerService.LogError($"ActivateAudioInterface: Exception - {ex.Message}");
                    throw ex;
                }

                LoggerService.LogDebug($"ActivateAudioInterface: Waiting for completion...");
                completionHandler.WaitForCompletion();
                LoggerService.LogDebug($"ActivateAudioInterface: Completion handler completed");

                audioClient = completionHandler.GetActivatedInterface();
                LoggerService.LogDebug($"ActivateAudioInterface: GetActivatedInterface returned, audioClient is null: {audioClient == null}");

                if (audioClient != null && resultPtr != IntPtr.Zero)
                {
                    LoggerService.LogDebug($"ActivateAudioInterface: Releasing resultPtr=0x{resultPtr:X}");
                    Marshal.Release(resultPtr);
                }
                if (audioClient == null)
                {
                    throw new InvalidOperationException("ActivateAudioInterface: audioClient is null after activation");
                }
                LoggerService.LogInfo("ActivateAudioInterface: Success");
                return audioClient;
            }
            finally
            {
                LoggerService.LogDebug($"ActivateAudioInterface: Releasing completionHandlerPtr=0x{completionHandlerPtr:X}");
                Marshal.Release(completionHandlerPtr);
                LoggerService.LogDebug("ActivateAudioInterface: completionHandlerPtr released successfully");
            }
        }
        finally
        {
            LoggerService.LogDebug("ActivateAudioInterface: Freeing GCHandle");
            gcHandle.Free();
            LoggerService.LogDebug("ActivateAudioInterface: GCHandle freed");
        }

        // This should never be reached, but compiler requires it
        throw new InvalidOperationException("Unexpected code path in ActivateAudioInterface");
    }

    private static IAudioCaptureClient GetCaptureClient(IAudioClient audioClient)
    {
        LoggerService.LogDebug($"GetCaptureClient: Called with audioClient Type={audioClient.GetType().Name}");
        var iid = typeof(IAudioCaptureClient).GUID;
        LoggerService.LogDebug($"GetCaptureClient: IAudioCaptureClient IID={iid:B}");
        try
        {
            ThrowOnError(audioClient.GetService(ref iid, out var captureClient));
            LoggerService.LogDebug($"GetCaptureClient: GetService succeeded, captureClient Type={captureClient!.GetType().Name}");
            var result = (IAudioCaptureClient)captureClient;
            LoggerService.LogDebug($"GetCaptureClient: Cast to IAudioCaptureClient succeeded");
            return result;
        }
        catch (Exception ex)
        {
            LoggerService.LogError($"GetCaptureClient: Failed - {ex.GetType().Name}: {ex.Message}");
            throw;
        }
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
            byte[]? rentedBuffer = null;
            int rentedSize = 0;

            while (_isCapturing)
            {
                ThrowOnError(_captureClient.GetNextPacketSize(out var packetFrames));
                while (packetFrames > 0)
                {
                    ThrowOnError(_captureClient.GetBuffer(out var dataPointer, out var numFrames, out var flags, out _, out _));
                    var bytesToRead = (int)(numFrames * (uint)frameSize);

                    // ArrayPool を使用してバッファを再利用（パフォーマンス最適化）
                    if (rentedBuffer == null || rentedSize < bytesToRead)
                    {
                        if (rentedBuffer != null)
                        {
                            ArrayPool<byte>.Shared.Return(rentedBuffer);
                        }
                        rentedBuffer = ArrayPool<byte>.Shared.Rent(bytesToRead);
                        rentedSize = rentedBuffer.Length;
                    }

                    if ((flags & AudioClientBufferFlags.Silent) != 0)
                    {
                        Array.Clear(rentedBuffer, 0, bytesToRead);
                    }
                    else
                    {
                        Marshal.Copy(dataPointer, rentedBuffer, 0, bytesToRead);
                    }

                    // WaveInEventArgsは配列をそのまま保持するため、コピーして渡す必要がある
                    var buffer = new byte[bytesToRead];
                    Array.Copy(rentedBuffer, buffer, bytesToRead);
                    DataAvailable?.Invoke(this, new WaveInEventArgs(buffer, bytesToRead));

                    ThrowOnError(_captureClient.ReleaseBuffer(numFrames));
                    ThrowOnError(_captureClient.GetNextPacketSize(out packetFrames));
                }

                Thread.Sleep(CaptureThreadSleepMs);
            }

            // 終了時にバッファを返却
            if (rentedBuffer != null)
            {
                ArrayPool<byte>.Shared.Return(rentedBuffer);
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

    [DllImport("mmdevapi.dll", CallingConvention = CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    private static extern int ActivateAudioInterfaceAsync(
        string deviceInterfacePath,
        ref Guid iid,
        IntPtr activationParams,
        IntPtr completionHandler,
        out IntPtr asyncOperation);

    /// <summary>
    /// IAudioClient インターフェースのGUID (基本)
    /// Process Loopback API は IAudioClient を要求する
    /// </summary>
    private static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

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

    /// <summary>
    /// IAudioClient インターフェース (基本)
    /// Windows Vista以降で使用可能
    /// </summary>
    [ComImport]
    [Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        /// <summary>オーディオストリームを初期化</summary>
        int Initialize(AudioClientShareMode shareMode, AudioClientStreamFlags streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr format, ref Guid audioSessionGuid);

        /// <summary>バッファサイズを取得</summary>
        int GetBufferSize(out uint bufferSize);

        /// <summary>ストリーム遅延を取得</summary>
        int GetStreamLatency(out long latency);

        /// <summary>現在のパディングを取得</summary>
        int GetCurrentPadding(out uint currentPadding);

        /// <summary>フォーマットがサポートされているか確認</summary>
        int IsFormatSupported(AudioClientShareMode shareMode, IntPtr format, out IntPtr closestMatch);

        /// <summary>ミックスフォーマットを取得</summary>
        int GetMixFormat(out IntPtr deviceFormat);

        /// <summary>デバイス期間を取得</summary>
        int GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);

        /// <summary>オーディオストリームを開始</summary>
        int Start();

        /// <summary>オーディオストリームを停止</summary>
        int Stop();

        /// <summary>オーディオストリームをリセット</summary>
        int Reset();

        /// <summary>イベントハンドルを設定</summary>
        int SetEventHandle(IntPtr eventHandle);

        /// <summary>サービスを取得</summary>
        int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    /// <summary>
    /// IAudioClient3 インターフェース (拡張)
    /// Windows 10以降で使用可能
    /// 注意: Process Loopback APIでは使用されない場合がある
    /// </summary>
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
        public AudioClientActivationType ActivationType;
        public AudioClientProcessLoopbackParams ProcessLoopbackParams;
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

    /// <summary>
    /// COMコールバックハンドラ
    /// COM Callable Wrapper として正しく機能するために ComVisible と ClassInterface 属性が必要
    /// </summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ActivateCompletionHandler : IActivateAudioInterfaceCompletionHandler
    {
        private const int ActivationTimeoutMs = 5000;
        private readonly ManualResetEventSlim _completedEvent = new(false);
        private int _activateResult;
        private object? _activatedInterface;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                operation.GetActivateResult(out _activateResult, out var activatedInterface);
                LoggerService.LogDebug($"ActivateCompleted: GetActivateResult returned HRESULT=0x{_activateResult:X8}, Interface={activatedInterface?.GetType().Name ?? "null"}");
                if (_activateResult == 0)
                {
                    if (activatedInterface != null)
                    {
                        LoggerService.LogDebug($"ActivateCompleted: Storing activatedInterface, InterfaceType={activatedInterface.GetType().Name}");
                        _activatedInterface = activatedInterface;
                        LoggerService.LogDebug("ActivateCompleted: Successfully stored activatedInterface");
                    }
                    else
                    {
                        LoggerService.LogError("ActivateCompleted: activatedInterface is null despite HRESULT success");
                    }
                }
                else if (activatedInterface != null)
                {
                    LoggerService.LogDebug($"ActivateCompleted: HRESULT indicates failure (0x{_activateResult:X8}), releasing interface");
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

        public IAudioClient GetActivatedInterface()
        {
            LoggerService.LogDebug($"GetActivatedInterface: _activatedInterface is {(_activatedInterface == null ? "null" : "not null")}, _activateResult=0x{_activateResult:X8}");
            if (_activatedInterface == null)
            {
                var errorMsg = $"Audio client activation failed: _activatedInterface is null, _activateResult=0x{_activateResult:X8}";
                LoggerService.LogError(errorMsg);
                throw new InvalidOperationException(errorMsg);
            }

            LoggerService.LogDebug($"GetActivatedInterface: Attempting to cast _activatedInterface to IAudioClient");
            try
            {
                var audioClient = (IAudioClient)_activatedInterface;
                LoggerService.LogDebug($"GetActivatedInterface: Successfully cast to IAudioClient");

                // Add a reference to keep the COM object alive
                Marshal.AddRef(Marshal.GetIUnknownForObject(audioClient));
                LoggerService.LogDebug($"GetActivatedInterface: Added reference to keep COM object alive");

                return audioClient;
            }
            catch (InvalidCastException castEx)
            {
                LoggerService.LogError($"GetActivatedInterface: Cast failed - {castEx.Message}");

                // Try QueryInterface approach
                try
                {
                    var iid = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"); // IAudioClient IID
                    var hr = Marshal.QueryInterface(Marshal.GetIUnknownForObject(_activatedInterface), in iid, out var pAudioClient);
                    if (hr == 0 && pAudioClient != IntPtr.Zero)
                    {
                        var audioClient = (IAudioClient)Marshal.GetObjectForIUnknown(pAudioClient);
                        Marshal.Release(pAudioClient);
                        LoggerService.LogDebug($"GetActivatedInterface: QueryInterface succeeded");
                        return audioClient;
                    }
                    else
                    {
                        LoggerService.LogError($"GetActivatedInterface: QueryInterface failed, HRESULT=0x{hr:X8}");
                    }
                }
                catch (Exception qiEx)
                {
                    LoggerService.LogError($"GetActivatedInterface: QueryInterface exception - {qiEx.Message}");
                }

                throw;
            }
        }
    }

    private static class AudioFormatSubType
    {
        public static readonly Guid IeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");
    }

}
