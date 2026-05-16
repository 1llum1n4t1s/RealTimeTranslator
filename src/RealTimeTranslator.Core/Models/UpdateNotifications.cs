using System;

namespace RealTimeTranslator.Core.Models;

public enum UpdateStatus
{
    Idle,
    Disabled,
    Checking,
    UpdateAvailable,
    Failed
}

public class UpdateStatusChangedEventArgs : EventArgs
{
    public UpdateStatusChangedEventArgs(UpdateStatus status, string message)
    {
        Status = status;
        Message = message;
    }

    public UpdateStatus Status { get; }

    public string Message { get; }
}
