using CodexAutoReset.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexAutoReset.Tests;

[TestClass]
public sealed class DecisionEngineTests
{
    private readonly ResetDecisionEngine engine = new();

    [TestMethod]
    public void DefaultBlankThresholdsAndOffTogglesNeverTriggerAtZeroRemaining()
    {
        var now = DateTimeOffset.UtcNow;
        var limits = CreateLimits(
            now,
            CreateDualSnapshot(
                now,
                fiveHourUsed: 100,
                weeklyUsed: 100));

        var trigger = engine.EvaluateTrigger(
            GuardSettings.Default,
            limits,
            now);
        var result = engine.Evaluate(
            GuardSettings.Default,
            limits,
            now);

        Assert.IsNull(GuardSettings.Default.WeeklyRemainingThresholdPercent);
        Assert.IsFalse(GuardSettings.Default.WeeklyAutomationEnabled);
        Assert.IsNull(GuardSettings.Default.FiveHourRemainingThresholdPercent);
        Assert.IsFalse(GuardSettings.Default.FiveHourAutomationEnabled);
        Assert.IsFalse(GuardSettings.Default.AnyAutomationEnabled);
        Assert.IsFalse(trigger.ThresholdReached);
        Assert.AreEqual(DecisionKind.NoAction, result.Decision.Kind);
        Assert.AreEqual(DecisionReason.AboveThreshold, result.Decision.Reason);
    }

    [TestMethod]
    public void RawWeeklyToggleWithNullThresholdIsEffectivelyOff()
    {
        var now = DateTimeOffset.UtcNow;
        var settings = GuardSettings.Default with
        {
            RemainingThresholdPercent = null,
            AutomationEnabled = true,
            FiveHourRemainingThresholdPercent = null,
            FiveHourAutomationEnabled = false,
        };

        var trigger = engine.EvaluateTrigger(
            settings,
            CreateLimits(now, weeklyUsed: 100),
            now);
        var result = engine.Evaluate(
            settings,
            CreateLimits(now, weeklyUsed: 100),
            now);

        Assert.IsFalse(settings.IsAutomationEnabled(TriggerLimit.Weekly));
        Assert.IsFalse(settings.AnyAutomationEnabled);
        Assert.IsFalse(trigger.ThresholdReached);
        Assert.AreEqual(DecisionKind.NoAction, result.Decision.Kind);
        Assert.AreEqual(DecisionReason.AboveThreshold, result.Decision.Reason);
    }

    [TestMethod]
    public void WeeklyZeroThresholdConsumesOnlyAtZeroRemaining()
    {
        var now = DateTimeOffset.UtcNow;
        var settings = GuardSettings.Default with
        {
            RemainingThresholdPercent = 0,
            AutomationEnabled = true,
            FiveHourRemainingThresholdPercent = null,
            FiveHourAutomationEnabled = false,
        };

        var atZero = engine.Evaluate(
            settings,
            CreateLimits(now, weeklyUsed: 100),
            now);
        var aboveZero = engine.Evaluate(
            settings,
            CreateLimits(now, weeklyUsed: 99.9),
            now);

        Assert.AreEqual(DecisionKind.WouldConsume, atZero.Decision.Kind);
        Assert.AreEqual(TriggerLimit.Weekly, atZero.Decision.SelectedLimit);
        Assert.AreEqual(0d, atZero.Decision.TriggerWindow!.RemainingPercent);
        Assert.AreEqual(DecisionKind.NoAction, aboveZero.Decision.Kind);
        Assert.AreEqual(DecisionReason.AboveThreshold, aboveZero.Decision.Reason);
    }

    [TestMethod]
    public void NullFiveHourThresholdNeverBecomesEligibleWhenToggleIsOn()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = CreateDualSnapshot(
            now,
            fiveHourUsed: 100,
            weeklyUsed: 50);
        var settings = GuardSettings.Default with
        {
            RemainingThresholdPercent = 0,
            AutomationEnabled = false,
            FiveHourRemainingThresholdPercent = null,
            FiveHourAutomationEnabled = true,
        };

        var trigger = engine.EvaluateTrigger(
            settings,
            CreateLimits(now, snapshot),
            now);
        var result = engine.Evaluate(
            settings,
            CreateLimits(now, snapshot),
            now);

