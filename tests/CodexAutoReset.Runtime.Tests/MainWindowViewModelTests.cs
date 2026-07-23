using CodexAutoReset.AppServer;
using CodexAutoReset.Core;
using CodexAutoReset.Desktop;
using CodexAutoReset.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexAutoReset.Runtime.Tests;

[TestClass]
public sealed class MainWindowViewModelTests
{
    [TestMethod]
    public async Task Constructor_RegistryReadFailureKeepsStartupStatusUnknownWithoutWrite()
    {
        using var directory = new TemporaryDirectory();
        var paths = RuntimePaths.ForTesting(directory.Path);
        var settingsStore = new JsonSettingsStore(paths.SettingsFile);
        await settingsStore.SaveAsync(GuardSettings.Default, CancellationToken.None);
        var registry = new ReadFailingRegistryStore();
        await using var monitor = new GuardMonitorService(
            settingsStore,
            new ImmediateCycleExecutor(),
            GuardSettings.Default);

        var viewModel = new MainWindowViewModel(
            settingsStore,
            new StartupService(registry),
            monitor,
            GuardSettings.Default);

        Assert.IsNull(viewModel.ActualStartupStatus);
        Assert.AreEqual("자동 시작 상태를 확인할 수 없음", viewModel.StartupStatusText);
        Assert.IsFalse(viewModel.IsStartupActuallyEnabled);
        Assert.AreEqual(0, registry.WriteCount);
    }

    [TestMethod]
    public async Task CodexExecutableSelection_PersistsOnlyPathAndReturnsToAutomatic()
    {
        using var directory = new TemporaryDirectory();
        var paths = RuntimePaths.ForTesting(directory.Path);
        var settingsStore = new JsonSettingsStore(paths.SettingsFile);
        await settingsStore.SaveAsync(GuardSettings.Default, CancellationToken.None);
        var codexPath = System.IO.Path.Combine(directory.Path, "codex.exe");
        await File.WriteAllBytesAsync(codexPath, [0]);
        await using var monitor = new GuardMonitorService(
            settingsStore,
            new ImmediateCycleExecutor(),
            GuardSettings.Default);
        var viewModel = new MainWindowViewModel(
            settingsStore,
            new StartupService(new MemoryRegistryStore()),
            monitor,
            GuardSettings.Default)
        {
            ThresholdText = "99",
            PollIntervalText = "41",
            AutomationEnabled = true,
        };

        var selected = viewModel.TrySetCodexExecutablePath(
            codexPath,
            out var errorMessage);

        Assert.IsTrue(selected, errorMessage);
        Assert.IsTrue(viewModel.HasCustomCodexExecutablePath);
        Assert.AreEqual("연결 방식 · 직접 지정", viewModel.CodexExecutableModeText);
        Assert.AreNotEqual(codexPath, viewModel.CodexExecutableDisplayText);
        Assert.IsFalse(
            viewModel.CodexExecutableDisplayText.Contains(
                directory.Path,
                StringComparison.OrdinalIgnoreCase));

        Assert.IsTrue(await viewModel.SaveCodexExecutablePathAsync());

        var selectedSettings = await settingsStore.LoadAsync(CancellationToken.None);
        Assert.AreEqual(codexPath, selectedSettings.CodexExecutablePath);
        Assert.IsFalse(selectedSettings.AutomationEnabled);
        Assert.AreEqual(GuardSettings.Default.RemainingThresholdPercent, selectedSettings.RemainingThresholdPercent);
        Assert.AreEqual(GuardSettings.Default.PollIntervalMinutes, selectedSettings.PollIntervalMinutes);
        Assert.AreEqual("99", viewModel.ThresholdText);
        Assert.AreEqual("41", viewModel.PollIntervalText);
        Assert.IsTrue(viewModel.AutomationEnabled);
        Assert.AreEqual(
            "연결 경로를 저장했습니다. 다음 확인부터 사용합니다.",
            viewModel.CodexConnectionStatus);

        viewModel.UseAutomaticCodexExecutablePath();
        Assert.IsTrue(await viewModel.SaveCodexExecutablePathAsync());

        var automaticSettings = await settingsStore.LoadAsync(CancellationToken.None);
        Assert.IsNull(automaticSettings.CodexExecutablePath);
        Assert.IsFalse(automaticSettings.AutomationEnabled);
        Assert.IsFalse(viewModel.HasCustomCodexExecutablePath);
        Assert.AreEqual("연결 방식 · 자동 찾기", viewModel.CodexExecutableModeText);
    }

