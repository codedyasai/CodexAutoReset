using CodexAutoReset.AppServer;
using CodexAutoReset.Core;

namespace CodexAutoReset.Runtime;

public sealed class GuardCycleExecutor : IGuardCycleExecutor
{
    private static readonly TimeSpan CompatibilityVerificationDelay =
        TimeSpan.FromSeconds(10);
    private static readonly string CompatibilityRevision =
        string.Concat(
            typeof(GuardCycleExecutor).Assembly.GetName().Version?.ToString(3)
                ?? "0.0.0",
            "|",
            AppServerProtocolParser.AuditedConsumeSchemaVersion);

    private readonly IRateLimitClientFactory clientFactory;
    private readonly ISecretProtector secretProtector;
    private readonly ILiveResetFailureClassifier failureClassifier;
    private readonly TimeProvider timeProvider;
    private readonly ResetDecisionEngine decisionEngine = new();
    private readonly JsonLiveAttemptStore liveStore;
    private readonly JsonWeeklyUsageResetTracker weeklyUsageResetTracker;
    private readonly SafeJsonlLogger logger;
    private readonly LiveResetSafetyLatch liveSafetyLatch;
    private string? readCompatibilityCandidateCode;
    private DateTimeOffset? readCompatibilityEligibleAt;

    public GuardCycleExecutor(RuntimePaths paths)
        : this(
            paths,
            new CodexRateLimitClientFactory(),
            new DpapiSecretProtector(),
            AppServerLiveResetFailureClassifier.Instance,
            TimeProvider.System)
    {
    }

    internal GuardCycleExecutor(
        RuntimePaths paths,
        IRateLimitClientFactory clientFactory,
        ISecretProtector secretProtector,
        ILiveResetFailureClassifier failureClassifier,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(paths);
        this.clientFactory = clientFactory
            ?? throw new ArgumentNullException(nameof(clientFactory));
        this.secretProtector = secretProtector
            ?? throw new ArgumentNullException(nameof(secretProtector));
        this.failureClassifier = failureClassifier
            ?? throw new ArgumentNullException(nameof(failureClassifier));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        liveStore = new JsonLiveAttemptStore(paths.LiveStateFile);
        weeklyUsageResetTracker = new JsonWeeklyUsageResetTracker(
            paths.UsageResetStateFile);
        liveSafetyLatch = new LiveResetSafetyLatch(
            paths.LiveSafetyBlockFile,
            CompatibilityRevision);
        logger = new SafeJsonlLogger(paths.LogDirectory);
    }

