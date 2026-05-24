namespace RealTimeTranslator.Core.Models;

public enum UpdateStatus
{
    Idle,
    Checking,
    UpdateAvailable,
    Failed
}

public sealed record UpdateStatusChangedEventArgs(UpdateStatus Status, string Message);