    [TestMethod]
    public async Task CodexAutomaticSelection_ConflictRestoresPersistedCustomPath()
    {
        using var directory = new TemporaryDirectory();
        var paths = RuntimePaths.ForTesting(directory.Path);
        var settingsStore = new JsonSettingsStore(paths.SettingsFile);
        var codexPath = System.IO.Path.Combine(directory.Path, "codex.exe");
        await File.WriteAllBytesAsync(codexPath, [0]);
        var initialSettings = GuardSettings.Default with
        {
            CodexExecutablePath = codexPath,
        };
        await settingsStore.SaveAsync(initialSettings, CancellationToken.None);
        await using var monitor = new GuardMonitorService(
            settingsStore,
            new ImmediateCycleExecutor(),
            initialSettings);
        var viewModel = new MainWindowViewModel(
            settingsStore,
            new StartupService(new MemoryRegistryStore()),
            monitor,
            initialSettings);
        var externallyChanged = initialSettings with
        {
            RemainingThresholdPercent = 42,
        };
        await settingsStore.SaveAsync(externallyChanged, CancellationToken.None);

        viewModel.UseAutomaticCodexExecutablePath();
        Assert.IsFalse(viewModel.HasCustomCodexExecutablePath);

        Assert.IsFalse(await viewModel.SaveCodexExecutablePathAsync());

        Assert.IsTrue(viewModel.HasCustomCodexExecutablePath);
        Assert.AreEqual(codexPath, viewModel.ConfiguredCodexExecutablePath);
        Assert.AreEqual(externallyChanged, await settingsStore.LoadAsync(CancellationToken.None));
        Assert.IsTrue(viewModel.CanEditCodexConnection);
        StringAssert.Contains(viewModel.CodexConnectionStatus, "다시 선택");
    }

    [TestMethod]
    public async Task CodexExecutableSave_DisablesConnectionControlsWhileWaiting()
    {
        using var directory = new TemporaryDirectory();
        var paths = RuntimePaths.ForTesting(directory.Path);
        var settingsStore = new JsonSettingsStore(paths.SettingsFile);
        await settingsStore.SaveAsync(GuardSettings.Default, CancellationToken.None);
        var codexPath = System.IO.Path.Combine(directory.Path, "codex.exe");
        await File.WriteAllBytesAsync(codexPath, [0]);
        var executor = new BlockingCycleExecutor();
        await using var monitor = new GuardMonitorService(
            settingsStore,
            executor,
            GuardSettings.Default);
        var viewModel = new MainWindowViewModel(
            settingsStore,
            new StartupService(new MemoryRegistryStore()),
            monitor,
            GuardSettings.Default);
        var cycleTask = monitor.RefreshAsync();
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(viewModel.TrySetCodexExecutablePath(codexPath, out _));

        var saveTask = viewModel.SaveCodexExecutablePathAsync();

        Assert.IsFalse(saveTask.IsCompleted);
        Assert.IsFalse(viewModel.CanEditCodexConnection);

        executor.Release.TrySetResult();
        await cycleTask;
        Assert.IsTrue(await saveTask);
        Assert.IsTrue(viewModel.CanEditCodexConnection);
        Assert.AreEqual(
            codexPath,
            (await settingsStore.LoadAsync(CancellationToken.None)).CodexExecutablePath);
    }

    [TestMethod]
    public async Task CodexExecutableSelection_RejectsWrongMissingAndRelativeFiles()
    {
        using var directory = new TemporaryDirectory();
        var paths = RuntimePaths.ForTesting(directory.Path);
        var settingsStore = new JsonSettingsStore(paths.SettingsFile);
        await settingsStore.SaveAsync(GuardSettings.Default, CancellationToken.None);
        var wrongNamePath = System.IO.Path.Combine(directory.Path, "not-codex.exe");
        await File.WriteAllBytesAsync(wrongNamePath, [0]);
        await using var monitor = new GuardMonitorService(
            settingsStore,
            new ImmediateCycleExecutor(),
            GuardSettings.Default);
        var viewModel = new MainWindowViewModel(
            settingsStore,
            new StartupService(new MemoryRegistryStore()),
            monitor,
            GuardSettings.Default);

        Assert.IsFalse(viewModel.TrySetCodexExecutablePath(wrongNamePath, out _));
        Assert.IsFalse(viewModel.TrySetCodexExecutablePath(
            System.IO.Path.Combine(directory.Path, "codex.exe"),
            out _));
        Assert.IsFalse(viewModel.TrySetCodexExecutablePath("codex.exe", out _));
        Assert.IsNull(viewModel.ConfiguredCodexExecutablePath);
    }

