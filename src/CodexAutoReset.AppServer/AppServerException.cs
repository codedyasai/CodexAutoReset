namespace CodexAutoReset.AppServer;

public enum AppServerFailureCategory
{
    ExecutableNotFound,
    ExecutableBecameUnavailable,
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

public enum AppServerOperation
{
    Unknown,
    Read,
    Mutation,
}

public sealed class AppServerException : Exception
{
    public AppServerException(
        AppServerFailureCategory category,
        int? remoteCode = null,
        Exception? innerException = null,
        AppServerOperation operation = AppServerOperation.Unknown)
        : base(category.ToString(), innerException)
    {
        Category = category;
        RemoteCode = remoteCode;
        Operation = operation;
    }

    public AppServerFailureCategory Category { get; }

    public int? RemoteCode { get; }

    public AppServerOperation Operation { get; private set; }

    internal void AttachOperation(AppServerOperation operation)
    {
        if (Operation == AppServerOperation.Unknown)
        {
            Operation = operation;
        }
    }
}
