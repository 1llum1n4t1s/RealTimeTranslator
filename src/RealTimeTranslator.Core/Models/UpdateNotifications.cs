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

public sealed record UpdateStatusChangedEventArgs(UpdateStatus Status, string Message);
