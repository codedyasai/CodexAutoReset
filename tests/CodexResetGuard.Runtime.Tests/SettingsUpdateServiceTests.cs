using CodexResetGuard.Core;
using CodexResetGuard.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexResetGuard.Runtime.Tests;

[TestClass]
public sealed class SettingsUpdateServiceTests
{
    private string temporaryDirectory = null!;
    private string executablePath = null!;

    [TestInitialize]
    public void Initialize()
    {
        temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"CodexResetGuard.Runtime.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        executablePath = Path.Combine(temporaryDirectory, "CodexResetGuard.exe");
        File.WriteAllBytes(executablePath, [0]);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [TestMethod]
    public async Task SaveAsync_UnchangedStartupDoesNotTouchForeignRegistryValue()
    {
        var registry = new CountingRegistryStore();
        registry.Seed(
            StartupService.RunSubKey,
            StartupService.RunValueName,
            "foreign-command.exe");
        var saveCount = 0;
        var service = new SettingsUpdateService(
            new StartupService(registry),
            _ => Task.FromResult(GuardSettings.Default),
            (_, _) =>
            {
                saveCount++;
                return Task.CompletedTask;
            });
        var updated = GuardSettings.Default with { RemainingThresholdPercent = 12 };

        await service.SaveAsync(
            GuardSettings.Default,
            updated,
            currentExecutablePath: null,
            CancellationToken.None);

        Assert.AreEqual(1, saveCount);
        Assert.AreEqual(0, registry.WriteCount);
        Assert.AreEqual(
            "foreign-command.exe",
            registry.GetString(StartupService.RunSubKey, StartupService.RunValueName));
    }

    [TestMethod]
    public async Task SaveAsync_StartupEnableRollsBackWhenSettingsWriteFails()
    {
        var registry = new CountingRegistryStore();
        var service = new SettingsUpdateService(
            new StartupService(registry),
            _ => Task.FromResult(GuardSettings.Default),
            (_, _) => throw new SettingsException("simulated_write_failure"));
        var updated = GuardSettings.Default with { StartWithWindows = true };

        await Assert.ThrowsExceptionAsync<SettingsException>(() => service.SaveAsync(
            GuardSettings.Default,
            updated,
            executablePath,
            CancellationToken.None));

        Assert.IsNull(registry.GetString(
            StartupService.RunSubKey,
            StartupService.RunValueName));
        Assert.IsNull(registry.GetString(
            StartupService.OwnershipSubKey,
            StartupService.OwnerValueName));
    }

    [TestMethod]
    public async Task SaveAsync_StartupEnableCommitsAfterSettingsWriteSucceeds()
    {
        var registry = new CountingRegistryStore();
        var service = new SettingsUpdateService(
            new StartupService(registry),
            _ => Task.FromResult(GuardSettings.Default),
            (_, _) => Task.CompletedTask);
        var updated = GuardSettings.Default with { StartWithWindows = true };

        await service.SaveAsync(
            GuardSettings.Default,
            updated,
            executablePath,
            CancellationToken.None);

        Assert.IsNotNull(registry.GetString(
            StartupService.RunSubKey,
            StartupService.RunValueName));
        Assert.IsNotNull(registry.GetString(
            StartupService.OwnershipSubKey,
            StartupService.OwnerValueName));
    }

    [TestMethod]
    public async Task SaveAsync_PersistsSettingsBeforeChangingRegistry()
    {
        var events = new List<string>();
        var persisted = GuardSettings.Default;
        var registry = new CountingRegistryStore
        {
            OnWrite = () => events.Add("registry"),
        };
        var service = new SettingsUpdateService(
            new StartupService(registry),
            _ => Task.FromResult(persisted),
            (settings, _) =>
            {
                persisted = settings;
                events.Add("settings");
                return Task.CompletedTask;
            });
        var updated = persisted with { StartWithWindows = true };

        await service.SaveAsync(
            persisted,
            updated,
            executablePath,
            CancellationToken.None);

        Assert.AreEqual("settings", events[0]);
        Assert.AreEqual(updated, persisted);
        Assert.AreEqual(StartupStatus.Enabled, new StartupService(registry).GetState().Status);
    }

    [TestMethod]
    public async Task SaveAsync_DisabledRegistryFailureKeepsPersistedDisabledState()
    {
        var persisted = GuardSettings.Default;
        var savedValues = new List<GuardSettings>();
        var registry = new CountingRegistryStore
        {
            ThrowOnWrite = true,
        };
        var service = new SettingsUpdateService(
            new StartupService(registry),
            _ => Task.FromResult(persisted),
            (settings, _) =>
            {
                persisted = settings;
                savedValues.Add(settings);
                return Task.CompletedTask;
            });
        var updated = persisted with { StartWithWindows = true };

        var exception = await Assert.ThrowsExceptionAsync<SettingsPartiallyAppliedException>(
            () => service.SaveAsync(
            GuardSettings.Default,
            updated,
            executablePath,
            CancellationToken.None));

        CollectionAssert.AreEqual(
            new[] { updated },
            savedValues);
        Assert.AreEqual(updated, persisted);
        Assert.IsFalse(persisted.AutomationEnabled);
        Assert.AreEqual(updated, exception.PersistedSettings);
    }

    [TestMethod]
    public async Task SaveAsync_EnabledTargetChangesRegistryFirstAndRollsItBackOnFileFailure()
    {
        var events = new List<string>();
        var registry = new CountingRegistryStore
        {
            OnWrite = () => events.Add("registry"),
        };
        var target = GuardSettings.Default with
        {
            StartWithWindows = true,
            AutomationEnabled = true,
        };
        var service = new SettingsUpdateService(
            new StartupService(registry),
            _ => Task.FromResult(GuardSettings.Default),
            (_, _) =>
            {
                events.Add("settings");
                throw new SettingsException("simulated_write_failure");
            });

        await Assert.ThrowsExceptionAsync<SettingsException>(() => service.SaveAsync(
            GuardSettings.Default,
            target,
            executablePath,
            CancellationToken.None));

        Assert.AreEqual("registry", events[0]);
        CollectionAssert.Contains(events, "settings");
        Assert.AreEqual(StartupStatus.Disabled, new StartupService(registry).GetState().Status);
    }

    [TestMethod]
    public async Task SaveAsync_SameEnabledSettingRepairsMissingOwnedRegistration()
    {
        var enabledSettings = GuardSettings.Default with
        {
            StartWithWindows = true,
            AutomationEnabled = true,
        };
        var registry = new CountingRegistryStore();
        var service = new SettingsUpdateService(
            new StartupService(registry),
            _ => Task.FromResult(enabledSettings),
            (_, _) => Task.CompletedTask);

        await service.SaveAsync(
            enabledSettings,
            enabledSettings,
            executablePath,
            CancellationToken.None);

        Assert.AreEqual(StartupStatus.Enabled, new StartupService(registry).GetState().Status);
    }

    [TestMethod]
    public async Task SaveAsync_EnabledSettingRefusesForeignRegistrationBeforeFileWrite()
    {
        var enabledSettings = GuardSettings.Default with { StartWithWindows = true };
        var registry = new CountingRegistryStore();
        registry.Seed(
            StartupService.RunSubKey,
            StartupService.RunValueName,
            "foreign-command.exe");
        var saveCount = 0;
        var service = new SettingsUpdateService(
            new StartupService(registry),
            _ => Task.FromResult(enabledSettings),
            (_, _) =>
            {
                saveCount++;
                return Task.CompletedTask;
            });

        var exception = await Assert.ThrowsExceptionAsync<StartupException>(() =>
            service.SaveAsync(
                enabledSettings,
                enabledSettings,
                executablePath,
                CancellationToken.None));

        Assert.AreEqual("startup_foreign_value", exception.ReasonCode);
        Assert.AreEqual(0, saveCount);
        Assert.AreEqual(0, registry.WriteCount);
        Assert.AreEqual(
            "foreign-command.exe",
            registry.GetString(StartupService.RunSubKey, StartupService.RunValueName));
    }

    [TestMethod]
    public async Task SaveAsync_DisabledEnableRejectsMissingExecutableBeforeFileWrite()
    {
        var enabledSettings = GuardSettings.Default with { StartWithWindows = true };
        var saveCount = 0;
        var service = new SettingsUpdateService(
            new StartupService(new CountingRegistryStore()),
            _ => Task.FromResult(GuardSettings.Default),
            (_, _) =>
            {
                saveCount++;
                return Task.CompletedTask;
            });
        var missingExecutable = Path.Combine(
            temporaryDirectory,
            "missing",
            "CodexResetGuard.exe");

        var exception = await Assert.ThrowsExceptionAsync<StartupException>(() =>
            service.SaveAsync(
                GuardSettings.Default,
                enabledSettings,
                missingExecutable,
                CancellationToken.None));

        Assert.AreEqual("startup_executable_missing", exception.ReasonCode);
        Assert.AreEqual(0, saveCount);
    }

    [TestMethod]
    public async Task SaveAsync_ExternalDisableBlocksStaleEnabledOverwrite()
    {
        var registry = new CountingRegistryStore();
        var previous = GuardSettings.Default with
        {
            AutomationEnabled = true,
        };
        var current = GuardSettings.Default;
        var proposed = previous with { RemainingThresholdPercent = 12 };
        var saveCount = 0;
        var service = new SettingsUpdateService(
            new StartupService(registry),
            _ => Task.FromResult(current),
            (_, _) =>
            {
                saveCount++;
                return Task.CompletedTask;
            });

        var exception = await Assert.ThrowsExceptionAsync<SettingsConflictException>(
            () => service.SaveAsync(
                previous,
                proposed,
                executablePath,
                CancellationToken.None));

        Assert.IsFalse(exception.CurrentSettings.AutomationEnabled);
        Assert.AreEqual(0, saveCount);
        Assert.AreEqual(0, registry.WriteCount);
    }

    private sealed class CountingRegistryStore : ICurrentUserRegistryStore
    {
        private readonly Dictionary<(string Key, string Name), string> values = new();

        public int WriteCount { get; private set; }

        public Action? OnWrite { get; init; }

        public bool ThrowOnWrite { get; init; }

        public string? GetString(string subKey, string valueName) =>
            values.GetValueOrDefault((subKey, valueName));

        public CurrentUserRegistryValue ReadValue(string subKey, string valueName) =>
            values.TryGetValue((subKey, valueName), out var value)
                ? CurrentUserRegistryValue.FromString(value)
                : CurrentUserRegistryValue.Missing;

        public void SetString(string subKey, string valueName, string value)
        {
            WriteCount++;
            OnWrite?.Invoke();
            if (ThrowOnWrite)
            {
                throw new IOException("simulated_registry_failure");
            }

            values[(subKey, valueName)] = value;
        }

        public void DeleteValue(string subKey, string valueName)
        {
            WriteCount++;
            OnWrite?.Invoke();
            if (ThrowOnWrite)
            {
                throw new IOException("simulated_registry_failure");
            }

            values.Remove((subKey, valueName));
        }

        public void Seed(string subKey, string valueName, string value) =>
            values[(subKey, valueName)] = value;
    }
}
