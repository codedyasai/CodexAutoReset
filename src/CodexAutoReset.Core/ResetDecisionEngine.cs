using System.Globalization;

namespace CodexAutoReset.Core;

public sealed class ResetDecisionEngine
{
    private const long FiveHourMinutes = 300;
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
            return CreateEvaluation(
                trigger,
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
            return CreateEvaluation(
                trigger,
                new GuardDecision(
                    DecisionKind.NoAction,
                    trigger.Reason,
                    triggerWindow,
                    null,
                    trigger.IntervalKey)
                {
                    SelectedLimit = trigger.SelectedLimit,
                },
                accountRateLimits.ResetCredits?.AvailableCount);
        }

        var creditSummary = accountRateLimits.ResetCredits;
        if (creditSummary is null)
        {
            return WithDecision(
                trigger,
                DecisionKind.Blocked,
                DecisionReason.CreditSummaryUnavailable,
                null,
                null);
        }

        if (creditSummary.AvailableCount < 0)
        {
            return WithDecision(
                trigger,
                DecisionKind.Blocked,
                DecisionReason.InvalidCreditCount,
                null,
                creditSummary.AvailableCount);
        }

        if (creditSummary.AvailableCount == 0)
        {
            return WithDecision(
                trigger,
                DecisionKind.NoAction,
                DecisionReason.NoCredits,
                null,
                creditSummary.AvailableCount);
        }

        if (creditSummary.Credits is null)
        {
            return WithDecision(
                trigger,
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
                trigger,
                DecisionKind.Blocked,
                DecisionReason.NoEligibleCredit,
                null,
                creditSummary.AvailableCount);
        }

        return WithDecision(
            trigger,
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

        var weekly = FindWindow(
            bucketResult.Snapshot,
            TriggerLimit.Weekly,
            now,
            required: true);
        var fiveHour = FindWindow(
            bucketResult.Snapshot,
            TriggerLimit.FiveHour,
            now,
            required: false);
        if (weekly.Reading is null)
        {
            return new TriggerEvaluation(
                null,
                null,
                weekly.Reason,
                null,
                false)
            {
                FiveHour = fiveHour.Reading,
            };
        }

        if (fiveHour.Reading is null
            && fiveHour.Reason is not DecisionReason.AboveThreshold)
        {
            return new TriggerEvaluation(
                weekly.Reading,
                null,
                fiveHour.Reason,
                null,
                false)
            {
                FiveHour = null,
            };
        }

        var candidates = CreateTriggerCandidates(settings, weekly, fiveHour);
        var reached = candidates
            .Where(candidate => candidate.Window.Reason
                != DecisionReason.ScheduledResetImminent)
            .Where(candidate => candidate.Window.Reading!.RemainingPercent
                <= candidate.ThresholdPercent)
            .OrderBy(candidate => candidate.Limit == TriggerLimit.Weekly ? 0 : 1)
            .FirstOrDefault();
        if (reached is not null)
        {
            return CreateTriggerEvaluation(
                bucketResult.Snapshot,
                weekly.Reading,
                fiveHour.Reading,
                reached,
                DecisionReason.ThresholdReached,
                thresholdReached: true);
        }

        var imminent = candidates
            .Where(candidate => candidate.Window.Reason
                == DecisionReason.ScheduledResetImminent)
            .Where(candidate => candidate.Window.Reading!.RemainingPercent
                <= candidate.ThresholdPercent)
            .OrderBy(candidate => candidate.Limit == TriggerLimit.Weekly ? 0 : 1)
            .FirstOrDefault();
        if (imminent is not null)
        {
            return CreateTriggerEvaluation(
                bucketResult.Snapshot,
                weekly.Reading,
                fiveHour.Reading,
                imminent,
                DecisionReason.ScheduledResetImminent,
                thresholdReached: false);
        }

        var selected = candidates
            .OrderBy(candidate => candidate.Limit == TriggerLimit.Weekly ? 0 : 1)
            .FirstOrDefault()
            ?? new TriggerCandidate(
                TriggerLimit.Weekly,
                weekly,
                GuardSettings.MinimumThreshold);

        return CreateTriggerEvaluation(
            bucketResult.Snapshot,
            weekly.Reading,
            fiveHour.Reading,
            selected,
            DecisionReason.AboveThreshold,
            thresholdReached: false);
    }

