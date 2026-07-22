using CodexResetGuard.Core;

namespace CodexResetGuard.Runtime;

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

        if (newSettings.AutomationEnabled)
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
