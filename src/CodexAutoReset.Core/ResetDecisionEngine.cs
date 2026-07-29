using System.Globalization;

namespace CodexAutoReset.Core;

public sealed class ResetDecisionEngine
{
    private const long WeeklyMinutes = 10_080;
    private const long DurationToleranceMinutes = 1;
    private const long ResetClockSkewSeconds = 60;
    private const long MinimumResetLeadTimeSeconds = 5 * 60;
    private const long ResetUpperSlackSeconds = 300;

    public EvaluationResult Evaluate(
        GuardSettings settings,
        AccountRateLimits accountRateLimits,
        DateTimeOffset now)
    {
        var trigger = EvaluateTrigger(settings, accountRateLimits, now);
        if (trigger.SelectedWindow is null)
        {
            return new EvaluationResult(
                trigger.Weekly,
                new GuardDecision(
                    DecisionKind.Blocked,
                    trigger.Reason,
                    null,
                    null,
                    null),
                accountRateLimits.ResetCredits?.AvailableCount);
        }

        var triggerWindow = trigger.SelectedWindow;
        if (!trigger.ThresholdReached)
        {
            return new EvaluationResult(
                trigger.Weekly,
                new GuardDecision(
                    DecisionKind.NoAction,
                    trigger.Reason,
                    triggerWindow,
                    null,
                    trigger.IntervalKey),
                accountRateLimits.ResetCredits?.AvailableCount);
        }

        var creditSummary = accountRateLimits.ResetCredits;
        if (creditSummary is null)
        {
            return WithDecision(
                trigger.Weekly,
                triggerWindow,
                DecisionKind.Blocked,
                DecisionReason.CreditSummaryUnavailable,
                null,
                null);
        }

        if (creditSummary.AvailableCount < 0)
        {
            return WithDecision(
                trigger.Weekly,
                triggerWindow,
                DecisionKind.Blocked,
                DecisionReason.InvalidCreditCount,
                null,
                creditSummary.AvailableCount);
        }

        if (creditSummary.AvailableCount == 0)
        {
            return WithDecision(
                trigger.Weekly,
                triggerWindow,
                DecisionKind.NoAction,
                DecisionReason.NoCredits,
                null,
                creditSummary.AvailableCount);
        }

        if (creditSummary.Credits is null)
        {
            return WithDecision(
                trigger.Weekly,
                triggerWindow,
                DecisionKind.Blocked,
                DecisionReason.CreditDetailsUnavailable,
                null,
                creditSummary.AvailableCount);
        }

        var nowUnix = now.ToUnixTimeSeconds();
        var eligibleCredit = creditSummary.Credits
            .Where(credit => !string.IsNullOrWhiteSpace(credit.Id))
            .Where(credit => string.Equals(
                credit.ResetType,
                "codexRateLimits",
                StringComparison.Ordinal))
            .Where(credit => string.Equals(
                credit.Status,
                "available",
                StringComparison.Ordinal))
            .Where(credit => credit.ExpiresAt is null || credit.ExpiresAt > nowUnix)
            .OrderBy(credit => credit.ExpiresAt ?? long.MaxValue)
            .ThenBy(credit => credit.Id, StringComparer.Ordinal)
            .FirstOrDefault();

        if (eligibleCredit is null)
        {
            return WithDecision(
                trigger.Weekly,
                triggerWindow,
                DecisionKind.Blocked,
                DecisionReason.NoEligibleCredit,
                null,
                creditSummary.AvailableCount);
        }

        return WithDecision(
            trigger.Weekly,
            triggerWindow,
            DecisionKind.WouldConsume,
            DecisionReason.ThresholdReached,
            eligibleCredit,
            creditSummary.AvailableCount,
            trigger.IntervalKey);
    }

    public TriggerEvaluation EvaluateTrigger(
        GuardSettings settings,
        AccountRateLimits accountRateLimits,
        DateTimeOffset now)
    {
        JsonSettingsStore.Validate(settings);

        var bucketResult = SelectCodexBucket(accountRateLimits);
        if (bucketResult.Snapshot is null)
        {
            return new TriggerEvaluation(
                null,
                null,
                bucketResult.Reason,
                null,
                false);
        }

        var weekly = FindWeeklyWindow(bucketResult.Snapshot, now);
        if (weekly.Reading is null)
        {
            return new TriggerEvaluation(
                null,
                null,
                weekly.Reason,
                null,
                false);
        }

        var intervalKey = BuildIntervalKey(bucketResult.Snapshot, weekly.Reading);
        if (weekly.Reason == DecisionReason.ScheduledResetImminent)
        {
            return new TriggerEvaluation(
                weekly.Reading,
                weekly.Reading,
                DecisionReason.ScheduledResetImminent,
                intervalKey,
                false);
        }

        var thresholdReached = weekly.Reading.RemainingPercent
            <= settings.RemainingThresholdPercent;

        return new TriggerEvaluation(
            weekly.Reading,
            weekly.Reading,
            thresholdReached
                ? DecisionReason.ThresholdReached
                : DecisionReason.AboveThreshold,
            intervalKey,
            thresholdReached);
    }

