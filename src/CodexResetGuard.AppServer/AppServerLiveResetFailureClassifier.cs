using CodexResetGuard.Core;

namespace CodexResetGuard.AppServer;

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

        return appServerException.Category switch
        {
            AppServerFailureCategory.InvalidResponse =>
                LiveResetFailureDisposition.ProtocolMismatch,
            AppServerFailureCategory.ExecutableNotFound
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
