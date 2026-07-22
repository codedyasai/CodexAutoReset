using CodexResetGuard.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexResetGuard.Tests;

[TestClass]
public sealed class DecisionEngineTests
{
    private readonly ResetDecisionEngine engine = new();

    [TestMethod]
    public void WeeklyTriggerEvaluationDoesNotDependOnCreditAvailability()
    {
        var now = DateTimeOffset.UtcNow;
        var limits = CreateLimits(now, weeklyUsed: 93) with { ResetCredits = null };

        var trigger = engine.EvaluateTrigger(GuardSettings.Default, limits, now);

        Assert.IsTrue(trigger.ThresholdReached);
        Assert.AreEqual(DecisionReason.ThresholdReached, trigger.Reason);
        Assert.AreEqual(10_080L, trigger.Weekly!.NormalizedDurationMinutes);
        StringAssert.Contains(trigger.IntervalKey!, "|weekly|10080|");
    }

    [TestMethod]
    public void RemainingEqualToThresholdProducesEligibleConsumeDecision()
    {
        var now = DateTimeOffset.UtcNow;

        var result = engine.Evaluate(
            GuardSettings.Default,
            CreateLimits(now, weeklyUsed: 93),
            now);

        Assert.AreEqual(DecisionKind.WouldConsume, result.Decision.Kind);
        Assert.AreEqual(DecisionReason.ThresholdReached, result.Decision.Reason);
        Assert.AreEqual(7d, result.Decision.TriggerWindow!.RemainingPercent);
        StringAssert.Contains(result.Decision.IntervalKey!, "|weekly|10080|");
    }

    [TestMethod]
    public void RemainingAboveThresholdDoesNothing()
    {
        var now = DateTimeOffset.UtcNow;

        var result = engine.Evaluate(
            GuardSettings.Default,
            CreateLimits(now, weeklyUsed: 92),
            now);

        Assert.AreEqual(DecisionKind.NoAction, result.Decision.Kind);
        Assert.AreEqual(DecisionReason.AboveThreshold, result.Decision.Reason);
    }

    [DataTestMethod]
    [DataRow(300L)]
    [DataRow(301L)]
    [DataRow(60L)]
    [DataRow(43_200L)]
    public void ExtraOtherDurationWindowIsIgnored(long otherDuration)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new RateLimitSnapshot(
            "codex",
            null,
            new RateLimitWindow(99, otherDuration, now.AddHours(4).ToUnixTimeSeconds()),
            new RateLimitWindow(93, 10_079, now.AddDays(5).ToUnixTimeSeconds()));

        var result = engine.Evaluate(
            GuardSettings.Default,
            CreateLimits(now, snapshot),
            now);

