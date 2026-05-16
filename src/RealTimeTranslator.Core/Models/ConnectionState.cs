namespace RealTimeTranslator.Core.Models;

/// <summary>
/// Realtime API クライアントの接続状態。
/// </summary>
/// <remarks>
/// Interface 層（Core.Interfaces）が Services 層（Core.Services）の型に依存しないよう、
/// この enum はドメインモデルとして Core.Models に置く（DIP / 依存方向の整合）。
/// </remarks>
public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Failed
}
