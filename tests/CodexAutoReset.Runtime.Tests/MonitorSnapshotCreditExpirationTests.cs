using CodexAutoReset.Core;
using CodexAutoReset.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexAutoReset.Runtime.Tests;

[TestClass]
public sealed class MonitorSnapshotCreditExpirationTests
{
    [TestMethod]
    public void FromResult_ProjectsOnlyAvailableExpirationsInExpiryOrder()
    {
        const long earlier = 2_000_000_000;
        const long later = 2_100_000_000;
        var summary = new ResetCreditSummary(
            8,
            [
                CreateCredit("later", "available", later),
                CreateCredit("redeemed", "redeemed", earlier - 1),
                CreateCredit("earlier-a", "available", earlier),
                CreateCredit("redeeming", "redeeming", earlier - 2),
                CreateCredit("no-expiry", "available", null),
                CreateCredit("earlier-b", "available", earlier),
                CreateCredit("expired", "available", 1_800_000_000),
                CreateCredit(
                    "wrong-reset-type",
                    "available",
                    earlier - 3,
                    resetType: "unknown"),
            ]);

        var snapshot = CreateSnapshot(summary);

        CollectionAssert.AreEqual(
            new long?[] { earlier, earlier, later, null },
            snapshot.AvailableCreditExpirations!.ToArray());
    }

    [TestMethod]
    public void FromResult_PreservesPartialExpirationDetailsWithoutInventingRows()
    {
        const long knownExpiration = 2_000_000_000;
        var snapshot = CreateSnapshot(
            new ResetCreditSummary(
                3,
                [CreateCredit("known", "available", knownExpiration)]));

        Assert.AreEqual(3L, snapshot.AvailableCreditCount);
        CollectionAssert.AreEqual(
            new long?[] { knownExpiration },
            snapshot.AvailableCreditExpirations!.ToArray());
    }

    [TestMethod]
    public void FromResult_DistinguishesMissingDetailsFromAnEmptyList()
    {
        var missingDetails = CreateSnapshot(new ResetCreditSummary(2, null));
        var noCredits = CreateSnapshot(new ResetCreditSummary(0, []));

        Assert.IsNull(missingDetails.AvailableCreditExpirations);
        Assert.IsNotNull(noCredits.AvailableCreditExpirations);
        Assert.AreEqual(0, noCredits.AvailableCreditExpirations.Count);
    }

    [TestMethod]
    public void FromResult_NormalizesAnUnrepresentableFutureExpiry()
    {
        var snapshot = CreateSnapshot(
            new ResetCreditSummary(
                1,
                [CreateCredit("invalid", "available", long.MaxValue)]));

        CollectionAssert.AreEqual(
            new long?[] { null },
            snapshot.AvailableCreditExpirations!.ToArray());
    }

    private static MonitorSnapshot CreateSnapshot(ResetCreditSummary? summary)
    {
        var observedAt = DateTimeOffset.FromUnixTimeSeconds(1_900_000_000);
        var accountRateLimits = new AccountRateLimits(
            new RateLimitSnapshot("codex", "Codex", null, null),
            null,
            summary,
            observedAt);
        var evaluation = new EvaluationResult(
            null,
            new GuardDecision(
                DecisionKind.NoAction,
                DecisionReason.AboveThreshold,
                null,
                null,
                null),
            summary?.AvailableCount);
        var result = new GuardCycleResult(
            accountRateLimits,
            evaluation,
            CycleActionKind.None,
            "no_action");

        return MonitorSnapshot.FromResult(GuardSettings.Default, result);
    }

    private static ResetCredit CreateCredit(
        string id,
        string status,
        long? expiresAt,
        string resetType = "codexRateLimits") => new(
        id,
        resetType,
        status,
        1_900_000_000,
        expiresAt,
        null,
        null);
}