    private static IReadOnlyList<TriggerCandidate> CreateTriggerCandidates(
        GuardSettings settings,
        WindowSelection weekly,
        WindowSelection fiveHour)
    {
        var candidates = new List<TriggerCandidate>(2);

        if (weekly.Reading is not null
            && settings.IsAutomationEnabled(TriggerLimit.Weekly)
            && settings.WeeklyRemainingThresholdPercent is { } weeklyThreshold)
        {
            candidates.Add(new TriggerCandidate(
                TriggerLimit.Weekly,
                weekly,
                weeklyThreshold));
        }

        if (fiveHour.Reading is not null
            && settings.IsAutomationEnabled(TriggerLimit.FiveHour)
            && settings.FiveHourRemainingThresholdPercent is { } threshold)
        {
            candidates.Add(new TriggerCandidate(
                TriggerLimit.FiveHour,
                fiveHour,
                threshold));
        }

        return candidates;
    }

    private static TriggerEvaluation CreateTriggerEvaluation(
        RateLimitSnapshot snapshot,
        WindowReading weekly,
        WindowReading? fiveHour,
        TriggerCandidate selected,
        DecisionReason reason,
        bool thresholdReached) => new(
            weekly,
            selected.Window.Reading,
            reason,
            BuildIntervalKey(
                snapshot,
                selected.Window.Reading!,
                selected.Limit),
            thresholdReached)
        {
            FiveHour = fiveHour,
            SelectedLimit = selected.Limit,
        };

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

    private static WindowSelection FindWindow(
        RateLimitSnapshot snapshot,
        TriggerLimit triggerLimit,
        DateTimeOffset now,
        bool required)
    {
        var expectedDurationMinutes = triggerLimit switch
        {
            TriggerLimit.FiveHour => FiveHourMinutes,
            TriggerLimit.Weekly => WeeklyMinutes,
            _ => throw new ArgumentOutOfRangeException(nameof(triggerLimit)),
        };
        var candidates = new[] { snapshot.Primary, snapshot.Secondary }
            .Where(window => window is not null)
            .Cast<RateLimitWindow>()
            .Where(window => window.WindowDurationMins is long duration
                && duration >= expectedDurationMinutes - DurationToleranceMinutes
                && duration <= expectedDurationMinutes + DurationToleranceMinutes)
            .ToArray();

        if (candidates.Length == 0)
        {
            return new WindowSelection(
                null,
                required
                    ? DecisionReason.SelectedWindowMissing
                    : DecisionReason.AboveThreshold);
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
        var maximumResetAt =
            nowUnix + (expectedDurationMinutes * 60) + ResetUpperSlackSeconds;
        if (resetsAt < nowUnix - ResetClockSkewSeconds || resetsAt > maximumResetAt)
        {
            return new WindowSelection(null, DecisionReason.InvalidResetTime);
        }

        return new WindowSelection(
            new WindowReading(
                selected.UsedPercent,
                100d - selected.UsedPercent,
                selected.WindowDurationMins!.Value,
                expectedDurationMinutes,
                resetsAt),
            resetsAt < nowUnix + MinimumResetLeadTimeSeconds
                ? DecisionReason.ScheduledResetImminent
                : DecisionReason.AboveThreshold);
    }

    private static EvaluationResult WithDecision(
        TriggerEvaluation trigger,
        DecisionKind kind,
        DecisionReason reason,
        ResetCredit? credit,
        long? availableCreditCount,
        string? intervalKey = null) => CreateEvaluation(
            trigger,
            new GuardDecision(
                kind,
                reason,
                trigger.SelectedWindow,
                credit,
                intervalKey)
            {
                SelectedLimit = trigger.SelectedLimit,
            },
            availableCreditCount);

    private static EvaluationResult CreateEvaluation(
        TriggerEvaluation trigger,
        GuardDecision decision,
        long? availableCreditCount) => new(
            trigger.Weekly,
            decision,
            availableCreditCount)
        {
            FiveHour = trigger.FiveHour,
        };

    private static string BuildIntervalKey(
        RateLimitSnapshot snapshot,
        WindowReading window,
        TriggerLimit triggerLimit)
    {
        var limitId = string.IsNullOrWhiteSpace(snapshot.LimitId)
            ? "codex"
            : snapshot.LimitId.Trim().ToLowerInvariant();
        var trigger = triggerLimit == TriggerLimit.FiveHour
            ? "fiveHour"
            : "weekly";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{limitId}|{trigger}|{window.NormalizedDurationMinutes}|{window.ResetsAt}");
    }

    private sealed record BucketSelection(
        RateLimitSnapshot? Snapshot,
        DecisionReason Reason);

    private sealed record WindowSelection(
        WindowReading? Reading,
        DecisionReason Reason);

    private sealed record TriggerCandidate(
        TriggerLimit Limit,
        WindowSelection Window,
        int ThresholdPercent);
}