        Assert.IsFalse(trigger.ThresholdReached);
        Assert.AreEqual(DecisionKind.NoAction, result.Decision.Kind);
        Assert.AreEqual(DecisionReason.AboveThreshold, result.Decision.Reason);
        Assert.AreNotEqual(
            TriggerLimit.FiveHour,
            result.Decision.SelectedLimit);
    }

    [TestMethod]
    public void NullFiveHourThresholdDoesNotSuppressIndependentWeeklyTrigger()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = CreateDualSnapshot(
            now,
            fiveHourUsed: 100,
            weeklyUsed: 100);
        var settings = GuardSettings.Default with
        {
            RemainingThresholdPercent = 0,
            AutomationEnabled = true,
            FiveHourRemainingThresholdPercent = null,
            FiveHourAutomationEnabled = true,
        };

        var result = engine.Evaluate(
            settings,
            CreateLimits(now, snapshot),
            now);

        Assert.AreEqual(DecisionKind.WouldConsume, result.Decision.Kind);
        Assert.AreEqual(TriggerLimit.Weekly, result.Decision.SelectedLimit);
        StringAssert.Contains(result.Decision.IntervalKey!, "|weekly|10080|");
    }

    [TestMethod]
    public void WeeklyTriggerEvaluationDoesNotDependOnCreditAvailability()
    {
        var now = DateTimeOffset.UtcNow;
        var limits = CreateLimits(now, weeklyUsed: 93) with { ResetCredits = null };

        var trigger = engine.EvaluateTrigger(
            WeeklySevenPercentSettings,
            limits,
            now);

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
            WeeklySevenPercentSettings,
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
            WeeklySevenPercentSettings,
            CreateLimits(now, weeklyUsed: 92),
            now);

        Assert.AreEqual(DecisionKind.NoAction, result.Decision.Kind);
        Assert.AreEqual(DecisionReason.AboveThreshold, result.Decision.Reason);
    }

    [DataTestMethod]
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
            WeeklySevenPercentSettings,
            CreateLimits(now, snapshot),
            now);

        Assert.AreEqual(DecisionKind.WouldConsume, result.Decision.Kind);
        Assert.AreEqual(10_080L, result.Weekly!.NormalizedDurationMinutes);
        Assert.IsNull(result.FiveHour);
    }

    [TestMethod]
    public void MissingFiveHourWindowIsNormalAndDoesNotBlockWeeklyTrigger()
    {
        var now = DateTimeOffset.UtcNow;
        var weekly = new RateLimitWindow(
            93,
            10_080,
            now.AddDays(5).ToUnixTimeSeconds());
        var snapshot = new RateLimitSnapshot("codex", null, weekly, null);
        var settings = GuardSettings.Default with
        {
            RemainingThresholdPercent = 7,
            AutomationEnabled = true,
            FiveHourAutomationEnabled = true,
        };

        var result = engine.Evaluate(
            settings,
            CreateLimits(now, snapshot),
            now);

        Assert.AreEqual(DecisionKind.WouldConsume, result.Decision.Kind);
        Assert.AreEqual(TriggerLimit.Weekly, result.Decision.SelectedLimit);
        Assert.IsNull(result.FiveHour);
        Assert.AreEqual(10_080L, result.Weekly!.NormalizedDurationMinutes);
    }

    [TestMethod]
    public void MissingFiveHourWindowDoesNotBecomeACompatibilityBlock()
    {
        var now = DateTimeOffset.UtcNow;
        var weekly = new RateLimitWindow(
            99,
            10_080,
            now.AddDays(5).ToUnixTimeSeconds());
        var snapshot = new RateLimitSnapshot("codex", null, weekly, null);
        var settings = GuardSettings.Default with
        {
            AutomationEnabled = false,
            FiveHourRemainingThresholdPercent = 7,
            FiveHourAutomationEnabled = true,
        };

        var result = engine.Evaluate(
            settings,
            CreateLimits(now, snapshot),
            now);

        Assert.AreEqual(DecisionKind.NoAction, result.Decision.Kind);
        Assert.AreEqual(DecisionReason.AboveThreshold, result.Decision.Reason);
        Assert.IsNull(result.FiveHour);
        Assert.IsNotNull(result.Weekly);
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void FiveHourAndWeeklyWindowsAreSelectedByDurationNotPosition(
        bool fiveHourIsPrimary)
    {
        var now = DateTimeOffset.UtcNow;
        var fiveHour = new RateLimitWindow(
            40,
            300,
            now.AddHours(4).ToUnixTimeSeconds());
        var weekly = new RateLimitWindow(
            80,
            10_080,
            now.AddDays(5).ToUnixTimeSeconds());
        var snapshot = new RateLimitSnapshot(
            "codex",
            null,
            fiveHourIsPrimary ? fiveHour : weekly,
            fiveHourIsPrimary ? weekly : fiveHour);

        var result = engine.Evaluate(
            WeeklySevenPercentSettings,
            CreateLimits(now, snapshot),
            now);

        Assert.AreEqual(60d, result.FiveHour!.RemainingPercent);
        Assert.AreEqual(20d, result.Weekly!.RemainingPercent);
        Assert.AreEqual(300L, result.FiveHour.NormalizedDurationMinutes);
        Assert.AreEqual(10_080L, result.Weekly.NormalizedDurationMinutes);
        Assert.AreEqual(TriggerLimit.Weekly, result.Decision.SelectedLimit);
        Assert.AreEqual(DecisionKind.NoAction, result.Decision.Kind);
    }

    [DataTestMethod]
    [DataRow(299L)]
    [DataRow(300L)]
    [DataRow(301L)]
    public void FiveHourDurationToleranceAcceptsExactlyOneMinute(long duration)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new RateLimitSnapshot(
            "codex",
            null,
            new RateLimitWindow(
                93,
                duration,
                now.AddHours(4).ToUnixTimeSeconds()),
            new RateLimitWindow(
                50,
                10_080,
                now.AddDays(5).ToUnixTimeSeconds()));
        var settings = GuardSettings.Default with
        {
            AutomationEnabled = false,
            FiveHourRemainingThresholdPercent = 7,
            FiveHourAutomationEnabled = true,
        };

        var result = engine.Evaluate(
            settings,
            CreateLimits(now, snapshot),
            now);

        Assert.AreEqual(DecisionKind.WouldConsume, result.Decision.Kind);
        Assert.AreEqual(TriggerLimit.FiveHour, result.Decision.SelectedLimit);
        Assert.AreEqual(duration, result.FiveHour!.ReportedDurationMinutes);
        Assert.AreEqual(300L, result.FiveHour.NormalizedDurationMinutes);
        StringAssert.Contains(
            result.Decision.IntervalKey!,
            "|fiveHour|300|");
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
            WeeklySevenPercentSettings,
            CreateLimits(now, snapshot),
            now);

        Assert.AreEqual(DecisionKind.WouldConsume, result.Decision.Kind);
        Assert.AreEqual(duration, result.Weekly!.ReportedDurationMinutes);
        Assert.AreEqual(10_080L, result.Weekly.NormalizedDurationMinutes);
    }

    [TestMethod]
    public void IndependentThresholdsCanSelectFiveHourWhenWeeklyIsAboveItsOwn()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = CreateDualSnapshot(
            now,
            fiveHourUsed: 88,
            weeklyUsed: 92);
        var settings = GuardSettings.Default with
        {
            RemainingThresholdPercent = 7,
            FiveHourRemainingThresholdPercent = 12,
            AutomationEnabled = true,
            FiveHourAutomationEnabled = true,
        };

        var result = engine.Evaluate(
            settings,
            CreateLimits(now, snapshot),
            now);

        Assert.AreEqual(DecisionKind.WouldConsume, result.Decision.Kind);
        Assert.AreEqual(TriggerLimit.FiveHour, result.Decision.SelectedLimit);
        Assert.AreEqual(12d, result.Decision.TriggerWindow!.RemainingPercent);
    }

    [TestMethod]
    public void IndependentThresholdsCanSelectWeeklyWhenFiveHourIsAboveItsOwn()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = CreateDualSnapshot(
            now,
            fiveHourUsed: 92,
            weeklyUsed: 88);
        var settings = GuardSettings.Default with
        {
            RemainingThresholdPercent = 12,
            FiveHourRemainingThresholdPercent = 7,
            AutomationEnabled = true,
            FiveHourAutomationEnabled = true,
        };

        var result = engine.Evaluate(
            settings,
            CreateLimits(now, snapshot),
            now);

        Assert.AreEqual(DecisionKind.WouldConsume, result.Decision.Kind);
        Assert.AreEqual(TriggerLimit.Weekly, result.Decision.SelectedLimit);
        Assert.AreEqual(12d, result.Decision.TriggerWindow!.RemainingPercent);
    }

    [TestMethod]
    public void DisabledFiveHourAutomationIgnoresReachedFiveHourThreshold()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = CreateDualSnapshot(
            now,
            fiveHourUsed: 99,
            weeklyUsed: 50);
        var settings = GuardSettings.Default with
        {
            RemainingThresholdPercent = 7,
            AutomationEnabled = true,
            FiveHourAutomationEnabled = false,
        };

        var result = engine.Evaluate(
            settings,
            CreateLimits(now, snapshot),
            now);

        Assert.AreEqual(DecisionKind.NoAction, result.Decision.Kind);
        Assert.AreEqual(TriggerLimit.Weekly, result.Decision.SelectedLimit);
    }

    [TestMethod]
    public void DisabledWeeklyAutomationIgnoresReachedWeeklyThreshold()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = CreateDualSnapshot(
            now,
            fiveHourUsed: 50,
            weeklyUsed: 99);
        var settings = GuardSettings.Default with
        {
            AutomationEnabled = false,
            FiveHourRemainingThresholdPercent = 7,
            FiveHourAutomationEnabled = true,
        };

        var result = engine.Evaluate(
            settings,
            CreateLimits(now, snapshot),
            now);

        Assert.AreEqual(DecisionKind.NoAction, result.Decision.Kind);
        Assert.AreEqual(TriggerLimit.FiveHour, result.Decision.SelectedLimit);
    }

    [TestMethod]
    public void WeeklyWinsDeterministicallyWhenBothWindowsReachThreshold()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = CreateDualSnapshot(
            now,
            fiveHourUsed: 95,
            weeklyUsed: 95);
        var settings = GuardSettings.Default with
        {
            RemainingThresholdPercent = 7,
            AutomationEnabled = true,
            FiveHourRemainingThresholdPercent = 7,
            FiveHourAutomationEnabled = true,
        };

        var result = engine.Evaluate(
            settings,
            CreateLimits(now, snapshot),
            now);

        Assert.AreEqual(DecisionKind.WouldConsume, result.Decision.Kind);
        Assert.AreEqual(TriggerLimit.Weekly, result.Decision.SelectedLimit);
        Assert.AreEqual(10_080L, result.Decision.TriggerWindow!
            .NormalizedDurationMinutes);
        StringAssert.Contains(result.Decision.IntervalKey!, "|weekly|10080|");
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
            WeeklySevenPercentSettings,
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
            WeeklySevenPercentSettings,
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
            WeeklySevenPercentSettings,
            CreateLimits(now, snapshot),
            now);

        Assert.AreEqual(DecisionKind.Blocked, result.Decision.Kind);
        Assert.AreEqual(DecisionReason.InvalidUsedPercent, result.Decision.Reason);
    }

    [DataTestMethod]
    [DataRow(-0.1)]
    [DataRow(100.1)]
    [DataRow(double.NaN)]
    public void MalformedFiveHourUsageFailsClosedEvenWithValidWeekly(
        double usedPercent)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new RateLimitSnapshot(
            "codex",
            null,
            new RateLimitWindow(
                usedPercent,
                300,
                now.AddHours(4).ToUnixTimeSeconds()),
            new RateLimitWindow(
                50,
                10_080,
                now.AddDays(5).ToUnixTimeSeconds()));

        var result = engine.Evaluate(
            WeeklySevenPercentSettings,
            CreateLimits(now, snapshot),
            now);

        Assert.AreEqual(DecisionKind.Blocked, result.Decision.Kind);
        Assert.AreEqual(
            DecisionReason.InvalidUsedPercent,
            result.Decision.Reason);
        Assert.IsNull(result.FiveHour);
        Assert.IsNotNull(result.Weekly);
    }

    [TestMethod]
    public void MissingFiveHourResetTimeFailsClosedEvenWithValidWeekly()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new RateLimitSnapshot(
            "codex",
            null,
            new RateLimitWindow(50, 300, null),
            new RateLimitWindow(
                50,
                10_080,
                now.AddDays(5).ToUnixTimeSeconds()));

        var result = engine.Evaluate(
            WeeklySevenPercentSettings,
            CreateLimits(now, snapshot),
            now);

        Assert.AreEqual(DecisionKind.Blocked, result.Decision.Kind);
        Assert.AreEqual(
            DecisionReason.InvalidResetTime,
            result.Decision.Reason);
        Assert.IsNull(result.FiveHour);
        Assert.IsNotNull(result.Weekly);
    }

    [TestMethod]
    public void DuplicateFiveHourCandidatesCannotReplaceRequiredWeeklyWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new RateLimitSnapshot(
            "codex",
            null,
            new RateLimitWindow(
                93,
                299,
                now.AddHours(4).ToUnixTimeSeconds()),
            new RateLimitWindow(
                93,
                301,
                now.AddHours(4).ToUnixTimeSeconds()));
        var settings = GuardSettings.Default with
        {
            FiveHourAutomationEnabled = true,
        };

        var result = engine.Evaluate(
            settings,
            CreateLimits(now, snapshot),
            now);

        Assert.AreEqual(DecisionKind.Blocked, result.Decision.Kind);
        Assert.AreEqual(
            DecisionReason.SelectedWindowMissing,
            result.Decision.Reason);
        Assert.IsNull(result.Weekly);
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
            WeeklySevenPercentSettings,
            CreateLimits(now, snapshot),
            now);

        Assert.AreEqual(DecisionKind.Blocked, result.Decision.Kind);
        Assert.AreEqual(DecisionReason.InvalidResetTime, result.Decision.Reason);
    }

    [DataTestMethod]
    [DataRow(-60L)]
    [DataRow(0L)]
    [DataRow(299L)]
    public void ScheduledResetWithinFiveMinuteGuardWindowDoesNotConsume(
        long resetOffsetSeconds)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new RateLimitSnapshot(
            "codex",
            null,
            new RateLimitWindow(20, 300, now.AddHours(4).ToUnixTimeSeconds()),
            new RateLimitWindow(
                93,
                10_080,
                now.AddSeconds(resetOffsetSeconds).ToUnixTimeSeconds()));

        var trigger = engine.EvaluateTrigger(
            WeeklySevenPercentSettings,
            CreateLimits(now, snapshot),
            now);
        var result = engine.Evaluate(
            WeeklySevenPercentSettings,
            CreateLimits(now, snapshot),
            now);

        Assert.IsFalse(trigger.ThresholdReached);
        Assert.AreEqual(DecisionReason.ScheduledResetImminent, trigger.Reason);
        Assert.AreEqual(DecisionKind.NoAction, result.Decision.Kind);
        Assert.AreEqual(
            DecisionReason.ScheduledResetImminent,
            result.Decision.Reason);
        Assert.IsNotNull(result.Weekly);
    }

    [DataTestMethod]
    [DataRow(-60L)]
    [DataRow(0L)]
    [DataRow(299L)]
    public void FiveHourResetWithinFiveMinuteGuardWindowDoesNotConsume(
        long resetOffsetSeconds)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new RateLimitSnapshot(
            "codex",
            null,
            new RateLimitWindow(
                93,
                300,
                now.AddSeconds(resetOffsetSeconds).ToUnixTimeSeconds()),
            new RateLimitWindow(
                50,
                10_080,
                now.AddDays(5).ToUnixTimeSeconds()));
        var settings = GuardSettings.Default with
        {
            AutomationEnabled = false,
            FiveHourRemainingThresholdPercent = 7,
            FiveHourAutomationEnabled = true,
        };

        var result = engine.Evaluate(
            settings,
            CreateLimits(now, snapshot),
            now);

        Assert.AreEqual(DecisionKind.NoAction, result.Decision.Kind);
        Assert.AreEqual(
            DecisionReason.ScheduledResetImminent,
            result.Decision.Reason);
        Assert.AreEqual(TriggerLimit.FiveHour, result.Decision.SelectedLimit);
        Assert.AreEqual(
            300L,
            result.Decision.TriggerWindow!.NormalizedDurationMinutes);
        StringAssert.Contains(
            result.Decision.IntervalKey!,
            "|fiveHour|300|");
    }

    [TestMethod]
    public void FiveHourResetAtFiveMinutesCanConsume()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new RateLimitSnapshot(
            "codex",
            null,
            new RateLimitWindow(
                93,
                300,
                now.AddMinutes(5).ToUnixTimeSeconds()),
            new RateLimitWindow(
                50,
                10_080,
                now.AddDays(5).ToUnixTimeSeconds()));
        var settings = GuardSettings.Default with
        {
            AutomationEnabled = false,
            FiveHourRemainingThresholdPercent = 7,
            FiveHourAutomationEnabled = true,
        };

        var result = engine.Evaluate(
            settings,
            CreateLimits(now, snapshot),
            now);

        Assert.AreEqual(DecisionKind.WouldConsume, result.Decision.Kind);
        Assert.AreEqual(TriggerLimit.FiveHour, result.Decision.SelectedLimit);
    }

    [DataTestMethod]
    [DataRow(300L)]
    [DataRow(301L)]
    public void ScheduledResetAtLeastFiveMinutesAwayCanConsume(
        long resetOffsetSeconds)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new RateLimitSnapshot(
            "codex",
            null,
            new RateLimitWindow(20, 300, now.AddHours(4).ToUnixTimeSeconds()),
            new RateLimitWindow(
                93,
                10_080,
                now.AddSeconds(resetOffsetSeconds).ToUnixTimeSeconds()));

        var result = engine.Evaluate(
            WeeklySevenPercentSettings,
            CreateLimits(now, snapshot),
            now);

        Assert.AreEqual(DecisionKind.WouldConsume, result.Decision.Kind);
        Assert.AreEqual(DecisionReason.ThresholdReached, result.Decision.Reason);
    }

    [TestMethod]
    public void ScheduledResetOlderThanClockSkewStillFailsClosed()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new RateLimitSnapshot(
            "codex",
            null,
            new RateLimitWindow(20, 300, now.AddHours(4).ToUnixTimeSeconds()),
            new RateLimitWindow(
                93,
                10_080,
                now.AddSeconds(-61).ToUnixTimeSeconds()));

        var result = engine.Evaluate(
            WeeklySevenPercentSettings,
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

        var result = engine.Evaluate(
            WeeklySevenPercentSettings,
            limits,
            now);

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

        var result = engine.Evaluate(
            WeeklySevenPercentSettings,
            limits,
            now);

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

        var result = engine.Evaluate(
            WeeklySevenPercentSettings,
            limits,
            now);

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

        var result = engine.Evaluate(
            WeeklySevenPercentSettings,
            limits,
            now);

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

        var result = engine.Evaluate(
            WeeklySevenPercentSettings,
            limits,
            now);

        Assert.AreEqual(DecisionReason.CodexBucketMissing, result.Decision.Reason);
    }

    [TestMethod]
    public void LegacyBucketWithoutCodexIdFailsClosed()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = CreateSnapshot(now, 93) with { LimitId = null };
        var limits = CreateLimits(now, snapshot) with { RateLimitsByLimitId = null };

        var result = engine.Evaluate(
            WeeklySevenPercentSettings,
            limits,
            now);

        Assert.AreEqual(DecisionReason.AmbiguousLegacyBucket, result.Decision.Reason);
    }

    [TestMethod]
    public void CodexMapKeyWithConflictingSnapshotIdFailsClosed()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = CreateSnapshot(now, 93) with { LimitId = "not-codex" };

        var result = engine.Evaluate(
            WeeklySevenPercentSettings,
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

        var result = engine.Evaluate(
            WeeklySevenPercentSettings,
            limits,
            now);

        Assert.AreEqual(DecisionKind.NoAction, result.Decision.Kind);
        Assert.AreEqual(DecisionReason.NoCredits, result.Decision.Reason);
    }

    private static AccountRateLimits CreateLimits(
        DateTimeOffset now,
        double weeklyUsed) => CreateLimits(now, CreateSnapshot(now, weeklyUsed));

    private static GuardSettings WeeklySevenPercentSettings =>
        GuardSettings.Default with
        {
            RemainingThresholdPercent = 7,
            AutomationEnabled = true,
            FiveHourRemainingThresholdPercent = null,
            FiveHourAutomationEnabled = false,
        };

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
        double weeklyUsed) => CreateDualSnapshot(
            now,
            fiveHourUsed: 20,
            weeklyUsed);

    private static RateLimitSnapshot CreateDualSnapshot(
        DateTimeOffset now,
        double fiveHourUsed,
        double weeklyUsed) => new(
            "codex",
            null,
            new RateLimitWindow(
                fiveHourUsed,
                300,
                now.AddHours(4).ToUnixTimeSeconds()),
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