    public async Task<GuardCycleResult> ExecuteAsync(
        GuardSettings settings,
        CancellationToken cancellationToken)
    {
        JsonSettingsStore.Validate(settings);
        try
        {
            await using var client = clientFactory.Create(settings);
            AccountRateLimits snapshot;
            try
            {
                snapshot = await client.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception readException) when (!IsFatal(readException))
            {
                var promotedException =
                    await HandleReadFailureAsync(
                    settings,
                    client,
                    readException).ConfigureAwait(false);
                if (promotedException is not null)
                {
                    throw promotedException;
                }

                throw;
            }
            var now = timeProvider.GetUtcNow();
            var initialEvaluation = decisionEngine.Evaluate(
                settings,
                snapshot,
                now);
            var usageResetDetection = await TryObserveWeeklyUsageAsync(
                snapshot,
                initialEvaluation.Weekly,
                WeeklyUsageResetAttribution.None,
                cancellationToken).ConfigureAwait(false);

            GuardCycleResult result;
            var semanticCompatibilityCode =
                GetSemanticCompatibilityCode(
                    initialEvaluation.Decision.Reason);
            if (!snapshot.ConsumeSchemaCompatible)
            {
                ResetReadCompatibilityFailures();
                var hasUnresolvedAttempt =
                    await HasUnresolvedAttemptAsync(cancellationToken)
                        .ConfigureAwait(false);
                if (hasUnresolvedAttempt)
                {
                    await BlockProtocolMismatchAsync(client)
                        .ConfigureAwait(false);
                }

                result = new GuardCycleResult(
                    snapshot,
                    initialEvaluation,
                    CycleActionKind.Blocked,
                    "mutation_schema_unverified");
            }
            else if (semanticCompatibilityCode is not null)
            {
                var hasUnresolvedAttempt =
                    await HasUnresolvedAttemptAsync(cancellationToken)
                        .ConfigureAwait(false);
                var compatibilityConfirmed =
                    liveSafetyLatch.BlockReason
                        == LiveAttemptBlockReason.ProtocolMismatch
                    || hasUnresolvedAttempt
                    || RegisterReadCompatibilityFailure(
                        semanticCompatibilityCode,
                        settings);
                if (compatibilityConfirmed && hasUnresolvedAttempt)
                {
                    await BlockProtocolMismatchAsync(client)
                        .ConfigureAwait(false);
                }

                result = new GuardCycleResult(
                    snapshot,
                    initialEvaluation,
                    compatibilityConfirmed
                        ? CycleActionKind.Blocked
                        : CycleActionKind.None,
                    compatibilityConfirmed
                        ? "protocol_read_unsupported"
                        : "protocol_verification_pending");
            }
            else
            {
                ResetReadCompatibilityFailures();
                var attempts = await liveStore.ReadAsync(cancellationToken)
                    .ConfigureAwait(false);
                var hasUnresolvedAttempt = attempts.Any(
                    attempt => attempt.Phase != LiveAttemptPhase.Terminal);
                liveSafetyLatch.TryClearProtocolMismatch(
                    compatibilityValidationSucceeded: true,
                    hasUnresolvedAttempt);

                if (liveSafetyLatch.BlockReason
                    == LiveAttemptBlockReason.ProtocolMismatch)
                {
                    result = new GuardCycleResult(
                        snapshot,
                        initialEvaluation,
                        CycleActionKind.Blocked,
                        "live_protocol_blocked");
                }
                else if (!settings.AutomationEnabled)
                {
                    result = new GuardCycleResult(
                        snapshot,
                        initialEvaluation,
                        CycleActionKind.None,
                        "automation_disabled");
                }
                else
                {
                    var coordinator = new LiveResetCoordinator(
                        decisionEngine,
                        liveStore,
                        secretProtector,
                        client,
                        failureClassifier,
                        timeProvider,
                        liveSafetyLatch);
                    try
                    {
                        var liveResult = await coordinator.ExecuteAsync(
                            settings,
                            snapshot,
                            now,
                            cancellationToken).ConfigureAwait(false);
                        result = MapLiveResult(settings, snapshot, liveResult, now);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (AppServerException exception) when (
                        exception.Operation == AppServerOperation.Mutation
                        && ClassifyFailure(exception)
                            == LiveResetFailureDisposition.ProtocolMismatch)
                    {
                        result = new GuardCycleResult(
                            snapshot,
                            initialEvaluation,
                            CycleActionKind.Blocked,
                            "mutation_schema_unverified");
                    }
                    catch (Exception exception) when (
                        !IsFatal(exception) && IsRetryableFailure(exception))
                    {
                        result = await CreateRetryPendingResultAsync(
                            settings,
                            snapshot,
                            now,
                            coordinator).ConfigureAwait(false);
                    }
                }
            }

            var hasRefreshedObservation =
                !ReferenceEquals(snapshot, result.AccountRateLimits);
            if (hasRefreshedObservation)
            {
                var refreshedDetection = await TryObserveWeeklyUsageAsync(
                    result.AccountRateLimits,
                    result.Evaluation.Weekly,
                    result.ActionKind == CycleActionKind.ResetSucceeded
                        ? WeeklyUsageResetAttribution.AutomaticCreditSucceeded
                        : WeeklyUsageResetAttribution.None,
                    result.ActionKind == CycleActionKind.ResetSucceeded
                        ? CancellationToken.None
                        : cancellationToken).ConfigureAwait(false);
                usageResetDetection = PreferUsageResetDetection(
                    usageResetDetection,
                    refreshedDetection);
            }
            else if (result.ActionKind == CycleActionKind.ResetSucceeded)
            {
                await TryMarkAutomaticCreditSucceededAsync(
                    timeProvider.GetUtcNow()).ConfigureAwait(false);
            }

            result = result with
            {
                UsageResetDetection = usageResetDetection,
            };

            var auditToken = result.ActionKind is
                CycleActionKind.ResetSucceeded or CycleActionKind.ResetNoEffect
                ? CancellationToken.None
                : cancellationToken;
            await TryLogResultAsync(settings, result, auditToken)
                .ConfigureAwait(false);
            return result;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            if (settings.AutomationEnabled
                && exception is not OperationCanceledException
                && ClassifyFailure(exception)
                    == LiveResetFailureDisposition.Unknown)
            {
                liveSafetyLatch.BlockUnknownFailure();
            }

            await TryLogFailureAsync(settings, exception, cancellationToken)
                .ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<WeeklyUsageResetDetection?> TryObserveWeeklyUsageAsync(
        AccountRateLimits snapshot,
        WindowReading? weekly,
        WeeklyUsageResetAttribution attribution,
        CancellationToken cancellationToken)
    {
        if (weekly is null)
        {
            return null;
        }

        try
        {
            var tracking = await weeklyUsageResetTracker.ObserveAsync(
                new WeeklyUsageObservation(
                    weekly.RemainingPercent,
                    weekly.ResetsAt,
                    snapshot.ObservedAt),
                attribution,
                cancellationToken).ConfigureAwait(false);
            return tracking.Detection;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            return null;
        }
    }

    private async Task TryMarkAutomaticCreditSucceededAsync(
        DateTimeOffset succeededAt)
    {
        try
        {
            _ = await weeklyUsageResetTracker.MarkAutomaticCreditSucceededAsync(
                succeededAt,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
        }
    }

    private static WeeklyUsageResetDetection? PreferUsageResetDetection(
        WeeklyUsageResetDetection? first,
        WeeklyUsageResetDetection? second)
    {
        if (second?.Kind == WeeklyUsageResetKind.AutomaticCredit)
        {
            return second;
        }

        if (first?.Kind == WeeklyUsageResetKind.AutomaticCredit)
        {
            return first;
        }

        return second ?? first;
    }

    private GuardCycleResult MapLiveResult(
        GuardSettings settings,
        AccountRateLimits originalSnapshot,
        LiveResetCycleResult liveResult,
        DateTimeOffset now)
    {
        var displayedSnapshot = liveResult.RefreshedRateLimits ?? originalSnapshot;
        var displayedEvaluation = liveResult.Evaluation;
        if (liveResult.RefreshedRateLimits is not null)
        {
            var refreshedEvaluation = decisionEngine.Evaluate(
                settings,
                displayedSnapshot,
                now);
            displayedEvaluation = new EvaluationResult(
                refreshedEvaluation.Weekly,
                liveResult.Evaluation.Decision,
                refreshedEvaluation.AvailableCreditCount);
        }

        var (actionKind, actionCode) = liveResult.Kind switch
        {
            LiveResetCycleKind.NoAction => (CycleActionKind.None, "no_action"),
            LiveResetCycleKind.Blocked => (
                CycleActionKind.Blocked,
                MapBlockedCode(
                    liveResult.Attempt?.BlockReason
                        ?? liveResult.ProcessBlockReason)),
            LiveResetCycleKind.DuplicateSuppressed =>
                (CycleActionKind.None, "duplicate_suppressed"),
            LiveResetCycleKind.Completed => MapCompletedOutcome(
                liveResult.Outcome,
                liveResult.RequiresRefresh),
            LiveResetCycleKind.AutomationDisabled =>
                (CycleActionKind.None, "automation_disabled"),
            _ => (CycleActionKind.Blocked, "live_result_unknown"),
        };

        return new GuardCycleResult(
            displayedSnapshot,
            displayedEvaluation,
            actionKind,
            actionCode,
            liveResult.Kind == LiveResetCycleKind.DuplicateSuppressed);
    }

    private static (CycleActionKind Kind, string Code) MapCompletedOutcome(
        ConsumeResetCreditOutcome? outcome,
        bool requiresRefresh) => (outcome, requiresRefresh) switch
        {
            (ConsumeResetCreditOutcome.Reset, true) =>
                (CycleActionKind.ResetSucceeded, "live_reset_refresh_pending"),
            (ConsumeResetCreditOutcome.Reset, false) =>
                (CycleActionKind.ResetSucceeded, "live_reset"),
            (ConsumeResetCreditOutcome.AlreadyRedeemed, true) =>
                (CycleActionKind.ResetSucceeded, "live_redeemed_refresh_pending"),
            (ConsumeResetCreditOutcome.AlreadyRedeemed, false) =>
                (CycleActionKind.ResetSucceeded, "live_already_redeemed"),
            (ConsumeResetCreditOutcome.NothingToReset, true) =>
                (CycleActionKind.ResetNoEffect, "live_nothing_refresh_pending"),
            (ConsumeResetCreditOutcome.NothingToReset, false) =>
                (CycleActionKind.ResetNoEffect, "live_nothing_to_reset"),
            (ConsumeResetCreditOutcome.NoCredit, true) =>
                (CycleActionKind.ResetNoEffect, "live_no_credit_refresh_pending"),
            (ConsumeResetCreditOutcome.NoCredit, false) =>
                (CycleActionKind.ResetNoEffect, "live_no_credit"),
            _ => (CycleActionKind.Blocked, "live_outcome_unknown"),
        };

    private static string MapBlockedCode(LiveAttemptBlockReason? reason) =>
        reason switch
        {
            LiveAttemptBlockReason.ProtocolMismatch => "live_protocol_blocked",
            LiveAttemptBlockReason.ContextChanged => "live_context_changed",
            LiveAttemptBlockReason.LegacyTriggerUnsupported =>
                "legacy_trigger_unsupported",
            LiveAttemptBlockReason.SecretUnavailable => "live_secret_unavailable",
            LiveAttemptBlockReason.DispatchLimitReached => "live_dispatch_limit",
            LiveAttemptBlockReason.UnknownFailure => "live_needs_review",
            _ => "live_blocked",
        };

    private async Task TryLogResultAsync(
        GuardSettings settings,
        GuardCycleResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            var eventType = result.ActionKind switch
            {
                CycleActionKind.ResetPending
                    or CycleActionKind.ResetSucceeded
                    or CycleActionKind.ResetNoEffect => "live",
                _ => "poll",
            };
            await logger.WriteAsync(
                new SafeLogEvent(
                    timeProvider.GetUtcNow(),
                    eventType,
                    ToLogOutcome(result.ActionCode),
                    ToCode(result.Evaluation.Decision.Reason),
                    "weekly",
                    result.Evaluation.Decision.TriggerWindow?.RemainingPercent,
                    settings.RemainingThresholdPercent,
                    result.Evaluation.AvailableCreditCount,
                    result.DuplicateSuppressed,
                    "desktop_monitor"),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException)
        {
        }
    }

    private async Task TryLogFailureAsync(
        GuardSettings settings,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var reason = exception switch
        {
            AppServerException appServerException => ToCode(appServerException.Category),
            LiveStateException => "live_state_failure",
            IOException => "local_io_error",
            UnauthorizedAccessException => "local_access_denied",
            OperationCanceledException => "cancelled",
            _ => "unexpected_local_failure",
        };

        try
        {
            await logger.WriteAsync(
                new SafeLogEvent(
                    timeProvider.GetUtcNow(),
                    "failure",
                    "blocked",
                    reason,
                    "weekly",
                    ThresholdPercent: settings.RemainingThresholdPercent,
                    ComponentCategory: "desktop_monitor"),
                cancellationToken.IsCancellationRequested
                    ? CancellationToken.None
                    : cancellationToken).ConfigureAwait(false);
        }
        catch (Exception logException) when (
            logException is IOException
                or UnauthorizedAccessException
                or ArgumentException)
        {
        }
    }

    private static bool IsFatal(Exception exception) => exception is
        OutOfMemoryException
        or StackOverflowException
        or AccessViolationException;

    private async Task<Exception?> HandleReadFailureAsync(
        GuardSettings settings,
        IAccountRateLimitClient client,
        Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            return null;
        }

        var disposition = ClassifyFailure(exception);
        if (disposition == LiveResetFailureDisposition.ProtocolMismatch)
        {
            var hasUnresolvedAttempt =
                await HasUnresolvedAttemptAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            var confirmed =
                liveSafetyLatch.BlockReason
                    == LiveAttemptBlockReason.ProtocolMismatch
                || hasUnresolvedAttempt
                || RegisterReadCompatibilityFailure(
                    GetReadCompatibilityFailureCode(exception),
                    settings);
            if (confirmed && hasUnresolvedAttempt)
            {
                await BlockProtocolMismatchAsync(client).ConfigureAwait(false);
            }

            if (hasUnresolvedAttempt
                && exception is AppServerException appServerException)
            {
                return new AppServerException(
                    appServerException.Category,
                    appServerException.RemoteCode,
                    appServerException,
                    AppServerOperation.Mutation);
            }

            return null;
        }

        ResetReadCompatibilityFailures();
        if (settings.AutomationEnabled
            && disposition == LiveResetFailureDisposition.Unknown)
        {
            await BlockFailureAsync(
                client,
                LiveAttemptBlockReason.UnknownFailure).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<bool> HasUnresolvedAttemptAsync(
        CancellationToken cancellationToken)
    {
        var attempts = await liveStore.ReadAsync(cancellationToken)
            .ConfigureAwait(false);
        return attempts.Any(
            attempt => attempt.Phase != LiveAttemptPhase.Terminal);
    }

    private Task BlockProtocolMismatchAsync(
        IAccountRateLimitClient client) =>
        BlockFailureAsync(
            client,
            LiveAttemptBlockReason.ProtocolMismatch);

    private async Task BlockFailureAsync(
        IAccountRateLimitClient client,
        LiveAttemptBlockReason reason)
    {
        if (reason is not (
            LiveAttemptBlockReason.ProtocolMismatch
            or LiveAttemptBlockReason.UnknownFailure))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        var coordinator = new LiveResetCoordinator(
            decisionEngine,
            liveStore,
            secretProtector,
            client,
            failureClassifier,
            timeProvider,
            liveSafetyLatch);
        await coordinator.BlockPendingAsync(
            reason,
            timeProvider.GetUtcNow(),
            CancellationToken.None).ConfigureAwait(false);
    }

    private bool RegisterReadCompatibilityFailure(
        string candidateCode,
        GuardSettings settings)
    {
        var contextCode = string.Concat(
            candidateCode,
            "|",
            settings.CodexExecutablePath?.ToUpperInvariant() ?? "<AUTO>");
        var now = timeProvider.GetUtcNow();
        if (!string.Equals(
            readCompatibilityCandidateCode,
            contextCode,
            StringComparison.Ordinal))
        {
            readCompatibilityCandidateCode = contextCode;
            readCompatibilityEligibleAt =
                now.Add(CompatibilityVerificationDelay);
            return false;
        }

        return readCompatibilityEligibleAt is { } eligibleAt
            && now >= eligibleAt;
    }

    private void ResetReadCompatibilityFailures()
    {
        readCompatibilityCandidateCode = null;
        readCompatibilityEligibleAt = null;
    }

    private static string GetReadCompatibilityFailureCode(
        Exception exception) => exception is AppServerException appServerException
        ? string.Concat(
            appServerException.Category,
            ":",
            appServerException.RemoteCode?.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
                ?? "none")
        : exception.GetType().Name;

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

    private LiveResetFailureDisposition ClassifyFailure(Exception exception)
    {
        try
        {
            var disposition = failureClassifier.Classify(exception);
            return Enum.IsDefined(disposition)
                ? disposition
                : LiveResetFailureDisposition.Unknown;
        }
        catch (Exception classifierException) when (!IsFatal(classifierException))
        {
            return LiveResetFailureDisposition.Unknown;
        }
    }

    private static string ToLogOutcome(string actionCode) => actionCode switch
    {
        "no_action" => "no_action",
        "evaluation_blocked" => "evaluation_blocked",
        "duplicate_suppressed" => "duplicate_suppressed",
        "live_blocked" => "live_blocked",
        "live_protocol_blocked" => "live_protocol_blocked",
        "protocol_read_unsupported" => "protocol_read_unsupported",
        "protocol_verification_pending" => "protocol_verification_pending",
        "mutation_schema_unverified" => "mutation_schema_unverified",
        "live_context_changed" => "live_context_changed",
        "live_secret_unavailable" => "live_secret_unavailable",
        "live_dispatch_limit" => "live_dispatch_limit",
        "live_needs_review" => "live_needs_review",
        "live_retry_pending" => "live_retry_pending",
        "live_reset" => "live_reset",
        "live_reset_refresh_pending" => "live_reset_refresh_pending",
        "live_already_redeemed" => "live_already_redeemed",
        "live_redeemed_refresh_pending" => "live_redeemed_refresh_pending",
        "live_nothing_to_reset" => "live_nothing_to_reset",
        "live_nothing_refresh_pending" => "live_nothing_refresh_pending",
        "live_no_credit" => "live_no_credit",
        "live_no_credit_refresh_pending" => "live_no_credit_refresh_pending",
        "automation_disabled" => "automation_disabled",
        "legacy_trigger_unsupported" => "legacy_trigger_unsupported",
        _ => "blocked",
    };

    private async Task<GuardCycleResult> CreateRetryPendingResultAsync(
        GuardSettings settings,
        AccountRateLimits snapshot,
        DateTimeOffset now,
        LiveResetCoordinator coordinator)
    {
        var evaluation = decisionEngine.Evaluate(settings, snapshot, now);
        var trigger = decisionEngine.EvaluateTrigger(settings, snapshot, now);
        var attempts = await coordinator.ReadAttemptsAsync(CancellationToken.None)
            .ConfigureAwait(false);
        var pending = attempts.SingleOrDefault(attempt =>
            IsSameTriggerContext(settings, trigger, attempt));
        if (pending is null)
        {
            throw new LiveStateException("live_attempt_missing");
        }

        return new GuardCycleResult(
            snapshot,
            evaluation,
            CycleActionKind.ResetPending,
            "live_retry_pending",
            DuplicateSuppressed: false);
    }

    private bool IsRetryableFailure(Exception exception)
    {
        try
        {
            return failureClassifier.Classify(exception)
                == LiveResetFailureDisposition.Retryable;
        }
        catch (Exception classifierException) when (!IsFatal(classifierException))
        {
            return false;
        }
    }

    private static bool IsSameTriggerContext(
        GuardSettings settings,
        TriggerEvaluation trigger,
        LiveAttemptSnapshot attempt) =>
        attempt.Phase == LiveAttemptPhase.Pending
        && settings.AutomationEnabled
        && attempt.TriggerLimit == TriggerLimit.Weekly
        && settings.RemainingThresholdPercent == attempt.ThresholdPercent
        && trigger.ThresholdReached
        && trigger.SelectedWindow is not null
        && trigger.SelectedWindow.NormalizedDurationMinutes
            == attempt.NormalizedDurationMinutes
        && trigger.SelectedWindow.ResetsAt == attempt.ResetsAt
        && string.Equals(
            trigger.IntervalKey,
            attempt.IntervalKey,
            StringComparison.Ordinal);

    private static string ToCode<T>(T value)
        where T : struct, Enum => string.Concat(
            value.ToString().Select((character, index) =>
                char.IsUpper(character) && index > 0
                    ? $"_{char.ToLowerInvariant(character)}"
                    : char.ToLowerInvariant(character).ToString()));
}

internal interface IRateLimitClientFactory
{
    IAccountRateLimitClient Create(GuardSettings settings);
}

internal sealed class CodexRateLimitClientFactory : IRateLimitClientFactory
{
    public IAccountRateLimitClient Create(GuardSettings settings)
    {
        var executable = CodexExecutableLocator.Resolve(
            settings.CodexExecutablePath);
        return new CodexAppServerClient(
            executable,
            AppContext.BaseDirectory);
    }
}
