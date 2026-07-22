using CodexResetGuard.AppServer;
using CodexResetGuard.Core;

namespace CodexResetGuard.Runtime;

public enum CycleActionKind
{
    None,
    Blocked,
    ResetPending,
    ResetSucceeded,
    ResetNoEffect,
}

public sealed record GuardCycleResult(
    AccountRateLimits AccountRateLimits,
    EvaluationResult Evaluation,
    CycleActionKind ActionKind,
    string ActionCode,
    bool? DuplicateSuppressed = null);

public sealed record MonitorSnapshot(
    DateTimeOffset ObservedAt,
    GuardSettings Settings,
    WindowReading? Weekly,
    long? AvailableCreditCount,
    DecisionKind? DecisionKind,
    DecisionReason? DecisionReason,
    CycleActionKind ActionKind,
    string StatusCode,
    bool? DuplicateSuppressed,
    bool IsFailure)
{
    public static MonitorSnapshot Waiting(GuardSettings settings) => new(
        DateTimeOffset.UtcNow,
        settings,
        null,
        null,
        null,
        null,
        CycleActionKind.None,
        "waiting",
        null,
        IsFailure: false);

    public static MonitorSnapshot FromResult(
        GuardSettings settings,
        GuardCycleResult result) => new(
        result.AccountRateLimits.ObservedAt,
        settings,
        result.Evaluation.Weekly,
        result.Evaluation.AvailableCreditCount,
        result.Evaluation.Decision.Kind,
        result.Evaluation.Decision.Reason,
        result.ActionKind,
        result.ActionCode,
        result.DuplicateSuppressed,
        IsFailure: false);

    public static MonitorSnapshot Failure(
        GuardSettings settings,
        string statusCode) => new(
        DateTimeOffset.UtcNow,
        settings,
        null,
        null,
        null,
        null,
        CycleActionKind.Blocked,
        statusCode,
        null,
        IsFailure: true);
}

public interface IGuardCycleExecutor : IAsyncDisposable
{
    Task<GuardCycleResult> ExecuteAsync(
        GuardSettings settings,
        CancellationToken cancellationToken);
}

public sealed class GuardMonitorService : IAsyncDisposable
{
    private readonly JsonSettingsStore settingsStore;
    private readonly IGuardCycleExecutor cycleExecutor;
    private readonly SafeJsonlLogger? logger;
    private readonly SemaphoreSlim cycleGate = new(1, 1);
    private readonly SemaphoreSlim refreshSignal = new(0, 1);
    private readonly CancellationTokenSource stopSource = new();
    private Task? monitorTask;
    private CancellationTokenRegistration startCancellationRegistration;
    private GuardSettings currentSettings;
    private MonitorSnapshot currentSnapshot;

    public GuardMonitorService(
        JsonSettingsStore settingsStore,
        IGuardCycleExecutor cycleExecutor,
        GuardSettings initialSettings,
        SafeJsonlLogger? logger = null)
    {
        this.settingsStore = settingsStore
            ?? throw new ArgumentNullException(nameof(settingsStore));
        this.cycleExecutor = cycleExecutor
            ?? throw new ArgumentNullException(nameof(cycleExecutor));
        this.logger = logger;
        JsonSettingsStore.Validate(initialSettings);
        currentSettings = initialSettings;
        currentSnapshot = MonitorSnapshot.Waiting(initialSettings);
    }

    public event EventHandler<MonitorSnapshot>? SnapshotChanged;

    public MonitorSnapshot CurrentSnapshot => Volatile.Read(ref currentSnapshot);

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (monitorTask is not null)
        {
            throw new InvalidOperationException("monitor_already_started");
        }

        if (cancellationToken.CanBeCanceled)
        {
            startCancellationRegistration = cancellationToken.Register(stopSource.Cancel);
        }

