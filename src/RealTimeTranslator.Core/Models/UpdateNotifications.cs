using System;

namespace RealTimeTranslator.Core.Models;

public enum UpdateStatus
{
    Idle,
    Disabled,
    Checking,
    UpdateAvailable,
    ReadyToApply,
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

public class UpdateAvailableEventArgs : EventArgs
{
    public UpdateAvailableEventArgs(string message)
    {
        Message = message;
    }

    public string Message { get; }
}

public class UpdateReadyEventArgs : EventArgs
{
    public UpdateReadyEventArgs(string message)
    {
        Message = message;
    }

    public string Message { get; }
}
