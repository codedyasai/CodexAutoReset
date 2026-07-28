using CodexAutoReset.AppServer;
using CodexAutoReset.Core;
using CodexAutoReset.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexAutoReset.Runtime.Tests;

[TestClass]
public sealed class CompatibilityGuardMonitorTests
{
    private static readonly DateTimeOffset InitialNow =
        new(2026, 7, 28, 3, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task ReadMismatchConfirmsOnlyAfterSameSignalSurvivesTenSeconds()
    {
        var time = new MutableTimeProvider(InitialNow);
        var executor = new SequenceCycleExecutor(
            ReadFailure(AppServerFailureCategory.InvalidResponse),
            ReadFailure(AppServerFailureCategory.InvalidResponse),
            ReadFailure(AppServerFailureCategory.InvalidResponse));
        await WithMonitorAsync(executor, time, async monitor =>
        {
            await monitor.RefreshAsync();
            Assert.AreEqual(
                CodexCompatibilityState.VerificationPending,
                monitor.CurrentSnapshot.CompatibilityState);

            time.Advance(TimeSpan.FromSeconds(9));
            await monitor.RefreshAsync();
            Assert.AreEqual(
                CodexCompatibilityState.VerificationPending,
                monitor.CurrentSnapshot.CompatibilityState);

            time.Advance(TimeSpan.FromSeconds(1));
            await monitor.RefreshAsync();
            Assert.AreEqual(
                CodexCompatibilityState.ReadUnsupported,
                monitor.CurrentSnapshot.CompatibilityState);
        });
    }

    [TestMethod]
    public async Task OrdinaryFailureBreaksCompatibilityFailureSequence()
    {
        var time = new MutableTimeProvider(InitialNow);
        var executor = new SequenceCycleExecutor(
            ReadFailure(AppServerFailureCategory.InvalidResponse),
            ReadFailure(AppServerFailureCategory.Timeout),
            ReadFailure(AppServerFailureCategory.InvalidResponse),
            ReadFailure(AppServerFailureCategory.InvalidResponse));
        await WithMonitorAsync(executor, time, async monitor =>
        {
            await monitor.RefreshAsync();
            time.Advance(TimeSpan.FromSeconds(11));

            await monitor.RefreshAsync();
            Assert.AreEqual(
                CodexCompatibilityState.Unknown,
                monitor.CurrentSnapshot.CompatibilityState);

            await monitor.RefreshAsync();
            Assert.AreEqual(
                CodexCompatibilityState.VerificationPending,
                monitor.CurrentSnapshot.CompatibilityState);

            time.Advance(TimeSpan.FromSeconds(10));
            await monitor.RefreshAsync();
            Assert.AreEqual(
                CodexCompatibilityState.ReadUnsupported,
                monitor.CurrentSnapshot.CompatibilityState);
        });
    }

    [TestMethod]
    public async Task DifferentRemoteProtocolCodesStartDifferentVerificationWindows()
    {
        var time = new MutableTimeProvider(InitialNow);
        var executor = new SequenceCycleExecutor(
            ReadFailure(AppServerFailureCategory.RemoteError, -32601),
            ReadFailure(AppServerFailureCategory.RemoteError, -32602),
            ReadFailure(AppServerFailureCategory.RemoteError, -32602));
        await WithMonitorAsync(executor, time, async monitor =>
        {
            await monitor.RefreshAsync();
            time.Advance(TimeSpan.FromSeconds(10));

            await monitor.RefreshAsync();
            Assert.AreEqual(
                CodexCompatibilityState.VerificationPending,
                monitor.CurrentSnapshot.CompatibilityState);

            time.Advance(TimeSpan.FromSeconds(10));
            await monitor.RefreshAsync();
            Assert.AreEqual(
                CodexCompatibilityState.ReadUnsupported,
                monitor.CurrentSnapshot.CompatibilityState);
        });
    }

    [TestMethod]
    public async Task MutationSchemaMismatchWinsOverSemanticReadAnomaly()
    {
        var time = new MutableTimeProvider(InitialNow);
        var result = CreateSemanticFailureResult(
            time.GetUtcNow(),
            consumeSchemaCompatible: false,
            "mutation_schema_unverified");
        var executor = new SequenceCycleExecutor(() => result);
        await WithMonitorAsync(executor, time, async monitor =>
        {
            await monitor.RefreshAsync();

            Assert.AreEqual(
                CodexCompatibilityState.MutationUnverified,
                monitor.CurrentSnapshot.CompatibilityState);
            Assert.AreEqual(
                "mutation_schema_unverified",
                monitor.CurrentSnapshot.StatusCode);
        });
    }

    [TestMethod]
    public async Task SuccessfulReadRearmsCompatibilityVerification()
    {
        var time = new MutableTimeProvider(InitialNow);
        var executor = new SequenceCycleExecutor(
            ReadFailure(AppServerFailureCategory.InvalidResponse),
            ReadFailure(AppServerFailureCategory.InvalidResponse),
            () => CreateCompatibleResult(time.GetUtcNow()),
            ReadFailure(AppServerFailureCategory.InvalidResponse));
        await WithMonitorAsync(executor, time, async monitor =>
        {
            await monitor.RefreshAsync();
            time.Advance(TimeSpan.FromSeconds(10));
            await monitor.RefreshAsync();
            Assert.AreEqual(
                CodexCompatibilityState.ReadUnsupported,
                monitor.CurrentSnapshot.CompatibilityState);

            await monitor.RefreshAsync();
            Assert.AreEqual(
                CodexCompatibilityState.Compatible,
                monitor.CurrentSnapshot.CompatibilityState);

            time.Advance(TimeSpan.FromSeconds(10));
            await monitor.RefreshAsync();
            Assert.AreEqual(
                CodexCompatibilityState.VerificationPending,
                monitor.CurrentSnapshot.CompatibilityState);
        });
    }

    [TestMethod]
    public async Task CodexExecutablePathChangeStartsANewVerificationWindow()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var time = new MutableTimeProvider(InitialNow);
            var firstSettings = GuardSettings.Default with
            {
                CodexExecutablePath = Path.Combine(root, "first", "codex.exe"),
            };
            var secondSettings = firstSettings with
            {
                CodexExecutablePath = Path.Combine(root, "second", "codex.exe"),
            };
            var settingsStore = new JsonSettingsStore(
                RuntimePaths.ForTesting(root).SettingsFile);
            await settingsStore.SaveAsync(firstSettings, CancellationToken.None);
            var executor = new SequenceCycleExecutor(
                ReadFailure(AppServerFailureCategory.InvalidResponse),
                ReadFailure(AppServerFailureCategory.InvalidResponse),
                ReadFailure(AppServerFailureCategory.InvalidResponse));
            await using var monitor = new GuardMonitorService(
                settingsStore,
                executor,
                firstSettings,
                timeProvider: time);

            await monitor.RefreshAsync();
            time.Advance(TimeSpan.FromSeconds(10));
            await settingsStore.SaveAsync(secondSettings, CancellationToken.None);
            await monitor.RefreshAsync();

            Assert.AreEqual(
                CodexCompatibilityState.VerificationPending,
                monitor.CurrentSnapshot.CompatibilityState);

            time.Advance(TimeSpan.FromSeconds(10));
            await monitor.RefreshAsync();

            Assert.AreEqual(
                CodexCompatibilityState.ReadUnsupported,
                monitor.CurrentSnapshot.CompatibilityState);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [TestMethod]
    public async Task ChangedCandidateInvalidatesThePreviouslyScheduledRefresh()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var time = new MutableTimeProvider(InitialNow);
            var settingsStore = new JsonSettingsStore(
                RuntimePaths.ForTesting(root).SettingsFile);
            await settingsStore.SaveAsync(
                GuardSettings.Default,
                CancellationToken.None);
            var executor = new SequenceCycleExecutor(
                ReadFailure(AppServerFailureCategory.RemoteError, -32601),
                ReadFailure(AppServerFailureCategory.RemoteError, -32602),
                ReadFailure(AppServerFailureCategory.RemoteError, -32602));
            await using var monitor = new GuardMonitorService(
                settingsStore,
                executor,
                GuardSettings.Default,
                timeProvider: time);
            var firstPending = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var confirmed = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            monitor.SnapshotChanged += (_, snapshot) =>
            {
                if (executor.CallCount == 1
                    && snapshot.CompatibilityState
                        == CodexCompatibilityState.VerificationPending)
                {
                    firstPending.TrySetResult();
                }

                if (snapshot.CompatibilityState
                    == CodexCompatibilityState.ReadUnsupported)
                {
                    confirmed.TrySetResult();
                }
            };

            await monitor.StartAsync();
            await firstPending.Task.WaitAsync(TimeSpan.FromSeconds(5));

            time.Advance(TimeSpan.FromSeconds(5));
            await monitor.RefreshAsync();
            time.Advance(TimeSpan.FromSeconds(5));
            await Task.Delay(25);

            Assert.AreEqual(2, executor.CallCount);
            Assert.AreEqual(
                CodexCompatibilityState.VerificationPending,
                monitor.CurrentSnapshot.CompatibilityState);

            time.Advance(TimeSpan.FromSeconds(5));
            await confirmed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(3, executor.CallCount);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [TestMethod]
    public async Task StartedMonitorSchedulesItsOwnVerificationRefresh()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var time = new MutableTimeProvider(InitialNow);
            var settingsStore = new JsonSettingsStore(
                RuntimePaths.ForTesting(root).SettingsFile);
            await settingsStore.SaveAsync(
                GuardSettings.Default,
                CancellationToken.None);
            var executor = new AlwaysInvalidReadExecutor();
            await using var monitor = new GuardMonitorService(
                settingsStore,
                executor,
                GuardSettings.Default,
                timeProvider: time);
            var pending = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var confirmed = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            monitor.SnapshotChanged += (_, snapshot) =>
            {
                if (snapshot.CompatibilityState
                    == CodexCompatibilityState.VerificationPending)
                {
                    pending.TrySetResult();
                }

                if (snapshot.CompatibilityState
                    == CodexCompatibilityState.ReadUnsupported)
                {
                    confirmed.TrySetResult();
                }
            };

            await monitor.StartAsync();
            await pending.Task.WaitAsync(TimeSpan.FromSeconds(5));
            time.Advance(TimeSpan.FromSeconds(10));
            await confirmed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.IsTrue(executor.CallCount >= 2);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [TestMethod]
    public async Task EarlyVerificationTimerCallbackSchedulesRemainingDelay()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var time = new MutableTimeProvider(InitialNow);
            var settingsStore = new JsonSettingsStore(
                RuntimePaths.ForTesting(root).SettingsFile);
            await settingsStore.SaveAsync(
                GuardSettings.Default,
                CancellationToken.None);
            var executor = new AlwaysInvalidReadExecutor();
            await using var monitor = new GuardMonitorService(
                settingsStore,
                executor,
                GuardSettings.Default,
                timeProvider: time);
            var firstPending = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var secondPending = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var confirmed = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            monitor.SnapshotChanged += (_, snapshot) =>
            {
                if (snapshot.CompatibilityState
                    == CodexCompatibilityState.VerificationPending)
                {
                    if (executor.CallCount == 1)
                    {
                        firstPending.TrySetResult();
                    }
                    else if (executor.CallCount == 2)
                    {
                        secondPending.TrySetResult();
                    }
                }

                if (snapshot.CompatibilityState
                    == CodexCompatibilityState.ReadUnsupported)
                {
                    confirmed.TrySetResult();
                }
            };

            await monitor.StartAsync();
            await firstPending.Task.WaitAsync(TimeSpan.FromSeconds(5));

            time.FireTimersEarly();
            await secondPending.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(
                CodexCompatibilityState.VerificationPending,
                monitor.CurrentSnapshot.CompatibilityState);

            time.Advance(TimeSpan.FromSeconds(10));
            await confirmed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(3, executor.CallCount);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    private static Func<GuardCycleResult> ReadFailure(
        AppServerFailureCategory category,
        int? remoteCode = null) => () => throw new AppServerException(
            category,
            remoteCode,
            operation: AppServerOperation.Read);

    private static GuardCycleResult CreateSemanticFailureResult(
        DateTimeOffset observedAt,
        bool consumeSchemaCompatible,
        string actionCode)
    {
        var codex = new RateLimitSnapshot("codex", "Codex", null, null);
        var limits = new AccountRateLimits(
            codex,
            new Dictionary<string, RateLimitSnapshot>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["codex"] = codex,
            },
            new ResetCreditSummary(0, []),
            observedAt,
            consumeSchemaCompatible);
        var evaluation = new ResetDecisionEngine().Evaluate(
            GuardSettings.Default,
            limits,
            observedAt);
        return new GuardCycleResult(
            limits,
            evaluation,
            CycleActionKind.Blocked,
            actionCode);
    }

