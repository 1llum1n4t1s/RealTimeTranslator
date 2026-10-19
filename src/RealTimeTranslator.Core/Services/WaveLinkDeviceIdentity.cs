using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;

namespace RealTimeTranslator.Core.Services;

internal enum VoiceAudioDeviceKind
{
    Unavailable,
    Microphone,
    WaveLinkOutput,
}

/// <summary>Parent が null の場合は、取得に成功したデバイスツリーの root を示す。</summary>
internal sealed record WaveLinkDeviceNode(IReadOnlyList<string> HardwareIds, uint? Parent);

/// <summary>Wave Link 3 の仮想ドライバーと用途を、表示名に依存せず識別する。</summary>
internal static partial class WaveLinkDeviceIdentity
{
    private const uint CrSuccess = 0;
    private const uint CrBufferSmall = 0x1A;
    private const uint CrNoSuchValue = 0x25;
    private const uint HardwareIdProperty = 2;
    private const uint RegMultiSz = 7;
    private const int MaxDeviceNodes = 32;
    private const int MaxHardwareIdBytes = 65536;

    internal static VoiceAudioDeviceKind Classify(MMDevice device)
    {
        try
        {
            var instanceId = GetConnectedAdapterInstanceId(device);
            if (instanceId is null
                || LocateDeviceNode(out var adapterNode, instanceId, 0) != CrSuccess
                || LocateDeviceNode(out var rootNode, null, 0) != CrSuccess)
                return VoiceAudioDeviceKind.Unavailable;

            device.Properties.TryGetValue<string>(PropertyKeys.PKEY_Device_DeviceDesc, out var description);
            return Classify(device.DataFlow, description, adapterNode, node => ReadNode(node, rootNode));
        }
        catch (COMException)
        {
            // 取り外し中などでプロパティを読めない機器は候補にしない。
            return VoiceAudioDeviceKind.Unavailable;
        }
    }

    /// <summary>endpoint 自体の PnP ID ではなく、接続先音声アダプターの PnP ID を取得する。</summary>
    internal static string? GetConnectedAdapterInstanceId(MMDevice endpoint)
    {
        try
        {
            // endpoint の topology は1個の connector から音声アダプターへ接続する。
            // NAudio の topology / connector wrapper に Dispose はなく、COM 参照は wrapper が所有する。
            var topology = endpoint.DeviceTopology;
            if (topology.ConnectorCount != 1)
                return null;
            var connector = topology.GetConnector(0);
            if (!connector.IsConnected)
                return null;
            var adapterId = connector.ConnectedToDeviceId;
            if (string.IsNullOrWhiteSpace(adapterId) || string.Equals(adapterId, endpoint.ID, StringComparison.OrdinalIgnoreCase))
                return null;

            using var enumerator = new MMDeviceEnumerator();
            using var adapter = enumerator.GetDevice(adapterId);
            return adapter.Properties.TryGetValue<string>(PropertyKeys.PKEY_Device_InstanceId, out var instanceId)
                && !string.IsNullOrWhiteSpace(instanceId) ? instanceId : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>列挙・開始で共用する判定。readNode の null は取得失敗を示す。</summary>
    internal static VoiceAudioDeviceKind Classify(DataFlow flow, string? description, uint? adapterNode,
        Func<uint, WaveLinkDeviceNode?> readNode)
    {
        if (flow is not (DataFlow.Capture or DataFlow.Render))
            return VoiceAudioDeviceKind.Unavailable;

        var visited = new HashSet<uint>();
        var isWaveLink = false;
        for (var depth = 0; depth < MaxDeviceNodes && adapterNode is { } current; depth++)
        {
            if (!visited.Add(current) || readNode(current) is not { } node)
                return VoiceAudioDeviceKind.Unavailable;

            // 公式 3.0.0.466 INF の HardwareID。部分一致では他製品を許可しない。
            isWaveLink |= node.HardwareIds.Any(id =>
                string.Equals(id, @"USB\VID_0FD9&PID_0096", StringComparison.OrdinalIgnoreCase)
                || string.Equals(id, @"TUSBAUDIO_ENUM\VID_0FD9&PID_0096&KS", StringComparison.OrdinalIgnoreCase));
            if (node.Parent is null)
            {
                // 同じ親を持つ録音 Mix はすべて入力から除外する。
                // Wave Mic は再生側にあっても翻訳の注入先にはしない。
                if (isWaveLink)
                    return flow == DataFlow.Render && description is
                        "System" or "Music" or "Browser" or "Voice Chat" or "SFX" or "Game" or "Aux 1" or "Aux 2"
                        ? VoiceAudioDeviceKind.WaveLinkOutput
                        : VoiceAudioDeviceKind.Unavailable;
                return flow == DataFlow.Capture ? VoiceAudioDeviceKind.Microphone : VoiceAudioDeviceKind.Unavailable;
            }
            adapterNode = node.Parent;
        }

        return VoiceAudioDeviceKind.Unavailable;
    }

    private static WaveLinkDeviceNode? ReadNode(uint deviceNode, uint rootNode)
    {
        // CM_Get_Parent の失敗を root と見なさず、Locate した本当の root だけを終端にする。
        if (deviceNode == rootNode)
            return new WaveLinkDeviceNode([], null);
        var hardwareIds = ReadHardwareIds(deviceNode);
        if (hardwareIds is null || GetParent(out var parent, deviceNode, 0) != CrSuccess)
            return null;
        return new WaveLinkDeviceNode(hardwareIds, parent);
    }

    private static unsafe string[]? ReadHardwareIds(uint deviceNode)
    {
        uint length = 0;
        var result = GetDeviceRegistryProperty(deviceNode, HardwareIdProperty, out _, null, ref length, 0);
        if (result == CrNoSuchValue)
            return [];
        if (result is not (CrBufferSmall or CrSuccess))
            return null;

        // プロパティ更新中のサイズ変更だけを、上限付きで取り直す。
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (length is < 4 or > MaxHardwareIdBytes || (length & 1) != 0)
                return null;
            var buffer = new byte[(int)length];
            uint type;
            fixed (byte* pointer = buffer)
                result = GetDeviceRegistryProperty(deviceNode, HardwareIdProperty, out type, pointer, ref length, 0);
            if (result == CrBufferSmall)
                continue;
            if (result != CrSuccess || type != RegMultiSz || length < 4 || length > buffer.Length || (length & 1) != 0)
                return null;
            var value = Encoding.Unicode.GetString(buffer, 0, (int)length);
            if (!value.EndsWith("\0\0", StringComparison.Ordinal))
                return null;
            if (value.Length == 2)
                return [];
            var hardwareIds = value[..^2].Split('\0');
            return hardwareIds.Any(string.IsNullOrEmpty) ? null : hardwareIds;
        }
        return null;
    }

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint LocateDeviceNode(out uint deviceNode, string? deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Parent")]
    private static partial uint GetParent(out uint parent, uint deviceNode, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_Registry_PropertyW")]
    private static unsafe partial uint GetDeviceRegistryProperty(uint deviceNode, uint property, out uint type,
        byte* buffer, ref uint length, uint flags);
}