        Assert.AreEqual(DecisionKind.WouldConsume, result.Decision.Kind);
        Assert.AreEqual(10_080L, result.Weekly!.NormalizedDurationMinutes);
    }

    [DataTestMethod]
    [DataRow(10_079L)]
    [DataRow(10_080L)]
    [DataRow(10_081L)]
    public void WeeklyDurationToleranceAcceptsExactlyOneMinute(long duration)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new RateLimitSnapshot(
            "codex",
            null,
            new RateLimitWindow(93, duration, now.AddDays(5).ToUnixTimeSeconds()),
            null);

        var result = engine.Evaluate(
            GuardSettings.Default,
            CreateLimits(now, snapshot),
            now);

        Assert.AreEqual(DecisionKind.WouldConsume, result.Decision.Kind);
        Assert.AreEqual(duration, result.Weekly!.ReportedDurationMinutes);
        Assert.AreEqual(10_080L, result.Weekly.NormalizedDurationMinutes);
    }

    [DataTestMethod]
    [DataRow(10_078L)]
    [DataRow(10_082L)]
    [DataRow(long.MinValue)]
    public void MissingWeeklyWindowFailsClosed(long duration)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new RateLimitSnapshot(
            "codex",
            null,
            new RateLimitWindow(93, duration, now.AddDays(5).ToUnixTimeSeconds()),
            new RateLimitWindow(20, 300, now.AddHours(4).ToUnixTimeSeconds()));

        var result = engine.Evaluate(
            GuardSettings.Default,
            CreateLimits(now, snapshot),
            now);

        Assert.AreEqual(DecisionKind.Blocked, result.Decision.Kind);
        Assert.AreEqual(DecisionReason.SelectedWindowMissing, result.Decision.Reason);
        Assert.IsNull(result.Weekly);
    }

    [TestMethod]
    public void DuplicateMatchingWeeklyWindowsFailClosed()
    {
        var now = DateTimeOffset.UtcNow;
        var reset = now.AddDays(5).ToUnixTimeSeconds();
        var snapshot = new RateLimitSnapshot(
            "codex",
            null,
            new RateLimitWindow(93, 10_080, reset),
            new RateLimitWindow(93, 10_081, reset));

        var result = engine.Evaluate(
            GuardSettings.Default,
            CreateLimits(now, snapshot),
            now);

        Assert.AreEqual(DecisionKind.Blocked, result.Decision.Kind);
        Assert.AreEqual(DecisionReason.SelectedWindowAmbiguous, result.Decision.Reason);
    }

    [DataTestMethod]
    [DataRow(-0.1)]
    [DataRow(100.1)]
    [DataRow(double.NaN)]
    public void InvalidUsagePercentageFailsClosed(double usedPercent)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = CreateSnapshot(now, usedPercent);

        var result = engine.Evaluate(
            GuardSettings.Default,
            CreateLimits(now, snapshot),
            now);

        Assert.AreEqual(DecisionKind.Blocked, result.Decision.Kind);
        Assert.AreEqual(DecisionReason.InvalidUsedPercent, result.Decision.Reason);
    }

    [TestMethod]
    public void MissingWeeklyResetTimeFailsClosed()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new RateLimitSnapshot(
            "codex",
            null,
            new RateLimitWindow(20, 300, now.AddHours(4).ToUnixTimeSeconds()),
            new RateLimitWindow(93, 10_080, null));

        var result = engine.Evaluate(
            GuardSettings.Default,
            CreateLimits(now, snapshot),
            now);

        Assert.AreEqual(DecisionKind.Blocked, result.Decision.Kind);
        Assert.AreEqual(DecisionReason.InvalidResetTime, result.Decision.Reason);
    }

    [TestMethod]
    public void CreditCountWithoutDetailsFailsClosed()
    {
        var now = DateTimeOffset.UtcNow;
        var limits = CreateLimits(now, weeklyUsed: 93) with
        {
            ResetCredits = new ResetCreditSummary(2, null),
        };

        var result = engine.Evaluate(GuardSettings.Default, limits, now);

        Assert.AreEqual(DecisionKind.Blocked, result.Decision.Kind);
        Assert.AreEqual(DecisionReason.CreditDetailsUnavailable, result.Decision.Reason);
    }

    [TestMethod]
    public void EarliestExpiringEligibleCreditIsSelected()
    {
        var now = DateTimeOffset.UtcNow;
        var limits = CreateLimits(now, weeklyUsed: 93) with
        {
            ResetCredits = new ResetCreditSummary(
                2,
                [
                    CreateCredit("later", now.AddDays(3).ToUnixTimeSeconds()),
                    CreateCredit("earlier", now.AddDays(1).ToUnixTimeSeconds()),
                ]),
        };

        var result = engine.Evaluate(GuardSettings.Default, limits, now);

        Assert.AreEqual("earlier", result.Decision.SelectedCredit!.Id);
    }

    [TestMethod]
    public void NoEligibleCreditFailsClosed()
    {
        var now = DateTimeOffset.UtcNow;
        var limits = CreateLimits(now, weeklyUsed: 93) with
        {
            ResetCredits = new ResetCreditSummary(
                2,
                [
                    new ResetCredit("wrong", "unknown", "available", 1, null, null, null),
                    new ResetCredit("used", "codexRateLimits", "redeemed", 1, null, null, null),
                ]),
        };

        var result = engine.Evaluate(GuardSettings.Default, limits, now);

        Assert.AreEqual(DecisionKind.Blocked, result.Decision.Kind);
        Assert.AreEqual(DecisionReason.NoEligibleCredit, result.Decision.Reason);
    }

    [TestMethod]
    public void MultiBucketResponseWithoutCodexFailsClosed()
    {
        var now = DateTimeOffset.UtcNow;
        var limits = CreateLimits(now, weeklyUsed: 93) with
        {
            RateLimitsByLimitId = new Dictionary<string, RateLimitSnapshot>
            {
                ["other"] = CreateSnapshot(now, 93),
            },
        };

        var result = engine.Evaluate(GuardSettings.Default, limits, now);

        Assert.AreEqual(DecisionKind.Blocked, result.Decision.Kind);
        Assert.AreEqual(DecisionReason.CodexBucketMissing, result.Decision.Reason);
    }

    [TestMethod]
    public void PresentEmptyBucketMapDoesNotFallBackToLegacy()
    {
        var now = DateTimeOffset.UtcNow;
        var limits = CreateLimits(now, weeklyUsed: 93) with
        {
            RateLimitsByLimitId = new Dictionary<string, RateLimitSnapshot>(),
        };

        var result = engine.Evaluate(GuardSettings.Default, limits, now);

        Assert.AreEqual(DecisionReason.CodexBucketMissing, result.Decision.Reason);
    }

    [TestMethod]
    public void LegacyBucketWithoutCodexIdFailsClosed()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = CreateSnapshot(now, 93) with { LimitId = null };
        var limits = CreateLimits(now, snapshot) with { RateLimitsByLimitId = null };

        var result = engine.Evaluate(GuardSettings.Default, limits, now);

        Assert.AreEqual(DecisionReason.AmbiguousLegacyBucket, result.Decision.Reason);
    }

    [TestMethod]
    public void CodexMapKeyWithConflictingSnapshotIdFailsClosed()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = CreateSnapshot(now, 93) with { LimitId = "not-codex" };

        var result = engine.Evaluate(
            GuardSettings.Default,
            CreateLimits(now, snapshot),
            now);

        Assert.AreEqual(DecisionReason.CodexBucketMismatch, result.Decision.Reason);
    }

    [TestMethod]
    public void ZeroCreditsDoesNotProduceConsumeDecision()
    {
        var now = DateTimeOffset.UtcNow;
        var limits = CreateLimits(now, weeklyUsed: 93) with
        {
            ResetCredits = new ResetCreditSummary(0, []),
        };

        var result = engine.Evaluate(GuardSettings.Default, limits, now);

        Assert.AreEqual(DecisionKind.NoAction, result.Decision.Kind);
        Assert.AreEqual(DecisionReason.NoCredits, result.Decision.Reason);
    }

    private static AccountRateLimits CreateLimits(
        DateTimeOffset now,
        double weeklyUsed) => CreateLimits(now, CreateSnapshot(now, weeklyUsed));

    private static AccountRateLimits CreateLimits(
        DateTimeOffset now,
        RateLimitSnapshot snapshot) => new(
            snapshot,
            new Dictionary<string, RateLimitSnapshot> { ["codex"] = snapshot },
            new ResetCreditSummary(
                1,
                [CreateCredit("credit-1", now.AddDays(2).ToUnixTimeSeconds())]),
            now);

    private static RateLimitSnapshot CreateSnapshot(
        DateTimeOffset now,
        double weeklyUsed) => new(
            "codex",
            null,
            new RateLimitWindow(20, 300, now.AddHours(4).ToUnixTimeSeconds()),
            new RateLimitWindow(
                weeklyUsed,
                10_080,
                now.AddDays(5).ToUnixTimeSeconds()));

    private static ResetCredit CreateCredit(string id, long expiresAt) => new(
        id,
        "codexRateLimits",
        "available",
        DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds(),
        expiresAt,
        null,
        null);
}