    [TestMethod]
    public async Task TrayStartupToggle_ChangesOnlyFreshPersistedStartupField()
    {
        using var directory = new TemporaryDirectory();
        var paths = RuntimePaths.ForTesting(directory.Path);
        var settingsStore = new JsonSettingsStore(paths.SettingsFile);
        await settingsStore.SaveAsync(GuardSettings.Default, CancellationToken.None);
        var executablePath = System.IO.Path.Combine(
            directory.Path,
            "CodexAutoReset.exe");
        await File.WriteAllBytesAsync(executablePath, [0]);
        var startupService = new StartupService(new MemoryRegistryStore());
        await using var monitor = new GuardMonitorService(
            settingsStore,
            new ImmediateCycleExecutor(),
            GuardSettings.Default);
        var viewModel = new MainWindowViewModel(
            settingsStore,
            startupService,
            monitor,
            GuardSettings.Default,
            () => executablePath)
        {
            ThresholdText = "99",
            PollIntervalText = "41",
            AutomationEnabled = true,
        };

        await viewModel.SetStartWithWindowsAsync(enabled: true);

        var persisted = await settingsStore.LoadAsync(CancellationToken.None);
        Assert.AreEqual(GuardSettings.Default with { StartWithWindows = true }, persisted);
        Assert.AreEqual("99", viewModel.ThresholdText);
        Assert.AreEqual("41", viewModel.PollIntervalText);
        Assert.IsTrue(viewModel.AutomationEnabled);
        Assert.IsTrue(viewModel.IsStartupActuallyEnabled);
        Assert.AreEqual(StartupStatus.Enabled, viewModel.ActualStartupStatus);
    }

