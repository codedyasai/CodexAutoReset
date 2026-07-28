using CodexAutoReset.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexAutoReset.Tests;

[TestClass]
public sealed class WeeklyUsageResetDetectorTests
{
    private static readonly DateTimeOffset Now = new(
        2026,
        7,
        28,
        3,
        0,
        0,
        TimeSpan.Zero);

    [TestMethod]
    public void FirstValidObservationOnlyEstablishesBaseline()
    {
        var evaluation = WeeklyUsageResetDetector.Evaluate(
            previous: null,
            Observation(25, Now.AddDays(2), Now));

        Assert.AreEqual(
            WeeklyUsageObservationDisposition.FirstObservation,
            evaluation.Disposition);
        Assert.IsTrue(evaluation.ShouldPersistObservation);
        Assert.IsNull(evaluation.Detection);
    }

    [TestMethod]
    public void AdvancedResetTimeAfterPreviousDeadlineIsScheduledReset()
    {
        var previous = Observation(20, Now.AddMinutes(-1), Now.AddMinutes(-5));
        var current = Observation(10, Now.AddDays(7), Now);

        var evaluation = WeeklyUsageResetDetector.Evaluate(previous, current);

        AssertReset(
            evaluation,
            WeeklyUsageResetKind.Scheduled,
            current.ResetsAt);
    }

    [TestMethod]
    public void AdvancedResetTimeBeforePreviousDeadlineIsEarlyReset()
    {
        var previous = Observation(20, Now.AddDays(2), Now.AddMinutes(-5));
        var current = Observation(10, Now.AddDays(9), Now);

        var evaluation = WeeklyUsageResetDetector.Evaluate(previous, current);

        AssertReset(evaluation, WeeklyUsageResetKind.Early, current.ResetsAt);
    }

    [TestMethod]
    public void AdvancedResetTimeIsEnoughWhenRemainingDidNotIncrease()
    {
        var previous = Observation(80, Now.AddDays(2), Now.AddMinutes(-5));
        var current = Observation(70, Now.AddDays(9), Now);

        var evaluation = WeeklyUsageResetDetector.Evaluate(previous, current);

        AssertReset(evaluation, WeeklyUsageResetKind.Early, current.ResetsAt);
    }

    [TestMethod]
    public void IncreasedRemainingAtSameResetTimeIsEarlyReset()
    {
        var resetAt = Now.AddDays(2);
        var previous = Observation(20, resetAt, Now.AddMinutes(-5));
        var current = Observation(65, resetAt, Now);

        var evaluation = WeeklyUsageResetDetector.Evaluate(previous, current);

        AssertReset(evaluation, WeeklyUsageResetKind.Early, current.ResetsAt);
    }

    [TestMethod]
    public void IncreasedRemainingAtSameResetTimeAfterDeadlineIsScheduledReset()
    {
        var resetAt = Now.AddMinutes(-1);
        var previous = Observation(20, resetAt, Now.AddMinutes(-5));
        var current = Observation(65, resetAt, Now);

        var evaluation = WeeklyUsageResetDetector.Evaluate(previous, current);

        AssertReset(
            evaluation,
            WeeklyUsageResetKind.Scheduled,
            current.ResetsAt);
    }

    [TestMethod]
    public void AutomaticCreditAttributionOverridesTimingClassification()
    {
        var previous = Observation(20, Now.AddDays(2), Now.AddMinutes(-5));
        var current = Observation(100, Now.AddDays(9), Now);

        var evaluation = WeeklyUsageResetDetector.Evaluate(
            previous,
            current,
            WeeklyUsageResetAttribution.AutomaticCreditSucceeded);

        AssertReset(
            evaluation,
            WeeklyUsageResetKind.AutomaticCredit,
            current.ResetsAt);
    }

    [TestMethod]
    public void AttributionWithoutObservedResetDoesNotCreateDetection()
    {
        var resetAt = Now.AddDays(2);
        var previous = Observation(20, resetAt, Now.AddMinutes(-5));
        var current = Observation(20, resetAt, Now);

        var evaluation = WeeklyUsageResetDetector.Evaluate(
            previous,
            current,
            WeeklyUsageResetAttribution.AutomaticCreditSucceeded);

        Assert.AreEqual(
            WeeklyUsageObservationDisposition.NoReset,
            evaluation.Disposition);
        Assert.IsTrue(evaluation.ShouldPersistObservation);
        Assert.IsNull(evaluation.Detection);
    }