    private static GuardCycleResult CreateCompatibleResult(
        DateTimeOffset observedAt)
    {
        var resetAt = observedAt.AddDays(6).ToUnixTimeSeconds();
        var codex = new RateLimitSnapshot(
            "codex",
            "Codex",
            null,
            new RateLimitWindow(50, 10_080, resetAt));
        var limits = new AccountRateLimits(
            codex,
            new Dictionary<string, RateLimitSnapshot>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["codex"] = codex,
            },
            new ResetCreditSummary(0, []),
            observedAt);
        var evaluation = new ResetDecisionEngine().Evaluate(
            GuardSettings.Default,
            limits,
            observedAt);
        return new GuardCycleResult(
            limits,
            evaluation,
            CycleActionKind.None,
            "automation_disabled");
    }

    private static async Task WithMonitorAsync(
        IGuardCycleExecutor executor,
        TimeProvider timeProvider,
        Func<GuardMonitorService, Task> test)
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var settingsStore = new JsonSettingsStore(
                RuntimePaths.ForTesting(root).SettingsFile);
            await settingsStore.SaveAsync(
                GuardSettings.Default,
                CancellationToken.None);
            await using var monitor = new GuardMonitorService(
                settingsStore,
                executor,
                GuardSettings.Default,
                timeProvider: timeProvider);
            await test(monitor);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"CodexAutoReset-compat-monitor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTemporaryDirectory(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (exception is
            DirectoryNotFoundException
                or IOException
                or UnauthorizedAccessException)
        {
        }
    }

    private sealed class SequenceCycleExecutor : IGuardCycleExecutor
    {
        private readonly Queue<Func<GuardCycleResult>> sequence;

        public SequenceCycleExecutor(params Func<GuardCycleResult>[] sequence)
        {
            this.sequence = new Queue<Func<GuardCycleResult>>(sequence);
        }

        public int CallCount { get; private set; }

        public Task<GuardCycleResult> ExecuteAsync(
            GuardSettings settings,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            if (!sequence.TryDequeue(out var next))
            {
                throw new InvalidOperationException("sequence_exhausted");
            }

            return Task.FromResult(next());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class AlwaysInvalidReadExecutor : IGuardCycleExecutor
    {
        public int CallCount { get; private set; }

        public Task<GuardCycleResult> ExecuteAsync(
            GuardSettings settings,
            CancellationToken cancellationToken)
        {
            CallCount++;
            throw new AppServerException(
                AppServerFailureCategory.InvalidResponse,
                operation: AppServerOperation.Read);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<MutableTimer> timers = [];
        private DateTimeOffset now;

        public MutableTimeProvider(DateTimeOffset now)
        {
            this.now = now;
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (gate)
            {
                return now;
            }
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            var timer = new MutableTimer(
                this,
                callback,
                state,
                dueTime,
                period);
            lock (gate)
            {
                timers.Add(timer);
            }

            return timer;
        }

        public void Advance(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(duration));
            }

            List<MutableTimer> due;
            lock (gate)
            {
                now = now.Add(duration);
                due = timers
                    .Where(timer => timer.IsDue(now))
                    .ToList();
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        public void FireTimersEarly()
        {
            List<MutableTimer> active;
            lock (gate)
            {
                active = timers.ToList();
            }

            foreach (var timer in active)
            {
                timer.Fire();
            }
        }

        private void Remove(MutableTimer timer)
        {
            lock (gate)
            {
                timers.Remove(timer);
            }
        }

        private sealed class MutableTimer : ITimer
        {
            private readonly MutableTimeProvider owner;
            private readonly TimerCallback callback;
            private readonly object? state;
            private DateTimeOffset? dueAt;
            private TimeSpan period;
            private bool disposed;

            public MutableTimer(
                MutableTimeProvider owner,
                TimerCallback callback,
                object? state,
                TimeSpan dueTime,
                TimeSpan period)
            {
                this.owner = owner;
                this.callback = callback;
                this.state = state;
                _ = Change(dueTime, period);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (disposed)
                {
                    return false;
                }

                this.period = period;
                dueAt = dueTime == Timeout.InfiniteTimeSpan
                    ? null
                    : owner.GetUtcNow().Add(dueTime);
                return true;
            }

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                dueAt = null;
                owner.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public bool IsDue(DateTimeOffset current) =>
                !disposed
                && dueAt is { } value
                && current >= value;

            public void Fire()
            {
                if (disposed || dueAt is null)
                {
                    return;
                }

                if (period == Timeout.InfiniteTimeSpan)
                {
                    Dispose();
                }
                else
                {
                    dueAt = owner.GetUtcNow().Add(period);
                }

                callback(state);
            }
        }
    }
}
