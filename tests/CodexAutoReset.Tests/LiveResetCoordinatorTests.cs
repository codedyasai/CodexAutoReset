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
    public async Task EveryKnownOutcomeBecomesTerminalAndSuccessfulOutcomeAwaitsRecovery(
        ConsumeResetCreditOutcome outcome)
    {
        using var directory = TemporaryDirectory.Create();
        var client = new FakeAccountRateLimitClient
        {
            ConsumeHandler = (_, _) => Task.FromResult(new ConsumeResetCreditResult(outcome)),
            ReadHandler = _ => Task.FromResult(CreateLimits(
                Now.AddSeconds(3),
                weeklyUsedPercent: 0)),
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
        var recoveryExpected = outcome is
            ConsumeResetCreditOutcome.Reset
            or ConsumeResetCreditOutcome.AlreadyRedeemed;
        Assert.AreEqual(recoveryExpected, result.RequiresRefresh);
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
        Assert.AreEqual(recoveryExpected, attempt.RefreshRequired);
    }

    [TestMethod]
    public async Task DisabledAutomationNeverCreatesStateOrCallsConsumer()
    {
        using var directory = TemporaryDirectory.Create();
        var client = new FakeAccountRateLimitClient();
        var coordinator = CreateCoordinator(directory, client);

        var result = await coordinator.ExecuteAsync(
            GuardSettings.Default with
            {
                RemainingThresholdPercent = 7,
                AutomationEnabled = false,
                FiveHourRemainingThresholdPercent = 7,
                FiveHourAutomationEnabled = false,
            },
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
    public async Task DefaultBlankLimitsNeverTriggerAtZeroRemaining()
    {
        using var directory = TemporaryDirectory.Create();
        var client = new FakeAccountRateLimitClient();
        var coordinator = CreateCoordinator(directory, client);

        var result = await coordinator.ExecuteAsync(
            GuardSettings.Default,
            CreateLimits(
                Now,
                weeklyUsedPercent: 100,
                fiveHourUsedPercent: 100),
            Now,
            CancellationToken.None);

        Assert.IsNull(GuardSettings.Default.RemainingThresholdPercent);
        Assert.IsFalse(GuardSettings.Default.AutomationEnabled);
        Assert.IsNull(GuardSettings.Default.FiveHourRemainingThresholdPercent);
        Assert.IsFalse(GuardSettings.Default.FiveHourAutomationEnabled);
        Assert.AreEqual(LiveResetCycleKind.AutomationDisabled, result.Kind);
        Assert.AreEqual(0, client.ConsumeRequests.Count);
        Assert.AreEqual(
            0,
            (await coordinator.ReadAttemptsAsync(CancellationToken.None)).Count);
    }

    [TestMethod]
    public async Task WeeklyFlagWithoutThresholdCannotEnableLiveAutomation()
    {
        using var directory = TemporaryDirectory.Create();
        var client = new FakeAccountRateLimitClient();
        var coordinator = CreateCoordinator(directory, client);
        var settings = GuardSettings.Default with
        {
            RemainingThresholdPercent = null,
            AutomationEnabled = true,
            FiveHourRemainingThresholdPercent = null,
            FiveHourAutomationEnabled = false,
        };

        var result = await coordinator.ExecuteAsync(
            settings,
            CreateLimits(
                Now,
                weeklyUsedPercent: 100,
                fiveHourUsedPercent: 100),
            Now,
            CancellationToken.None);

        Assert.IsFalse(settings.AnyAutomationEnabled);
        Assert.IsFalse(settings.IsAutomationEnabled(TriggerLimit.Weekly));
        Assert.AreEqual(LiveResetCycleKind.AutomationDisabled, result.Kind);
        Assert.AreEqual(0, client.ConsumeRequests.Count);
        Assert.AreEqual(
            0,
            (await coordinator.ReadAttemptsAsync(CancellationToken.None)).Count);
    }

    [TestMethod]
    public async Task FiveHourFlagWithoutThresholdCannotEnableLiveAutomation()
    {
        using var directory = TemporaryDirectory.Create();
        var client = new FakeAccountRateLimitClient();
        var coordinator = CreateCoordinator(directory, client);
        var settings = GuardSettings.Default with
        {
            RemainingThresholdPercent = 7,
            AutomationEnabled = false,
            FiveHourRemainingThresholdPercent = null,
            FiveHourAutomationEnabled = true,
        };

        var result = await coordinator.ExecuteAsync(
            settings,
            CreateLimits(
                Now,
                weeklyUsedPercent: 20,
                fiveHourUsedPercent: 100),
            Now,
            CancellationToken.None);

        Assert.AreEqual(LiveResetCycleKind.AutomationDisabled, result.Kind);
        Assert.AreEqual(0, client.ConsumeRequests.Count);
        Assert.AreEqual(
            0,
            (await coordinator.ReadAttemptsAsync(CancellationToken.None)).Count);
    }

    [TestMethod]
    public async Task WeeklyZeroThresholdConsumesAtExactZeroRemaining()
    {
        using var directory = TemporaryDirectory.Create();
        var client = SuccessfulClient();
        var coordinator = CreateCoordinator(directory, client);
        var settings = GuardSettings.Default with
        {
            RemainingThresholdPercent = 0,
            AutomationEnabled = true,
            FiveHourRemainingThresholdPercent = null,
            FiveHourAutomationEnabled = false,
        };

        var result = await coordinator.ExecuteAsync(
            settings,
            CreateLimits(
                Now,
                weeklyUsedPercent: 100,
                fiveHourUsedPercent: 100),
            Now,
            CancellationToken.None);

        Assert.IsTrue(settings.AnyAutomationEnabled);
        Assert.IsTrue(settings.IsAutomationEnabled(TriggerLimit.Weekly));
        Assert.AreEqual(LiveResetCycleKind.Completed, result.Kind);
        Assert.AreEqual(TriggerLimit.Weekly, result.Attempt!.TriggerLimit);
        Assert.AreEqual(0, result.Attempt.ThresholdPercent);
        Assert.AreEqual(1, client.ConsumeRequests.Count);
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
    public async Task CancellationAfterResponseKeepsRecoveryGateAcrossChangedInterval()
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
        Assert.AreEqual(LiveResetCycleKind.DuplicateSuppressed, later.Kind);
        Assert.IsTrue(later.RequiresRefresh);
        Assert.AreEqual(1, client.ConsumeRequests.Count);
    }

    [DataTestMethod]
    [DataRow(ConsumeResetCreditOutcome.Reset)]
    [DataRow(ConsumeResetCreditOutcome.AlreadyRedeemed)]
    public async Task SuccessfulOutcomeBlocksChangedIntervalWhileUsageHasNotRecovered(
        ConsumeResetCreditOutcome outcome)
    {
        using var directory = TemporaryDirectory.Create();
        var client = SuccessfulClient();
        client.ConsumeHandler = (_, _) => Task.FromResult(
            new ConsumeResetCreditResult(outcome));
        client.ReadHandler = _ => Task.FromException<AccountRateLimits>(
            new RetryableException());
        var coordinator = CreateCoordinator(
            directory,
            client,
            classifier: new FixedFailureClassifier(
                LiveResetFailureDisposition.Retryable));
        var credits = CreateCredits("first-credit", "second-credit");

        var completed = await coordinator.ExecuteAsync(
            LiveSettings(),
            CreateLimits(Now, credits: credits),
            Now,
            CancellationToken.None);
        var suppressed = await coordinator.ExecuteAsync(
            LiveSettings(),
            CreateLimits(
                Now.AddMinutes(2),
                observedAt: Now.AddMinutes(2),
                weeklyResetsAt: Now.AddDays(6).ToUnixTimeSeconds(),
                credits: credits),
            Now,
            CancellationToken.None);

        Assert.AreEqual(LiveResetCycleKind.Completed, completed.Kind);
        Assert.IsTrue(completed.RequiresRefresh);
        Assert.AreEqual(LiveResetCycleKind.DuplicateSuppressed, suppressed.Kind);
        Assert.IsTrue(suppressed.RequiresRefresh);
        Assert.AreEqual(1, client.ConsumeRequests.Count);
        Assert.AreEqual("first-credit", client.ConsumeRequests[0].CreditId);
    }

    [TestMethod]
    public async Task PostConsumeHighReadRequiresSeparateRecoveryCycleBeforeRearm()
    {
        using var directory = TemporaryDirectory.Create();
        var client = SuccessfulClient();
        var coordinator = CreateCoordinator(directory, client);
        var firstCredits = CreateCredits("first-credit", "second-credit");
        var secondCredit = CreateCredits("second-credit");
        var nextResetAt = Now.AddDays(6).ToUnixTimeSeconds();

        var completed = await coordinator.ExecuteAsync(
            LiveSettings(),
            CreateLimits(Now, credits: firstCredits),
            Now,
            CancellationToken.None);
        var lowAfterPostRead = await coordinator.ExecuteAsync(
            LiveSettings(),
            CreateLimits(
                Now.AddMinutes(2),
                observedAt: Now.AddMinutes(2),
                weeklyResetsAt: nextResetAt,
                credits: secondCredit),
            Now,
            CancellationToken.None);
        var regularRecoveryCycle = await coordinator.ExecuteAsync(
            LiveSettings(),
            CreateLimits(
                Now.AddMinutes(3),
                observedAt: Now.AddMinutes(3),
                weeklyResetsAt: nextResetAt,
                weeklyUsedPercent: 0,
                credits: secondCredit),
            Now,
            CancellationToken.None);
        var actualLaterLowUsage = await coordinator.ExecuteAsync(
            LiveSettings(),
            CreateLimits(
                Now.AddMinutes(4),
                observedAt: Now.AddMinutes(4),
                weeklyResetsAt: nextResetAt,
                credits: secondCredit),
            Now,
            CancellationToken.None);

        Assert.AreEqual(LiveResetCycleKind.Completed, completed.Kind);
        Assert.IsTrue(completed.RequiresRefresh);
        Assert.AreEqual(
            LiveResetCycleKind.DuplicateSuppressed,
            lowAfterPostRead.Kind);
        Assert.AreEqual(LiveResetCycleKind.NoAction, regularRecoveryCycle.Kind);
        Assert.AreEqual(LiveResetCycleKind.Completed, actualLaterLowUsage.Kind);
        Assert.AreEqual(2, client.ConsumeRequests.Count);
        Assert.AreEqual("first-credit", client.ConsumeRequests[0].CreditId);
        Assert.AreEqual("second-credit", client.ConsumeRequests[1].CreditId);
    }

    [DataTestMethod]
    [DataRow(ConsumeResetCreditOutcome.NothingToReset)]
    [DataRow(ConsumeResetCreditOutcome.NoCredit)]
    public async Task NoEffectOutcomeDoesNotCreateRecoveryGate(
        ConsumeResetCreditOutcome outcome)
    {
        using var directory = TemporaryDirectory.Create();
        var client = SuccessfulClient();
        client.ConsumeHandler = (_, _) => Task.FromResult(
            new ConsumeResetCreditResult(outcome));
        client.ReadHandler = _ => Task.FromException<AccountRateLimits>(
            new RetryableException());
        var coordinator = CreateCoordinator(
            directory,
            client,
            classifier: new FixedFailureClassifier(
                LiveResetFailureDisposition.Retryable));

        _ = await coordinator.ExecuteAsync(
            LiveSettings(),
            CreateLimits(
                Now,
                credits: CreateCredits("first-credit", "second-credit")),
            Now,
            CancellationToken.None);
        var nextInterval = await coordinator.ExecuteAsync(
            LiveSettings(),
            CreateLimits(
                Now.AddMinutes(2),
                observedAt: Now.AddMinutes(2),
                weeklyResetsAt: Now.AddDays(6).ToUnixTimeSeconds(),
                credits: CreateCredits("second-credit")),
            Now,
            CancellationToken.None);

        Assert.AreEqual(LiveResetCycleKind.Completed, nextInterval.Kind);
        Assert.AreEqual(2, client.ConsumeRequests.Count);
        Assert.AreEqual("second-credit", client.ConsumeRequests[1].CreditId);
    }

    [TestMethod]
    public async Task RecoveryGateUsesHigherCurrentThresholdAndRearmsAfterRecoveryCycle()
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
        var firstCredits = CreateCredits("first-credit", "second-credit");
        var secondCredit = CreateCredits("second-credit");

        _ = await coordinator.ExecuteAsync(
            LiveSettings(),
            CreateLimits(Now, credits: firstCredits),
            Now,
            CancellationToken.None);

        var raisedThresholdSettings = LiveSettings() with
        {
            RemainingThresholdPercent = 50,
        };
        var belowCurrentThreshold = await coordinator.ExecuteAsync(
            raisedThresholdSettings,
            CreateLimits(
                Now.AddMinutes(2),
                observedAt: Now.AddMinutes(2),
                weeklyResetsAt: Now.AddDays(6).ToUnixTimeSeconds(),
                weeklyUsedPercent: 60,
                credits: secondCredit),
            Now,
            CancellationToken.None);
        var recoveryCycle = await coordinator.ExecuteAsync(
            raisedThresholdSettings,
            CreateLimits(
                Now.AddMinutes(3),
                observedAt: Now.AddMinutes(3),
                weeklyResetsAt: Now.AddDays(6).ToUnixTimeSeconds(),
                weeklyUsedPercent: 40,
                credits: secondCredit),
            Now,
            CancellationToken.None);
        var afterRearm = await coordinator.ExecuteAsync(
            raisedThresholdSettings,
            CreateLimits(
                Now.AddMinutes(4),
                observedAt: Now.AddMinutes(4),
                weeklyResetsAt: Now.AddDays(6).ToUnixTimeSeconds(),
                weeklyUsedPercent: 60,
                credits: secondCredit),
            Now,
            CancellationToken.None);

        Assert.AreEqual(
            LiveResetCycleKind.DuplicateSuppressed,
            belowCurrentThreshold.Kind);
        Assert.AreEqual(LiveResetCycleKind.NoAction, recoveryCycle.Kind);
        Assert.AreEqual(LiveResetCycleKind.Completed, afterRearm.Kind);
        Assert.AreEqual(2, client.ConsumeRequests.Count);
        Assert.AreEqual("first-credit", client.ConsumeRequests[0].CreditId);
        Assert.AreEqual("second-credit", client.ConsumeRequests[1].CreditId);
    }

    [TestMethod]
    public async Task RecoveryGateSurvivesCoordinatorRestart()
    {
        using var directory = TemporaryDirectory.Create();
        var initialClient = SuccessfulClient();
        initialClient.ReadHandler = _ => Task.FromException<AccountRateLimits>(
            new RetryableException());
        var first = CreateCoordinator(
            directory,
            initialClient,
            classifier: new FixedFailureClassifier(
                LiveResetFailureDisposition.Retryable));
        var credits = CreateCredits("first-credit", "second-credit");

        _ = await first.ExecuteAsync(
            LiveSettings(),
            CreateLimits(Now, credits: credits),
            Now,
            CancellationToken.None);

        var restartedClient = SuccessfulClient();
        var restarted = CreateCoordinator(directory, restartedClient);
        var result = await restarted.ExecuteAsync(
            LiveSettings(),
            CreateLimits(
                Now.AddMinutes(2),
                observedAt: Now.AddMinutes(2),
                weeklyResetsAt: Now.AddDays(6).ToUnixTimeSeconds(),
                credits: credits),
            Now.AddMinutes(2),
            CancellationToken.None);

        Assert.AreEqual(LiveResetCycleKind.DuplicateSuppressed, result.Kind);
        Assert.IsTrue(result.RequiresRefresh);
        Assert.AreEqual(0, restartedClient.ConsumeRequests.Count);
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

        var suppressed = await coordinator.ExecuteAsync(
            LiveSettings(),
            CreateLimits(
                Now.AddMinutes(2),
                observedAt: Now.AddMinutes(2),
                weeklyResetsAt: Now.AddDays(6).ToUnixTimeSeconds()),
            Now,
            CancellationToken.None);

        Assert.AreEqual(LiveResetCycleKind.DuplicateSuppressed, suppressed.Kind);
        Assert.AreEqual(1, client.ConsumeRequests.Count);
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
    public async Task NewFiveHourAttemptIsDurableWhenEnabled()
    {
        using var directory = TemporaryDirectory.Create();
        var fiveHourResetsAt = Now.AddHours(4).ToUnixTimeSeconds();
        var credits = CreateCredits("five-hour-credit");
        var preflight = CreateLimits(
            Now.AddSeconds(1),
            observedAt: Now.AddSeconds(1),
            weeklyUsedPercent: 20,
            credits: credits,
            fiveHourUsedPercent: 95,
            fiveHourResetsAt: fiveHourResetsAt);
        var client = new FakeAccountRateLimitClient
        {
            ConsumeHandler = (_, _) =>
                Task.FromException<ConsumeResetCreditResult>(
                    new RetryableException()),
            ReadHandler = _ => Task.FromResult(preflight),
        };
        var classifier = new FixedFailureClassifier(
            LiveResetFailureDisposition.Retryable);
        var coordinator = CreateCoordinator(
            directory,
            client,
            classifier: classifier);

        await Assert.ThrowsExceptionAsync<RetryableException>(() =>
            coordinator.ExecuteAsync(
                FiveHourLiveSettings(),
                CreateLimits(
                    Now,
                    weeklyUsedPercent: 20,
                    credits: credits,
                    fiveHourUsedPercent: 95,
                    fiveHourResetsAt: fiveHourResetsAt),
                Now,
                CancellationToken.None));

        var pending = AssertSingleAttempt(await coordinator.ReadAttemptsAsync(
            CancellationToken.None));
        Assert.AreEqual(TriggerLimit.FiveHour, pending.TriggerLimit);
        Assert.AreEqual(LiveAttemptPhase.Pending, pending.Phase);
        Assert.AreEqual(1, pending.DispatchCount);
        Assert.AreEqual(300, pending.NormalizedDurationMinutes);
        Assert.AreEqual(fiveHourResetsAt, pending.ResetsAt);
        Assert.AreEqual(
            $"codex|fiveHour|300|{fiveHourResetsAt}",
            pending.IntervalKey);

        var restarted = CreateCoordinator(
            directory,
            client,
            classifier: classifier);
        var afterRestart = AssertSingleAttempt(await restarted.ReadAttemptsAsync(
            CancellationToken.None));
        Assert.AreEqual(pending, afterRestart);
    }

    [TestMethod]
    public async Task FiveHourEnabledFirstDispatchPreflightsBeforeConsume()
    {
        using var directory = TemporaryDirectory.Create();
        var events = new List<string>();
        var readCount = 0;
        var weeklyResetsAt = Now.AddDays(5).ToUnixTimeSeconds();
        var fiveHourResetsAt = Now.AddHours(4).ToUnixTimeSeconds();
        var credits = CreateCredits("preflight-credit");
        var client = new FakeAccountRateLimitClient
        {
            ConsumeHandler = (_, _) =>
            {
                events.Add("consume");
                return Task.FromResult(new ConsumeResetCreditResult(
                    ConsumeResetCreditOutcome.Reset));
            },
            ReadHandler = _ =>
            {
                readCount++;
                events.Add("read");
                return Task.FromResult(readCount == 1
                    ? CreateLimits(
                        Now.AddSeconds(1),
                        observedAt: Now.AddSeconds(1),
                        weeklyResetsAt: weeklyResetsAt,
                        weeklyUsedPercent: 95,
                        credits: credits,
                        fiveHourUsedPercent: 20,
                        fiveHourResetsAt: fiveHourResetsAt)
                    : CreateLimits(
                        Now.AddSeconds(2),
                        observedAt: Now.AddSeconds(2),
                        weeklyResetsAt: weeklyResetsAt,
                        weeklyUsedPercent: 0,
                        credits: credits,
                        fiveHourUsedPercent: 0,
                        fiveHourResetsAt: fiveHourResetsAt));
            },
        };
        var coordinator = CreateCoordinator(directory, client);

        var result = await coordinator.ExecuteAsync(
            DualLiveSettings(),
            CreateLimits(
                Now,
                weeklyResetsAt: weeklyResetsAt,
                weeklyUsedPercent: 95,
                credits: credits,
                fiveHourUsedPercent: 20,
                fiveHourResetsAt: fiveHourResetsAt),
            Now,
            CancellationToken.None);

        Assert.AreEqual(LiveResetCycleKind.Completed, result.Kind);
        Assert.AreEqual(TriggerLimit.Weekly, result.Attempt!.TriggerLimit);
        CollectionAssert.AreEqual(
            new[] { "read", "consume", "read" },
            events);
        Assert.AreEqual(1, client.ConsumeRequests.Count);
    }

    [DataTestMethod]
    [DataRow("window")]
    [DataRow("credit")]
    public async Task PreflightWindowOrCreditChangeBlocksBeforeConsume(
        string changedContext)
    {
        using var directory = TemporaryDirectory.Create();
        var readCount = 0;
        var fiveHourResetsAt = Now.AddHours(4).ToUnixTimeSeconds();
        var initialCredits = CreateCredits("initial-credit");
        var preflightCredits = changedContext == "credit"
            ? CreateCredits("different-credit")
            : initialCredits;
        var preflightResetsAt = changedContext == "window"
            ? fiveHourResetsAt + 60
            : fiveHourResetsAt;
        var client = new FakeAccountRateLimitClient
        {
            ReadHandler = _ =>
            {
                readCount++;
                return Task.FromResult(CreateLimits(
                    Now.AddSeconds(1),
                    observedAt: Now.AddSeconds(1),
                    weeklyUsedPercent: 20,
                    credits: preflightCredits,
                    fiveHourUsedPercent: 95,
                    fiveHourResetsAt: preflightResetsAt));
            },
        };
        var coordinator = CreateCoordinator(directory, client);
        var settings = FiveHourLiveSettings();
        var initial = CreateLimits(
            Now,
            weeklyUsedPercent: 20,
            credits: initialCredits,
            fiveHourUsedPercent: 95,
            fiveHourResetsAt: fiveHourResetsAt);

        var result = await coordinator.ExecuteAsync(
            settings,
            initial,
            Now,
            CancellationToken.None);

        Assert.AreEqual(LiveResetCycleKind.Blocked, result.Kind);
        Assert.IsFalse(result.ConsumeAttempted);
        Assert.IsNotNull(result.RefreshedRateLimits);
        Assert.AreEqual(0, client.ConsumeRequests.Count);
        Assert.AreEqual(1, readCount);
        Assert.AreEqual(LiveAttemptPhase.NeedsReview, result.Attempt!.Phase);
        Assert.AreEqual(
            LiveAttemptBlockReason.ContextChanged,
            result.Attempt.BlockReason);
        Assert.AreEqual(0, result.Attempt.DispatchCount);

        var restarted = CreateCoordinator(directory, client);
        var stillBlocked = await restarted.ExecuteAsync(
            settings,
            initial with { ObservedAt = Now.AddMinutes(1) },
            Now.AddMinutes(1),
            CancellationToken.None);
        Assert.AreEqual(LiveResetCycleKind.Blocked, stillBlocked.Kind);
        Assert.AreEqual(
            LiveAttemptBlockReason.ContextChanged,
            stillBlocked.Attempt!.BlockReason);
        Assert.AreEqual(0, client.ConsumeRequests.Count);
        Assert.AreEqual(1, readCount);
    }

    [TestMethod]
    public async Task BothLowPrioritizesWeeklyAndConsumesExactlyOnce()
    {
        using var directory = TemporaryDirectory.Create();
        var readCount = 0;
        var weeklyResetsAt = Now.AddDays(5).ToUnixTimeSeconds();
        var fiveHourResetsAt = Now.AddHours(4).ToUnixTimeSeconds();
        var credits = CreateCredits("first-credit", "second-credit");
        var client = new FakeAccountRateLimitClient
        {
            ConsumeHandler = (_, _) => Task.FromResult(
                new ConsumeResetCreditResult(ConsumeResetCreditOutcome.Reset)),
            ReadHandler = _ =>
            {
                readCount++;
                return Task.FromResult(readCount == 1
                    ? CreateLimits(
                        Now.AddSeconds(1),
                        observedAt: Now.AddSeconds(1),
                        weeklyResetsAt: weeklyResetsAt,
                        weeklyUsedPercent: 95,
                        credits: credits,
                        fiveHourUsedPercent: 95,
                        fiveHourResetsAt: fiveHourResetsAt)
                    : CreateLimits(
                        Now.AddSeconds(2),
                        observedAt: Now.AddSeconds(2),
                        weeklyResetsAt: weeklyResetsAt,
                        weeklyUsedPercent: 0,
                        credits: credits,
                        fiveHourUsedPercent: 0,
                        fiveHourResetsAt: fiveHourResetsAt));
            },
        };
        var coordinator = CreateCoordinator(directory, client);
        var settings = DualLiveSettings();

        var completed = await coordinator.ExecuteAsync(
            settings,
            CreateLimits(
                Now,
                weeklyResetsAt: weeklyResetsAt,
                weeklyUsedPercent: 95,
                credits: credits,
                fiveHourUsedPercent: 95,
                fiveHourResetsAt: fiveHourResetsAt),
            Now,
            CancellationToken.None);
        var suppressed = await coordinator.ExecuteAsync(
            settings,
            CreateLimits(
                Now.AddMinutes(1),
                observedAt: Now.AddMinutes(1),
                weeklyResetsAt: weeklyResetsAt,
                weeklyUsedPercent: 95,
                credits: CreateCredits("second-credit"),
                fiveHourUsedPercent: 95,
                fiveHourResetsAt: fiveHourResetsAt),
            Now.AddMinutes(1),
            CancellationToken.None);

        Assert.AreEqual(LiveResetCycleKind.Completed, completed.Kind);
        Assert.AreEqual(
            TriggerLimit.Weekly,
            completed.Evaluation.Decision.SelectedLimit);
        Assert.AreEqual(TriggerLimit.Weekly, completed.Attempt!.TriggerLimit);
        Assert.AreEqual(
            LiveResetCycleKind.DuplicateSuppressed,
            suppressed.Kind);
        Assert.AreEqual(1, client.ConsumeRequests.Count);
        Assert.AreEqual("first-credit", client.ConsumeRequests[0].CreditId);
        Assert.AreEqual(
            1,
            (await coordinator.ReadAttemptsAsync(CancellationToken.None)).Count);
    }

    [TestMethod]
    public async Task SuccessfulResetWaitsForEveryEnabledPresentWindowToRecover()
    {
        using var directory = TemporaryDirectory.Create();
        var weeklyResetsAt = Now.AddDays(5).ToUnixTimeSeconds();
        var fiveHourResetsAt = Now.AddHours(4).ToUnixTimeSeconds();
        var initialCredits = CreateCredits("first-credit", "second-credit");
        var remainingCredit = CreateCredits("second-credit");
        var reads = new Queue<AccountRateLimits>(
        [
            CreateLimits(
                Now.AddSeconds(1),
                observedAt: Now.AddSeconds(1),
                weeklyResetsAt: weeklyResetsAt,
                weeklyUsedPercent: 95,
                credits: initialCredits,
                fiveHourUsedPercent: 95,
                fiveHourResetsAt: fiveHourResetsAt),
            CreateLimits(
                Now.AddSeconds(2),
                observedAt: Now.AddSeconds(2),
                weeklyResetsAt: weeklyResetsAt,
                weeklyUsedPercent: 0,
                credits: remainingCredit,
                fiveHourUsedPercent: 95,
                fiveHourResetsAt: fiveHourResetsAt),
        ]);
        var client = new FakeAccountRateLimitClient
        {
            ConsumeHandler = (_, _) => Task.FromResult(
                new ConsumeResetCreditResult(ConsumeResetCreditOutcome.Reset)),
            ReadHandler = _ => Task.FromResult(reads.Dequeue()),
        };
        var coordinator = CreateCoordinator(directory, client);
        var settings = DualLiveSettings();

        var completed = await coordinator.ExecuteAsync(
            settings,
            CreateLimits(
                Now,
                weeklyResetsAt: weeklyResetsAt,
                weeklyUsedPercent: 95,
                credits: initialCredits,
                fiveHourUsedPercent: 95,
                fiveHourResetsAt: fiveHourResetsAt),
            Now,
            CancellationToken.None);
        var partialRecovery = await coordinator.ExecuteAsync(
            settings,
            CreateLimits(
                Now.AddMinutes(1),
                observedAt: Now.AddMinutes(1),
                weeklyResetsAt: weeklyResetsAt,
                weeklyUsedPercent: 0,
                credits: remainingCredit,
                fiveHourUsedPercent: 95,
                fiveHourResetsAt: fiveHourResetsAt),
            Now.AddMinutes(1),
            CancellationToken.None);
        var pendingAfterPartial = AssertSingleAttempt(
            await coordinator.ReadAttemptsAsync(CancellationToken.None));
        var fullRecovery = await coordinator.ExecuteAsync(
            settings,
            CreateLimits(
                Now.AddMinutes(2),
                observedAt: Now.AddMinutes(2),
                weeklyResetsAt: weeklyResetsAt,
                weeklyUsedPercent: 0,
                credits: remainingCredit,
                fiveHourUsedPercent: 0,
                fiveHourResetsAt: fiveHourResetsAt),
            Now.AddMinutes(2),
            CancellationToken.None);
        var recovered = AssertSingleAttempt(await coordinator.ReadAttemptsAsync(
            CancellationToken.None));

        Assert.AreEqual(LiveResetCycleKind.Completed, completed.Kind);
        Assert.IsTrue(completed.RequiresRefresh);
        Assert.AreEqual(
            LiveResetCycleKind.DuplicateSuppressed,
            partialRecovery.Kind);
        Assert.AreEqual(
            TriggerLimit.FiveHour,
            partialRecovery.Evaluation.Decision.SelectedLimit);
        Assert.IsTrue(pendingAfterPartial.RefreshRequired);
        Assert.AreEqual(LiveResetCycleKind.NoAction, fullRecovery.Kind);
        Assert.IsFalse(recovered.RefreshRequired);
        Assert.AreEqual(1, client.ConsumeRequests.Count);
        Assert.AreEqual("first-credit", client.ConsumeRequests[0].CreditId);
        Assert.AreEqual(0, reads.Count);
    }

    [TestMethod]
    public async Task LegacyFiveHourPendingWithSettingDisabledBecomesContextChanged()
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
            LiveAttemptBlockReason.ContextChanged,
            result.Attempt!.BlockReason);
        Assert.AreEqual(LiveAttemptPhase.NeedsReview, result.Attempt.Phase);
        Assert.AreEqual(TriggerLimit.FiveHour, result.Attempt.TriggerLimit);
        Assert.AreEqual(1, result.Attempt.DispatchCount);
        Assert.AreEqual(1, client.ConsumeRequests.Count);
        Assert.AreNotEqual(legacyState, await File.ReadAllTextAsync(path));

        var restarted = CreateCoordinator(directory, client);
        var stillBlocked = await restarted.ExecuteAsync(
            LiveSettings(),
            CreateLimits(
                Now.AddMinutes(1),
                observedAt: Now.AddMinutes(1)),
            Now.AddMinutes(1),
            CancellationToken.None);
        Assert.AreEqual(LiveResetCycleKind.Blocked, stillBlocked.Kind);
        Assert.AreEqual(
            LiveAttemptBlockReason.ContextChanged,
            stillBlocked.Attempt!.BlockReason);
        Assert.AreEqual(1, client.ConsumeRequests.Count);
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
        ReadHandler = _ => Task.FromResult(CreateLimits(
            Now.AddSeconds(3),
            weeklyUsedPercent: 0)),
    };

    private static GuardSettings LiveSettings() => GuardSettings.Default with
    {
        RemainingThresholdPercent = 7,
        AutomationEnabled = true,
        FiveHourRemainingThresholdPercent = null,
        FiveHourAutomationEnabled = false,
    };

    private static GuardSettings FiveHourLiveSettings() => GuardSettings.Default with
    {
        RemainingThresholdPercent = 7,
        AutomationEnabled = false,
        FiveHourRemainingThresholdPercent = 7,
        FiveHourAutomationEnabled = true,
    };

    private static GuardSettings DualLiveSettings() => LiveSettings() with
    {
        FiveHourRemainingThresholdPercent = 7,
        FiveHourAutomationEnabled = true,
    };

    private static IReadOnlyList<ResetCredit> CreateCredits(params string[] ids) =>
        ids.Select((id, index) => new ResetCredit(
            id,
            "codexRateLimits",
            "available",
            Now.AddDays(-1).ToUnixTimeSeconds(),
            Now.AddDays(index + 1).ToUnixTimeSeconds(),
            null,
            null)).ToArray();

    private static AccountRateLimits CreateLimits(
        DateTimeOffset now,
        DateTimeOffset? observedAt = null,
        long? weeklyResetsAt = null,
        double weeklyUsedPercent = 93,
        IReadOnlyList<ResetCredit>? credits = null,
        double fiveHourUsedPercent = 20,
        long? fiveHourResetsAt = null)
    {
        var snapshot = new RateLimitSnapshot(
            "codex",
            null,
            new RateLimitWindow(
                fiveHourUsedPercent,
                300,
                fiveHourResetsAt ?? now.AddHours(4).ToUnixTimeSeconds()),
            new RateLimitWindow(
                weeklyUsedPercent,
                10_080,
                weeklyResetsAt ?? Now.AddDays(5).ToUnixTimeSeconds()));
        var availableCredits = credits ??
        [
            new ResetCredit(
                "opaque-credit-sentinel",
                "codexRateLimits",
                "available",
                Now.AddDays(-1).ToUnixTimeSeconds(),
                Now.AddDays(2).ToUnixTimeSeconds(),
                null,
                null),
        ];
        return new AccountRateLimits(
            snapshot,
            new Dictionary<string, RateLimitSnapshot> { ["codex"] = snapshot },
            new ResetCreditSummary(
                availableCredits.Count,
                availableCredits),
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