    [TestMethod]
    public async Task TrayStartupToggle_MergesUntouchedControlsFromExternallyChangedSettings()
    {
        using var directory = new TemporaryDirectory();
        var paths = RuntimePaths.ForTesting(directory.Path);
        var settingsStore = new JsonSettingsStore(paths.SettingsFile);
        await settingsStore.SaveAsync(GuardSettings.Default, CancellationToken.None);
        var executablePath = System.IO.Path.Combine(
            directory.Path,
            "CodexAutoReset.exe");
        await File.WriteAllBytesAsync(executablePath, [0]);
        var startupService = new StartupService(new MemoryRegistryStore());
        await using var monitor = new GuardMonitorService(
            settingsStore,
            new ImmediateCycleExecutor(),
            GuardSettings.Default);
        var viewModel = new MainWindowViewModel(
            settingsStore,
            startupService,
            monitor,
            GuardSettings.Default,
            () => executablePath);
        var externallyChanged = GuardSettings.Default with
        {
            RemainingThresholdPercent = 31,
            PollIntervalMinutes = 19,
            AutomationEnabled = true,
        };
        await settingsStore.SaveAsync(externallyChanged, CancellationToken.None);

        await viewModel.SetStartWithWindowsAsync(enabled: true);

        var expected = externallyChanged with { StartWithWindows = true };
        Assert.AreEqual(expected, await settingsStore.LoadAsync(CancellationToken.None));
        Assert.AreEqual("31", viewModel.ThresholdText);
        Assert.AreEqual("19", viewModel.PollIntervalText);
        Assert.IsTrue(viewModel.AutomationEnabled);
        Assert.IsTrue(viewModel.IsStartupActuallyEnabled);

        await viewModel.SaveAsync();
        Assert.AreEqual(expected, await settingsStore.LoadAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task StopAndDrainSettingsAsync_WaitsForActiveSaveAndRejectsLaterSave()
    {
        using var directory = new TemporaryDirectory();
        var paths = RuntimePaths.ForTesting(directory.Path);
        var settingsStore = new JsonSettingsStore(paths.SettingsFile);
        await settingsStore.SaveAsync(GuardSettings.Default, CancellationToken.None);
        var executor = new BlockingCycleExecutor();
        await using var monitor = new GuardMonitorService(
            settingsStore,
            executor,
            GuardSettings.Default);
        var viewModel = new MainWindowViewModel(
            settingsStore,
            new StartupService(new MemoryRegistryStore()),
            monitor,
            GuardSettings.Default);

        var cycleTask = monitor.RefreshAsync();
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.ThresholdText = "12";
        var saveTask = viewModel.SaveAsync();
        var drainTask = viewModel.StopAndDrainSettingsAsync();

        Assert.IsFalse(saveTask.IsCompleted);
        Assert.IsFalse(drainTask.IsCompleted);

        executor.Release.TrySetResult();
        await cycleTask;
        await saveTask;
        await drainTask;

        Assert.AreEqual(
            12,
            (await settingsStore.LoadAsync(CancellationToken.None))
                .RemainingThresholdPercent);

        viewModel.ThresholdText = "13";
        await viewModel.SaveAsync();
        Assert.AreEqual(
            12,
            (await settingsStore.LoadAsync(CancellationToken.None))
                .RemainingThresholdPercent);
    }

    [TestMethod]
    public async Task ResetPendingSnapshot_IsShownAsAutomaticRetryInsteadOfSafetyBlock()
    {
        using var directory = new TemporaryDirectory();
        var paths = RuntimePaths.ForTesting(directory.Path);
        var settingsStore = new JsonSettingsStore(paths.SettingsFile);
        var automationSettings = GuardSettings.Default with
        {
            AutomationEnabled = true,
        };
        await settingsStore.SaveAsync(automationSettings, CancellationToken.None);
        await using var monitor = new GuardMonitorService(
            settingsStore,
            new PendingCycleExecutor(),
            automationSettings);
        var viewModel = new MainWindowViewModel(
            settingsStore,
            new StartupService(new MemoryRegistryStore()),
            monitor,
            automationSettings);

        await monitor.RefreshAsync();

        Assert.AreEqual(CycleActionKind.ResetPending, monitor.CurrentSnapshot.ActionKind);
        StringAssert.Contains(viewModel.OverallStatus, "자동 재시도");
        Assert.IsFalse(viewModel.OverallStatus.Contains("안전 차단", StringComparison.Ordinal));
    }

    [DataTestMethod]
    [DataRow(AppServerFailureCategory.ExecutableNotFound, "Codex CLI를 찾지 못했습니다")]
    [DataRow(AppServerFailureCategory.ExecutableBecameUnavailable, "업데이트되어 실행 경로가 바뀐")]
    [DataRow(AppServerFailureCategory.StartFailed, "Codex CLI를 시작하지 못했습니다")]
    public async Task ExecutableFailure_ShowsAnActionableConnectionMessage(
        AppServerFailureCategory category,
        string expectedText)
    {
        using var directory = new TemporaryDirectory();
        var paths = RuntimePaths.ForTesting(directory.Path);
        var settingsStore = new JsonSettingsStore(paths.SettingsFile);
        await settingsStore.SaveAsync(GuardSettings.Default, CancellationToken.None);
        await using var monitor = new GuardMonitorService(
            settingsStore,
            new AppServerFailureCycleExecutor(category),
            GuardSettings.Default);
        var viewModel = new MainWindowViewModel(
            settingsStore,
            new StartupService(new MemoryRegistryStore()),
            monitor,
            GuardSettings.Default);

        await monitor.RefreshAsync();

        StringAssert.Contains(viewModel.OverallStatus, expectedText);
    }

    [TestMethod]
    public async Task SaveAsync_RequiresExplicitConfirmationBeforeEnablingAutomation()
    {
        using var directory = new TemporaryDirectory();
        var paths = RuntimePaths.ForTesting(directory.Path);
        var settingsStore = new JsonSettingsStore(paths.SettingsFile);
        await settingsStore.SaveAsync(GuardSettings.Default, CancellationToken.None);
        await using var monitor = new GuardMonitorService(
            settingsStore,
            new ImmediateCycleExecutor(),
            GuardSettings.Default);
        var viewModel = new MainWindowViewModel(
            settingsStore,
            new StartupService(new MemoryRegistryStore()),
            monitor,
            GuardSettings.Default)
        {
            AutomationEnabled = true,
        };

        await viewModel.SaveAsync();

        Assert.IsFalse(
            (await settingsStore.LoadAsync(CancellationToken.None)).AutomationEnabled);
        Assert.IsTrue(viewModel.RequiresAutomationEnableConfirmation);
        StringAssert.Contains(viewModel.SaveStatus, "확인");

        await viewModel.SaveAsync(automationEnableConfirmed: true);

        Assert.IsTrue(
            (await settingsStore.LoadAsync(CancellationToken.None)).AutomationEnabled);
        Assert.IsFalse(viewModel.RequiresAutomationEnableConfirmation);
    }

    [TestMethod]
    public async Task WeeklySnapshot_UpdatesRemainingProgressAndResetText()
    {
        using var directory = new TemporaryDirectory();
        var paths = RuntimePaths.ForTesting(directory.Path);
        var settingsStore = new JsonSettingsStore(paths.SettingsFile);
        await settingsStore.SaveAsync(GuardSettings.Default, CancellationToken.None);
        var resetAt = DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds();
        var weekly = new WindowReading(
            58.75,
            41.25,
            10_080,
            10_080,
            resetAt);
        await using var monitor = new GuardMonitorService(
            settingsStore,
            new ImmediateCycleExecutor(weekly),
            GuardSettings.Default);
        var viewModel = new MainWindowViewModel(
            settingsStore,
            new StartupService(new MemoryRegistryStore()),
            monitor,
            GuardSettings.Default);

        await monitor.RefreshAsync();

        Assert.AreEqual($"{41.25:F1}%", viewModel.WeeklyRemainingText);
        Assert.AreEqual(41.25, viewModel.WeeklyRemainingPercent);
        StringAssert.Contains(viewModel.WeeklyResetStatus, "다음 갱신 예정");
        Assert.AreEqual("0", viewModel.CreditStatus);
    }

    private static GuardCycleResult CreateResult(WindowReading? weekly = null)
    {
        var observedAt = DateTimeOffset.UtcNow;
        var rateLimits = new AccountRateLimits(
            new RateLimitSnapshot("codex", "Codex", null, null),
            null,
            new ResetCreditSummary(0, []),
            observedAt);
        var evaluation = new EvaluationResult(
            weekly,
            new GuardDecision(
                DecisionKind.NoAction,
                DecisionReason.AboveThreshold,
                null,
                null,
                null),
            0);
        return new GuardCycleResult(
            rateLimits,
            evaluation,
            CycleActionKind.None,
            "no_action");
    }

    private sealed class ImmediateCycleExecutor : IGuardCycleExecutor
    {
        private readonly WindowReading? weekly;

        public ImmediateCycleExecutor(WindowReading? weekly = null)
        {
            this.weekly = weekly;
        }

        public Task<GuardCycleResult> ExecuteAsync(
            GuardSettings settings,
            CancellationToken cancellationToken) => Task.FromResult(CreateResult(weekly));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingCycleExecutor : IGuardCycleExecutor
    {
        public TaskCompletionSource Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<GuardCycleResult> ExecuteAsync(
            GuardSettings settings,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return CreateResult();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PendingCycleExecutor : IGuardCycleExecutor
    {
        public Task<GuardCycleResult> ExecuteAsync(
            GuardSettings settings,
            CancellationToken cancellationToken)
        {
            var result = CreateResult() with
            {
                ActionKind = CycleActionKind.ResetPending,
                ActionCode = "reset_retry_pending",
            };
            return Task.FromResult(result);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class AppServerFailureCycleExecutor : IGuardCycleExecutor
    {
        private readonly AppServerFailureCategory category;

        public AppServerFailureCycleExecutor(AppServerFailureCategory category)
        {
            this.category = category;
        }

        public Task<GuardCycleResult> ExecuteAsync(
            GuardSettings settings,
            CancellationToken cancellationToken) =>
            Task.FromException<GuardCycleResult>(new AppServerException(category));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MemoryRegistryStore : ICurrentUserRegistryStore
    {
        private readonly Dictionary<(string Key, string Name), string> values = new();

        public CurrentUserRegistryValue ReadValue(string subKey, string valueName) =>
            values.TryGetValue((subKey, valueName), out var value)
                ? CurrentUserRegistryValue.FromString(value)
                : CurrentUserRegistryValue.Missing;

        public void SetString(string subKey, string valueName, string value) =>
            values[(subKey, valueName)] = value;

        public void DeleteValue(string subKey, string valueName) =>
            values.Remove((subKey, valueName));
    }

    private sealed class ReadFailingRegistryStore : ICurrentUserRegistryStore
    {
        public int WriteCount { get; private set; }

        public CurrentUserRegistryValue ReadValue(string subKey, string valueName) =>
            throw new IOException("simulated_registry_read_failure");

        public void SetString(string subKey, string valueName, string value) =>
            WriteCount++;

        public void DeleteValue(string subKey, string valueName) =>
            WriteCount++;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"CodexAutoReset.Runtime.Tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
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
    }
}
