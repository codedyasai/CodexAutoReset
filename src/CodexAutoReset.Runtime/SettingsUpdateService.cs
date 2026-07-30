using CodexAutoReset.Core;

namespace CodexAutoReset.Runtime;

public sealed class SettingsUpdateService
{
    private readonly StartupService startupService;
    private readonly Func<CancellationToken, Task<GuardSettings>> loadSettingsAsync;
    private readonly Func<GuardSettings, CancellationToken, Task> saveSettingsAsync;

    public SettingsUpdateService(
        JsonSettingsStore settingsStore,
        StartupService startupService)
        : this(
            startupService,
            cancellationToken => settingsStore.LoadAsync(cancellationToken),
            (settings, cancellationToken) =>
                settingsStore.SaveAsync(settings, cancellationToken))
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
    }

    internal SettingsUpdateService(
        StartupService startupService,
        Func<CancellationToken, Task<GuardSettings>> loadSettingsAsync,
        Func<GuardSettings, CancellationToken, Task> saveSettingsAsync)
    {
        this.startupService = startupService
            ?? throw new ArgumentNullException(nameof(startupService));
        this.loadSettingsAsync = loadSettingsAsync
            ?? throw new ArgumentNullException(nameof(loadSettingsAsync));
        this.saveSettingsAsync = saveSettingsAsync
            ?? throw new ArgumentNullException(nameof(saveSettingsAsync));
    }

    public Task<GuardSettings> LoadAsync(CancellationToken cancellationToken = default) =>
        loadSettingsAsync(cancellationToken);

    public Task<GuardSettings> SaveAutomationEnabledAsync(
        bool automationEnabled,
        CancellationToken cancellationToken) =>
        SaveAutomationEnabledAsync(
            TriggerLimit.Weekly,
            automationEnabled,
            cancellationToken);

    public Task<GuardSettings> SaveAutomationEnabledAsync(
        TriggerLimit triggerLimit,
        bool automationEnabled,
        CancellationToken cancellationToken) =>
        SaveSettingsPatchAsync(
            settings => triggerLimit switch
            {
                TriggerLimit.Weekly => settings with
                {
                    RemainingThresholdPercent =
                        automationEnabled
                            ? settings.RemainingThresholdPercent
                                ?? GuardSettings.MinimumThreshold
                            : settings.RemainingThresholdPercent,
                    AutomationEnabled = automationEnabled,
                },
                TriggerLimit.FiveHour => settings with
                {
                    FiveHourRemainingThresholdPercent =
                        automationEnabled
                            ? settings.FiveHourRemainingThresholdPercent
                                ?? GuardSettings.MinimumThreshold
                            : settings.FiveHourRemainingThresholdPercent,
                    FiveHourAutomationEnabled = automationEnabled,
                },
                _ => throw new ArgumentOutOfRangeException(nameof(triggerLimit)),
            },
            cancellationToken);

    public Task<GuardSettings> SaveNotifyOnUsageResetAsync(
        bool notifyOnUsageReset,
        CancellationToken cancellationToken) =>
        SaveSettingsPatchAsync(
            settings => settings with
            {
                NotifyOnUsageReset = notifyOnUsageReset,
            },
            cancellationToken);

    public Task<GuardSettings> SaveRemainingThresholdPercentAsync(
        int remainingThresholdPercent,
        CancellationToken cancellationToken) =>
        SaveRemainingThresholdPercentAsync(
            TriggerLimit.Weekly,
            remainingThresholdPercent,
            cancellationToken);

    public Task<GuardSettings> SaveRemainingThresholdPercentAsync(
        TriggerLimit triggerLimit,
        int? remainingThresholdPercent,
        CancellationToken cancellationToken) =>
        SaveSettingsPatchAsync(
            settings => triggerLimit switch
            {
                TriggerLimit.Weekly => settings with
                {
                    RemainingThresholdPercent = remainingThresholdPercent,
                    AutomationEnabled =
                        remainingThresholdPercent.HasValue
                        && settings.AutomationEnabled,
                },
                TriggerLimit.FiveHour => settings with
                {
                    FiveHourRemainingThresholdPercent =
                        remainingThresholdPercent,
                    FiveHourAutomationEnabled =
                        remainingThresholdPercent.HasValue
                        && settings.FiveHourAutomationEnabled,
                },
                _ => throw new ArgumentOutOfRangeException(nameof(triggerLimit)),
            },
            cancellationToken);

    public Task<GuardSettings> SavePollIntervalMinutesAsync(
        int pollIntervalMinutes,
        CancellationToken cancellationToken) =>
        SaveSettingsPatchAsync(
            settings => settings with
            {
                PollIntervalMinutes = pollIntervalMinutes,
            },
            cancellationToken);

    public Task<GuardSettings> SaveCodexExecutablePathAsync(
        string? codexExecutablePath,
        CancellationToken cancellationToken) =>
        SaveSettingsPatchAsync(
            settings => settings with
            {
                CodexExecutablePath = codexExecutablePath,
            },
            cancellationToken);

    public async Task<GuardSettings> SaveCodexExecutablePathAsync(
        GuardSettings previousSettings,
        string? codexExecutablePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(previousSettings);
        JsonSettingsStore.Validate(previousSettings);

        var currentSettings = await loadSettingsAsync(cancellationToken)
            .ConfigureAwait(false);
        JsonSettingsStore.Validate(currentSettings);
        if (currentSettings != previousSettings)
        {
            throw new SettingsConflictException(currentSettings);
        }

        var updatedSettings = currentSettings with
        {
            CodexExecutablePath = codexExecutablePath,
        };
        JsonSettingsStore.Validate(updatedSettings);
        await saveSettingsAsync(updatedSettings, cancellationToken).ConfigureAwait(false);
        return updatedSettings;
    }

    private async Task<GuardSettings> SaveSettingsPatchAsync(
        Func<GuardSettings, GuardSettings> applyPatch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(applyPatch);

        var currentSettings = await loadSettingsAsync(cancellationToken)
            .ConfigureAwait(false);
        JsonSettingsStore.Validate(currentSettings);
        var updatedSettings = applyPatch(currentSettings);
        JsonSettingsStore.Validate(updatedSettings);
        if (updatedSettings == currentSettings)
        {
            return currentSettings;
        }

        await saveSettingsAsync(updatedSettings, cancellationToken).ConfigureAwait(false);
        return updatedSettings;
    }

    public async Task SaveAsync(
        GuardSettings previousSettings,
        GuardSettings newSettings,
        string? currentExecutablePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(previousSettings);
        ArgumentNullException.ThrowIfNull(newSettings);
        JsonSettingsStore.Validate(previousSettings);
        JsonSettingsStore.Validate(newSettings);

        var currentSettings = await loadSettingsAsync(cancellationToken)
            .ConfigureAwait(false);
        JsonSettingsStore.Validate(currentSettings);
        if (currentSettings != previousSettings)
        {
            throw new SettingsConflictException(currentSettings);
        }

        var startupState = startupService.GetState();
        var changeStartup = RequiresStartupChange(
            newSettings.StartWithWindows,
            startupState,
            currentExecutablePath);
        if (!changeStartup)
        {
            await saveSettingsAsync(newSettings, cancellationToken).ConfigureAwait(false);
            return;
        }

        startupService.ValidateChange(
            newSettings.StartWithWindows,
            currentExecutablePath);

        if (newSettings.AnyAutomationEnabled)
        {
            using var mutation = startupService.BeginChange(
                newSettings.StartWithWindows,
                GetExecutablePath(newSettings.StartWithWindows, currentExecutablePath));
            await saveSettingsAsync(newSettings, cancellationToken).ConfigureAwait(false);
            mutation.Commit();
            return;
        }

        await saveSettingsAsync(newSettings, cancellationToken).ConfigureAwait(false);
        try
        {
            using var mutation = startupService.BeginChange(
                newSettings.StartWithWindows,
                GetExecutablePath(newSettings.StartWithWindows, currentExecutablePath));
            mutation.Commit();
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            throw new SettingsPartiallyAppliedException(newSettings);
        }
    }

    private static bool RequiresStartupChange(
        bool desiredEnabled,
        StartupState startupState,
        string? currentExecutablePath)
    {
        if (!desiredEnabled)
        {
            return startupState.Status is StartupStatus.Enabled
                or StartupStatus.InvalidOwnedValue;
        }

        return startupState.Status != StartupStatus.Enabled
            || currentExecutablePath is not null
                && !string.Equals(
                    startupState.ExecutablePath,
                    currentExecutablePath,
                    StringComparison.OrdinalIgnoreCase);
    }

    private static string GetExecutablePath(
        bool enable,
        string? currentExecutablePath) => enable
        ? currentExecutablePath
            ?? throw new StartupException("startup_executable_unavailable")
        : string.Empty;
}

public sealed class SettingsConflictException : Exception
{
    public SettingsConflictException(GuardSettings currentSettings)
        : base("settings_changed_externally")
    {
        CurrentSettings = currentSettings
            ?? throw new ArgumentNullException(nameof(currentSettings));
    }

    public GuardSettings CurrentSettings { get; }
}

public sealed class SettingsPartiallyAppliedException : Exception
{
    public SettingsPartiallyAppliedException(GuardSettings persistedSettings)
        : base("startup_change_failed_after_automation_disabled_persisted")
    {
        PersistedSettings = persistedSettings
            ?? throw new ArgumentNullException(nameof(persistedSettings));
    }

    public string ReasonCode =>
        "startup_change_failed_after_automation_disabled_persisted";

    public GuardSettings PersistedSettings { get; }
}
