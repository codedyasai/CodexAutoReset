namespace CodexResetGuard.Core;

public interface ISecretProtector
{
    string Protect(string plaintext);

    string Unprotect(string protectedValue);
}

public enum LiveResetFailureDisposition
{
    Retryable,
    ProtocolMismatch,
    Unknown,
}

public interface ILiveResetFailureClassifier
{
    LiveResetFailureDisposition Classify(Exception exception);
}

public sealed record TriggerEvaluation(
    WindowReading? Weekly,
    WindowReading? SelectedWindow,
    DecisionReason Reason,
    string? IntervalKey,
    bool ThresholdReached);

public enum LiveAttemptPhase
{
    Pending,
    Terminal,
    NeedsReview,
    ProtocolBlocked,
}

public enum LiveAttemptBlockReason
{
    ContextChanged,
    LegacyTriggerUnsupported,
    SecretUnavailable,
    ProtocolMismatch,
    UnknownFailure,
    DispatchLimitReached,
}

public sealed record LiveAttemptSnapshot(
    string IntervalKey,
    TriggerLimit TriggerLimit,
    int ThresholdPercent,
    long NormalizedDurationMinutes,
    long ResetsAt,
    LiveAttemptPhase Phase,
    int DispatchCount,
    ConsumeResetCreditOutcome? Outcome,
    LiveAttemptBlockReason? BlockReason,
    bool RefreshRequired,
    DateTimeOffset PreparedAt,
    DateTimeOffset UpdatedAt);

public enum LiveResetCycleKind
{
    AutomationDisabled,
    NoAction,
    Blocked,
    DuplicateSuppressed,
    Completed,
}

public sealed record LiveResetCycleResult(
    LiveResetCycleKind Kind,
    EvaluationResult Evaluation,
    LiveAttemptSnapshot? Attempt,
    ConsumeResetCreditOutcome? Outcome,
    bool ConsumeAttempted,
    bool RequiresRefresh,
    AccountRateLimits? RefreshedRateLimits,
    LiveAttemptBlockReason? ProcessBlockReason = null)
{
    public override string ToString() => nameof(LiveResetCycleResult);
}

public sealed class LiveStateException : Exception
{
    public LiveStateException(string reasonCode)
        : base(reasonCode)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}
