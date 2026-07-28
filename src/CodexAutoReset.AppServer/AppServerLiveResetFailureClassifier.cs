using CodexAutoReset.Core;

namespace CodexAutoReset.AppServer;

public sealed class AppServerLiveResetFailureClassifier : ILiveResetFailureClassifier
{
    private AppServerLiveResetFailureClassifier()
    {
    }

    public static AppServerLiveResetFailureClassifier Instance { get; } = new();

    public LiveResetFailureDisposition Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is not AppServerException appServerException)
        {
            return LiveResetFailureDisposition.Unknown;
        }

        if (appServerException.Category == AppServerFailureCategory.RemoteError
            && appServerException.RemoteCode is -32601 or -32602)
        {
            return LiveResetFailureDisposition.ProtocolMismatch;
        }

        return appServerException.Category switch
        {
            AppServerFailureCategory.InvalidResponse =>
                LiveResetFailureDisposition.ProtocolMismatch,
            AppServerFailureCategory.ExecutableNotFound
                or AppServerFailureCategory.ExecutableBecameUnavailable
                or AppServerFailureCategory.StartFailed
                or AppServerFailureCategory.ProcessExited
                or AppServerFailureCategory.Timeout
                or AppServerFailureCategory.RemoteError
                or AppServerFailureCategory.IoError =>
                LiveResetFailureDisposition.Retryable,
            AppServerFailureCategory.OutboundMethodNotAllowed
                or AppServerFailureCategory.InvalidOutboundMessage =>
                LiveResetFailureDisposition.Unknown,
            _ => LiveResetFailureDisposition.Unknown,
        };
    }
}
