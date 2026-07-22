namespace CodexResetGuard.Core;

// Retained only for compatibility with durable live-state records written by
// older releases. New attempts are always weekly.
public enum TriggerLimit
{
    FiveHour,
    Weekly,
}

public enum UiLanguage
{
    Auto,
    Korean,
    English,
}

public sealed record GuardSettings(
    int RemainingThresholdPercent,
    int PollIntervalMinutes,
    UiLanguage UiLanguage,
    bool StartWithWindows,
    string? CodexExecutablePath,
    bool AutomationEnabled = false)
{
    public const int MinimumThreshold = 1;
    public const int MaximumThreshold = 100;
    public const int MinimumPollIntervalMinutes = 1;
    public const int MaximumPollIntervalMinutes = 60;

    public static GuardSettings Default { get; } = new(
        RemainingThresholdPercent: 7,
        PollIntervalMinutes: 5,
        UiLanguage: UiLanguage.Auto,
        StartWithWindows: false,
        CodexExecutablePath: null,
        AutomationEnabled: false);
}

public sealed record RateLimitWindow(
    double UsedPercent,
    long? WindowDurationMins,
    long? ResetsAt);

public sealed record RateLimitSnapshot(
    string? LimitId,
    string? LimitName,
    RateLimitWindow? Primary,
    RateLimitWindow? Secondary);

public sealed record ResetCredit(
    string Id,
    string ResetType,
    string Status,
    long GrantedAt,
    long? ExpiresAt,
    string? Title,
    string? Description);

public sealed record ResetCreditSummary(
    long AvailableCount,
    IReadOnlyList<ResetCredit>? Credits);

public sealed record AccountRateLimits(
    RateLimitSnapshot LegacyRateLimits,
    IReadOnlyDictionary<string, RateLimitSnapshot>? RateLimitsByLimitId,
    ResetCreditSummary? ResetCredits,
    DateTimeOffset ObservedAt);

public sealed record WindowReading(
    double UsedPercent,
    double RemainingPercent,
    long ReportedDurationMinutes,
    long NormalizedDurationMinutes,
    long ResetsAt);

public enum DecisionKind
{
    NoAction,
    Blocked,
    WouldConsume,
}

public enum DecisionReason
{
    AboveThreshold,
    ThresholdReached,
    CodexBucketMissing,
    CodexBucketMismatch,
    AmbiguousLegacyBucket,
    SelectedWindowMissing,
    SelectedWindowAmbiguous,
    InvalidUsedPercent,
    InvalidResetTime,
    CreditSummaryUnavailable,
    InvalidCreditCount,
    NoCredits,
    CreditDetailsUnavailable,
    NoEligibleCredit,
}

public sealed record GuardDecision(
    DecisionKind Kind,
    DecisionReason Reason,
    WindowReading? TriggerWindow,
    ResetCredit? SelectedCredit,
    string? IntervalKey);

public sealed record EvaluationResult(
    WindowReading? Weekly,
    GuardDecision Decision,
    long? AvailableCreditCount);

public sealed record ConsumeResetCreditRequest
{
    public ConsumeResetCreditRequest(
        string idempotencyKey,
        string creditId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(creditId);

        IdempotencyKey = idempotencyKey;
        CreditId = creditId;
    }

    public string IdempotencyKey { get; }

    public string CreditId { get; }

    public override string ToString() => nameof(ConsumeResetCreditRequest);
}

public enum ConsumeResetCreditOutcome
{
    Reset,
    NothingToReset,
    NoCredit,
    AlreadyRedeemed,
}

public sealed record ConsumeResetCreditResult(
    ConsumeResetCreditOutcome Outcome);

public interface IAccountUsageSource : IAsyncDisposable
{
    Task<AccountRateLimits> ReadAsync(CancellationToken cancellationToken);
}

public interface IAccountRateLimitClient : IAccountUsageSource
{
    Task<ConsumeResetCreditResult> ConsumeResetCreditAsync(
        ConsumeResetCreditRequest request,
        CancellationToken cancellationToken);
}
