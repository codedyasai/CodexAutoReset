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
            FiveHourThresholdText = "88",
            AutomationEnabled = true,
            FiveHourAutomationEnabled = true,
        };

        var selected = viewModel.TrySetCodexExecutablePath(
            codexPath,
            out var errorMessage);

        Assert.IsTrue(selected, errorMessage);
        Assert.IsTrue(viewModel.HasCustomCodexExecutablePath);
        Assert.AreNotEqual(codexPath, viewModel.CodexExecutableDisplayText);
        Assert.IsFalse(
            viewModel.CodexExecutableDisplayText.Contains(
                directory.Path,
                StringComparison.OrdinalIgnoreCase));

        Assert.IsTrue(await viewModel.SaveCodexExecutablePathAsync());

        var selectedSettings = await settingsStore.LoadAsync(CancellationToken.None);
        Assert.AreEqual(codexPath, selectedSettings.CodexExecutablePath);
        Assert.IsFalse(selectedSettings.AutomationEnabled);
        Assert.IsFalse(selectedSettings.FiveHourAutomationEnabled);
        Assert.AreEqual(GuardSettings.Default.RemainingThresholdPercent, selectedSettings.RemainingThresholdPercent);
        Assert.AreEqual(
            GuardSettings.Default.FiveHourRemainingThresholdPercent,
            selectedSettings.FiveHourRemainingThresholdPercent);
        Assert.AreEqual(
            GuardSettings.FixedPollIntervalMinutes,
            selectedSettings.PollIntervalMinutes);
        Assert.AreEqual("99", viewModel.ThresholdText);
        Assert.AreEqual("88", viewModel.FiveHourThresholdText);
        Assert.IsTrue(viewModel.AutomationEnabled);
        Assert.IsTrue(viewModel.FiveHourAutomationEnabled);
        Assert.AreEqual(
            "연결 경로를 저장했습니다. 다음 확인부터 사용합니다.",
            viewModel.CodexConnectionStatus);

        viewModel.UseAutomaticCodexExecutablePath();
        Assert.IsTrue(await viewModel.SaveCodexExecutablePathAsync());

        var automaticSettings = await settingsStore.LoadAsync(CancellationToken.None);
        Assert.IsNull(automaticSettings.CodexExecutablePath);
        Assert.IsFalse(automaticSettings.AutomationEnabled);
        Assert.IsFalse(automaticSettings.FiveHourAutomationEnabled);
        Assert.IsFalse(viewModel.HasCustomCodexExecutablePath);
    }

    [TestMethod]
    public async Task CodexExecutableAutomaticDisplay_DoesNotDescribeConnectionMode()
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
            GuardSettings.Default);

        Assert.IsFalse(
            viewModel.CodexExecutableDisplayText.Contains(
                "자동으로 찾",
                StringComparison.Ordinal));
        var userProfile = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile);
        Assert.IsTrue(
            string.IsNullOrWhiteSpace(userProfile)
            || !viewModel.CodexExecutableDisplayText.Contains(
                userProfile,
                StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task CodexAutomaticSelection_MergesExternalSettingsAndClearsPath()
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

        Assert.IsTrue(await viewModel.SaveCodexExecutablePathAsync());

        var expected = externallyChanged with
        {
            CodexExecutablePath = null,
        };
        Assert.IsFalse(viewModel.HasCustomCodexExecutablePath);
        Assert.IsNull(viewModel.ConfiguredCodexExecutablePath);
        Assert.AreEqual(expected, await settingsStore.LoadAsync(CancellationToken.None));
        Assert.AreEqual("42", viewModel.ThresholdText);
        Assert.IsTrue(viewModel.CanEditCodexConnection);
        StringAssert.Contains(viewModel.CodexConnectionStatus, "저장했습니다");
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
    public async Task AutomaticSettings_IndependentThresholdsApplyWithoutSaveButton()
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
            GuardSettings.Default);

        viewModel.ThresholdText = "18";
        Assert.IsTrue(await viewModel.SaveThresholdAsync());
        viewModel.FiveHourThresholdText = "11";
        Assert.IsTrue(await viewModel.SaveFiveHourThresholdAsync());

        var persisted = await settingsStore.LoadAsync(CancellationToken.None);
        Assert.AreEqual(18, persisted.RemainingThresholdPercent);
        Assert.AreEqual(11, persisted.FiveHourRemainingThresholdPercent);
        Assert.AreEqual(
            GuardSettings.FixedPollIntervalMinutes,
            persisted.PollIntervalMinutes);
        Assert.IsFalse(persisted.AutomationEnabled);
        Assert.IsFalse(persisted.FiveHourAutomationEnabled);
        Assert.IsFalse(persisted.StartWithWindows);
        Assert.IsNull(persisted.CodexExecutablePath);
        var persistedJson = await File.ReadAllTextAsync(paths.SettingsFile);
        StringAssert.Contains(persistedJson, "\"schemaVersion\": 6");
        Assert.IsFalse(
            persistedJson.Contains(
                "pollIntervalMinutes",
                StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AutomaticSettings_InvalidNumericTextDoesNotOverwritePersistedValues()
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
            GuardSettings.Default);

        viewModel.ThresholdText = "100";
        Assert.IsFalse(await viewModel.SaveThresholdAsync());
        viewModel.FiveHourThresholdText = "100";
        Assert.IsFalse(await viewModel.SaveFiveHourThresholdAsync());

        Assert.AreEqual(
            GuardSettings.Default,
            await settingsStore.LoadAsync(CancellationToken.None));
        StringAssert.Contains(viewModel.SaveStatus, "0~99%");
    }

    [TestMethod]
    public async Task AutomaticSettings_QueuedThresholdChangesPersistWithoutLoss()
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

        viewModel.ThresholdText = "23";
        var thresholdTask = viewModel.SaveThresholdAsync();
        viewModel.FiveHourThresholdText = "17";
        var fiveHourThresholdTask =
            viewModel.SaveFiveHourThresholdAsync();

        Assert.IsFalse(thresholdTask.IsCompleted);
        Assert.IsFalse(fiveHourThresholdTask.IsCompleted);
        executor.Release.TrySetResult();
        await cycleTask;
        Assert.IsTrue(await thresholdTask);
        Assert.IsTrue(await fiveHourThresholdTask);

        var persisted = await settingsStore.LoadAsync(CancellationToken.None);
        Assert.AreEqual(23, persisted.RemainingThresholdPercent);
        Assert.AreEqual(17, persisted.FiveHourRemainingThresholdPercent);
        Assert.AreEqual(
            GuardSettings.FixedPollIntervalMinutes,
            persisted.PollIntervalMinutes);
    }

    [TestMethod]
    public async Task AutomaticSettings_RevertedValueWhileSaveWaitsIsNotLost()
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

        viewModel.ThresholdText = "23";
        var firstSave = viewModel.SaveThresholdAsync();
        viewModel.ThresholdText = "7";
        var revertedSave = viewModel.SaveThresholdAsync();

        Assert.IsFalse(firstSave.IsCompleted);
        Assert.IsFalse(revertedSave.IsCompleted);
        executor.Release.TrySetResult();
        await cycleTask;
        Assert.IsTrue(await firstSave);
        Assert.IsTrue(await revertedSave);

        Assert.AreEqual("7", viewModel.ThresholdText);
        Assert.AreEqual(
            7,
            (await settingsStore.LoadAsync(CancellationToken.None))
                .RemainingThresholdPercent);
    }

    [TestMethod]
    public async Task AutomaticSettings_RiskyThresholdIncreaseRequiresConfirmation()
    {
        using var directory = new TemporaryDirectory();
        var paths = RuntimePaths.ForTesting(directory.Path);
        var settingsStore = new JsonSettingsStore(paths.SettingsFile);
        var settings = GuardSettings.Default with
        {
            RemainingThresholdPercent = 7,
            AutomationEnabled = true,
        };
        await settingsStore.SaveAsync(settings, CancellationToken.None);
        var resetAt = DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds();
        var weekly = new WindowReading(
            72.4,
            27.6,
            10_080,
            10_080,
            resetAt);
        await using var monitor = new GuardMonitorService(
            settingsStore,
            new ImmediateCycleExecutor(weekly, availableCreditCount: 1),
            settings);
        var viewModel = new MainWindowViewModel(
            settingsStore,
            new StartupService(new MemoryRegistryStore()),
            monitor,
            settings);
        await monitor.RefreshAsync();

        viewModel.ThresholdText = "30";

        Assert.IsTrue(viewModel.RequiresThresholdChangeConfirmation());
        Assert.IsFalse(await viewModel.SaveThresholdAsync());
        Assert.AreEqual("7", viewModel.ThresholdText);
        Assert.AreEqual(
            7,
            (await settingsStore.LoadAsync(CancellationToken.None))
                .RemainingThresholdPercent);
        StringAssert.Contains(viewModel.SaveStatus, "확인");

        viewModel.ThresholdText = "30";
        Assert.IsTrue(await viewModel.SaveThresholdAsync(
            immediateResetRiskConfirmed: true));
        Assert.AreEqual(
            30,
            (await settingsStore.LoadAsync(CancellationToken.None))
                .RemainingThresholdPercent);
    }

    [TestMethod]
    public async Task FiveHourRiskyThresholdIncreaseRequiresConfirmationIndependently()
    {
        using var directory = new TemporaryDirectory();
        var paths = RuntimePaths.ForTesting(directory.Path);
        var settingsStore = new JsonSettingsStore(paths.SettingsFile);
        var settings = GuardSettings.Default with
        {
            FiveHourRemainingThresholdPercent = 7,
            FiveHourAutomationEnabled = true,
        };
        await settingsStore.SaveAsync(settings, CancellationToken.None);
        var fiveHour = new WindowReading(
            72.4,
            27.6,
            300,
            300,
            DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds());
        await using var monitor = new GuardMonitorService(
            settingsStore,
            new ImmediateCycleExecutor(
                fiveHour: fiveHour,
                availableCreditCount: 1),
            settings);
        var viewModel = new MainWindowViewModel(
            settingsStore,
            new StartupService(new MemoryRegistryStore()),
            monitor,
            settings);
        await monitor.RefreshAsync();

        viewModel.FiveHourThresholdText = "30";

        Assert.IsTrue(
            viewModel.RequiresFiveHourThresholdChangeConfirmation());
        Assert.IsFalse(await viewModel.SaveFiveHourThresholdAsync());
        Assert.AreEqual("7", viewModel.FiveHourThresholdText);
        Assert.AreEqual(
            7,
            (await settingsStore.LoadAsync(CancellationToken.None))
                .FiveHourRemainingThresholdPercent);
        Assert.AreEqual(
            GuardSettings.Default.RemainingThresholdPercent,
            (await settingsStore.LoadAsync(CancellationToken.None))
                .RemainingThresholdPercent);

        viewModel.FiveHourThresholdText = "30";
        Assert.IsTrue(await viewModel.SaveFiveHourThresholdAsync(
            immediateResetRiskConfirmed: true));
        Assert.AreEqual(
            30,
            (await settingsStore.LoadAsync(CancellationToken.None))
                .FiveHourRemainingThresholdPercent);
    }

    [TestMethod]
    public async Task AutomaticSettings_IndependentAutomationTogglesRequireConfirmation()
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
            GuardSettings.Default);

        Assert.IsFalse(await viewModel.SetAutomationEnabledAsync(enabled: true));
        var persisted =
            await settingsStore.LoadAsync(CancellationToken.None);
        Assert.IsFalse(persisted.AutomationEnabled);
        Assert.IsFalse(persisted.FiveHourAutomationEnabled);
        StringAssert.Contains(viewModel.SaveStatus, "확인");

        Assert.IsFalse(
            await viewModel.SetFiveHourAutomationEnabledAsync(enabled: true));
        persisted = await settingsStore.LoadAsync(CancellationToken.None);
        Assert.IsFalse(persisted.AutomationEnabled);
        Assert.IsFalse(persisted.FiveHourAutomationEnabled);

        Assert.IsTrue(await viewModel.SetFiveHourAutomationEnabledAsync(
            enabled: true,
            automationEnableConfirmed: true));
        persisted = await settingsStore.LoadAsync(CancellationToken.None);
        Assert.IsNull(persisted.RemainingThresholdPercent);
        Assert.IsFalse(persisted.AutomationEnabled);
        Assert.AreEqual(0, persisted.FiveHourRemainingThresholdPercent);
        Assert.IsTrue(persisted.FiveHourAutomationEnabled);

        Assert.IsTrue(await viewModel.SetAutomationEnabledAsync(
            enabled: true,
            automationEnableConfirmed: true));
        persisted = await settingsStore.LoadAsync(CancellationToken.None);
        Assert.AreEqual(0, persisted.RemainingThresholdPercent);
        Assert.IsTrue(persisted.AutomationEnabled);
        Assert.AreEqual(0, persisted.FiveHourRemainingThresholdPercent);
        Assert.IsTrue(persisted.FiveHourAutomationEnabled);

        Assert.IsTrue(
            await viewModel.SetFiveHourAutomationEnabledAsync(enabled: false));
        persisted = await settingsStore.LoadAsync(CancellationToken.None);
        Assert.IsTrue(persisted.AutomationEnabled);
        Assert.AreEqual(0, persisted.FiveHourRemainingThresholdPercent);
        Assert.IsFalse(persisted.FiveHourAutomationEnabled);
    }

    [TestMethod]
    public async Task FreshSettings_ShowBlankThresholdsAndDisabledAutomation()
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
            GuardSettings.Default);

        Assert.AreEqual(string.Empty, viewModel.ThresholdText);
        Assert.IsFalse(viewModel.AutomationEnabled);
        Assert.AreEqual(string.Empty, viewModel.FiveHourThresholdText);
        Assert.IsFalse(viewModel.FiveHourAutomationEnabled);
        Assert.IsNull(GuardSettings.Default.RemainingThresholdPercent);
        Assert.IsNull(
            GuardSettings.Default.FiveHourRemainingThresholdPercent);
        Assert.IsFalse(GuardSettings.Default.AnyAutomationEnabled);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EnablingBlankThreshold_AtomicallyPersistsZeroAndEnabled(
        bool fiveHour)
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
            GuardSettings.Default);

        var saved = fiveHour
            ? await viewModel.SetFiveHourAutomationEnabledAsync(
                enabled: true,
                automationEnableConfirmed: true)
            : await viewModel.SetAutomationEnabledAsync(
                enabled: true,
                automationEnableConfirmed: true);
        var persisted = await settingsStore.LoadAsync(CancellationToken.None);

        Assert.IsTrue(saved);
        if (fiveHour)
        {
            Assert.IsNull(persisted.RemainingThresholdPercent);
            Assert.IsFalse(persisted.AutomationEnabled);
            Assert.AreEqual(0, persisted.FiveHourRemainingThresholdPercent);
            Assert.IsTrue(persisted.FiveHourAutomationEnabled);
            Assert.AreEqual(string.Empty, viewModel.ThresholdText);
            Assert.IsFalse(viewModel.AutomationEnabled);
            Assert.AreEqual("0", viewModel.FiveHourThresholdText);
            Assert.IsTrue(viewModel.FiveHourAutomationEnabled);
        }
        else
        {
            Assert.AreEqual(0, persisted.RemainingThresholdPercent);
            Assert.IsTrue(persisted.AutomationEnabled);
            Assert.IsNull(persisted.FiveHourRemainingThresholdPercent);
            Assert.IsFalse(persisted.FiveHourAutomationEnabled);
            Assert.AreEqual("0", viewModel.ThresholdText);
            Assert.IsTrue(viewModel.AutomationEnabled);
            Assert.AreEqual(string.Empty, viewModel.FiveHourThresholdText);
            Assert.IsFalse(viewModel.FiveHourAutomationEnabled);
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ClearingThreshold_AtomicallyDisablesOnlyThatLimit(
        bool fiveHour)
    {
        using var directory = new TemporaryDirectory();
        var paths = RuntimePaths.ForTesting(directory.Path);
        var initialSettings = GuardSettings.Default with
        {
            RemainingThresholdPercent = 12,
            AutomationEnabled = true,
            FiveHourRemainingThresholdPercent = 14,
            FiveHourAutomationEnabled = true,
        };
        var settingsStore = new JsonSettingsStore(paths.SettingsFile);
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

        if (fiveHour)
        {
            viewModel.FiveHourThresholdText = string.Empty;
            Assert.IsTrue(await viewModel.SaveFiveHourThresholdAsync());
        }
        else
        {
            viewModel.ThresholdText = string.Empty;
            Assert.IsTrue(await viewModel.SaveThresholdAsync());
        }

        var persisted = await settingsStore.LoadAsync(CancellationToken.None);
        if (fiveHour)
        {
            Assert.AreEqual(12, persisted.RemainingThresholdPercent);
            Assert.IsTrue(persisted.AutomationEnabled);
            Assert.IsNull(persisted.FiveHourRemainingThresholdPercent);
            Assert.IsFalse(persisted.FiveHourAutomationEnabled);
            Assert.AreEqual("12", viewModel.ThresholdText);
            Assert.IsTrue(viewModel.AutomationEnabled);
            Assert.AreEqual(string.Empty, viewModel.FiveHourThresholdText);
            Assert.IsFalse(viewModel.FiveHourAutomationEnabled);
        }
        else
        {
            Assert.IsNull(persisted.RemainingThresholdPercent);
            Assert.IsFalse(persisted.AutomationEnabled);
            Assert.AreEqual(14, persisted.FiveHourRemainingThresholdPercent);
            Assert.IsTrue(persisted.FiveHourAutomationEnabled);
            Assert.AreEqual(string.Empty, viewModel.ThresholdText);
            Assert.IsFalse(viewModel.AutomationEnabled);
            Assert.AreEqual("14", viewModel.FiveHourThresholdText);
            Assert.IsTrue(viewModel.FiveHourAutomationEnabled);
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EnablingExistingNumericThreshold_PreservesItsValue(
        bool fiveHour)
    {
        using var directory = new TemporaryDirectory();
        var paths = RuntimePaths.ForTesting(directory.Path);
        var initialSettings = GuardSettings.Default with
        {
            RemainingThresholdPercent = 12,
            AutomationEnabled = false,
            FiveHourRemainingThresholdPercent = 14,
            FiveHourAutomationEnabled = false,
        };
        var settingsStore = new JsonSettingsStore(paths.SettingsFile);
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

        var saved = fiveHour
            ? await viewModel.SetFiveHourAutomationEnabledAsync(
                enabled: true,
                automationEnableConfirmed: true)
            : await viewModel.SetAutomationEnabledAsync(
                enabled: true,
                automationEnableConfirmed: true);
        var persisted = await settingsStore.LoadAsync(CancellationToken.None);

        Assert.IsTrue(saved);
        Assert.AreEqual(12, persisted.RemainingThresholdPercent);
        Assert.AreEqual(14, persisted.FiveHourRemainingThresholdPercent);
        Assert.AreEqual(!fiveHour, persisted.AutomationEnabled);
        Assert.AreEqual(fiveHour, persisted.FiveHourAutomationEnabled);
        Assert.AreEqual("12", viewModel.ThresholdText);
        Assert.AreEqual("14", viewModel.FiveHourThresholdText);
    }

    [TestMethod]
    public async Task RawEnabledFlagsWithNullThresholds_RenderAndEvaluateAsOff()
    {
        using var directory = new TemporaryDirectory();
        var paths = RuntimePaths.ForTesting(directory.Path);
        var rawSettings = GuardSettings.Default with
        {
            RemainingThresholdPercent = null,
            AutomationEnabled = true,
            FiveHourRemainingThresholdPercent = null,
            FiveHourAutomationEnabled = true,
        };
        var settingsStore = new JsonSettingsStore(paths.SettingsFile);
        await settingsStore.SaveAsync(rawSettings, CancellationToken.None);
        await using var monitor = new GuardMonitorService(
            settingsStore,
            new ImmediateCycleExecutor(),
            rawSettings);
        var viewModel = new MainWindowViewModel(
            settingsStore,
            new StartupService(new MemoryRegistryStore()),
            monitor,
            rawSettings);

        Assert.IsTrue(rawSettings.AutomationEnabled);
        Assert.IsTrue(rawSettings.FiveHourAutomationEnabled);
        Assert.IsFalse(rawSettings.IsAutomationEnabled(TriggerLimit.Weekly));
        Assert.IsFalse(rawSettings.IsAutomationEnabled(TriggerLimit.FiveHour));
        Assert.IsFalse(rawSettings.AnyAutomationEnabled);
        Assert.AreEqual(string.Empty, viewModel.ThresholdText);
        Assert.IsFalse(viewModel.AutomationEnabled);
        Assert.AreEqual(string.Empty, viewModel.FiveHourThresholdText);
        Assert.IsFalse(viewModel.FiveHourAutomationEnabled);
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AutomationEnable_PreservesUsageWhileImmediateRefreshIsInFlight(
        bool fiveHour)
    {
        using var directory = new TemporaryDirectory();
        var paths = RuntimePaths.ForTesting(directory.Path);
        var settingsStore = new JsonSettingsStore(paths.SettingsFile);
        await settingsStore.SaveAsync(GuardSettings.Default, CancellationToken.None);
        var weekly = new WindowReading(
            42,
            58,
            10_080,
            10_080,
            DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds());
        var fiveHourReading = new WindowReading(
            35,
            65,
            300,
            300,
            DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds());
        var executor = new BlockingRefreshCycleExecutor(
            weekly,
            fiveHourReading,
            availableCreditCount: 3);
        await using var monitor = new GuardMonitorService(
            settingsStore,
            executor,
            GuardSettings.Default);
        var viewModel = new MainWindowViewModel(
            settingsStore,
            new StartupService(new MemoryRegistryStore()),
            monitor,
            GuardSettings.Default);
        var initialSnapshotPublished =
            new TaskCompletionSource<MonitorSnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.SnapshotChanged += (_, snapshot) =>
        {
            if (snapshot.Weekly is not null && snapshot.FiveHour is not null)
            {
                initialSnapshotPublished.TrySetResult(snapshot);
            }
        };

        await monitor.StartAsync();
        var initialSnapshot = await initialSnapshotPublished.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        try
        {
            var saved = fiveHour
                ? await viewModel.SetFiveHourAutomationEnabledAsync(
                    enabled: true,
                    automationEnableConfirmed: true)
                : await viewModel.SetAutomationEnabledAsync(
                    enabled: true,
                    automationEnableConfirmed: true);
            Assert.IsTrue(saved);
            await executor.RefreshEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(5));

            Assert.AreEqual(2, executor.CallCount);
            Assert.AreEqual(
                initialSnapshot.Weekly,
                monitor.CurrentSnapshot.Weekly);
            Assert.AreEqual(
                initialSnapshot.FiveHour,
                monitor.CurrentSnapshot.FiveHour);
            Assert.AreEqual(
                initialSnapshot.AvailableCreditCount,
                monitor.CurrentSnapshot.AvailableCreditCount);
            Assert.AreEqual(
                initialSnapshot.ObservedAt,
                monitor.CurrentSnapshot.ObservedAt);
            Assert.AreEqual(
                initialSnapshot.LastSuccessfulObservationAt,
                monitor.CurrentSnapshot.LastSuccessfulObservationAt);
            Assert.AreEqual(
                initialSnapshot.StatusCode,
                monitor.CurrentSnapshot.StatusCode);
            Assert.AreEqual("58%", viewModel.WeeklyRemainingText);
            Assert.AreEqual("65%", viewModel.FiveHourRemainingText);
            Assert.AreEqual("3", viewModel.CreditStatus);
        }
        finally
        {
            executor.ReleaseRefresh.TrySetResult();
        }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AutomationDisable_DoesNotReplaceCurrentUsageWithWaiting(
        bool fiveHour)
    {
        using var directory = new TemporaryDirectory();
        var paths = RuntimePaths.ForTesting(directory.Path);
        var initialSettings = GuardSettings.Default with
        {
            RemainingThresholdPercent = 7,
            FiveHourRemainingThresholdPercent = 7,
            AutomationEnabled = !fiveHour,
            FiveHourAutomationEnabled = fiveHour,
        };
        var settingsStore = new JsonSettingsStore(paths.SettingsFile);
        await settingsStore.SaveAsync(initialSettings, CancellationToken.None);
        var weekly = new WindowReading(
            42,
            58,
            10_080,
            10_080,
            DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds());
        var fiveHourReading = new WindowReading(
            35,
            65,
            300,
            300,
            DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds());
        var executor = new ImmediateCycleExecutor(
            weekly,
            fiveHourReading,
            availableCreditCount: 3);
        await using var monitor = new GuardMonitorService(
            settingsStore,
            executor,
            initialSettings);
        var viewModel = new MainWindowViewModel(
            settingsStore,
            new StartupService(new MemoryRegistryStore()),
            monitor,
            initialSettings);
        await monitor.RefreshAsync();
        var initialSnapshot = monitor.CurrentSnapshot;

        var saved = fiveHour
            ? await viewModel.SetFiveHourAutomationEnabledAsync(enabled: false)
            : await viewModel.SetAutomationEnabledAsync(enabled: false);

        Assert.IsTrue(saved);
        Assert.AreEqual(1, executor.CallCount);
        Assert.AreEqual(initialSnapshot.Weekly, monitor.CurrentSnapshot.Weekly);
        Assert.AreEqual(initialSnapshot.FiveHour, monitor.CurrentSnapshot.FiveHour);
        Assert.AreEqual(
            initialSnapshot.AvailableCreditCount,
            monitor.CurrentSnapshot.AvailableCreditCount);
        Assert.AreEqual(initialSnapshot.ObservedAt, monitor.CurrentSnapshot.ObservedAt);
        Assert.AreEqual(
            initialSnapshot.LastSuccessfulObservationAt,
            monitor.CurrentSnapshot.LastSuccessfulObservationAt);
        Assert.AreEqual(initialSnapshot.StatusCode, monitor.CurrentSnapshot.StatusCode);
        Assert.AreEqual("58%", viewModel.WeeklyRemainingText);
        Assert.AreEqual("65%", viewModel.FiveHourRemainingText);
        Assert.AreEqual("3", viewModel.CreditStatus);
    }

    [TestMethod]
    public async Task UsageResetNotification_AppliesImmediatelyWithoutClearingUsage()
    {
        using var directory = new TemporaryDirectory();
        var paths = RuntimePaths.ForTesting(directory.Path);
        var settingsStore = new JsonSettingsStore(paths.SettingsFile);
        await settingsStore.SaveAsync(GuardSettings.Default, CancellationToken.None);
        var weekly = new WindowReading(
            42,
            58,
            10_080,
            10_080,
            DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds());
        var fiveHour = new WindowReading(
            35,
            65,
            300,
            300,
            DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds());
        await using var monitor = new GuardMonitorService(
            settingsStore,
            new ImmediateCycleExecutor(weekly, fiveHour),
            GuardSettings.Default);
        var viewModel = new MainWindowViewModel(
            settingsStore,
            new StartupService(new MemoryRegistryStore()),
            monitor,
            GuardSettings.Default);
        await monitor.RefreshAsync();
        var observedAt = monitor.CurrentSnapshot.ObservedAt;

        Assert.IsTrue(await viewModel.SetNotifyOnUsageResetAsync(enabled: false));

        Assert.IsFalse(viewModel.NotifyOnUsageReset);
        Assert.IsFalse(
            (await settingsStore.LoadAsync(CancellationToken.None)).NotifyOnUsageReset);
        Assert.AreEqual(58, monitor.CurrentSnapshot.Weekly?.RemainingPercent);
        Assert.AreEqual(65, monitor.CurrentSnapshot.FiveHour?.RemainingPercent);
        Assert.AreEqual(observedAt, monitor.CurrentSnapshot.ObservedAt);
        Assert.AreEqual("no_action", monitor.CurrentSnapshot.StatusCode);
    }

    [TestMethod]
    public async Task CurrentSnapshot_ReturnsTheSnapshotAppliedToTheViewModel()
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
            GuardSettings.Default);
        var appliedSnapshot = MonitorSnapshot.Waiting(GuardSettings.Default) with
        {
            StatusCode = "applied_snapshot",
        };
        var applySnapshot = typeof(MainWindowViewModel).GetMethod(
            "ApplySnapshot",
            System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic);

        Assert.IsNotNull(applySnapshot);
        applySnapshot.Invoke(viewModel, [appliedSnapshot]);

        Assert.AreSame(appliedSnapshot, viewModel.CurrentSnapshot);
        Assert.AreNotSame(appliedSnapshot, monitor.CurrentSnapshot);
    }

    [TestMethod]
    public async Task RefreshNowAsync_ShowsBusyStateAndBlocksReentryUntilComplete()
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

        var refreshTask = viewModel.RefreshNowAsync();
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(viewModel.IsRefreshing);
        Assert.IsFalse(viewModel.CanRefresh);
        Assert.AreEqual("확인 중…", viewModel.RefreshStatusText);
        Assert.AreEqual("사용량 새로고침 중", viewModel.RefreshAutomationName);
        await viewModel.RefreshNowAsync();
        Assert.IsFalse(refreshTask.IsCompleted);

        executor.Release.TrySetResult();
        await refreshTask;
        Assert.IsFalse(viewModel.IsRefreshing);
        Assert.IsTrue(viewModel.CanRefresh);
        Assert.AreEqual(string.Empty, viewModel.RefreshStatusText);
        Assert.AreEqual("사용량 새로고침", viewModel.RefreshAutomationName);
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
            FiveHourThresholdText = "41",
            AutomationEnabled = true,
            FiveHourAutomationEnabled = true,
        };

        await viewModel.SetStartWithWindowsAsync(enabled: true);

        var persisted = await settingsStore.LoadAsync(CancellationToken.None);
        Assert.AreEqual(GuardSettings.Default with { StartWithWindows = true }, persisted);
        Assert.AreEqual("99", viewModel.ThresholdText);
        Assert.AreEqual("41", viewModel.FiveHourThresholdText);
        Assert.IsTrue(viewModel.AutomationEnabled);
        Assert.IsTrue(viewModel.FiveHourAutomationEnabled);
        Assert.IsTrue(viewModel.IsStartupActuallyEnabled);
        Assert.AreEqual(StartupStatus.Enabled, viewModel.ActualStartupStatus);
    }

    [TestMethod]
    public async Task StartWithWindowsToggle_PreservesUsageWithoutAnotherCheck()
    {
        using var directory = new TemporaryDirectory();
        var paths = RuntimePaths.ForTesting(directory.Path);
        var settingsStore = new JsonSettingsStore(paths.SettingsFile);
        await settingsStore.SaveAsync(GuardSettings.Default, CancellationToken.None);
        var executablePath = System.IO.Path.Combine(
            directory.Path,
            "CodexAutoReset.exe");
        await File.WriteAllBytesAsync(executablePath, [0]);
        var weekly = new WindowReading(
            42,
            58,
            10_080,
            10_080,
            DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds());
        var fiveHour = new WindowReading(
            35,
            65,
            300,
            300,
            DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds());
        var executor = new ImmediateCycleExecutor(
            weekly,
            fiveHour,
            availableCreditCount: 3);
        await using var monitor = new GuardMonitorService(
            settingsStore,
            executor,
            GuardSettings.Default);
        var viewModel = new MainWindowViewModel(
            settingsStore,
            new StartupService(new MemoryRegistryStore()),
            monitor,
            GuardSettings.Default,
            () => executablePath);
        await monitor.RefreshAsync();
        var initialSnapshot = monitor.CurrentSnapshot;

        await viewModel.SetStartWithWindowsAsync(enabled: true);

        Assert.AreEqual(1, executor.CallCount);
        Assert.AreEqual(initialSnapshot.Weekly, monitor.CurrentSnapshot.Weekly);
        Assert.AreEqual(initialSnapshot.FiveHour, monitor.CurrentSnapshot.FiveHour);
        Assert.AreEqual(
            initialSnapshot.AvailableCreditCount,
            monitor.CurrentSnapshot.AvailableCreditCount);
        Assert.AreEqual(initialSnapshot.ObservedAt, monitor.CurrentSnapshot.ObservedAt);
        Assert.AreEqual(
            initialSnapshot.LastSuccessfulObservationAt,
            monitor.CurrentSnapshot.LastSuccessfulObservationAt);
        Assert.AreEqual(initialSnapshot.StatusCode, monitor.CurrentSnapshot.StatusCode);
        Assert.AreEqual("58%", viewModel.WeeklyRemainingText);
        Assert.AreEqual("65%", viewModel.FiveHourRemainingText);
        Assert.AreEqual("3", viewModel.CreditStatus);
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
            FiveHourRemainingThresholdPercent = 19,
            AutomationEnabled = true,
            FiveHourAutomationEnabled = true,
        };
        await settingsStore.SaveAsync(externallyChanged, CancellationToken.None);

        await viewModel.SetStartWithWindowsAsync(enabled: true);

        var expected = externallyChanged with { StartWithWindows = true };
        Assert.AreEqual(expected, await settingsStore.LoadAsync(CancellationToken.None));
        Assert.AreEqual("31", viewModel.ThresholdText);
        Assert.AreEqual("19", viewModel.FiveHourThresholdText);
        Assert.IsTrue(viewModel.AutomationEnabled);
        Assert.IsTrue(viewModel.FiveHourAutomationEnabled);
        Assert.AreEqual(
            GuardSettings.FixedPollIntervalMinutes,
            (await settingsStore.LoadAsync(CancellationToken.None))
                .PollIntervalMinutes);
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
        viewModel.FiveHourThresholdText = "14";
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
        Assert.AreEqual(
            14,
            (await settingsStore.LoadAsync(CancellationToken.None))
                .FiveHourRemainingThresholdPercent);

        viewModel.ThresholdText = "13";
        viewModel.FiveHourThresholdText = "15";
        await viewModel.SaveAsync();
        Assert.AreEqual(
            12,
            (await settingsStore.LoadAsync(CancellationToken.None))
                .RemainingThresholdPercent);
        Assert.AreEqual(
            14,
            (await settingsStore.LoadAsync(CancellationToken.None))
                .FiveHourRemainingThresholdPercent);
    }

    [TestMethod]
    public async Task ResetPendingSnapshot_IsShownAsAutomaticRetryInsteadOfSafetyBlock()
    {
        using var directory = new TemporaryDirectory();
        var paths = RuntimePaths.ForTesting(directory.Path);
        var settingsStore = new JsonSettingsStore(paths.SettingsFile);
        var automationSettings = GuardSettings.Default with
        {
            RemainingThresholdPercent = 7,
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
        Assert.AreEqual("-", viewModel.FiveHourRemainingText);
        Assert.AreEqual(0, viewModel.FiveHourRemainingPercent);
        Assert.AreEqual(
            "다음 갱신 예정 · -",
            viewModel.FiveHourResetStatus);
    }

    [TestMethod]
    public async Task SaveAsync_RequiresExplicitConfirmationBeforeEnablingAutomation()
    {
        using var directory = new TemporaryDirectory();
        var paths = RuntimePaths.ForTesting(directory.Path);
        var settingsStore = new JsonSettingsStore(paths.SettingsFile);
        var initialSettings = GuardSettings.Default with
        {
            RemainingThresholdPercent = 7,
            AutomationEnabled = false,
            FiveHourRemainingThresholdPercent = 7,
            FiveHourAutomationEnabled = false,
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
            initialSettings)
        {
            AutomationEnabled = true,
            FiveHourAutomationEnabled = true,
        };

        await viewModel.SaveAsync();

        var persisted =
            await settingsStore.LoadAsync(CancellationToken.None);
        Assert.IsFalse(persisted.AutomationEnabled);
        Assert.IsFalse(persisted.FiveHourAutomationEnabled);
        Assert.IsTrue(viewModel.RequiresAutomationEnableConfirmation);
        Assert.IsTrue(
            viewModel.RequiresFiveHourAutomationEnableConfirmation);
        StringAssert.Contains(viewModel.SaveStatus, "확인");

        await viewModel.SaveAsync(automationEnableConfirmed: true);

        persisted = await settingsStore.LoadAsync(CancellationToken.None);
        Assert.IsTrue(persisted.AutomationEnabled);
        Assert.IsTrue(persisted.FiveHourAutomationEnabled);
        Assert.IsFalse(viewModel.RequiresAutomationEnableConfirmation);
        Assert.IsFalse(
            viewModel.RequiresFiveHourAutomationEnableConfirmation);
    }

    [TestMethod]
    public async Task InitialSnapshot_ClearlyMarksFiveHourAsNotChecked()
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
            GuardSettings.Default);

        Assert.AreEqual("-", viewModel.WeeklyRemainingText);
        Assert.AreEqual("-", viewModel.FiveHourRemainingText);
        Assert.AreEqual(0, viewModel.FiveHourRemainingPercent);
        Assert.AreEqual(
            "다음 갱신 예정 · -",
            viewModel.FiveHourResetStatus);
        Assert.AreEqual("-", viewModel.CreditStatus);
    }

    [TestMethod]
    public async Task WeeklySnapshot_MarksMissingFiveHourLimitAsNotApplicable()
    {
        using var directory = new TemporaryDirectory();
        var paths = RuntimePaths.ForTesting(directory.Path);
        var settingsStore = new JsonSettingsStore(paths.SettingsFile);
        await settingsStore.SaveAsync(GuardSettings.Default, CancellationToken.None);
        var resetAt = DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds();
        var weekly = new WindowReading(
            0,
            100,
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

        Assert.AreEqual("100%", viewModel.WeeklyRemainingText);
        Assert.AreEqual(100, viewModel.WeeklyRemainingPercent);
        StringAssert.Contains(viewModel.WeeklyResetStatus, "다음 갱신 예정");
        Assert.AreEqual("-", viewModel.FiveHourRemainingText);
        Assert.AreEqual(0, viewModel.FiveHourRemainingPercent);
        Assert.AreEqual(
            "다음 갱신 예정 · -",
            viewModel.FiveHourResetStatus);
        Assert.IsFalse(
            viewModel.FiveHourResetStatus.Contains(
                "현재 계정에는",
                StringComparison.Ordinal));
        Assert.AreEqual("0", viewModel.CreditStatus);
    }

    [TestMethod]
    public async Task FiveHourSnapshot_UsesItsOwnPercentAndResetStatus()
    {
        using var directory = new TemporaryDirectory();
        var paths = RuntimePaths.ForTesting(directory.Path);
        var settingsStore = new JsonSettingsStore(paths.SettingsFile);
        await settingsStore.SaveAsync(GuardSettings.Default, CancellationToken.None);
        var resetAt = DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds();
        var fiveHour = new WindowReading(
            42.4,
            57.6,
            300,
            300,
            resetAt);
        await using var monitor = new GuardMonitorService(
            settingsStore,
            new ImmediateCycleExecutor(fiveHour: fiveHour),
            GuardSettings.Default);
        var viewModel = new MainWindowViewModel(
            settingsStore,
            new StartupService(new MemoryRegistryStore()),
            monitor,
            GuardSettings.Default);

        await monitor.RefreshAsync();

        Assert.AreEqual("58%", viewModel.FiveHourRemainingText);
        Assert.AreEqual(57.6, viewModel.FiveHourRemainingPercent);
        StringAssert.Contains(
            viewModel.FiveHourResetStatus,
            "다음 갱신 예정");
        Assert.AreEqual(
            resetAt,
            monitor.CurrentSnapshot.FiveHour?.ResetsAt);
    }

    private static GuardCycleResult CreateResult(
        WindowReading? weekly = null,
        WindowReading? fiveHour = null,
        int availableCreditCount = 0)
    {
        var observedAt = DateTimeOffset.UtcNow;
        var rateLimits = new AccountRateLimits(
            new RateLimitSnapshot("codex", "Codex", null, null),
            null,
            new ResetCreditSummary(availableCreditCount, []),
            observedAt);
        var evaluation = new EvaluationResult(
            weekly,
            new GuardDecision(
                DecisionKind.NoAction,
                DecisionReason.AboveThreshold,
                null,
                null,
                null),
            availableCreditCount)
        {
            FiveHour = fiveHour,
        };
        return new GuardCycleResult(
            rateLimits,
            evaluation,
            CycleActionKind.None,
            "no_action");
    }

    private sealed class ImmediateCycleExecutor : IGuardCycleExecutor
    {
        private readonly WindowReading? weekly;
        private readonly WindowReading? fiveHour;
        private readonly int availableCreditCount;

        public ImmediateCycleExecutor(
            WindowReading? weekly = null,
            WindowReading? fiveHour = null,
            int availableCreditCount = 0)
        {
            this.weekly = weekly;
            this.fiveHour = fiveHour;
            this.availableCreditCount = availableCreditCount;
        }

        public int CallCount { get; private set; }

        public Task<GuardCycleResult> ExecuteAsync(
            GuardSettings settings,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(CreateResult(
                weekly,
                fiveHour,
                availableCreditCount));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingRefreshCycleExecutor : IGuardCycleExecutor
    {
        private readonly WindowReading weekly;
        private readonly WindowReading fiveHour;
        private readonly int availableCreditCount;
        private int callCount;

        public BlockingRefreshCycleExecutor(
            WindowReading weekly,
            WindowReading fiveHour,
            int availableCreditCount)
        {
            this.weekly = weekly;
            this.fiveHour = fiveHour;
            this.availableCreditCount = availableCreditCount;
        }

        public TaskCompletionSource RefreshEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseRefresh { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref callCount);

        public async Task<GuardCycleResult> ExecuteAsync(
            GuardSettings settings,
            CancellationToken cancellationToken)
        {
            var currentCall = Interlocked.Increment(ref callCount);
            if (currentCall > 1)
            {
                RefreshEntered.TrySetResult();
                await ReleaseRefresh.Task.WaitAsync(cancellationToken);
            }

            return CreateResult(
                weekly,
                fiveHour,
                availableCreditCount);
        }

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
