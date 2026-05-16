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

public class UpdateAvailableEventArgs : EventArgs
{
    public UpdateAvailableEventArgs(string message, object updateData)
    {
        Message = message;
        UpdateData = updateData;
    }

    public string Message { get; }

    /// <summary>
    /// SelfUpdateWindow に渡す結果オブジェクト。VelopackUpdate / AlreadyUpToDate / SelfUpdateFailed
    /// のいずれかが入る。object 型にすることで Core が UI 層の VelopackUpdate に型依存しない。
    /// </summary>
    public object UpdateData { get; }
}
