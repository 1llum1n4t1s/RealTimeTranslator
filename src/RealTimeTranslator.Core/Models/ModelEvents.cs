namespace RealTimeTranslator.Core.Models;

public enum ModelStatusType
{
    Info,
    Downloading,
    DownloadCompleted,
    DownloadFailed,
    LoadSucceeded,
    LoadFailed,
    Fallback
}

public sealed class ModelDownloadProgressEventArgs : EventArgs
{
    public string ServiceName { get; }
    public string ModelName { get; }
    public long BytesReceived { get; }
    public long? TotalBytes { get; }
    public double? ProgressPercentage { get; }

    public ModelDownloadProgressEventArgs(
        string serviceName,
        string modelName,
        long bytesReceived,
        long? totalBytes,
        double? progressPercentage)
    {
        ServiceName = serviceName;
        ModelName = modelName;
        BytesReceived = bytesReceived;
        TotalBytes = totalBytes;
        ProgressPercentage = progressPercentage;
    }
}

public sealed class ModelStatusChangedEventArgs : EventArgs
{
    public string ServiceName { get; }
    public string ModelName { get; }
    public ModelStatusType Status { get; }
    public string Message { get; }
    public Exception? Exception { get; }

    public ModelStatusChangedEventArgs(
        string serviceName,
        string modelName,
        ModelStatusType status,
        string message,
        Exception? exception = null)
    {
        ServiceName = serviceName;
        ModelName = modelName;
        Status = status;
        Message = message;
        Exception = exception;
    }
}
