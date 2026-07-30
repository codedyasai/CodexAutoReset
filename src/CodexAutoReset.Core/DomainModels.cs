namespace CodexAutoReset.Core;

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
    int? RemainingThresholdPercent,
    int PollIntervalMinutes,
    UiLanguage UiLanguage,
    bool StartWithWindows,
    string? CodexExecutablePath,
    bool AutomationEnabled = false,
    bool NotifyOnUsageReset = true,
    int? FiveHourRemainingThresholdPercent = null,
    bool FiveHourAutomationEnabled = false)
{
    public const int MinimumThreshold = 0;
    public const int MaximumThreshold = 99;
    public const int FixedPollIntervalMinutes = 1;
    public const int MinimumPollIntervalMinutes = 1;
    public const int MaximumPollIntervalMinutes = 60;

    public static GuardSettings Default { get; } = new(
        RemainingThresholdPercent: null,
        PollIntervalMinutes: FixedPollIntervalMinutes,
        UiLanguage: UiLanguage.Auto,
        StartWithWindows: false,
        CodexExecutablePath: null,
        AutomationEnabled: false,
        NotifyOnUsageReset: true,
        FiveHourRemainingThresholdPercent: null,
        FiveHourAutomationEnabled: false);

    public int? WeeklyRemainingThresholdPercent => RemainingThresholdPercent;

    public bool WeeklyAutomationEnabled => AutomationEnabled;

    public bool AnyAutomationEnabled =>
        (WeeklyRemainingThresholdPercent.HasValue
            && WeeklyAutomationEnabled)
        || (FiveHourRemainingThresholdPercent.HasValue
            && FiveHourAutomationEnabled);

    public int? GetRemainingThresholdPercent(TriggerLimit triggerLimit) =>
        triggerLimit switch
        {
            TriggerLimit.Weekly => WeeklyRemainingThresholdPercent,
            TriggerLimit.FiveHour => FiveHourRemainingThresholdPercent,
            _ => throw new ArgumentOutOfRangeException(nameof(triggerLimit)),
        };

    public bool IsAutomationEnabled(TriggerLimit triggerLimit) =>
        triggerLimit switch
        {
            TriggerLimit.Weekly =>
                WeeklyRemainingThresholdPercent.HasValue
                && WeeklyAutomationEnabled,
            TriggerLimit.FiveHour =>
                FiveHourRemainingThresholdPercent.HasValue
                && FiveHourAutomationEnabled,
            _ => throw new ArgumentOutOfRangeException(nameof(triggerLimit)),
        };
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
    DateTimeOffset ObservedAt,
    bool ConsumeSchemaCompatible = true);

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
    ScheduledResetImminent,
}

public sealed record GuardDecision(
    DecisionKind Kind,
    DecisionReason Reason,
    WindowReading? TriggerWindow,
    ResetCredit? SelectedCredit,
    string? IntervalKey)
{
    public TriggerLimit? SelectedLimit { get; init; }
}

public sealed record EvaluationResult(
    WindowReading? Weekly,
    GuardDecision Decision,
    long? AvailableCreditCount)
{
    public WindowReading? FiveHour { get; init; }
}

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