    private static BucketSelection SelectCodexBucket(AccountRateLimits limits)
    {
        if (limits.RateLimitsByLimitId is { } byLimitId)
        {
            foreach (var pair in byLimitId)
            {
                if (string.Equals(pair.Key, "codex", StringComparison.OrdinalIgnoreCase))
                {
                    if (pair.Value.LimitId is { } snapshotLimitId
                        && !string.Equals(
                            snapshotLimitId,
                            "codex",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return new BucketSelection(
                            null,
                            DecisionReason.CodexBucketMismatch);
                    }

                    return new BucketSelection(pair.Value, DecisionReason.CodexBucketMissing);
                }
            }

            return new BucketSelection(null, DecisionReason.CodexBucketMissing);
        }

        var legacyId = limits.LegacyRateLimits.LimitId;
        if (string.Equals(legacyId, "codex", StringComparison.OrdinalIgnoreCase))
        {
            return new BucketSelection(limits.LegacyRateLimits, DecisionReason.CodexBucketMissing);
        }

        return new BucketSelection(null, DecisionReason.AmbiguousLegacyBucket);
    }

    private static WindowSelection FindWeeklyWindow(
        RateLimitSnapshot snapshot,
        DateTimeOffset now)
    {
        var candidates = new[] { snapshot.Primary, snapshot.Secondary }
            .Where(window => window is not null)
            .Cast<RateLimitWindow>()
            .Where(window => window.WindowDurationMins is long duration
                && duration >= WeeklyMinutes - DurationToleranceMinutes
                && duration <= WeeklyMinutes + DurationToleranceMinutes)
            .ToArray();

        if (candidates.Length == 0)
        {
            return new WindowSelection(null, DecisionReason.SelectedWindowMissing);
        }

        if (candidates.Length > 1)
        {
            return new WindowSelection(null, DecisionReason.SelectedWindowAmbiguous);
        }

        var selected = candidates[0];
        if (!double.IsFinite(selected.UsedPercent)
            || selected.UsedPercent is < 0 or > 100)
        {
            return new WindowSelection(null, DecisionReason.InvalidUsedPercent);
        }

        if (selected.ResetsAt is not long resetsAt)
        {
            return new WindowSelection(null, DecisionReason.InvalidResetTime);
        }

        var nowUnix = now.ToUnixTimeSeconds();
        var maximumResetAt = nowUnix + (WeeklyMinutes * 60) + ResetUpperSlackSeconds;
        if (resetsAt < nowUnix - ResetClockSkewSeconds || resetsAt > maximumResetAt)
        {
            return new WindowSelection(null, DecisionReason.InvalidResetTime);
        }

        return new WindowSelection(
            new WindowReading(
                selected.UsedPercent,
                100d - selected.UsedPercent,
                selected.WindowDurationMins!.Value,
                WeeklyMinutes,
                resetsAt),
            resetsAt < nowUnix + MinimumResetLeadTimeSeconds
                ? DecisionReason.ScheduledResetImminent
                : DecisionReason.AboveThreshold);
    }

    private static EvaluationResult WithDecision(
        WindowReading? weekly,
        WindowReading triggerWindow,
        DecisionKind kind,
        DecisionReason reason,
        ResetCredit? credit,
        long? availableCreditCount,
        string? intervalKey = null) => new(
            weekly,
            new GuardDecision(
                kind,
                reason,
                triggerWindow,
                credit,
                intervalKey),
            availableCreditCount);

    private static string BuildIntervalKey(
        RateLimitSnapshot snapshot,
        WindowReading window)
    {
        var limitId = string.IsNullOrWhiteSpace(snapshot.LimitId)
            ? "codex"
            : snapshot.LimitId.Trim().ToLowerInvariant();

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{limitId}|weekly|{window.NormalizedDurationMinutes}|{window.ResetsAt}");
    }

    private sealed record BucketSelection(
        RateLimitSnapshot? Snapshot,
        DecisionReason Reason);

    private sealed record WindowSelection(
        WindowReading? Reading,
        DecisionReason Reason);
}
