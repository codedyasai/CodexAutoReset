namespace CodexAutoReset.AppServer;

public enum AppServerFailureCategory
{
    ExecutableNotFound,
    StartFailed,
    ProcessExited,
    Timeout,
    InvalidResponse,
    RemoteError,
    IoError,
    OutboundMethodNotAllowed,
    InvalidOutboundMessage,
    UntrustedExecutableForMutation,
}

public sealed class AppServerException : Exception
{
    public AppServerException(
        AppServerFailureCategory category,
        int? remoteCode = null,
        Exception? innerException = null)
        : base(category.ToString(), innerException)
    {
        Category = category;
        RemoteCode = remoteCode;
    }

    public AppServerFailureCategory Category { get; }

    public int? RemoteCode { get; }
}
