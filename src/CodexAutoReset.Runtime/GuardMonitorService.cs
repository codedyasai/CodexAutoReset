using CodexAutoReset.AppServer;
using CodexAutoReset.Core;

namespace CodexAutoReset.Runtime;

public enum CycleActionKind
{
    None,
    Blocked,
    ResetPending,
    ResetSucceeded,
    ResetNoEffect,
}

public enum CodexCompatibilityState
{
    Unknown,
    Compatible,
    VerificationPending,
    ReadUnsupported,
    MutationUnverified,
}

public sealed record GuardCycleResult(
    AccountRateLimits AccountRateLimits,
    EvaluationResult Evaluation,
    CycleActionKind ActionKind,
    string ActionCode,
    bool? DuplicateSuppressed = null,
    WeeklyUsageResetDetection? UsageResetDetection = null);

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
    bool IsFailure,
    WeeklyUsageResetDetection? UsageResetDetection = null,
    CodexCompatibilityState CompatibilityState = CodexCompatibilityState.Unknown,
    DateTimeOffset? LastSuccessfulObservationAt = null)
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
        IsFailure: false,
        result.UsageResetDetection,
        CodexCompatibilityState.Compatible,
        result.AccountRateLimits.ObservedAt);

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
    private static readonly TimeSpan DefaultCompatibilityVerificationDelay =
        TimeSpan.FromSeconds(10);

    private readonly JsonSettingsStore settingsStore;
    private readonly IGuardCycleExecutor cycleExecutor;
    private readonly SafeJsonlLogger? logger;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan compatibilityVerificationDelay;
    private readonly SemaphoreSlim cycleGate = new(1, 1);
    private readonly SemaphoreSlim refreshSignal = new(0, 1);
    private readonly CancellationTokenSource stopSource = new();
    private Task? monitorTask;
    private Task? compatibilityVerificationTask;
    private CancellationTokenSource? compatibilityVerificationSource;
    private CancellationTokenRegistration startCancellationRegistration;
    private GuardSettings currentSettings;
    private MonitorSnapshot currentSnapshot;
    private string? compatibilityCandidateCode;
    private DateTimeOffset? compatibilityCandidateEligibleAt;
    private long compatibilityVerificationRevision;
    private DateTimeOffset? lastSuccessfulObservationAt;

    public GuardMonitorService(
        JsonSettingsStore settingsStore,
        IGuardCycleExecutor cycleExecutor,
        GuardSettings initialSettings,
        SafeJsonlLogger? logger = null,
        TimeProvider? timeProvider = null,
        TimeSpan? compatibilityVerificationDelay = null)
    {
        this.settingsStore = settingsStore
            ?? throw new ArgumentNullException(nameof(settingsStore));
        this.cycleExecutor = cycleExecutor
            ?? throw new ArgumentNullException(nameof(cycleExecutor));
        this.logger = logger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.compatibilityVerificationDelay = compatibilityVerificationDelay
            ?? DefaultCompatibilityVerificationDelay;
        if (this.compatibilityVerificationDelay <= TimeSpan.Zero
            || this.compatibilityVerificationDelay > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(compatibilityVerificationDelay));
        }

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

    public Task<GuardSettings> SetAutomationEnabledAsync(
        SettingsUpdateService settingsUpdateService,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settingsUpdateService);
        return ApplySettingsPatchAsync(
            token => settingsUpdateService.SaveAutomationEnabledAsync(enabled, token),
            requestRefresh: true,
            cancellationToken);
    }

    public Task<GuardSettings> SetNotifyOnUsageResetAsync(
        SettingsUpdateService settingsUpdateService,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settingsUpdateService);
        return ApplySettingsPatchAsync(
            token => settingsUpdateService.SaveNotifyOnUsageResetAsync(enabled, token),
            requestRefresh: false,
            cancellationToken);
    }

    public Task<GuardSettings> SetRemainingThresholdPercentAsync(
        SettingsUpdateService settingsUpdateService,
        int remainingThresholdPercent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settingsUpdateService);
        return ApplySettingsPatchAsync(
            token => settingsUpdateService.SaveRemainingThresholdPercentAsync(
                remainingThresholdPercent,
                token),
            requestRefresh: true,
            cancellationToken);
    }

    public Task<GuardSettings> SetPollIntervalMinutesAsync(
        SettingsUpdateService settingsUpdateService,
        int pollIntervalMinutes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settingsUpdateService);
        return ApplySettingsPatchAsync(
            token => settingsUpdateService.SavePollIntervalMinutesAsync(
                pollIntervalMinutes,
                token),
            requestRefresh: true,
            cancellationToken);
    }

    public Task<GuardSettings> SetCodexExecutablePathAsync(
        SettingsUpdateService settingsUpdateService,
        string? codexExecutablePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settingsUpdateService);
        return ApplySettingsPatchAsync(
            token => settingsUpdateService.SaveCodexExecutablePathAsync(
                codexExecutablePath,
                token),
            requestRefresh: false,
            cancellationToken);
    }

    public async Task<GuardSettings> SetCodexExecutablePathAsync(
        SettingsUpdateService settingsUpdateService,
        GuardSettings previousSettings,
        string? codexExecutablePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settingsUpdateService);
        ArgumentNullException.ThrowIfNull(previousSettings);

        GuardSettings updatedSettings;
        await cycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            updatedSettings = await settingsUpdateService.SaveCodexExecutablePathAsync(
                previousSettings,
                codexExecutablePath,
                cancellationToken).ConfigureAwait(false);
            ApplyPersistedSettings(
                updatedSettings,
                preserveCurrentObservation: true);
        }
        finally
        {
            cycleGate.Release();
        }

        return updatedSettings;
    }

    private async Task<GuardSettings> ApplySettingsPatchAsync(
        Func<CancellationToken, Task<GuardSettings>> savePatchAsync,
        bool requestRefresh,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(savePatchAsync);

        GuardSettings updatedSettings;
        await cycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            updatedSettings = await savePatchAsync(cancellationToken)
                .ConfigureAwait(false);
            ApplyPersistedSettings(
                updatedSettings,
                preserveCurrentObservation: !requestRefresh);
        }
        finally
        {
            cycleGate.Release();
        }

        if (requestRefresh)
        {
            RequestRefresh();
        }

        return updatedSettings;
    }

    public async ValueTask DisposeAsync()
    {
        stopSource.Cancel();
        compatibilityVerificationSource?.Cancel();
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

        if (compatibilityVerificationTask is not null)
        {
            try
            {
                await compatibilityVerificationTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await cycleExecutor.DisposeAsync().ConfigureAwait(false);
        compatibilityVerificationSource?.Dispose();
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
                if (!HasSameCodexExecutablePath(currentSettings, settings))
                {
                    ResetCompatibilityCandidate();
                }

                Volatile.Write(ref currentSettings, settings);
            }
            catch (SettingsException exception)
            {
                ResetCompatibilityCandidate();
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
                Publish(CreateSnapshotFromSuccessfulResult(settings, result));
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (AppServerException exception)
            {
                if (IsProtocolCompatibilityFailure(exception))
                {
                    Publish(exception.Operation == AppServerOperation.Mutation
                        ? CreateMutationCompatibilityFailure(settings)
                        : CreateReadCompatibilityFailure(
                            settings,
                            CreateCompatibilitySignalCode(exception)));
                    return;
                }

                ResetCompatibilityCandidate();
                var pendingSnapshot = TryPreserveRetryPendingSnapshot(
                    settings,
                    exception);
                Publish(pendingSnapshot ?? MonitorSnapshot.Failure(
                    settings,
                    ToCode(exception.Category)) with
                {
                    LastSuccessfulObservationAt = lastSuccessfulObservationAt,
                });
            }
            catch (LiveStateException exception)
            {
                ResetCompatibilityCandidate();
                Publish(MonitorSnapshot.Failure(settings, exception.ReasonCode));
            }
            catch (Exception exception) when (
                exception is IOException
                    or UnauthorizedAccessException
                    or InvalidDataException)
            {
                ResetCompatibilityCandidate();
                Publish(MonitorSnapshot.Failure(settings, "local_runtime_failure"));
            }
            catch (Exception)
            {
                ResetCompatibilityCandidate();
                Publish(MonitorSnapshot.Failure(settings, "unexpected_local_failure"));
            }
        }
        finally
        {
            cycleGate.Release();
        }
    }

    private MonitorSnapshot CreateSnapshotFromSuccessfulResult(
        GuardSettings settings,
        GuardCycleResult result)
    {
        if (!result.AccountRateLimits.ConsumeSchemaCompatible
            || string.Equals(
                result.ActionCode,
                "mutation_schema_unverified",
                StringComparison.Ordinal)
            || string.Equals(
                result.ActionCode,
                "live_protocol_blocked",
                StringComparison.Ordinal))
        {
            ResetCompatibilityCandidate();
            lastSuccessfulObservationAt = result.AccountRateLimits.ObservedAt;
            return CreateMutationCompatibilitySnapshot(settings, result);
        }

        if (string.Equals(
            result.ActionCode,
            "protocol_read_unsupported",
            StringComparison.Ordinal))
        {
            var semanticCode =
                GetSemanticCompatibilityCode(
                    result.Evaluation.Decision.Reason)
                ?? result.ActionCode;
            CancelCompatibilityVerification();
            compatibilityCandidateCode =
                CreateCompatibilityCandidateCode(semanticCode, settings);
            compatibilityCandidateEligibleAt = timeProvider.GetUtcNow();
            return CreateConfirmedReadCompatibilitySnapshot(settings);
        }

        if (string.Equals(
            result.ActionCode,
            "protocol_verification_pending",
            StringComparison.Ordinal))
        {
            var semanticCode =
                GetSemanticCompatibilityCode(
                    result.Evaluation.Decision.Reason)
                ?? result.ActionCode;
            return CreateReadCompatibilityFailure(settings, semanticCode);
        }

        var semanticCompatibilityCode =
            GetSemanticCompatibilityCode(result.Evaluation.Decision.Reason);
        if (semanticCompatibilityCode is not null)
        {
            return CreateReadCompatibilityFailure(
                settings,
                semanticCompatibilityCode);
        }

        ResetCompatibilityCandidate();
        lastSuccessfulObservationAt = result.AccountRateLimits.ObservedAt;
        return MonitorSnapshot.FromResult(settings, result) with
        {
            CompatibilityState = CodexCompatibilityState.Compatible,
            LastSuccessfulObservationAt = lastSuccessfulObservationAt,
        };
    }

    private MonitorSnapshot CreateMutationCompatibilitySnapshot(
        GuardSettings settings,
        GuardCycleResult result) =>
        MonitorSnapshot.FromResult(settings, result) with
        {
            ActionKind = CycleActionKind.Blocked,
            StatusCode = "mutation_schema_unverified",
            CompatibilityState = CodexCompatibilityState.MutationUnverified,
            LastSuccessfulObservationAt = lastSuccessfulObservationAt,
        };

    private MonitorSnapshot CreateMutationCompatibilityFailure(
        GuardSettings settings)
    {
        ResetCompatibilityCandidate();
        var snapshot = CurrentSnapshot;
        if (snapshot.Weekly is not null)
        {
            return snapshot with
            {
                ObservedAt = timeProvider.GetUtcNow(),
                Settings = settings,
                ActionKind = CycleActionKind.Blocked,
                StatusCode = "mutation_schema_unverified",
                IsFailure = true,
                UsageResetDetection = null,
                CompatibilityState =
                    CodexCompatibilityState.MutationUnverified,
                LastSuccessfulObservationAt =
                    lastSuccessfulObservationAt,
            };
        }

        return MonitorSnapshot.Failure(
            settings,
            "mutation_schema_unverified") with
        {
            ObservedAt = timeProvider.GetUtcNow(),
            CompatibilityState = CodexCompatibilityState.MutationUnverified,
            LastSuccessfulObservationAt = lastSuccessfulObservationAt,
        };
    }

    private MonitorSnapshot CreateReadCompatibilityFailure(
        GuardSettings settings,
        string signalCode)
    {
        var confirmed = RegisterCompatibilityCandidate(
            CreateCompatibilityCandidateCode(signalCode, settings));

        return confirmed
            ? CreateConfirmedReadCompatibilitySnapshot(settings)
            : CreatePendingReadCompatibilitySnapshot(settings);
    }

    private MonitorSnapshot CreatePendingReadCompatibilitySnapshot(
        GuardSettings settings) =>
        MonitorSnapshot.Failure(
            settings,
            "protocol_verification_pending") with
        {
            ObservedAt = timeProvider.GetUtcNow(),
            CompatibilityState = CodexCompatibilityState.VerificationPending,
            LastSuccessfulObservationAt = lastSuccessfulObservationAt,
        };

    private MonitorSnapshot CreateConfirmedReadCompatibilitySnapshot(
        GuardSettings settings) =>
        MonitorSnapshot.Failure(
            settings,
            "protocol_read_unsupported") with
        {
            ObservedAt = timeProvider.GetUtcNow(),
            CompatibilityState = CodexCompatibilityState.ReadUnsupported,
            LastSuccessfulObservationAt = lastSuccessfulObservationAt,
        };

    private void ResetCompatibilityCandidate()
    {
        compatibilityCandidateCode = null;
        compatibilityCandidateEligibleAt = null;
        CancelCompatibilityVerification();
    }

    private void CancelCompatibilityVerification()
    {
        Interlocked.Increment(ref compatibilityVerificationRevision);
        compatibilityVerificationSource?.Cancel();
        compatibilityVerificationSource?.Dispose();
        compatibilityVerificationSource = null;
    }

    private bool RegisterCompatibilityCandidate(string candidateCode)
    {
        var now = timeProvider.GetUtcNow();
        if (string.Equals(
            compatibilityCandidateCode,
            candidateCode,
            StringComparison.Ordinal))
        {
            var confirmed = compatibilityCandidateEligibleAt is { } eligibleAt
                && now >= eligibleAt;
            if (confirmed)
            {
                CancelCompatibilityVerification();
            }
            else if (compatibilityCandidateEligibleAt is { } pendingUntil)
            {
                ScheduleCompatibilityVerification(pendingUntil);
            }

            return confirmed;
        }

        compatibilityCandidateCode = candidateCode;
        compatibilityCandidateEligibleAt =
            now.Add(compatibilityVerificationDelay);
        ScheduleCompatibilityVerification(
            compatibilityCandidateEligibleAt.Value);
        return false;
    }

    private void ScheduleCompatibilityVerification(DateTimeOffset eligibleAt)
    {
        CancelCompatibilityVerification();
        if (monitorTask is null)
        {
            return;
        }

        compatibilityVerificationSource =
            CancellationTokenSource.CreateLinkedTokenSource(stopSource.Token);
        var revision = Volatile.Read(ref compatibilityVerificationRevision);
        compatibilityVerificationTask =
            ScheduleCompatibilityVerificationAsync(
                eligibleAt,
                revision,
                compatibilityVerificationSource.Token);
    }

    private async Task ScheduleCompatibilityVerificationAsync(
        DateTimeOffset eligibleAt,
        long revision,
        CancellationToken cancellationToken)
    {
        try
        {
            var delay = eligibleAt - timeProvider.GetUtcNow();
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(
                    delay,
                    timeProvider,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested
            || revision != Volatile.Read(
                ref compatibilityVerificationRevision))
        {
            return;
        }

        RequestRefresh();
    }

    private static string CreateCompatibilitySignalCode(
        AppServerException exception) => string.Concat(
            ToCode(exception.Category),
            ":",
            exception.RemoteCode?.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
                ?? "none");

    private static string CreateCompatibilityCandidateCode(
        string signalCode,
        GuardSettings settings) =>
        string.Concat(
            signalCode,
            "|",
            GetCompatibilityContext(settings));

    private static string GetCompatibilityContext(GuardSettings settings) =>
        settings.CodexExecutablePath?.ToUpperInvariant() ?? "<AUTO>";

    private static string? GetSemanticCompatibilityCode(
        DecisionReason reason) => reason switch
        {
            DecisionReason.CodexBucketMissing => "codex_bucket_missing",
            DecisionReason.CodexBucketMismatch => "codex_bucket_mismatch",
            DecisionReason.AmbiguousLegacyBucket => "ambiguous_legacy_bucket",
            DecisionReason.SelectedWindowMissing => "selected_window_missing",
            DecisionReason.SelectedWindowAmbiguous => "selected_window_ambiguous",
            DecisionReason.InvalidUsedPercent => "invalid_used_percent",
            DecisionReason.InvalidResetTime => "invalid_reset_time",
            DecisionReason.InvalidCreditCount => "invalid_credit_count",
            _ => null,
        };

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

    private void ApplyPersistedSettings(
        GuardSettings settings,
        bool preserveCurrentObservation = false)
    {
        JsonSettingsStore.Validate(settings);
        if (!HasSameCodexExecutablePath(currentSettings, settings))
        {
            ResetCompatibilityCandidate();
        }

        Volatile.Write(ref currentSettings, settings);
        Publish(preserveCurrentObservation
            ? CurrentSnapshot with
            {
                Settings = settings,
                UsageResetDetection = null,
            }
            : MonitorSnapshot.Waiting(settings));
    }

    private static bool HasSameCodexExecutablePath(
        GuardSettings first,
        GuardSettings second) => string.Equals(
            first.CodexExecutablePath,
            second.CodexExecutablePath,
            StringComparison.OrdinalIgnoreCase);

    private MonitorSnapshot? TryPreserveRetryPendingSnapshot(
        GuardSettings settings,
        AppServerException exception)
    {
        if (!IsRetryable(exception))
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
            UsageResetDetection = null,
        };
    }

    private static bool IsProtocolCompatibilityFailure(
        AppServerException exception) =>
        exception.Category == AppServerFailureCategory.InvalidResponse
        || (exception.Category == AppServerFailureCategory.RemoteError
            && exception.RemoteCode is -32601 or -32602);

    private static bool IsRetryable(AppServerException exception) =>
        !IsProtocolCompatibilityFailure(exception)
        && exception.Category is
        AppServerFailureCategory.ExecutableNotFound
        or AppServerFailureCategory.ExecutableBecameUnavailable
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
