using System.Text;
using CodexAutoReset.AppServer;
using CodexAutoReset.Core;
using CodexAutoReset.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexAutoReset.Runtime.Tests;

[TestClass]
public sealed class GuardCycleExecutorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 21, 3, 0, 0, TimeSpan.Zero);

    private string temporaryDirectory = null!;
    private RuntimePaths paths = null!;

    [TestInitialize]
    public void Initialize()
    {
        temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"CodexAutoReset.Runtime.Tests-{Guid.NewGuid():N}");
        paths = RuntimePaths.ForTesting(temporaryDirectory);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [TestMethod]
    public async Task ExecuteAsync_DisabledAutomationReadsWithoutConsumeOrSimulationLog()
    {
        var factory = new FakeClientFactory(CreateSnapshot(weeklyUsedPercent: 95));
        var executor = CreateExecutor(factory);

        var first = await executor.ExecuteAsync(
            GuardSettings.Default,
            CancellationToken.None);
        var second = await executor.ExecuteAsync(
            GuardSettings.Default,
            CancellationToken.None);

        Assert.AreEqual(0, factory.ConsumeCount);
        Assert.AreEqual(CycleActionKind.None, first.ActionKind);
        Assert.AreEqual("automation_disabled", first.ActionCode);
        Assert.AreEqual("automation_disabled", second.ActionCode);
        Assert.IsNull(first.DuplicateSuppressed);
        Assert.IsFalse(File.Exists(Path.Combine(paths.RootDirectory, "state.json")));
        var log = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(paths.LogDirectory).Select(File.ReadAllText));
        Assert.IsFalse(log.Contains("would_consume", StringComparison.Ordinal));
        Assert.IsFalse(log.Contains("dry_run", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ExecuteAsync_PropagatesExternalResetOnceAfterBaseline()
    {
        var previousResetAt = Now.AddDays(2);
        var nextResetAt = Now.AddDays(7);
        var factory = new SequencedSnapshotClientFactory(
        [
            CreateSnapshot(75, Now.AddMinutes(-1), previousResetAt),
            CreateSnapshot(5, Now, nextResetAt),
            CreateSnapshot(5, Now.AddMinutes(1), nextResetAt),
        ]);
        var executor = CreateExecutor(factory);

        var baseline = await executor.ExecuteAsync(
            GuardSettings.Default,
            CancellationToken.None);
        Assert.IsTrue(File.Exists(paths.UsageResetStateFile));
        var detected = await executor.ExecuteAsync(
            GuardSettings.Default,
            CancellationToken.None);
        var duplicate = await executor.ExecuteAsync(
            GuardSettings.Default,
            CancellationToken.None);

        Assert.IsNull(baseline.UsageResetDetection);
        Assert.AreEqual(
            nextResetAt.ToUnixTimeSeconds(),
            detected.Evaluation.Weekly?.ResetsAt);
        Assert.AreEqual(95, detected.Evaluation.Weekly?.RemainingPercent);
        Assert.AreEqual(
            WeeklyUsageResetKind.Early,
            detected.UsageResetDetection?.Kind);
        Assert.AreEqual(
            nextResetAt.ToUnixTimeSeconds(),
            detected.UsageResetDetection?.NextResetsAt);
        Assert.IsNull(duplicate.UsageResetDetection);
    }

    [TestMethod]
    public async Task ExecuteAsync_DetectedResetSettlesAcrossRestartBeforeConsume()
    {
        var previousResetAt = Now.AddDays(2);
        var detectedAt = Now.AddMinutes(1);
        var nextResetAt = detectedAt.AddDays(7);
        var settings = GuardSettings.Default with
        {
            AutomationEnabled = true,
        };

        var baselineFactory = new FakeClientFactory(
            CreateSnapshot(75, Now, previousResetAt));
        var baselineExecutor = CreateExecutor(
            baselineFactory,
            new FixedTimeProvider(Now));
        _ = await baselineExecutor.ExecuteAsync(
            settings,
            CancellationToken.None);

        var detectionFactory = new FakeClientFactory(
            CreateSnapshot(95, detectedAt, nextResetAt));
        var detectionExecutor = CreateExecutor(
            detectionFactory,
            new FixedTimeProvider(detectedAt));
        var detected = await detectionExecutor.ExecuteAsync(
            settings,
            CancellationToken.None);

        Assert.AreEqual(0, detectionFactory.ConsumeCount);
        Assert.AreEqual("usage_reset_settling", detected.ActionCode);
        Assert.AreEqual(
            WeeklyUsageResetKind.Early,
            detected.UsageResetDetection?.Kind);

        var stillSettlingFactory = new FakeClientFactory(
            CreateSnapshot(95, detectedAt.AddMinutes(4), nextResetAt));
        var restartedExecutor = CreateExecutor(
            stillSettlingFactory,
            new FixedTimeProvider(detectedAt.AddMinutes(4)));
        var stillSettling = await restartedExecutor.ExecuteAsync(
            settings,
            CancellationToken.None);

        Assert.AreEqual(0, stillSettlingFactory.ConsumeCount);
        Assert.AreEqual("usage_reset_settling", stillSettling.ActionCode);

        var settledFactory = new FakeClientFactory(
            CreateSnapshot(95, detectedAt.AddMinutes(5), nextResetAt));
        var settledExecutor = CreateExecutor(
            settledFactory,
            new FixedTimeProvider(detectedAt.AddMinutes(5)));
        var settled = await settledExecutor.ExecuteAsync(
            settings,
            CancellationToken.None);

        Assert.AreEqual(1, settledFactory.ConsumeCount);
        Assert.AreEqual(
            "live_reset_refresh_pending",
            settled.ActionCode);
    }

    [TestMethod]
    public async Task ExecuteAsync_UsageResetStateUnavailableBlocksNewConsume()
    {
        Directory.CreateDirectory(paths.RootDirectory);
        await File.WriteAllTextAsync(
            paths.UsageResetStateFile,
            """{"schemaVersion":99}""");
        var factory = new FakeClientFactory(
            CreateSnapshot(weeklyUsedPercent: 95));
        var executor = CreateExecutor(factory);
        var settings = GuardSettings.Default with
        {
            AutomationEnabled = true,
        };

        var result = await executor.ExecuteAsync(
            settings,
            CancellationToken.None);

        Assert.AreEqual(0, factory.ConsumeCount);
        Assert.AreEqual(CycleActionKind.Blocked, result.ActionKind);
        Assert.AreEqual(
            "usage_reset_state_unavailable",
            result.ActionCode);
    }

    [TestMethod]
    public async Task ExecuteAsync_LiveAboveThresholdDoesNotConsume()
    {
        var factory = new FakeClientFactory(CreateSnapshot(weeklyUsedPercent: 80));
        var executor = CreateExecutor(factory);
        var settings = GuardSettings.Default with
        {
            AutomationEnabled = true,
        };

        var result = await executor.ExecuteAsync(settings, CancellationToken.None);

        Assert.AreEqual(0, factory.ConsumeCount);
        Assert.AreEqual(CycleActionKind.None, result.ActionKind);
        Assert.AreEqual("no_action", result.ActionCode);
    }

    [TestMethod]
    public async Task ExecuteAsync_LiveThresholdConsumesOnceAndPersistsNoRawCreditId()
    {
        var factory = new FakeClientFactory(CreateSnapshot(weeklyUsedPercent: 95));
        var executor = CreateExecutor(factory);
        var settings = GuardSettings.Default with
        {
            AutomationEnabled = true,
        };

        var first = await executor.ExecuteAsync(settings, CancellationToken.None);
        var second = await executor.ExecuteAsync(settings, CancellationToken.None);

        Assert.AreEqual(1, factory.ConsumeCount);
        Assert.AreEqual(CycleActionKind.ResetSucceeded, first.ActionKind);
        Assert.AreEqual("live_reset_refresh_pending", first.ActionCode);
        Assert.AreEqual("live_recovery_pending", second.ActionCode);

        var stateText = await File.ReadAllTextAsync(paths.LiveStateFile);
        Assert.IsFalse(stateText.Contains("private-credit-id", StringComparison.Ordinal));
        var logText = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(paths.LogDirectory).Select(File.ReadAllText));
        Assert.IsFalse(logText.Contains("private-credit-id", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ExecuteAsync_RecoveryOscillationCannotConsumeSecondCredit()
    {
        var previousResetAt = Now.AddDays(6);
        var recoveredResetAt = Now.AddDays(7);
        var settings = GuardSettings.Default with
        {
            AutomationEnabled = true,
        };
        var initialFactory = new FakeClientFactory(
            CreateSnapshot(95, Now.AddMinutes(-1), previousResetAt))
        {
            PostConsumeSnapshot = CreateSnapshot(
                0,
                Now,
                recoveredResetAt),
        };
        var initialExecutor = CreateExecutor(
            initialFactory,
            new FixedTimeProvider(Now));

        var completed = await initialExecutor.ExecuteAsync(
            settings,
            CancellationToken.None);

        var lowFactory = new FakeClientFactory(
            CreateSnapshot(
                95,
                Now.AddMinutes(1),
                recoveredResetAt));
        var lowExecutor = CreateExecutor(
            lowFactory,
            new FixedTimeProvider(Now.AddMinutes(1)));
        var lowDuringSettlement = await lowExecutor.ExecuteAsync(
            settings,
            CancellationToken.None);

        var recoveryFactory = new FakeClientFactory(
            CreateSnapshot(
                0,
                Now.AddMinutes(2),
                recoveredResetAt));
        var recoveryExecutor = CreateExecutor(
            recoveryFactory,
            new FixedTimeProvider(Now.AddMinutes(2)));
        var recovery = await recoveryExecutor.ExecuteAsync(
            settings,
            CancellationToken.None);

        var oscillatingLowFactory = new FakeClientFactory(
            CreateSnapshot(
                95,
                Now.AddMinutes(6),
                recoveredResetAt));
        var oscillatingLowExecutor = CreateExecutor(
            oscillatingLowFactory,
            new FixedTimeProvider(Now.AddMinutes(6)));
        var oscillatingLow = await oscillatingLowExecutor.ExecuteAsync(
            settings,
            CancellationToken.None);

        Assert.AreEqual(1, initialFactory.ConsumeCount);
        Assert.AreEqual(
            "live_reset_refresh_pending",
            completed.ActionCode);
        Assert.AreEqual(0, lowFactory.ConsumeCount);
        Assert.AreEqual(
            "usage_reset_settling",
            lowDuringSettlement.ActionCode);
        Assert.AreEqual(0, recoveryFactory.ConsumeCount);
        Assert.AreEqual("no_action", recovery.ActionCode);
        Assert.AreEqual(0, oscillatingLowFactory.ConsumeCount);
        Assert.AreEqual(
            "usage_reset_settling",
            oscillatingLow.ActionCode);
    }

    [TestMethod]
    public async Task ExecuteAsync_AttributesRefreshedResetToAutomaticCredit()
    {
        var previousResetAt = Now.AddDays(6);
        var nextResetAt = Now.AddDays(7);
        var factory = new FakeClientFactory(
            CreateSnapshot(95, Now.AddMinutes(-1), previousResetAt))
        {
            PostConsumeSnapshot = CreateSnapshot(0, Now, nextResetAt),
        };
        var executor = CreateExecutor(factory);
        var settings = GuardSettings.Default with
        {
            AutomationEnabled = true,
        };

        var result = await executor.ExecuteAsync(settings, CancellationToken.None);

        Assert.AreEqual(CycleActionKind.ResetSucceeded, result.ActionKind);
        Assert.AreEqual(
            WeeklyUsageResetKind.AutomaticCredit,
            result.UsageResetDetection?.Kind);
        Assert.AreEqual(
            nextResetAt.ToUnixTimeSeconds(),
            result.UsageResetDetection?.NextResetsAt);
    }

    [TestMethod]
    public async Task ExecuteAsync_AttributesRefreshObservedBeforeSuccessTimestamp()
    {
        var resetAt = Now.AddDays(6);
        var factory = new FakeClientFactory(
            CreateSnapshot(95, Now.AddMinutes(-1), resetAt))
        {
            PostConsumeSnapshot = CreateSnapshot(
                0,
                Now.AddMilliseconds(-1),
                resetAt),
        };
        var executor = CreateExecutor(factory);
        var settings = GuardSettings.Default with
        {
            AutomationEnabled = true,
        };

        var result = await executor.ExecuteAsync(settings, CancellationToken.None);

        Assert.AreEqual(CycleActionKind.ResetSucceeded, result.ActionKind);
        Assert.AreEqual(
            WeeklyUsageResetKind.AutomaticCredit,
            result.UsageResetDetection?.Kind);
        Assert.AreEqual(
            resetAt.ToUnixTimeSeconds(),
            result.UsageResetDetection?.NextResetsAt);
    }

    [TestMethod]
    public async Task ExecuteAsync_PendingAutomaticCreditSurvivesExecutorRestart()
    {
        var previousResetAt = Now.AddDays(6);
        var nextResetAt = Now.AddDays(7);
        var initialFactory = new FakeClientFactory(
            CreateSnapshot(95, Now.AddMinutes(-1), previousResetAt))
        {
            FailPostConsumeRead = true,
        };
        var settings = GuardSettings.Default with
        {
            AutomationEnabled = true,
        };
        var initialExecutor = CreateExecutor(initialFactory);

        var pendingRefresh = await initialExecutor.ExecuteAsync(
            settings,
            CancellationToken.None);

        Assert.AreEqual(
            "live_reset_refresh_pending",
            pendingRefresh.ActionCode);
        Assert.IsNull(pendingRefresh.UsageResetDetection);

        var refreshedFactory = new FakeClientFactory(
            CreateSnapshot(0, Now, nextResetAt));
        var restartedExecutor = CreateExecutor(refreshedFactory);

        var detected = await restartedExecutor.ExecuteAsync(
            settings,
            CancellationToken.None);

        Assert.AreEqual(
            WeeklyUsageResetKind.AutomaticCredit,
            detected.UsageResetDetection?.Kind);
        Assert.AreEqual(0, refreshedFactory.ConsumeCount);
    }

    [TestMethod]
    public async Task ExecuteAsync_LiveNoCreditPreservesRefreshPendingStatus()
    {
        var factory = new FakeClientFactory(CreateSnapshot(weeklyUsedPercent: 95))
        {
            Outcome = ConsumeResetCreditOutcome.NoCredit,
            FailPostConsumeRead = true,
        };
        var executor = CreateExecutor(factory);
        var settings = GuardSettings.Default with
        {
            AutomationEnabled = true,
        };

        var result = await executor.ExecuteAsync(settings, CancellationToken.None);

        Assert.AreEqual(CycleActionKind.ResetNoEffect, result.ActionKind);
        Assert.AreEqual("live_no_credit_refresh_pending", result.ActionCode);
    }

    [TestMethod]
    public async Task ExecuteAsync_ScheduledResetImminentUsesExplicitWaitStatus()
    {
        var factory = new FakeClientFactory(
            CreateSnapshot(
                95,
                Now,
                Now.AddMinutes(4)));
        var executor = CreateExecutor(factory);
        var settings = GuardSettings.Default with
        {
            AutomationEnabled = true,
        };

        var result = await executor.ExecuteAsync(
            settings,
            CancellationToken.None);

        Assert.AreEqual(0, factory.ConsumeCount);
        Assert.AreEqual(CycleActionKind.None, result.ActionKind);
        Assert.AreEqual(
            DecisionReason.ScheduledResetImminent,
            result.Evaluation.Decision.Reason);
        Assert.AreEqual("scheduled_reset_imminent", result.ActionCode);
    }

    [TestMethod]
    public async Task ExecuteAsync_RetryableLiveFailureReturnsPendingAndReusesIntent()
    {
        var factory = new FakeClientFactory(CreateSnapshot(weeklyUsedPercent: 95))
        {
            RetryableConsumeFailuresRemaining = 1,
        };
        var executor = CreateExecutor(factory);
        var settings = GuardSettings.Default with
        {
            AutomationEnabled = true,
        };

        var pending = await executor.ExecuteAsync(settings, CancellationToken.None);
        var completed = await executor.ExecuteAsync(settings, CancellationToken.None);

        Assert.AreEqual(CycleActionKind.ResetPending, pending.ActionKind);
        Assert.AreEqual("live_retry_pending", pending.ActionCode);
        Assert.AreEqual(CycleActionKind.ResetSucceeded, completed.ActionKind);
        Assert.AreEqual(2, factory.ConsumeRequests.Count);
        Assert.AreEqual(
            factory.ConsumeRequests[0].IdempotencyKey,
            factory.ConsumeRequests[1].IdempotencyKey);
        Assert.AreEqual(
            factory.ConsumeRequests[0].CreditId,
            factory.ConsumeRequests[1].CreditId);

        var logText = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(paths.LogDirectory).Select(File.ReadAllText));
        StringAssert.Contains(logText, "live_retry_pending");
        Assert.IsFalse(logText.Contains("private-credit-id", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ExecuteAsync_TerminalLiveOutcomeIsLoggedAfterCallerCancellation()
    {
        using var cancellationSource = new CancellationTokenSource();
        var factory = new FakeClientFactory(CreateSnapshot(weeklyUsedPercent: 95))
        {
            OnConsume = cancellationSource.Cancel,
        };
        var executor = CreateExecutor(factory);
        var settings = GuardSettings.Default with
        {
            AutomationEnabled = true,
        };

        var result = await executor.ExecuteAsync(settings, cancellationSource.Token);

        Assert.AreEqual(CycleActionKind.ResetSucceeded, result.ActionKind);
        var logText = string.Join(
            Environment.NewLine,
            Directory.EnumerateFiles(paths.LogDirectory).Select(File.ReadAllText));
        StringAssert.Contains(logText, "live_reset_refresh_pending");
    }

    [TestMethod]
    public async Task ExecuteAsync_StickyWriteFailureLatchesAcrossPollingCoordinators()
    {
        var factory = new FakeClientFactory(CreateSnapshot(weeklyUsedPercent: 95))
        {
            Outcome = (ConsumeResetCreditOutcome)999,
            OnConsume = () =>
            {
                File.Delete(paths.LiveStateFile);
                Directory.CreateDirectory(paths.LiveStateFile);
            },
        };
        var executor = CreateExecutor(factory);
        var settings = GuardSettings.Default with
        {
            AutomationEnabled = true,
        };

        var first = await Assert.ThrowsExceptionAsync<LiveStateException>(() =>
            executor.ExecuteAsync(settings, CancellationToken.None));
        var second = await executor.ExecuteAsync(settings, CancellationToken.None);

        Assert.AreEqual("live_sticky_state_missing", first.ReasonCode);
        Assert.AreEqual(CycleActionKind.Blocked, second.ActionKind);
        Assert.AreEqual("live_protocol_blocked", second.ActionCode);
        Assert.AreEqual(1, factory.ConsumeCount);

        Directory.Delete(paths.LiveStateFile, recursive: true);
        var restartedFactory = new FakeClientFactory(
            CreateSnapshot(weeklyUsedPercent: 95));
        var restarted = CreateExecutor(restartedFactory);
        var afterRestart = await restarted.ExecuteAsync(
            settings,
            CancellationToken.None);

        Assert.AreEqual(CycleActionKind.Blocked, afterRestart.ActionKind);
        Assert.AreEqual("live_protocol_blocked", afterRestart.ActionCode);
        Assert.AreEqual(0, restartedFactory.ConsumeCount);
        var marker = await File.ReadAllTextAsync(paths.LiveSafetyBlockFile);
        Assert.IsFalse(marker.Contains("private-credit-id", StringComparison.Ordinal));
        Assert.IsFalse(marker.Contains("idempotency", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task ExecuteAsync_MalformedDurableSafetyMarkerBlocksConsume()
    {
        Directory.CreateDirectory(paths.RootDirectory);
        await File.WriteAllTextAsync(paths.LiveSafetyBlockFile, "{malformed");
        var factory = new FakeClientFactory(CreateSnapshot(weeklyUsedPercent: 95));
        var executor = CreateExecutor(factory);
        var settings = GuardSettings.Default with
        {
            AutomationEnabled = true,
        };

        var result = await executor.ExecuteAsync(settings, CancellationToken.None);

        Assert.AreEqual(CycleActionKind.Blocked, result.ActionKind);
        Assert.AreEqual("live_needs_review", result.ActionCode);
        Assert.AreEqual(0, factory.ConsumeCount);
    }

    [TestMethod]
    public async Task ExecuteAsync_UnknownLiveReadFailureLatchesFutureConsume()
    {
        var factory = new FakeClientFactory(CreateSnapshot(weeklyUsedPercent: 95))
        {
            UnknownInitialReadFailures = 1,
        };
        var executor = CreateExecutor(factory);
        var settings = GuardSettings.Default with
        {
            AutomationEnabled = true,
        };

        await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
            executor.ExecuteAsync(settings, CancellationToken.None));
        var second = await executor.ExecuteAsync(settings, CancellationToken.None);

        Assert.AreEqual(CycleActionKind.Blocked, second.ActionKind);
        Assert.AreEqual("live_needs_review", second.ActionCode);
        Assert.AreEqual(0, factory.ConsumeCount);
    }

    [DataTestMethod]
    [DataRow(AppServerFailureCategory.Timeout, LiveResetFailureDisposition.Retryable)]
    [DataRow(AppServerFailureCategory.ExecutableBecameUnavailable, LiveResetFailureDisposition.Retryable)]
    [DataRow(AppServerFailureCategory.ProcessExited, LiveResetFailureDisposition.Retryable)]
    [DataRow(AppServerFailureCategory.IoError, LiveResetFailureDisposition.Retryable)]
    [DataRow(AppServerFailureCategory.InvalidResponse, LiveResetFailureDisposition.ProtocolMismatch)]
    [DataRow(AppServerFailureCategory.RemoteError, LiveResetFailureDisposition.Retryable)]
    public void FailureClassifier_UsesConservativeAppServerMapping(
        AppServerFailureCategory category,
        LiveResetFailureDisposition expected)
    {
        var classifier = AppServerLiveResetFailureClassifier.Instance;

        Assert.AreEqual(
            expected,
            classifier.Classify(new AppServerException(category)));
    }

    private GuardCycleExecutor CreateExecutor(FakeClientFactory factory) => new(
        paths,
        factory,
        new TestSecretProtector(),
        AppServerLiveResetFailureClassifier.Instance,
        new FixedTimeProvider(Now));

    private GuardCycleExecutor CreateExecutor(
        FakeClientFactory factory,
        TimeProvider timeProvider) => new(
            paths,
            factory,
            new TestSecretProtector(),
            AppServerLiveResetFailureClassifier.Instance,
            timeProvider);

    private GuardCycleExecutor CreateExecutor(
        SequencedSnapshotClientFactory factory) => new(
            paths,
            factory,
            new TestSecretProtector(),
            AppServerLiveResetFailureClassifier.Instance,
            new FixedTimeProvider(Now));

    private static AccountRateLimits CreateSnapshot(double weeklyUsedPercent) =>
        CreateSnapshot(
            weeklyUsedPercent,
            Now,
            Now.AddDays(6));

    private static AccountRateLimits CreateSnapshot(
        double weeklyUsedPercent,
        DateTimeOffset observedAt,
        DateTimeOffset weeklyResetAt)
    {
        var observedAtUnix = observedAt.ToUnixTimeSeconds();
        var snapshot = new RateLimitSnapshot(
            "codex",
            "Codex",
            new RateLimitWindow(20, 300, observedAtUnix + 60 * 60),
            new RateLimitWindow(
                weeklyUsedPercent,
                10_080,
                weeklyResetAt.ToUnixTimeSeconds()));
        return new AccountRateLimits(
            snapshot,
            new Dictionary<string, RateLimitSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                ["codex"] = snapshot,
            },
            new ResetCreditSummary(
                1,
                [
                    new ResetCredit(
                        "private-credit-id",
                        "codexRateLimits",
                        "available",
                        observedAtUnix - 60,
                        observedAtUnix + 24 * 60 * 60,
                        null,
                        null),
                ]),
            observedAt);
    }

    private sealed class FakeClientFactory : IRateLimitClientFactory
    {
        private readonly AccountRateLimits snapshot;

        public FakeClientFactory(AccountRateLimits snapshot)
        {
            this.snapshot = snapshot;
        }

        public int ConsumeCount { get; private set; }

        public List<ConsumeResetCreditRequest> ConsumeRequests { get; } = [];

        public ConsumeResetCreditOutcome Outcome { get; init; } =
            ConsumeResetCreditOutcome.Reset;

        public bool FailPostConsumeRead { get; init; }

        public AccountRateLimits? PostConsumeSnapshot { get; init; }

        public int UnknownInitialReadFailures { get; set; }

        public int RetryableConsumeFailuresRemaining { get; set; }

        public Action? OnConsume { get; init; }

        public IAccountRateLimitClient Create(GuardSettings settings) =>
            new FakeClient(this, snapshot);

        private sealed class FakeClient : IAccountRateLimitClient
        {
            private readonly FakeClientFactory owner;
            private readonly AccountRateLimits snapshot;
            private bool hasConsumed;

            public FakeClient(
                FakeClientFactory owner,
                AccountRateLimits snapshot)
            {
                this.owner = owner;
                this.snapshot = snapshot;
            }

            public Task<AccountRateLimits> ReadAsync(
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!hasConsumed && owner.UnknownInitialReadFailures > 0)
                {
                    owner.UnknownInitialReadFailures--;
                    throw new InvalidDataException("simulated");
                }

                if (hasConsumed && owner.FailPostConsumeRead)
                {
                    throw new AppServerException(AppServerFailureCategory.Timeout);
                }

                return Task.FromResult(
                    hasConsumed
                        ? owner.PostConsumeSnapshot ?? snapshot
                        : snapshot);
            }

            public Task<ConsumeResetCreditResult> ConsumeResetCreditAsync(
                ConsumeResetCreditRequest request,
                CancellationToken cancellationToken)
            {
                owner.ConsumeCount++;
                owner.ConsumeRequests.Add(request);
                hasConsumed = true;
                owner.OnConsume?.Invoke();
                if (owner.RetryableConsumeFailuresRemaining > 0)
                {
                    owner.RetryableConsumeFailuresRemaining--;
                    throw new AppServerException(AppServerFailureCategory.Timeout);
                }

                return Task.FromResult(new ConsumeResetCreditResult(
                    owner.Outcome));
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class SequencedSnapshotClientFactory : IRateLimitClientFactory
    {
        private readonly Queue<AccountRateLimits> snapshots;

        public SequencedSnapshotClientFactory(
            IEnumerable<AccountRateLimits> snapshots)
        {
            this.snapshots = new Queue<AccountRateLimits>(snapshots);
        }

        public IAccountRateLimitClient Create(GuardSettings settings)
        {
            if (!snapshots.TryDequeue(out var snapshot))
            {
                throw new InvalidOperationException("snapshot_sequence_exhausted");
            }

            return new ReadOnlyClient(snapshot);
        }

        private sealed class ReadOnlyClient : IAccountRateLimitClient
        {
            private readonly AccountRateLimits snapshot;

            public ReadOnlyClient(AccountRateLimits snapshot)
            {
                this.snapshot = snapshot;
            }

            public Task<AccountRateLimits> ReadAsync(
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(snapshot);
            }

            public Task<ConsumeResetCreditResult> ConsumeResetCreditAsync(
                ConsumeResetCreditRequest request,
                CancellationToken cancellationToken) =>
                throw new InvalidOperationException("consume_not_expected");

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class TestSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"protected:{plaintext}"));

        public string Unprotect(string protectedValue)
        {
            var value = Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue));
            return value["protected:".Length..];
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            this.now = now;
        }

        public override DateTimeOffset GetUtcNow() => now;
    }
}
