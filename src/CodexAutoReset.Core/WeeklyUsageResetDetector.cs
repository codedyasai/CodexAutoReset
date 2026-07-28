namespace CodexAutoReset.Core;

public enum WeeklyUsageResetKind
{
    Scheduled,
    Early,
    AutomaticCredit,
}

public enum WeeklyUsageResetAttribution
{
    None,
    AutomaticCreditSucceeded,
}

public enum WeeklyUsageObservationDisposition
{
    Invalid,
    FirstObservation,
    NoReset,
    ResetDetected,
    IgnoredOutOfOrder,
    IgnoredResetTimeRegression,
}

public sealed record WeeklyUsageObservation(
    double RemainingPercent,
    long ResetsAt,
    DateTimeOffset ObservedAt);

public sealed record WeeklyUsageResetDetection(
    WeeklyUsageResetKind Kind,
    long NextResetsAt,
    DateTimeOffset DetectedAt);

public sealed record WeeklyUsageResetEvaluation(
    WeeklyUsageObservationDisposition Disposition,
    bool ShouldPersistObservation,
    WeeklyUsageResetDetection? Detection);

public static class WeeklyUsageResetDetector
{
    private const long MaximumUnixTimeSeconds = 253_402_300_799;

    public static WeeklyUsageResetEvaluation Evaluate(
        WeeklyUsageObservation? previous,
        WeeklyUsageObservation current,
        WeeklyUsageResetAttribution attribution = WeeklyUsageResetAttribution.None)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (!IsValid(current) || !Enum.IsDefined(attribution))
        {
            return Ignored(WeeklyUsageObservationDisposition.Invalid);
        }

        if (previous is null)
        {
            return new WeeklyUsageResetEvaluation(
                WeeklyUsageObservationDisposition.FirstObservation,
                ShouldPersistObservation: true,
                Detection: null);
        }

        if (!IsValid(previous))
        {
            return Ignored(WeeklyUsageObservationDisposition.Invalid);
        }

        if (current.ObservedAt < previous.ObservedAt)
        {
            return Ignored(WeeklyUsageObservationDisposition.IgnoredOutOfOrder);
        }

        if (current.ResetsAt < previous.ResetsAt)
        {
            return Ignored(
                WeeklyUsageObservationDisposition.IgnoredResetTimeRegression);
        }

        var resetTimeAdvanced = current.ResetsAt > previous.ResetsAt;
        var remainingIncreasedAtSameResetTime =
            current.ResetsAt == previous.ResetsAt
            && current.RemainingPercent > previous.RemainingPercent;

        if (!resetTimeAdvanced && !remainingIncreasedAtSameResetTime)
        {
            return new WeeklyUsageResetEvaluation(
                WeeklyUsageObservationDisposition.NoReset,
                ShouldPersistObservation: true,
                Detection: null);
        }

        var kind = attribution == WeeklyUsageResetAttribution.AutomaticCreditSucceeded
            ? WeeklyUsageResetKind.AutomaticCredit
            : current.ObservedAt
                >= DateTimeOffset.FromUnixTimeSeconds(previous.ResetsAt)
                ? WeeklyUsageResetKind.Scheduled
                : WeeklyUsageResetKind.Early;

        return new WeeklyUsageResetEvaluation(
            WeeklyUsageObservationDisposition.ResetDetected,
            ShouldPersistObservation: true,
            new WeeklyUsageResetDetection(
                kind,
                current.ResetsAt,
                current.ObservedAt));
    }

    public static bool IsValid(WeeklyUsageObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        return double.IsFinite(observation.RemainingPercent)
            && observation.RemainingPercent is >= 0 and <= 100
            && observation.ResetsAt is > 0 and <= MaximumUnixTimeSeconds
            && observation.ObservedAt >= DateTimeOffset.UnixEpoch;
    }

    private static WeeklyUsageResetEvaluation Ignored(
        WeeklyUsageObservationDisposition disposition) => new(
            disposition,
            ShouldPersistObservation: false,
            Detection: null);
}
