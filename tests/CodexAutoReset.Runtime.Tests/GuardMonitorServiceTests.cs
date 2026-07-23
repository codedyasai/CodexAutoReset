using CodexAutoReset.AppServer;
using CodexAutoReset.Core;
using CodexAutoReset.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexAutoReset.Runtime.Tests;

[TestClass]
public sealed class GuardMonitorServiceTests
{
    [TestMethod]
    public async Task StartAsync_PerformsImmediateCycleAndPublishesSanitizedSnapshot()
    {
        var root = CreateTemporaryDirectory();
        var paths = RuntimePaths.ForTesting(root);
        try
        {
            var settingsStore = new JsonSettingsStore(paths.SettingsFile);
            await settingsStore.SaveAsync(GuardSettings.Default, CancellationToken.None);
            var executor = new FakeCycleExecutor();
            await using var monitor = new GuardMonitorService(
                settingsStore,
                executor,
                GuardSettings.Default);
            var published = new TaskCompletionSource<MonitorSnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            monitor.SnapshotChanged += (_, snapshot) => published.TrySetResult(snapshot);

            await monitor.StartAsync();
            var result = await published.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.AreEqual(1, executor.CallCount);
            Assert.AreEqual("no_action", result.StatusCode);
            Assert.AreEqual(3, result.AvailableCreditCount);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [TestMethod]
    public async Task RefreshAsync_InvalidSettingsFailsClosedAndWritesSafeLog()
    {
        var root = CreateTemporaryDirectory();
        var paths = RuntimePaths.ForTesting(root);
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(paths.SettingsFile, "{invalid");
            var executor = new FakeCycleExecutor();
            await using var monitor = new GuardMonitorService(
                new JsonSettingsStore(paths.SettingsFile),
                executor,
                GuardSettings.Default,
                new SafeJsonlLogger(paths.LogDirectory));

            await monitor.RefreshAsync();

            Assert.IsTrue(monitor.CurrentSnapshot.IsFailure);
            Assert.AreEqual("settings_invalid_json", monitor.CurrentSnapshot.StatusCode);
            Assert.AreEqual(0, executor.CallCount);
            var logText = string.Join(
                Environment.NewLine,
                Directory.EnumerateFiles(paths.LogDirectory).Select(File.ReadAllText));
            StringAssert.Contains(logText, "settings_invalid_json");
            Assert.IsFalse(logText.Contains(root, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [TestMethod]
    public async Task SaveSettingsAsync_WaitsForInFlightAutomationCycleBeforeSuccess()
    {
        var root = CreateTemporaryDirectory();
        var paths = RuntimePaths.ForTesting(root);
        try
        {
            var liveSettings = GuardSettings.Default with
            {
                AutomationEnabled = true,
            };
            var disabledSettings = liveSettings with
            {
                AutomationEnabled = false,
            };
            var settingsStore = new JsonSettingsStore(paths.SettingsFile);
            await settingsStore.SaveAsync(liveSettings, CancellationToken.None);
            var executor = new BlockingCycleExecutor();
            await using var monitor = new GuardMonitorService(
                settingsStore,
                executor,
                liveSettings);
            var updateService = new SettingsUpdateService(
                settingsStore,
                new StartupService(new EmptyRegistryStore()));

            var cycleTask = monitor.RefreshAsync();
            await executor.FirstCycleEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var saveTask = monitor.SaveSettingsAsync(
                updateService,
                liveSettings,
                disabledSettings,
                currentExecutablePath: null,
                CancellationToken.None);

            Assert.IsFalse(saveTask.IsCompleted);
            Assert.AreEqual(liveSettings, await settingsStore.LoadAsync(
                CancellationToken.None));

            executor.ReleaseFirstCycle.TrySetResult();
            await cycleTask;
            await saveTask;
            await monitor.RefreshAsync();

            Assert.AreEqual(disabledSettings, await settingsStore.LoadAsync(
                CancellationToken.None));
            CollectionAssert.AreEqual(
                new[] { true, false },
                executor.AutomationStates.ToArray());
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [TestMethod]
    public async Task SetStartWithWindowsAsync_ChangesOnlyFreshPersistedField()
    {
        var root = CreateTemporaryDirectory();
        var paths = RuntimePaths.ForTesting(root);
        try
        {
            var currentSettings = GuardSettings.Default with
            {
                RemainingThresholdPercent = 23,
                PollIntervalMinutes = 17,
            };
            var settingsStore = new JsonSettingsStore(paths.SettingsFile);
            await settingsStore.SaveAsync(currentSettings, CancellationToken.None);
            var registry = new EmptyRegistryStore();
            var updateService = new SettingsUpdateService(
                settingsStore,
                new StartupService(registry));
            var executablePath = Path.Combine(root, "CodexAutoReset.exe");
            await File.WriteAllBytesAsync(executablePath, [0]);
            await using var monitor = new GuardMonitorService(
                settingsStore,
                new FakeCycleExecutor(),
                GuardSettings.Default);

            var updated = await monitor.SetStartWithWindowsAsync(
                updateService,
                enabled: true,
                executablePath,
                CancellationToken.None);

            Assert.AreEqual(currentSettings with { StartWithWindows = true }, updated);
            Assert.AreEqual(updated, await settingsStore.LoadAsync(CancellationToken.None));
            Assert.AreEqual(StartupStatus.Enabled, new StartupService(registry).GetState().Status);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [TestMethod]
    public async Task SaveSettingsAsync_DisabledPartialStartupFailureAppliesFailClosedSettings()
    {
        var root = CreateTemporaryDirectory();
        var paths = RuntimePaths.ForTesting(root);
        try
        {
            var liveSettings = GuardSettings.Default with
            {
                AutomationEnabled = true,
            };
            var disabledSettings = liveSettings with
            {
                AutomationEnabled = false,
                StartWithWindows = true,
            };
            var settingsStore = new JsonSettingsStore(paths.SettingsFile);
            await settingsStore.SaveAsync(liveSettings, CancellationToken.None);
            var registry = new EmptyRegistryStore { ThrowOnWrite = true };
            var updateService = new SettingsUpdateService(
                settingsStore,
                new StartupService(registry));
            var executablePath = Path.Combine(root, "CodexAutoReset.exe");
            await File.WriteAllBytesAsync(executablePath, [0]);
            var executor = new FakeCycleExecutor();
            await using var monitor = new GuardMonitorService(
                settingsStore,
                executor,
                liveSettings);

            var exception = await Assert.ThrowsExceptionAsync<SettingsPartiallyAppliedException>(
                () => monitor.SaveSettingsAsync(
                    updateService,
                    liveSettings,
                    disabledSettings,
                    executablePath,
                    CancellationToken.None));

            Assert.AreEqual(disabledSettings, exception.PersistedSettings);
            Assert.AreEqual(disabledSettings, await settingsStore.LoadAsync(
                CancellationToken.None));
            Assert.AreEqual(
                false,
                monitor.CurrentSnapshot.Settings.AutomationEnabled);
            Assert.AreEqual("waiting", monitor.CurrentSnapshot.StatusCode);
            Assert.AreEqual(0, executor.CallCount);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [DataTestMethod]
    [DataRow(AppServerFailureCategory.Timeout)]
    [DataRow(AppServerFailureCategory.ProcessExited)]
    [DataRow(AppServerFailureCategory.IoError)]
    public async Task RefreshAsync_RetryableReadFailurePreservesDisplayedLivePending(
        AppServerFailureCategory category)
    {
        var root = CreateTemporaryDirectory();
        var paths = RuntimePaths.ForTesting(root);
        try
        {
            var liveSettings = GuardSettings.Default with
            {
                AutomationEnabled = true,
            };
            var settingsStore = new JsonSettingsStore(paths.SettingsFile);
            await settingsStore.SaveAsync(liveSettings, CancellationToken.None);
            var executor = new PendingThenFailureExecutor(category);
            await using var monitor = new GuardMonitorService(
                settingsStore,
                executor,
                liveSettings);

            await monitor.RefreshAsync();
            Assert.AreEqual(CycleActionKind.ResetPending, monitor.CurrentSnapshot.ActionKind);
            Assert.IsFalse(monitor.CurrentSnapshot.IsFailure);

            await monitor.RefreshAsync();

            Assert.AreEqual(CycleActionKind.ResetPending, monitor.CurrentSnapshot.ActionKind);
            Assert.AreEqual("live_retry_pending", monitor.CurrentSnapshot.StatusCode);
            Assert.IsTrue(monitor.CurrentSnapshot.IsFailure);
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
            $"CodexAutoReset.Runtime.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTemporaryDirectory(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
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

    private sealed class FakeCycleExecutor : IGuardCycleExecutor
    {
        public int CallCount { get; private set; }

        public Task<GuardCycleResult> ExecuteAsync(
            GuardSettings settings,
            CancellationToken cancellationToken)
        {
            CallCount++;
            var observedAt = DateTimeOffset.UtcNow;
            var snapshot = new AccountRateLimits(
                new RateLimitSnapshot("codex", "Codex", null, null),
                null,
                new ResetCreditSummary(3, []),
                observedAt);
            var evaluation = new EvaluationResult(
                null,
                new GuardDecision(
                    DecisionKind.NoAction,
                    DecisionReason.AboveThreshold,
                    null,
                    null,
                    null),
                3);
            return Task.FromResult(new GuardCycleResult(
                snapshot,
                evaluation,
                CycleActionKind.None,
                "no_action"));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingCycleExecutor : IGuardCycleExecutor
    {
        public TaskCompletionSource FirstCycleEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstCycle { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public List<bool> AutomationStates { get; } = [];

        public async Task<GuardCycleResult> ExecuteAsync(
            GuardSettings settings,
            CancellationToken cancellationToken)
        {
            AutomationStates.Add(settings.AutomationEnabled);
            if (AutomationStates.Count == 1)
            {
                FirstCycleEntered.TrySetResult();
                await ReleaseFirstCycle.Task.WaitAsync(cancellationToken);
            }

            var observedAt = DateTimeOffset.UtcNow;
            var snapshot = new AccountRateLimits(
                new RateLimitSnapshot("codex", "Codex", null, null),
                null,
                new ResetCreditSummary(0, []),
                observedAt);
            var evaluation = new EvaluationResult(
                null,
                new GuardDecision(
                    DecisionKind.NoAction,
                    DecisionReason.AboveThreshold,
                    null,
                    null,
                    null),
                0);
            return new GuardCycleResult(
                snapshot,
                evaluation,
                CycleActionKind.None,
                "no_action");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PendingThenFailureExecutor : IGuardCycleExecutor
    {
        private readonly AppServerFailureCategory category;
        private int callCount;

        public PendingThenFailureExecutor(AppServerFailureCategory category)
        {
            this.category = category;
        }

        public Task<GuardCycleResult> ExecuteAsync(
            GuardSettings settings,
            CancellationToken cancellationToken)
        {
            callCount++;
            if (callCount > 1)
            {
                throw new AppServerException(category);
            }

            var observedAt = DateTimeOffset.UtcNow;
            var snapshot = new AccountRateLimits(
                new RateLimitSnapshot("codex", "Codex", null, null),
                null,
                new ResetCreditSummary(1, []),
                observedAt);
            var evaluation = new EvaluationResult(
                null,
                new GuardDecision(
                    DecisionKind.WouldConsume,
                    DecisionReason.ThresholdReached,
                    null,
                    null,
                    null),
                1);
            return Task.FromResult(new GuardCycleResult(
                snapshot,
                evaluation,
                CycleActionKind.ResetPending,
                "live_retry_pending"));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EmptyRegistryStore : ICurrentUserRegistryStore
    {
        private readonly Dictionary<(string Key, string Name), string> values = new();

        public bool ThrowOnWrite { get; init; }

        public CurrentUserRegistryValue ReadValue(string subKey, string valueName) =>
            values.TryGetValue((subKey, valueName), out var value)
                ? CurrentUserRegistryValue.FromString(value)
                : CurrentUserRegistryValue.Missing;

        public void SetString(string subKey, string valueName, string value)
        {
            if (ThrowOnWrite)
            {
                throw new IOException("simulated_registry_failure");
            }

            values[(subKey, valueName)] = value;
        }

        public void DeleteValue(string subKey, string valueName)
        {
            if (ThrowOnWrite)
            {
                throw new IOException("simulated_registry_failure");
            }

            values.Remove((subKey, valueName));
        }
    }
}