        monitorTask = RunLoopAsync(stopSource.Token);
        RequestRefresh();
        return Task.CompletedTask;
    }

    public void RequestRefresh()
    {
        if (stopSource.IsCancellationRequested)
        {
            return;
        }

        if (refreshSignal.CurrentCount == 0)
        {
            try
            {
                refreshSignal.Release();
            }
            catch (SemaphoreFullException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await RunCycleSafelyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveSettingsAsync(
        SettingsUpdateService settingsUpdateService,
        GuardSettings previousSettings,
        GuardSettings newSettings,
        string? currentExecutablePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settingsUpdateService);
        ArgumentNullException.ThrowIfNull(previousSettings);
        ArgumentNullException.ThrowIfNull(newSettings);

        await cycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                await settingsUpdateService.SaveAsync(
                    previousSettings,
                    newSettings,
                    currentExecutablePath,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (SettingsPartiallyAppliedException exception)
            {
                ApplyPersistedSettings(exception.PersistedSettings);
                RequestRefresh();
                throw;
            }

            ApplyPersistedSettings(newSettings);
        }
        finally
        {
            cycleGate.Release();
        }

        RequestRefresh();
    }

    public async Task<GuardSettings> SetStartWithWindowsAsync(
        SettingsUpdateService settingsUpdateService,
        bool enabled,
        string? currentExecutablePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settingsUpdateService);

        GuardSettings updatedSettings;
        await cycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var persistedSettings = await settingsUpdateService.LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            updatedSettings = persistedSettings with
            {
                StartWithWindows = enabled,
            };
            try
            {
                await settingsUpdateService.SaveAsync(
                    persistedSettings,
                    updatedSettings,
                    currentExecutablePath,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (SettingsPartiallyAppliedException exception)
            {
                ApplyPersistedSettings(exception.PersistedSettings);
                RequestRefresh();
                throw;
            }

            ApplyPersistedSettings(updatedSettings);
        }
        finally
        {
            cycleGate.Release();
        }

        RequestRefresh();
        return updatedSettings;
    }

    public async ValueTask DisposeAsync()
    {
        stopSource.Cancel();
        if (monitorTask is not null)
        {
            try
            {
                await monitorTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await cycleExecutor.DisposeAsync().ConfigureAwait(false);
        startCancellationRegistration.Dispose();
        stopSource.Dispose();
        cycleGate.Dispose();
        refreshSignal.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromMinutes(
                Volatile.Read(ref currentSettings).PollIntervalMinutes);
            try
            {
                await refreshSignal.WaitAsync(interval, cancellationToken)
                    .ConfigureAwait(false);
                await RunCycleSafelyAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RunCycleSafelyAsync(CancellationToken cancellationToken)
    {
        await cycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GuardSettings settings;
            try
            {
                settings = await settingsStore.LoadAsync(cancellationToken)
                    .ConfigureAwait(false);
                Volatile.Write(ref currentSettings, settings);
            }
            catch (SettingsException exception)
            {
                await TryLogSettingsFailureAsync(exception.ReasonCode)
                    .ConfigureAwait(false);
                Publish(MonitorSnapshot.Failure(
                    Volatile.Read(ref currentSettings),
                    exception.ReasonCode));
                return;
            }

            try
            {
                var result = await cycleExecutor.ExecuteAsync(settings, cancellationToken)
                    .ConfigureAwait(false);
                Publish(MonitorSnapshot.FromResult(settings, result));
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (AppServerException exception)
            {
                var pendingSnapshot = TryPreserveRetryPendingSnapshot(
                    settings,
                    exception.Category);
                Publish(pendingSnapshot ?? MonitorSnapshot.Failure(
                    settings,
                    ToCode(exception.Category)));
            }
            catch (LiveStateException exception)
            {
                Publish(MonitorSnapshot.Failure(settings, exception.ReasonCode));
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or InvalidDataException)
            {
                Publish(MonitorSnapshot.Failure(settings, "local_runtime_failure"));
            }
            catch (Exception)
            {
                Publish(MonitorSnapshot.Failure(settings, "unexpected_local_failure"));
            }
        }
        finally
        {
            cycleGate.Release();
        }
    }

    private void Publish(MonitorSnapshot snapshot)
    {
        Volatile.Write(ref currentSnapshot, snapshot);
        var handlers = SnapshotChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<MonitorSnapshot> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, snapshot);
            }
            catch (Exception exception) when (exception is not (
                OutOfMemoryException or StackOverflowException or AccessViolationException))
            {
            }
        }
    }

    private void ApplyPersistedSettings(GuardSettings settings)
    {
        JsonSettingsStore.Validate(settings);
        Volatile.Write(ref currentSettings, settings);
        Publish(MonitorSnapshot.Waiting(settings));
    }

    private MonitorSnapshot? TryPreserveRetryPendingSnapshot(
        GuardSettings settings,
        AppServerFailureCategory category)
    {
        if (!IsRetryable(category))
        {
            return null;
        }

        var snapshot = CurrentSnapshot;
        if (snapshot.ActionKind != CycleActionKind.ResetPending
            || !snapshot.Settings.AutomationEnabled
            || !settings.AutomationEnabled
            || snapshot.Settings.RemainingThresholdPercent
                != settings.RemainingThresholdPercent)
        {
            return null;
        }

        return snapshot with
        {
            ObservedAt = DateTimeOffset.UtcNow,
            Settings = settings,
            IsFailure = true,
        };
    }

    private static bool IsRetryable(AppServerFailureCategory category) => category is
        AppServerFailureCategory.ExecutableNotFound
        or AppServerFailureCategory.StartFailed
        or AppServerFailureCategory.ProcessExited
        or AppServerFailureCategory.Timeout
        or AppServerFailureCategory.RemoteError
        or AppServerFailureCategory.IoError;

    private async Task TryLogSettingsFailureAsync(string reasonCode)
    {
        if (logger is null)
        {
            return;
        }

        try
        {
            await logger.WriteAsync(
                new SafeLogEvent(
                    DateTimeOffset.UtcNow,
                    "failure",
                    "blocked",
                    ToKnownSettingsCode(reasonCode),
                    ComponentCategory: "desktop_monitor"),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException)
        {
        }
    }

    private static string ToKnownSettingsCode(string reasonCode) => reasonCode switch
    {
        "settings_too_large" => "settings_too_large",
        "settings_invalid_json" => "settings_invalid_json",
        "settings_io_error" => "settings_io_error",
        "settings_access_denied" => "settings_access_denied",
        "settings_empty" => "settings_empty",
        "settings_schema_unsupported" => "settings_schema_unsupported",
        "settings_path_invalid" => "settings_path_invalid",
        "settings_path_forbidden" => "settings_path_forbidden",
        "threshold_out_of_range" => "threshold_out_of_range",
        "poll_interval_out_of_range" => "poll_interval_out_of_range",
        "trigger_limit_invalid" => "trigger_limit_invalid",
        "ui_language_invalid" => "ui_language_invalid",
        "execution_mode_invalid" => "execution_mode_invalid",
        "codex_executable_path_invalid" => "codex_executable_path_invalid",
        _ => "unexpected_local_failure",
    };

    private static string ToCode<T>(T value)
        where T : struct, Enum => string.Concat(
            value.ToString().Select((character, index) =>
                char.IsUpper(character) && index > 0
                    ? $"_{char.ToLowerInvariant(character)}"
                    : char.ToLowerInvariant(character).ToString()));
}