    [TestMethod]
    public void OrdinaryUsageDecreaseAdvancesBaselineWithoutReset()
    {
        var resetAt = Now.AddDays(2);
        var previous = Observation(80, resetAt, Now.AddMinutes(-5));
        var current = Observation(70, resetAt, Now);

        var evaluation = WeeklyUsageResetDetector.Evaluate(previous, current);

        Assert.AreEqual(
            WeeklyUsageObservationDisposition.NoReset,
            evaluation.Disposition);
        Assert.IsTrue(evaluation.ShouldPersistObservation);
        Assert.IsNull(evaluation.Detection);
    }

    [TestMethod]
    public void ResetTimeRegressionIsIgnoredWithoutChangingBaseline()
    {
        var previous = Observation(20, Now.AddDays(2), Now.AddMinutes(-5));
        var current = Observation(100, Now.AddDays(1), Now);

        var evaluation = WeeklyUsageResetDetector.Evaluate(previous, current);

        Assert.AreEqual(
            WeeklyUsageObservationDisposition.IgnoredResetTimeRegression,
            evaluation.Disposition);
        Assert.IsFalse(evaluation.ShouldPersistObservation);
        Assert.IsNull(evaluation.Detection);
    }

    [TestMethod]
    public void OutOfOrderObservationIsIgnoredWithoutChangingBaseline()
    {
        var previous = Observation(20, Now.AddDays(2), Now);
        var current = Observation(100, Now.AddDays(9), Now.AddMinutes(-1));

        var evaluation = WeeklyUsageResetDetector.Evaluate(previous, current);

        Assert.AreEqual(
            WeeklyUsageObservationDisposition.IgnoredOutOfOrder,
            evaluation.Disposition);
        Assert.IsFalse(evaluation.ShouldPersistObservation);
        Assert.IsNull(evaluation.Detection);
    }

    [DataTestMethod]
    [DataRow(double.NaN, 1_800_000_000L)]
    [DataRow(double.PositiveInfinity, 1_800_000_000L)]
    [DataRow(-0.1, 1_800_000_000L)]
    [DataRow(100.1, 1_800_000_000L)]
    [DataRow(50, 0L)]
    [DataRow(50, 253_402_300_800L)]
    public void InvalidObservationFailsClosed(
        double remainingPercent,
        long resetsAt)
    {
        var previous = Observation(20, Now.AddDays(2), Now.AddMinutes(-5));
        var current = new WeeklyUsageObservation(
            remainingPercent,
            resetsAt,
            Now);

        var evaluation = WeeklyUsageResetDetector.Evaluate(previous, current);

        Assert.AreEqual(
            WeeklyUsageObservationDisposition.Invalid,
            evaluation.Disposition);
        Assert.IsFalse(evaluation.ShouldPersistObservation);
        Assert.IsNull(evaluation.Detection);
    }

    private static WeeklyUsageObservation Observation(
        double remainingPercent,
        DateTimeOffset resetsAt,
        DateTimeOffset observedAt) => new(
            remainingPercent,
            resetsAt.ToUnixTimeSeconds(),
            observedAt);

    private static void AssertReset(
        WeeklyUsageResetEvaluation evaluation,
        WeeklyUsageResetKind expectedKind,
        long expectedNextResetsAt)
    {
        Assert.AreEqual(
            WeeklyUsageObservationDisposition.ResetDetected,
            evaluation.Disposition);
        Assert.IsTrue(evaluation.ShouldPersistObservation);
        Assert.IsNotNull(evaluation.Detection);
        Assert.AreEqual(expectedKind, evaluation.Detection.Kind);
        Assert.AreEqual(expectedNextResetsAt, evaluation.Detection.NextResetsAt);
        Assert.AreEqual(Now, evaluation.Detection.DetectedAt);
    }
}
