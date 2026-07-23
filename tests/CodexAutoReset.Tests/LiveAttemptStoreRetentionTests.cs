using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using CodexAutoReset.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexAutoReset.Tests;

[TestClass]
public sealed class LiveAttemptStoreRetentionTests
{
    private static readonly DateTimeOffset Now = new(
        2026,
        7,
        21,
        0,
        0,
        0,
        TimeSpan.Zero);

    [TestMethod]
    public async Task CapacityFreesOnlyOneOldExpiredRefreshedTerminalInterval()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "live-state.json");
        var attempts = new JsonArray();
        for (var index = 0; index < 1_022; index++)
        {
            attempts.Add(TerminalAttempt(
                index,
                Now.ToUnixTimeSeconds() - 1_000 - index,
                refreshRequired: false));
        }

        var unexpiredReset = Now.AddHours(1).ToUnixTimeSeconds();
        attempts.Add(TerminalAttempt(1_022, unexpiredReset, refreshRequired: false));
        var unrefreshedReset = Now.AddHours(-1).ToUnixTimeSeconds();
        attempts.Add(TerminalAttempt(1_023, unrefreshedReset, refreshRequired: true));
        await WriteStateAsync(path, attempts);

        var store = new JsonLiveAttemptStore(path);
        var candidateReset = Now.AddDays(5).ToUnixTimeSeconds();
        var result = await store.TryPrepareAsync(
            Candidate(candidateReset),
            "credit-id",
            new FakeSecretProtector(),
            Now,
            CancellationToken.None);

        Assert.AreEqual(LivePrepareDisposition.Prepared, result.Disposition);
        var snapshots = await store.ReadAsync(CancellationToken.None);
        Assert.AreEqual(1_024, snapshots.Count);
        Assert.IsTrue(snapshots.Any(attempt =>
            attempt.ResetsAt == candidateReset
            && attempt.Phase == LiveAttemptPhase.Pending));
        Assert.IsFalse(snapshots.Any(attempt =>
            attempt.ResetsAt == Now.ToUnixTimeSeconds() - 1_000));
        Assert.IsTrue(snapshots.Any(attempt =>
            attempt.ResetsAt == Now.ToUnixTimeSeconds() - 1_001
            && attempt.Phase == LiveAttemptPhase.Terminal));
        Assert.IsTrue(snapshots.Any(attempt =>
            attempt.ResetsAt == unexpiredReset
            && attempt.Phase == LiveAttemptPhase.Terminal));
        Assert.IsTrue(snapshots.Any(attempt =>
            attempt.ResetsAt == unrefreshedReset
            && attempt.Phase == LiveAttemptPhase.Terminal
            && attempt.RefreshRequired));
    }

    [TestMethod]
    public async Task CapacityNeverCompactsNeedsReviewEvidence()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "live-state.json");
        var attempts = new JsonArray();
        for (var index = 0; index < 1_023; index++)
        {
            attempts.Add(TerminalAttempt(
                index,
                Now.ToUnixTimeSeconds() - 1_000 - index,
                refreshRequired: false));
        }

        attempts.Add(NeedsReviewAttempt(1_023));
        await WriteStateAsync(path, attempts);

        var store = new JsonLiveAttemptStore(path);
        var result = await store.TryPrepareAsync(
            Candidate(Now.AddDays(5).ToUnixTimeSeconds()),
            "different-credit-id",
            new FakeSecretProtector(),
            Now,
            CancellationToken.None);

        Assert.AreEqual(LivePrepareDisposition.ExistingActive, result.Disposition);
        Assert.AreEqual(LiveAttemptPhase.NeedsReview, JsonLiveAttemptStore.ToSnapshot(
            result.Attempt).Phase);
        Assert.AreEqual(
            1_024,
            (await store.ReadAsync(CancellationToken.None)).Count);
    }

    [TestMethod]
    public async Task ExpiredCandidateIsRejectedBeforeRetentionChanges()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "live-state.json");
        var store = new JsonLiveAttemptStore(path);

        var exception = await Assert.ThrowsExceptionAsync<LiveStateException>(() =>
            store.TryPrepareAsync(
                Candidate(Now.AddSeconds(-61).ToUnixTimeSeconds()),
                "credit-id",
                new FakeSecretProtector(),
                Now,
                CancellationToken.None));

        Assert.AreEqual("live_candidate_invalid", exception.ReasonCode);
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public async Task ForwardClockJumpRetainsRecentIntervalForLaterRollback()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "live-state.json");
        var attempts = new JsonArray();
        var firstReset = Now.AddHours(1).ToUnixTimeSeconds();
        for (var index = 0; index < 1_024; index++)
        {
            attempts.Add(TerminalAttempt(
                index,
                firstReset + index,
                refreshRequired: false));
        }

        await WriteStateAsync(path, attempts);
        var jumpedNow = Now.AddDays(365);
        var store = new JsonLiveAttemptStore(path);
        var newReset = jumpedNow.AddDays(5).ToUnixTimeSeconds();
        var prepared = await store.TryPrepareAsync(
            Candidate(newReset),
            "credit-id",
            new FakeSecretProtector(),
            jumpedNow,
            CancellationToken.None);
        _ = await store.MarkDispatchStartedAsync(
            prepared.Attempt.IntervalKey,
            jumpedNow,
            CancellationToken.None);
        _ = await store.CompleteAsync(
            prepared.Attempt.IntervalKey,
            ConsumeResetCreditOutcome.NothingToReset,
            jumpedNow.AddSeconds(1),
            CancellationToken.None);
        await store.MarkRefreshedAsync(
            jumpedNow.AddSeconds(2),
            jumpedNow.AddSeconds(2),
            CancellationToken.None);

        var retainedReset = firstReset + 1_023;
        var reconstructed = new JsonLiveAttemptStore(path);
        var afterRollback = await reconstructed.TryPrepareAsync(
            Candidate(retainedReset),
            "different-credit-id",
            new FakeSecretProtector(),
            Now,
            CancellationToken.None);

        Assert.AreEqual(
            LivePrepareDisposition.ExistingTerminal,
            afterRollback.Disposition);
        Assert.AreEqual(retainedReset, afterRollback.Attempt.ResetsAt);
    }

    private static LiveAttemptCandidate Candidate(long resetsAt) => new(
        $"codex|weekly|10080|{resetsAt}",
        7,
        10_080,
        resetsAt);

    private static JsonObject TerminalAttempt(
        int index,
        long resetsAt,
        bool refreshRequired) => new()
        {
            ["intervalKey"] = $"codex|weekly|10080|{resetsAt}",
            ["triggerLimit"] = "weekly",
            ["thresholdPercent"] = 7,
            ["normalizedDurationMinutes"] = 10_080,
            ["resetsAt"] = resetsAt,
            ["idempotencyKey"] = IdempotencyKey(index),
            ["protectedCreditId"] = null,
            ["phase"] = "terminal",
            ["dispatchCount"] = 1,
            ["outcome"] = "nothingToReset",
            ["blockReason"] = null,
            ["refreshRequired"] = refreshRequired,
            ["preparedAt"] = Timestamp(Now.AddHours(-2)),
            ["updatedAt"] = Timestamp(Now.AddHours(-1)),
            ["completedAt"] = Timestamp(Now.AddHours(-1)),
        };

    private static JsonObject NeedsReviewAttempt(int index)
    {
        var resetsAt = Now.AddHours(-1).ToUnixTimeSeconds();
        return new JsonObject
        {
            ["intervalKey"] = $"codex|weekly|10080|{resetsAt}",
            ["triggerLimit"] = "weekly",
            ["thresholdPercent"] = 7,
            ["normalizedDurationMinutes"] = 10_080,
            ["resetsAt"] = resetsAt,
            ["idempotencyKey"] = IdempotencyKey(index),
            ["protectedCreditId"] = Convert.ToBase64String(
                Encoding.UTF8.GetBytes("protected:credit-id")),
            ["phase"] = "needsReview",
            ["dispatchCount"] = 1,
            ["outcome"] = null,
            ["blockReason"] = "unknownFailure",
            ["refreshRequired"] = false,
            ["preparedAt"] = Timestamp(Now.AddHours(-2)),
            ["updatedAt"] = Timestamp(Now.AddHours(-1)),
            ["completedAt"] = null,
        };
    }

    private static string IdempotencyKey(int index) => new Guid(
        index + 1,
        0,
        0,
        new byte[8]).ToString("D");

    private static string Timestamp(DateTimeOffset value) => value.ToString(
        "O",
        CultureInfo.InvariantCulture);

    private static Task WriteStateAsync(string path, JsonArray attempts) =>
        File.WriteAllTextAsync(
            path,
            new JsonObject
            {
                ["schemaVersion"] = 1,
                ["attempts"] = attempts,
            }.ToJsonString());

    private sealed class FakeSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"protected:{plaintext}"));

        public string Unprotect(string protectedValue) => throw new NotSupportedException();
    }
}
