using System.Text;
using System.Text.Json.Nodes;
using CodexAutoReset.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexAutoReset.Tests;

[TestClass]
public sealed class LiveResetCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(
        2026,
        7,
        21,
        0,
        0,
        0,
        TimeSpan.Zero);

    [DataTestMethod]
    [DataRow(ConsumeResetCreditOutcome.Reset)]
    [DataRow(ConsumeResetCreditOutcome.AlreadyRedeemed)]
    [DataRow(ConsumeResetCreditOutcome.NothingToReset)]
    [DataRow(ConsumeResetCreditOutcome.NoCredit)]
    public async Task EveryKnownOutcomeBecomesTerminalAndRefreshes(
        ConsumeResetCreditOutcome outcome)
    {
        using var directory = TemporaryDirectory.Create();
        var client = new FakeAccountRateLimitClient
        {
            ConsumeHandler = (_, _) => Task.FromResult(new ConsumeResetCreditResult(outcome)),
            ReadHandler = _ => Task.FromResult(CreateLimits(Now.AddSeconds(3))),
        };
        var coordinator = CreateCoordinator(directory, client);

        var result = await coordinator.ExecuteAsync(
            LiveSettings(),
            CreateLimits(Now),
            Now,
            CancellationToken.None);

        Assert.AreEqual(LiveResetCycleKind.Completed, result.Kind);
        Assert.AreEqual(outcome, result.Outcome);
        Assert.IsTrue(result.ConsumeAttempted);
        Assert.IsFalse(result.RequiresRefresh);
        Assert.IsNotNull(result.RefreshedRateLimits);
        Assert.AreEqual(1, client.ConsumeRequests.Count);
        Assert.AreEqual("opaque-credit-sentinel", client.ConsumeRequests[0].CreditId);
        Assert.IsTrue(Guid.TryParseExact(
            client.ConsumeRequests[0].IdempotencyKey,
            "D",
            out _));

        var attempt = AssertSingleAttempt(await coordinator.ReadAttemptsAsync(
            CancellationToken.None));
        Assert.AreEqual(LiveAttemptPhase.Terminal, attempt.Phase);
        Assert.AreEqual(outcome, attempt.Outcome);
        Assert.IsFalse(attempt.RefreshRequired);
    }

    [TestMethod]
    public async Task DisabledAutomationNeverCreatesStateOrCallsConsumer()
    {
        using var directory = TemporaryDirectory.Create();
        var client = new FakeAccountRateLimitClient();
        var coordinator = CreateCoordinator(directory, client);

        var result = await coordinator.ExecuteAsync(
            GuardSettings.Default,
            CreateLimits(Now),
            Now,
            CancellationToken.None);

        Assert.AreEqual(LiveResetCycleKind.AutomationDisabled, result.Kind);
        Assert.AreEqual(0, client.ConsumeRequests.Count);
        Assert.AreEqual(
            0,
            (await coordinator.ReadAttemptsAsync(CancellationToken.None)).Count);
    }

    [TestMethod]
    public async Task UnverifiedConsumeSchemaBlocksBeforeMutationWithoutDurableMarker()
    {
        using var directory = TemporaryDirectory.Create();
        var client = SuccessfulClient();
        var coordinator = CreateCoordinator(directory, client);
        var snapshot = CreateLimits(Now) with
        {
            ConsumeSchemaCompatible = false,
        };

        var result = await coordinator.ExecuteAsync(
            LiveSettings(),
            snapshot,
            Now,
            CancellationToken.None);

        Assert.AreEqual(LiveResetCycleKind.Blocked, result.Kind);
        Assert.AreEqual(
            LiveAttemptBlockReason.ProtocolMismatch,
            result.ProcessBlockReason);
        Assert.IsFalse(result.ConsumeAttempted);
        Assert.AreEqual(0, client.ConsumeRequests.Count);
        Assert.AreEqual(
            0,
            (await coordinator.ReadAttemptsAsync(CancellationToken.None)).Count);
        Assert.IsFalse(File.Exists(Path.Combine(
            directory.Path,
            "live-safety-block.json")));
    }

    [TestMethod]
    public async Task TerminalAttemptSuppressesSameIntervalAcrossCoordinatorInstances()
    {
        using var directory = TemporaryDirectory.Create();
        var client = SuccessfulClient();
        var first = CreateCoordinator(directory, client);
        await first.ExecuteAsync(
            LiveSettings(),
            CreateLimits(Now),
            Now,
            CancellationToken.None);

        var second = CreateCoordinator(directory, client);
        var result = await second.ExecuteAsync(
            LiveSettings(),
            CreateLimits(Now.AddMinutes(1), observedAt: Now.AddMinutes(1)),
            Now.AddMinutes(1),
            CancellationToken.None);

        Assert.AreEqual(LiveResetCycleKind.DuplicateSuppressed, result.Kind);
        Assert.AreEqual(1, client.ConsumeRequests.Count);
    }

    [TestMethod]
    public async Task RetryableFailureReusesExactIdempotencyKeyAndCreditId()
    {
        using var directory = TemporaryDirectory.Create();
        var callNumber = 0;
        var client = SuccessfulClient();
        client.ConsumeHandler = (_, _) =>
        {
            callNumber++;
            return callNumber == 1
                ? Task.FromException<ConsumeResetCreditResult>(new RetryableException())
                : Task.FromResult(new ConsumeResetCreditResult(
                    ConsumeResetCreditOutcome.Reset));
        };
        var classifier = new FixedFailureClassifier(
            LiveResetFailureDisposition.Retryable);
        var first = CreateCoordinator(directory, client, classifier: classifier);

        await Assert.ThrowsExceptionAsync<RetryableException>(() => first.ExecuteAsync(
            LiveSettings(),
            CreateLimits(Now),
            Now,
            CancellationToken.None));
        var pending = AssertSingleAttempt(await first.ReadAttemptsAsync(
            CancellationToken.None));
        Assert.AreEqual(LiveAttemptPhase.Pending, pending.Phase);
        Assert.AreEqual(1, pending.DispatchCount);

        var second = CreateCoordinator(directory, client, classifier: classifier);
        var result = await second.ExecuteAsync(
            LiveSettings(),
            CreateLimits(Now.AddMinutes(1), observedAt: Now.AddMinutes(1)),
            Now.AddMinutes(1),
            CancellationToken.None);

        Assert.AreEqual(LiveResetCycleKind.Completed, result.Kind);
        Assert.AreEqual(2, client.ConsumeRequests.Count);
        Assert.AreEqual(
            client.ConsumeRequests[0].IdempotencyKey,
            client.ConsumeRequests[1].IdempotencyKey);
        Assert.AreEqual(
            client.ConsumeRequests[0].CreditId,
            client.ConsumeRequests[1].CreditId);
    }

    [TestMethod]
    public async Task ProtocolFailureBecomesStickyAndCannotRetry()
    {
        await AssertStickyFailureAsync(
            LiveResetFailureDisposition.ProtocolMismatch,
            LiveAttemptPhase.ProtocolBlocked,
            LiveAttemptBlockReason.ProtocolMismatch);
    }

    [TestMethod]
    public async Task UnknownFailureBecomesStickyAndCannotRetry()
    {
        await AssertStickyFailureAsync(
            LiveResetFailureDisposition.Unknown,
            LiveAttemptPhase.NeedsReview,
            LiveAttemptBlockReason.UnknownFailure);
    }

    [TestMethod]
    public async Task CallerCancellationAfterDispatchPreservesPending()
    {
        using var directory = TemporaryDirectory.Create();
        var client = SuccessfulClient();
        client.ConsumeHandler = (_, _) =>
            Task.FromException<ConsumeResetCreditResult>(
                new OperationCanceledException());
        var coordinator = CreateCoordinator(directory, client);

        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() =>
            coordinator.ExecuteAsync(
                LiveSettings(),
                CreateLimits(Now),
                Now,
                CancellationToken.None));

        var attempt = AssertSingleAttempt(await coordinator.ReadAttemptsAsync(
            CancellationToken.None));
        Assert.AreEqual(LiveAttemptPhase.Pending, attempt.Phase);
        Assert.AreEqual(1, attempt.DispatchCount);
    }

    [TestMethod]
    public async Task CancellationAfterResponseCannotSkipTerminalPersistence()
    {
        using var directory = TemporaryDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        var client = SuccessfulClient();
        client.ReadHandler = cancellationToken => cancellationToken.IsCancellationRequested
            ? Task.FromCanceled<AccountRateLimits>(cancellationToken)
            : Task.FromResult(CreateLimits(Now.AddSeconds(3)));
        client.ConsumeHandler = (_, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(new ConsumeResetCreditResult(
                ConsumeResetCreditOutcome.Reset));
        };
        var coordinator = CreateCoordinator(directory, client);

        var result = await coordinator.ExecuteAsync(
            LiveSettings(),
            CreateLimits(Now),
            Now,
            cancellation.Token);

        Assert.AreEqual(LiveResetCycleKind.Completed, result.Kind);
        Assert.IsTrue(result.RequiresRefresh);
        var attempt = AssertSingleAttempt(await coordinator.ReadAttemptsAsync(
            CancellationToken.None));
        Assert.AreEqual(LiveAttemptPhase.Terminal, attempt.Phase);
        Assert.AreEqual(ConsumeResetCreditOutcome.Reset, attempt.Outcome);

        var later = await coordinator.ExecuteAsync(
            LiveSettings(),
            CreateLimits(
                Now,
                observedAt: Now,
                weeklyResetsAt: Now.AddDays(6).ToUnixTimeSeconds()),
            Now,
            CancellationToken.None);
        Assert.AreEqual(LiveResetCycleKind.Completed, later.Kind);
        Assert.AreEqual(2, client.ConsumeRequests.Count);
    }

    [TestMethod]
    public async Task ChangedThresholdBlocksPendingWithoutAnotherCall()
    {
        using var directory = TemporaryDirectory.Create();
        var client = SuccessfulClient();
        client.ConsumeHandler = (_, _) =>
            Task.FromException<ConsumeResetCreditResult>(new RetryableException());
        var classifier = new FixedFailureClassifier(
            LiveResetFailureDisposition.Retryable);
        var coordinator = CreateCoordinator(directory, client, classifier: classifier);
        await Assert.ThrowsExceptionAsync<RetryableException>(() => coordinator.ExecuteAsync(
            LiveSettings(),
            CreateLimits(Now),
            Now,
            CancellationToken.None));

        var changedSettings = LiveSettings() with { RemainingThresholdPercent = 8 };
        var result = await coordinator.ExecuteAsync(
            changedSettings,
            CreateLimits(Now.AddMinutes(1), observedAt: Now.AddMinutes(1)),
            Now.AddMinutes(1),
            CancellationToken.None);

        Assert.AreEqual(LiveResetCycleKind.Blocked, result.Kind);
        Assert.AreEqual(1, client.ConsumeRequests.Count);
        Assert.AreEqual(LiveAttemptBlockReason.ContextChanged, result.Attempt!.BlockReason);
        Assert.AreEqual(LiveAttemptPhase.NeedsReview, result.Attempt.Phase);
    }

    [TestMethod]
    public async Task PendingRetryUsesTriggerEvenWhenCreditSnapshotIsUnavailable()
    {
        using var directory = TemporaryDirectory.Create();
        var calls = 0;
        var client = SuccessfulClient();
        client.ConsumeHandler = (_, _) => ++calls == 1
            ? Task.FromException<ConsumeResetCreditResult>(new RetryableException())
            : Task.FromResult(new ConsumeResetCreditResult(
                ConsumeResetCreditOutcome.AlreadyRedeemed));
        var classifier = new FixedFailureClassifier(
            LiveResetFailureDisposition.Retryable);
        var coordinator = CreateCoordinator(directory, client, classifier: classifier);
        await Assert.ThrowsExceptionAsync<RetryableException>(() => coordinator.ExecuteAsync(
            LiveSettings(),
            CreateLimits(Now),
            Now,
            CancellationToken.None));

        var withoutCredits = CreateLimits(
            Now.AddMinutes(1),
            observedAt: Now.AddMinutes(1)) with
        {
            ResetCredits = null,
        };
        var result = await coordinator.ExecuteAsync(
            LiveSettings(),
            withoutCredits,
            Now.AddMinutes(1),
            CancellationToken.None);

        Assert.AreEqual(LiveResetCycleKind.Completed, result.Kind);
        Assert.AreEqual(ConsumeResetCreditOutcome.AlreadyRedeemed, result.Outcome);
        Assert.AreEqual(2, client.ConsumeRequests.Count);
    }

    [TestMethod]
    public async Task UnprotectFailureCreatesStickyNeedsReview()
    {
        using var directory = TemporaryDirectory.Create();
        var calls = 0;
        var client = SuccessfulClient();
        client.ConsumeHandler = (_, _) =>
        {
            calls++;
            return Task.FromException<ConsumeResetCreditResult>(new RetryableException());
        };
        var protector = new FakeSecretProtector();
        var classifier = new FixedFailureClassifier(
            LiveResetFailureDisposition.Retryable);
        var coordinator = CreateCoordinator(
            directory,
            client,
            protector,
            classifier);
        await Assert.ThrowsExceptionAsync<RetryableException>(() => coordinator.ExecuteAsync(
            LiveSettings(),
            CreateLimits(Now),
            Now,
            CancellationToken.None));

        protector.FailUnprotect = true;
        var result = await coordinator.ExecuteAsync(
            LiveSettings(),
            CreateLimits(Now.AddMinutes(1), observedAt: Now.AddMinutes(1)),
            Now.AddMinutes(1),
            CancellationToken.None);

        Assert.AreEqual(LiveResetCycleKind.Blocked, result.Kind);
        Assert.AreEqual(1, calls);
        Assert.AreEqual(LiveAttemptBlockReason.SecretUnavailable, result.Attempt!.BlockReason);
    }

    [TestMethod]
    public async Task PlaintextCreditIdIsNeverWrittenToPendingState()
    {
        using var directory = TemporaryDirectory.Create();
        var client = SuccessfulClient();
        client.ConsumeHandler = (_, _) =>
            Task.FromException<ConsumeResetCreditResult>(new RetryableException());
        var coordinator = CreateCoordinator(
            directory,
            client,
            classifier: new FixedFailureClassifier(
                LiveResetFailureDisposition.Retryable));
        await Assert.ThrowsExceptionAsync<RetryableException>(() => coordinator.ExecuteAsync(
            LiveSettings(),
            CreateLimits(Now),
            Now,
            CancellationToken.None));

        var json = await File.ReadAllTextAsync(
            System.IO.Path.Combine(directory.Path, "live-state.json"));
        Assert.IsFalse(json.Contains("opaque-credit-sentinel", StringComparison.Ordinal));
        StringAssert.Contains(json, "\"protectedCreditId\"");
    }

    [TestMethod]
    public async Task InvalidTypedOutcomeBecomesProtocolBlockedBeforeTerminal()
    {
        using var directory = TemporaryDirectory.Create();
        var client = SuccessfulClient();
        client.ConsumeHandler = (_, _) => Task.FromResult(new ConsumeResetCreditResult(
            (ConsumeResetCreditOutcome)999));
        var coordinator = CreateCoordinator(directory, client);

        var result = await coordinator.ExecuteAsync(
            LiveSettings(),
            CreateLimits(Now),
            Now,
            CancellationToken.None);

        Assert.AreEqual(LiveResetCycleKind.Blocked, result.Kind);
        Assert.IsTrue(result.ConsumeAttempted);
        Assert.IsTrue(result.RequiresRefresh);
        Assert.AreEqual(LiveAttemptPhase.ProtocolBlocked, result.Attempt!.Phase);
        Assert.IsNull(result.Attempt.Outcome);
    }

    [DataTestMethod]
    [DataRow(LiveResetFailureDisposition.ProtocolMismatch, "live_sticky_state_missing")]
    [DataRow(LiveResetFailureDisposition.Unknown, "live_sticky_state_missing")]
    public async Task StickyPersistenceFailureLatchesAcrossCoordinatorInstances(
        LiveResetFailureDisposition disposition,
        string expectedReasonCode)
    {
        using var directory = TemporaryDirectory.Create();
        var statePath = System.IO.Path.Combine(directory.Path, "live-state.json");
        var client = SuccessfulClient();
        client.ConsumeHandler = (_, _) =>
        {
            File.Delete(statePath);
            Directory.CreateDirectory(statePath);
            return Task.FromException<ConsumeResetCreditResult>(
                new ClassifiedException());
        };
        var latch = new LiveResetSafetyLatch();
        var classifier = new FixedFailureClassifier(disposition);
        var first = CreateCoordinator(
            directory,
            client,
            classifier: classifier,
            safetyLatch: latch);

        var firstFailure = await Assert.ThrowsExceptionAsync<LiveStateException>(() =>
            first.ExecuteAsync(
                LiveSettings(),
                CreateLimits(Now),
                Now,
                CancellationToken.None));
        Assert.AreEqual(expectedReasonCode, firstFailure.ReasonCode);

        var second = CreateCoordinator(
            directory,
            client,
            classifier: classifier,
            safetyLatch: latch);
        var secondResult = await second.ExecuteAsync(
            LiveSettings(),
            CreateLimits(Now.AddMinutes(1), observedAt: Now.AddMinutes(1)),
            Now.AddMinutes(1),
            CancellationToken.None);

        Assert.AreEqual(LiveResetCycleKind.Blocked, secondResult.Kind);
        Assert.AreEqual(
            disposition == LiveResetFailureDisposition.ProtocolMismatch
                ? LiveAttemptBlockReason.ProtocolMismatch
                : LiveAttemptBlockReason.UnknownFailure,
            secondResult.ProcessBlockReason);
        Assert.AreEqual(1, client.ConsumeRequests.Count);
    }

    [TestMethod]
    public async Task MarkerWriteFailureStillPersistsStickyStateBeforeRestart()
    {
        using var directory = TemporaryDirectory.Create();
        var markerPath = System.IO.Path.Combine(
            directory.Path,
            "live-safety-block.json");
        var firstClient = SuccessfulClient();
        firstClient.ConsumeHandler = (_, _) =>
            Task.FromException<ConsumeResetCreditResult>(
                new ClassifiedException());
        var firstLatch = new LiveResetSafetyLatch(
            markerPath,
            () => throw new IOException("simulated_marker_failure"));
        var classifier = new FixedFailureClassifier(
            LiveResetFailureDisposition.ProtocolMismatch);
        var first = CreateCoordinator(
            directory,
            firstClient,
            classifier: classifier,
            safetyLatch: firstLatch);

        var failure = await Assert.ThrowsExceptionAsync<LiveStateException>(() =>
            first.ExecuteAsync(
                LiveSettings(),
                CreateLimits(Now),
                Now,
                CancellationToken.None));

        Assert.AreEqual("live_safety_block_persist_failed", failure.ReasonCode);
        Assert.IsFalse(File.Exists(markerPath));
        Assert.IsFalse(File.Exists(markerPath + ".tmp"));
        var durableAttempt = AssertSingleAttempt(await first.ReadAttemptsAsync(
            CancellationToken.None));
        Assert.AreEqual(LiveAttemptPhase.ProtocolBlocked, durableAttempt.Phase);

        var restartedClient = SuccessfulClient();
        var restarted = CreateCoordinator(
            directory,
            restartedClient,
            classifier: classifier,
            safetyLatch: new LiveResetSafetyLatch(markerPath));
        var result = await restarted.ExecuteAsync(
            LiveSettings(),
            CreateLimits(Now.AddSeconds(3), observedAt: Now.AddSeconds(3)),
            Now.AddSeconds(3),
            CancellationToken.None);

        Assert.AreEqual(LiveResetCycleKind.Blocked, result.Kind);
        Assert.AreEqual(
            LiveAttemptBlockReason.ProtocolMismatch,
            result.Attempt!.BlockReason);
        Assert.AreEqual(1, firstClient.ConsumeRequests.Count);
        Assert.AreEqual(0, restartedClient.ConsumeRequests.Count);
    }

    [TestMethod]
    public async Task BlockPendingPersistsStateBeforeMarkerWriteFailure()
    {
        using var directory = TemporaryDirectory.Create();
        var retryableClient = SuccessfulClient();
        retryableClient.ConsumeHandler = (_, _) =>
            Task.FromException<ConsumeResetCreditResult>(new RetryableException());
        var retryableClassifier = new FixedFailureClassifier(
            LiveResetFailureDisposition.Retryable);
        var preparing = CreateCoordinator(
            directory,
            retryableClient,
            classifier: retryableClassifier);
        await Assert.ThrowsExceptionAsync<RetryableException>(() =>
            preparing.ExecuteAsync(
                LiveSettings(),
                CreateLimits(Now),
                Now,
                CancellationToken.None));

        var markerPath = System.IO.Path.Combine(
            directory.Path,
            "live-safety-block.json");
        var faulted = CreateCoordinator(
            directory,
            SuccessfulClient(),
            classifier: retryableClassifier,
            safetyLatch: new LiveResetSafetyLatch(
                markerPath,
                () => throw new IOException("simulated_marker_failure")));
        var failure = await Assert.ThrowsExceptionAsync<LiveStateException>(() =>
            faulted.BlockPendingAsync(
                LiveAttemptBlockReason.ProtocolMismatch,
                Now.AddSeconds(2),
                CancellationToken.None));

        Assert.AreEqual("live_safety_block_persist_failed", failure.ReasonCode);
        var durableAttempt = AssertSingleAttempt(await faulted.ReadAttemptsAsync(
            CancellationToken.None));
        Assert.AreEqual(LiveAttemptPhase.ProtocolBlocked, durableAttempt.Phase);

        var restartedClient = SuccessfulClient();
        var restarted = CreateCoordinator(
            directory,
            restartedClient,
            classifier: retryableClassifier,
            safetyLatch: new LiveResetSafetyLatch(markerPath));
        var result = await restarted.ExecuteAsync(
            LiveSettings(),
            CreateLimits(Now.AddSeconds(3), observedAt: Now.AddSeconds(3)),
            Now.AddSeconds(3),
            CancellationToken.None);

        Assert.AreEqual(LiveResetCycleKind.Blocked, result.Kind);
        Assert.AreEqual(0, restartedClient.ConsumeRequests.Count);
    }

    [TestMethod]
    public async Task MarkerOnlyWriteFailureBlocksCurrentProcessWithoutActiveAttempt()
    {
        using var directory = TemporaryDirectory.Create();
        var client = SuccessfulClient();
        var latch = new LiveResetSafetyLatch(
            System.IO.Path.Combine(directory.Path, "live-safety-block.json"),
            () => throw new IOException("simulated_marker_failure"));
        var coordinator = CreateCoordinator(
            directory,
            client,
            safetyLatch: latch);

        var failure = await Assert.ThrowsExceptionAsync<LiveStateException>(() =>
            coordinator.BlockPendingAsync(
                LiveAttemptBlockReason.UnknownFailure,
                Now,
                CancellationToken.None));
        var blocked = await coordinator.ExecuteAsync(
            LiveSettings(),
            CreateLimits(Now),
            Now,
            CancellationToken.None);

        Assert.AreEqual("live_safety_block_persist_failed", failure.ReasonCode);
        Assert.AreEqual(LiveResetCycleKind.Blocked, blocked.Kind);
        Assert.AreEqual(
            LiveAttemptBlockReason.UnknownFailure,
            blocked.ProcessBlockReason);
        Assert.AreEqual(0, client.ConsumeRequests.Count);
    }

    [TestMethod]
    public async Task LocalStateWriteFailurePreventsConsumerCall()
    {
        using var directory = TemporaryDirectory.Create();
        Directory.CreateDirectory(System.IO.Path.Combine(directory.Path, "live-state.json"));
        var client = SuccessfulClient();
        var coordinator = CreateCoordinator(directory, client);

        await Assert.ThrowsExceptionAsync<LiveStateException>(() => coordinator.ExecuteAsync(
            LiveSettings(),
            CreateLimits(Now),
            Now,
            CancellationToken.None));

        Assert.AreEqual(0, client.ConsumeRequests.Count);
    }

    [DataTestMethod]
    [DataRow("unknownRoot")]
    [DataRow("unknownAttempt")]
    [DataRow("duplicateRoot")]
    [DataRow("invalidPhase")]
    [DataRow("invalidIdempotency")]
    public async Task CorruptLiveStateFailsClosedWithoutSensitiveParserDetails(
        string corruption)
    {
        using var directory = TemporaryDirectory.Create();
        var client = SuccessfulClient();
        client.ConsumeHandler = (_, _) =>
            Task.FromException<ConsumeResetCreditResult>(new RetryableException());
        var coordinator = CreateCoordinator(
            directory,
            client,
            classifier: new FixedFailureClassifier(
                LiveResetFailureDisposition.Retryable));
        await Assert.ThrowsExceptionAsync<RetryableException>(() => coordinator.ExecuteAsync(
            LiveSettings(),
            CreateLimits(Now),
            Now,
            CancellationToken.None));

        var path = System.IO.Path.Combine(directory.Path, "live-state.json");
        var original = await File.ReadAllTextAsync(path);
        var document = JsonNode.Parse(original)!.AsObject();
        var attempt = document["attempts"]!.AsArray()[0]!.AsObject();
        var corrupted = corruption switch
        {
            "unknownRoot" => AddUnknown(document),
            "unknownAttempt" => AddUnknown(attempt, document),
            "duplicateRoot" => original.Replace(
                "\"schemaVersion\": 1,",
                "\"schemaVersion\": 1, \"schemaVersion\": 1,",
                StringComparison.Ordinal),
            "invalidPhase" => SetValue(attempt, "phase", "mystery", document),
            "invalidIdempotency" => SetValue(
                attempt,
                "idempotencyKey",
                "sensitive-sentinel",
                document),
            _ => throw new AssertFailedException(),
        };
        await File.WriteAllTextAsync(path, corrupted);

        var exception = await Assert.ThrowsExceptionAsync<LiveStateException>(() =>
            coordinator.ReadAttemptsAsync(CancellationToken.None));
        Assert.AreEqual("live_state_invalid", exception.Message);
        Assert.IsNull(exception.InnerException);
        Assert.IsFalse(exception.ToString().Contains(
            "sensitive-sentinel",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task PostRefreshFailureKeepsDurableTerminalRefreshRequirement()
    {
        using var directory = TemporaryDirectory.Create();
        var client = SuccessfulClient();
        client.ReadHandler = _ => Task.FromException<AccountRateLimits>(
            new RetryableException());
        var coordinator = CreateCoordinator(
            directory,
            client,
            classifier: new FixedFailureClassifier(
                LiveResetFailureDisposition.Retryable));

        var result = await coordinator.ExecuteAsync(
            LiveSettings(),
            CreateLimits(Now),
            Now,
            CancellationToken.None);

        Assert.AreEqual(LiveResetCycleKind.Completed, result.Kind);
        Assert.IsTrue(result.RequiresRefresh);
        Assert.AreEqual(LiveAttemptPhase.Terminal, result.Attempt!.Phase);
        Assert.AreEqual(ConsumeResetCreditOutcome.Reset, result.Attempt.Outcome);
    }

    [DataTestMethod]
    [DataRow(
        LiveResetFailureDisposition.ProtocolMismatch,
        LiveAttemptBlockReason.ProtocolMismatch)]
    [DataRow(
        LiveResetFailureDisposition.Unknown,
        LiveAttemptBlockReason.UnknownFailure)]
    public async Task NonRetryablePostRefreshFailureLatchesAfterTerminalOutcome(
        LiveResetFailureDisposition disposition,
        LiveAttemptBlockReason expectedReason)
    {
        using var directory = TemporaryDirectory.Create();
        var client = SuccessfulClient();
        client.ReadHandler = _ => Task.FromException<AccountRateLimits>(
            new ClassifiedException());
        var coordinator = CreateCoordinator(
            directory,
            client,
            classifier: new FixedFailureClassifier(disposition));

        var completed = await coordinator.ExecuteAsync(
            LiveSettings(),
            CreateLimits(Now),
            Now,
            CancellationToken.None);

        Assert.AreEqual(LiveResetCycleKind.Completed, completed.Kind);
        Assert.AreEqual(ConsumeResetCreditOutcome.Reset, completed.Outcome);
        Assert.IsTrue(completed.RequiresRefresh);
        Assert.AreEqual(LiveAttemptPhase.Terminal, completed.Attempt!.Phase);
        Assert.IsTrue(completed.Attempt.RefreshRequired);

        var laterSnapshot = CreateLimits(
            Now,
            observedAt: Now,
            weeklyResetsAt: Now.AddDays(6).ToUnixTimeSeconds());
        var blocked = await coordinator.ExecuteAsync(
            LiveSettings(),
            laterSnapshot,
            Now,
            CancellationToken.None);

        Assert.AreEqual(LiveResetCycleKind.Blocked, blocked.Kind);
        Assert.AreEqual(expectedReason, blocked.ProcessBlockReason);
        Assert.AreEqual(1, client.ConsumeRequests.Count);
        var terminal = AssertSingleAttempt(await coordinator.ReadAttemptsAsync(
            CancellationToken.None));
        Assert.AreEqual(LiveAttemptPhase.Terminal, terminal.Phase);
        Assert.AreEqual(ConsumeResetCreditOutcome.Reset, terminal.Outcome);
        Assert.IsTrue(terminal.RefreshRequired);
    }

    [TestMethod]
    public async Task ConcurrentSameIntervalExecutionsCallConsumerOnce()
    {
        using var directory = TemporaryDirectory.Create();
        var client = SuccessfulClient();
        client.ConsumeHandler = async (_, cancellationToken) =>
        {
            await Task.Delay(50, cancellationToken);
            return new ConsumeResetCreditResult(ConsumeResetCreditOutcome.Reset);
        };
        var coordinator = CreateCoordinator(directory, client);

        var first = coordinator.ExecuteAsync(
            LiveSettings(),
            CreateLimits(Now),
            Now,
            CancellationToken.None);
        var second = coordinator.ExecuteAsync(
            LiveSettings(),
            CreateLimits(Now),
            Now,
            CancellationToken.None);
        var results = await Task.WhenAll(first, second);

        Assert.AreEqual(1, client.ConsumeRequests.Count);
        Assert.IsTrue(results.Any(result => result.Kind == LiveResetCycleKind.Completed));
        Assert.IsTrue(results.Any(result =>
            result.Kind == LiveResetCycleKind.DuplicateSuppressed));
    }

    [TestMethod]
    public async Task LegacyFiveHourPendingAttemptBlocksWithoutMutationOrRetargeting()
    {
        using var directory = TemporaryDirectory.Create();
        var client = SuccessfulClient();
        client.ConsumeHandler = (_, _) =>
            Task.FromException<ConsumeResetCreditResult>(new RetryableException());
        var coordinator = CreateCoordinator(
            directory,
            client,
            classifier: new FixedFailureClassifier(
                LiveResetFailureDisposition.Retryable));
        await Assert.ThrowsExceptionAsync<RetryableException>(() => coordinator.ExecuteAsync(
            LiveSettings(),
            CreateLimits(Now),
            Now,
            CancellationToken.None));

        var path = System.IO.Path.Combine(directory.Path, "live-state.json");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        var attempt = document["attempts"]!.AsArray()[0]!.AsObject();
        var resetsAt = attempt["resetsAt"]!.GetValue<long>();
        attempt["triggerLimit"] = "fiveHour";
        attempt["normalizedDurationMinutes"] = 300;
        attempt["intervalKey"] = $"codex|fiveHour|300|{resetsAt}";
        var legacyState = document.ToJsonString();
        await File.WriteAllTextAsync(path, legacyState);

        var result = await coordinator.ExecuteAsync(
            LiveSettings(),
            CreateLimits(Now),
            Now,
            CancellationToken.None);

        Assert.AreEqual(LiveResetCycleKind.Blocked, result.Kind);
        Assert.AreEqual(
            LiveAttemptBlockReason.LegacyTriggerUnsupported,
            result.Attempt!.BlockReason);
        Assert.AreEqual(1, client.ConsumeRequests.Count);
        Assert.AreEqual(legacyState, await File.ReadAllTextAsync(path));
    }

    [TestMethod]
    public async Task OversizedLiveStateFailsClosed()
    {
        using var directory = TemporaryDirectory.Create();
        var path = System.IO.Path.Combine(directory.Path, "live-state.json");
        await File.WriteAllTextAsync(path, new string(' ', (4 * 1024 * 1024) + 1));
        var store = new JsonLiveAttemptStore(path);

        var exception = await Assert.ThrowsExceptionAsync<LiveStateException>(() =>
            store.ReadAsync(CancellationToken.None));

        Assert.AreEqual("live_state_invalid", exception.ReasonCode);
    }

    [DataTestMethod]
    [DataRow("twoActive")]
    [DataRow("dispatchOverflow")]
    public async Task InvalidPendingInvariantsFailClosed(string corruption)
    {
        using var directory = TemporaryDirectory.Create();
        var client = SuccessfulClient();
        client.ConsumeHandler = (_, _) =>
            Task.FromException<ConsumeResetCreditResult>(new RetryableException());
        var coordinator = CreateCoordinator(
            directory,
            client,
            classifier: new FixedFailureClassifier(
                LiveResetFailureDisposition.Retryable));
        await Assert.ThrowsExceptionAsync<RetryableException>(() => coordinator.ExecuteAsync(
            LiveSettings(),
            CreateLimits(Now),
            Now,
            CancellationToken.None));

        var path = System.IO.Path.Combine(directory.Path, "live-state.json");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        var attempts = document["attempts"]!.AsArray();
        var attempt = attempts[0]!.AsObject();
        if (corruption == "twoActive")
        {
            var duplicate = attempt.DeepClone().AsObject();
            var resetsAt = duplicate["resetsAt"]!.GetValue<long>() + 1;
            duplicate["resetsAt"] = resetsAt;
            duplicate["intervalKey"] = $"codex|weekly|10080|{resetsAt}";
            duplicate["idempotencyKey"] = Guid.NewGuid().ToString("D");
            attempts.Add(duplicate);
        }
        else
        {
            attempt["dispatchCount"] = 33;
        }

        await File.WriteAllTextAsync(path, document.ToJsonString());
        await Assert.ThrowsExceptionAsync<LiveStateException>(() =>
            coordinator.ReadAttemptsAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task TerminalRecordCannotRetainProtectedCreditMaterial()
    {
        using var directory = TemporaryDirectory.Create();
        var coordinator = CreateCoordinator(directory, SuccessfulClient());
        await coordinator.ExecuteAsync(
            LiveSettings(),
            CreateLimits(Now),
            Now,
            CancellationToken.None);

        var path = System.IO.Path.Combine(directory.Path, "live-state.json");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        var attempt = document["attempts"]!.AsArray()[0]!.AsObject();
        attempt["protectedCreditId"] = Convert.ToBase64String([1, 2, 3]);
        await File.WriteAllTextAsync(path, document.ToJsonString());

        await Assert.ThrowsExceptionAsync<LiveStateException>(() =>
            coordinator.ReadAttemptsAsync(CancellationToken.None));
    }

    private static async Task AssertStickyFailureAsync(
        LiveResetFailureDisposition disposition,
        LiveAttemptPhase expectedPhase,
        LiveAttemptBlockReason expectedReason)
    {
        using var directory = TemporaryDirectory.Create();
        var client = SuccessfulClient();
        client.ConsumeHandler = (_, _) =>
            Task.FromException<ConsumeResetCreditResult>(new ClassifiedException());
        var coordinator = CreateCoordinator(
            directory,
            client,
            classifier: new FixedFailureClassifier(disposition));

        await Assert.ThrowsExceptionAsync<ClassifiedException>(() =>
            coordinator.ExecuteAsync(
                LiveSettings(),
                CreateLimits(Now),
                Now,
                CancellationToken.None));
        var afterFailure = AssertSingleAttempt(await coordinator.ReadAttemptsAsync(
            CancellationToken.None));
        Assert.AreEqual(expectedPhase, afterFailure.Phase);
        Assert.AreEqual(expectedReason, afterFailure.BlockReason);

        var result = await coordinator.ExecuteAsync(
            LiveSettings(),
            CreateLimits(Now.AddMinutes(1), observedAt: Now.AddMinutes(1)),
            Now.AddMinutes(1),
            CancellationToken.None);
        Assert.AreEqual(LiveResetCycleKind.Blocked, result.Kind);
        Assert.AreEqual(expectedReason, result.ProcessBlockReason);
        Assert.AreEqual(1, client.ConsumeRequests.Count);
    }

    private static LiveResetCoordinator CreateCoordinator(
        TemporaryDirectory directory,
        FakeAccountRateLimitClient client,
        FakeSecretProtector? protector = null,
        ILiveResetFailureClassifier? classifier = null,
        LiveResetSafetyLatch? safetyLatch = null) => new(
            new ResetDecisionEngine(),
            new JsonLiveAttemptStore(System.IO.Path.Combine(
                directory.Path,
                "live-state.json")),
            protector ?? new FakeSecretProtector(),
            client,
            classifier,
            new FixedTimeProvider(Now.AddSeconds(1)),
            safetyLatch);

    private static FakeAccountRateLimitClient SuccessfulClient() => new()
    {
        ConsumeHandler = (_, _) => Task.FromResult(new ConsumeResetCreditResult(
            ConsumeResetCreditOutcome.Reset)),
        ReadHandler = _ => Task.FromResult(CreateLimits(Now.AddSeconds(3))),
    };

    private static GuardSettings LiveSettings() => GuardSettings.Default with
    {
        AutomationEnabled = true,
    };

    private static AccountRateLimits CreateLimits(
        DateTimeOffset now,
        DateTimeOffset? observedAt = null,
        long? weeklyResetsAt = null)
    {
        var snapshot = new RateLimitSnapshot(
            "codex",
            null,
            new RateLimitWindow(
                20,
                300,
                now.AddHours(4).ToUnixTimeSeconds()),
            new RateLimitWindow(
                93,
                10_080,
                weeklyResetsAt ?? Now.AddDays(5).ToUnixTimeSeconds()));
        return new AccountRateLimits(
            snapshot,
            new Dictionary<string, RateLimitSnapshot> { ["codex"] = snapshot },
            new ResetCreditSummary(
                1,
                [
                    new ResetCredit(
                        "opaque-credit-sentinel",
                        "codexRateLimits",
                        "available",
                        Now.AddDays(-1).ToUnixTimeSeconds(),
                        Now.AddDays(2).ToUnixTimeSeconds(),
                        null,
                        null),
                ]),
            observedAt ?? now);
    }

    private static LiveAttemptSnapshot AssertSingleAttempt(
        IReadOnlyList<LiveAttemptSnapshot> attempts)
    {
        Assert.AreEqual(1, attempts.Count);
        return attempts[0];
    }

    private static string AddUnknown(JsonObject target, JsonObject? document = null)
    {
        target["unexpected"] = true;
        return (document ?? target).ToJsonString();
    }

    private static string SetValue(
        JsonObject target,
        string name,
        string value,
        JsonObject document)
    {
        target[name] = value;
        return document.ToJsonString();
    }

    private sealed class FakeSecretProtector : ISecretProtector
    {
        public bool FailUnprotect { get; set; }

        public string Protect(string plaintext) => Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"protected:{plaintext}"));

        public string Unprotect(string protectedValue)
        {
            if (FailUnprotect)
            {
                throw new InvalidOperationException("private protector text");
            }

            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue));
            return decoded["protected:".Length..];
        }
    }

    private sealed class FakeAccountRateLimitClient : IAccountRateLimitClient
    {
        public ConsumeHandlerDelegate ConsumeHandler { get; set; } =
            (_, _) => throw new AssertFailedException("Unexpected consume call.");

        public Func<CancellationToken, Task<AccountRateLimits>> ReadHandler { get; set; } =
            _ => throw new AssertFailedException("Unexpected read call.");

        public List<ConsumeResetCreditRequest> ConsumeRequests { get; } = [];

        public Task<ConsumeResetCreditResult> ConsumeResetCreditAsync(
            ConsumeResetCreditRequest request,
            CancellationToken cancellationToken)
        {
            ConsumeRequests.Add(request);
            return ConsumeHandler(request, cancellationToken);
        }

        public Task<AccountRateLimits> ReadAsync(CancellationToken cancellationToken) =>
            ReadHandler(cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public delegate Task<ConsumeResetCreditResult> ConsumeHandlerDelegate(
            ConsumeResetCreditRequest request,
            CancellationToken cancellationToken);
    }

    private sealed class FixedFailureClassifier(
        LiveResetFailureDisposition disposition) : ILiveResetFailureClassifier
    {
        public LiveResetFailureDisposition Classify(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            return disposition;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class RetryableException : Exception;

    private sealed class ClassifiedException : Exception;
}
