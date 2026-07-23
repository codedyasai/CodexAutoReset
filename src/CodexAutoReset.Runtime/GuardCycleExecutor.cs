using CodexAutoReset.AppServer;
using CodexAutoReset.Core;

namespace CodexAutoReset.Runtime;

public sealed class GuardCycleExecutor : IGuardCycleExecutor
{
    private readonly IRateLimitClientFactory clientFactory;
    private readonly ISecretProtector secretProtector;
    private readonly ILiveResetFailureClassifier failureClassifier;
    private readonly TimeProvider timeProvider;
    private readonly ResetDecisionEngine decisionEngine = new();
    private readonly JsonLiveAttemptStore liveStore;
    private readonly SafeJsonlLogger logger;
    private readonly LiveResetSafetyLatch liveSafetyLatch;

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
        liveSafetyLatch = new LiveResetSafetyLatch(paths.LiveSafetyBlockFile);
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
                await BlockPendingAfterReadFailureAsync(
                    settings,
                    client,
                    readException).ConfigureAwait(false);
                throw;
            }
            var now = timeProvider.GetUtcNow();

            GuardCycleResult result;
            if (!settings.AutomationEnabled)
            {
                result = new GuardCycleResult(
                    snapshot,
                    decisionEngine.Evaluate(settings, snapshot, now),
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

    private async Task BlockPendingAfterReadFailureAsync(
        GuardSettings settings,
        IAccountRateLimitClient client,
        Exception exception)
    {
        if (!settings.AutomationEnabled)
        {
            return;
        }

        if (exception is OperationCanceledException)
        {
            return;
        }

        var disposition = ClassifyFailure(exception);
        var reason = disposition switch
        {
            LiveResetFailureDisposition.ProtocolMismatch =>
                LiveAttemptBlockReason.ProtocolMismatch,
            LiveResetFailureDisposition.Unknown => LiveAttemptBlockReason.UnknownFailure,
            _ => (LiveAttemptBlockReason?)null,
        };
        if (reason is null)
        {
            return;
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
            reason.Value,
            timeProvider.GetUtcNow(),
            CancellationToken.None).ConfigureAwait(false);
    }

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
