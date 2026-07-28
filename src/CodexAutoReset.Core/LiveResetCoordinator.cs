namespace CodexAutoReset.Core;

public sealed class LiveResetCoordinator
{
    private const int MaximumCreditIdLength = 4_096;

    private readonly ResetDecisionEngine decisionEngine;
    private readonly JsonLiveAttemptStore store;
    private readonly ISecretProtector secretProtector;
    private readonly IAccountRateLimitClient client;
    private readonly ILiveResetFailureClassifier failureClassifier;
    private readonly TimeProvider timeProvider;
    private readonly LiveResetSafetyLatch safetyLatch;
    private readonly SemaphoreSlim gate;

    public LiveResetCoordinator(
        ResetDecisionEngine decisionEngine,
        JsonLiveAttemptStore store,
        ISecretProtector secretProtector,
        IAccountRateLimitClient client,
        ILiveResetFailureClassifier? failureClassifier = null,
        TimeProvider? timeProvider = null,
        LiveResetSafetyLatch? safetyLatch = null)
    {
        this.decisionEngine = decisionEngine
            ?? throw new ArgumentNullException(nameof(decisionEngine));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.secretProtector = secretProtector
            ?? throw new ArgumentNullException(nameof(secretProtector));
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.failureClassifier = failureClassifier
            ?? ConservativeFailureClassifier.Instance;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.safetyLatch = safetyLatch ?? new LiveResetSafetyLatch();
        gate = this.safetyLatch.Gate;
    }

    public async Task<LiveResetCycleResult> ExecuteAsync(
        GuardSettings settings,
        AccountRateLimits snapshot,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(snapshot);
        JsonSettingsStore.Validate(settings);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (settings.AutomationEnabled
                && safetyLatch.BlockReason is { } processBlockReason)
            {
                return new LiveResetCycleResult(
                    LiveResetCycleKind.Blocked,
                    decisionEngine.Evaluate(settings, snapshot, now),
                    Attempt: null,
                    Outcome: null,
                    ConsumeAttempted: false,
                    RequiresRefresh: false,
                    RefreshedRateLimits: null,
                    processBlockReason);
            }

            var evaluation = decisionEngine.Evaluate(settings, snapshot, now);
            if (!settings.AutomationEnabled)
            {
                return CreateResult(LiveResetCycleKind.AutomationDisabled, evaluation);
            }

            if (!snapshot.ConsumeSchemaCompatible)
            {
                return new LiveResetCycleResult(
                    LiveResetCycleKind.Blocked,
                    evaluation,
                    Attempt: null,
                    Outcome: null,
                    ConsumeAttempted: false,
                    RequiresRefresh: false,
                    RefreshedRateLimits: null,
                    LiveAttemptBlockReason.ProtocolMismatch);
            }

            await store.MarkRefreshedAsync(
                snapshot.ObservedAt,
                now,
                cancellationToken).ConfigureAwait(false);

            var trigger = decisionEngine.EvaluateTrigger(settings, snapshot, now);
            var activeAttempt = await store.ReadActiveAsync(cancellationToken)
                .ConfigureAwait(false);
            if (activeAttempt is not null)
            {
                return await ReconcileAsync(
                    settings,
                    snapshot,
                    evaluation,
                    trigger,
                    activeAttempt,
                    now,
                    cancellationToken).ConfigureAwait(false);
            }

            if (evaluation.Decision.Kind != DecisionKind.WouldConsume)
            {
                var kind = evaluation.Decision.Kind == DecisionKind.Blocked
                    ? LiveResetCycleKind.Blocked
                    : LiveResetCycleKind.NoAction;
                return CreateResult(kind, evaluation);
            }

            var window = evaluation.Decision.TriggerWindow
                ?? throw new LiveStateException("live_candidate_invalid");
            var intervalKey = evaluation.Decision.IntervalKey
                ?? throw new LiveStateException("live_candidate_invalid");
            var creditId = evaluation.Decision.SelectedCredit?.Id
                ?? throw new LiveStateException("live_credit_invalid");
            var candidate = new LiveAttemptCandidate(
                intervalKey,
                settings.RemainingThresholdPercent,
                window.NormalizedDurationMinutes,
                window.ResetsAt);
            var prepared = await store.TryPrepareAsync(
                candidate,
                creditId,
                secretProtector,
                now,
                cancellationToken).ConfigureAwait(false);

            if (prepared.Disposition == LivePrepareDisposition.ExistingTerminal)
            {
                return CreateResult(
                    LiveResetCycleKind.DuplicateSuppressed,
                    evaluation,
                    JsonLiveAttemptStore.ToSnapshot(prepared.Attempt));
            }

            if (prepared.Disposition == LivePrepareDisposition.ExistingActive)
            {
                return await ReconcileAsync(
                    settings,
                    snapshot,
                    evaluation,
                    trigger,
                    prepared.Attempt,
                    now,
                    cancellationToken).ConfigureAwait(false);
            }

            return await DispatchAsync(
                evaluation,
                prepared.Attempt,
                now,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<LiveAttemptSnapshot?> BlockPendingAsync(
        LiveAttemptBlockReason reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (reason is not (LiveAttemptBlockReason.ProtocolMismatch
            or LiveAttemptBlockReason.UnknownFailure))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var effectiveReason = safetyLatch.BlockReason ?? reason;
            StoredLiveAttempt? attempt;
            var stateWriteFailed = false;
            try
            {
                attempt = await store.BlockActiveAsync(
                    effectiveReason,
                    now,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                attempt = null;
                stateWriteFailed = true;
            }

            safetyLatch.Block(effectiveReason);
            if (stateWriteFailed)
            {
                throw safetyLatch.CreateBlockedException();
            }

            return attempt is null ? null : JsonLiveAttemptStore.ToSnapshot(attempt);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<IReadOnlyList<LiveAttemptSnapshot>> ReadAttemptsAsync(
        CancellationToken cancellationToken) => store.ReadAsync(cancellationToken);

    private async Task<LiveResetCycleResult> ReconcileAsync(
        GuardSettings settings,
        AccountRateLimits snapshot,
        EvaluationResult evaluation,
        TriggerEvaluation trigger,
        StoredLiveAttempt activeAttempt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var activeSnapshot = JsonLiveAttemptStore.ToSnapshot(activeAttempt);
        if (activeSnapshot.Phase is LiveAttemptPhase.NeedsReview
            or LiveAttemptPhase.ProtocolBlocked)
        {
            return CreateResult(
                LiveResetCycleKind.Blocked,
                evaluation,
                activeSnapshot);
        }

        if (activeSnapshot.TriggerLimit == TriggerLimit.FiveHour)
        {
            return CreateResult(
                LiveResetCycleKind.Blocked,
                evaluation,
                activeSnapshot with
                {
                    BlockReason = LiveAttemptBlockReason.LegacyTriggerUnsupported,
                });
        }

        if (!IsSameTriggerContext(settings, trigger, activeSnapshot))
        {
            var blocked = await store.BlockActiveAsync(
                LiveAttemptBlockReason.ContextChanged,
                now,
                cancellationToken).ConfigureAwait(false);
            return CreateResult(
                LiveResetCycleKind.Blocked,
                evaluation,
                blocked is null ? activeSnapshot : JsonLiveAttemptStore.ToSnapshot(blocked));
        }

        return await DispatchAsync(
            evaluation,
            activeAttempt,
            now,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<LiveResetCycleResult> DispatchAsync(
        EvaluationResult evaluation,
        StoredLiveAttempt attempt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        safetyLatch.ThrowIfBlocked();

        string creditId;
        try
        {
            creditId = secretProtector.Unprotect(
                attempt.ProtectedCreditId
                    ?? throw new LiveStateException("live_secret_unavailable"));
        }
        catch (LiveStateException)
        {
            var blocked = await store.BlockActiveAsync(
                LiveAttemptBlockReason.SecretUnavailable,
                now,
                cancellationToken).ConfigureAwait(false);
            return CreateResult(
                LiveResetCycleKind.Blocked,
                evaluation,
                blocked is null ? null : JsonLiveAttemptStore.ToSnapshot(blocked));
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            var blocked = await store.BlockActiveAsync(
                LiveAttemptBlockReason.SecretUnavailable,
                now,
                cancellationToken).ConfigureAwait(false);
            return CreateResult(
                LiveResetCycleKind.Blocked,
                evaluation,
                blocked is null ? null : JsonLiveAttemptStore.ToSnapshot(blocked));
        }

        if (string.IsNullOrWhiteSpace(creditId) || creditId.Length > MaximumCreditIdLength)
        {
            var blocked = await store.BlockActiveAsync(
                LiveAttemptBlockReason.SecretUnavailable,
                now,
                cancellationToken).ConfigureAwait(false);
            return CreateResult(
                LiveResetCycleKind.Blocked,
                evaluation,
                blocked is null ? null : JsonLiveAttemptStore.ToSnapshot(blocked));
        }

        var dispatched = await store.MarkDispatchStartedAsync(
            attempt.IntervalKey,
            now,
            cancellationToken).ConfigureAwait(false);
        var dispatchedSnapshot = JsonLiveAttemptStore.ToSnapshot(dispatched);
        if (dispatchedSnapshot.Phase != LiveAttemptPhase.Pending)
        {
            return CreateResult(
                LiveResetCycleKind.Blocked,
                evaluation,
                dispatchedSnapshot);
        }

        ConsumeResetCreditResult result;
        try
        {
            result = await client.ConsumeResetCreditAsync(
                new ConsumeResetCreditRequest(dispatched.IdempotencyKey, creditId),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Dispatch was durably recorded. Startup reconciliation reuses this intent.
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            var disposition = ClassifyFailure(exception);
            if (disposition != LiveResetFailureDisposition.Retryable)
            {
                await PersistStickyBlockAsync(
                    disposition == LiveResetFailureDisposition.ProtocolMismatch
                        ? LiveAttemptBlockReason.ProtocolMismatch
                        : LiveAttemptBlockReason.UnknownFailure,
                    timeProvider.GetUtcNow(),
                    CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        if (result is null || !Enum.IsDefined(result.Outcome))
        {
            var blocked = await PersistStickyBlockAsync(
                LiveAttemptBlockReason.ProtocolMismatch,
                timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
            return new LiveResetCycleResult(
                LiveResetCycleKind.Blocked,
                evaluation,
                JsonLiveAttemptStore.ToSnapshot(blocked),
                null,
                ConsumeAttempted: true,
                RequiresRefresh: true,
                RefreshedRateLimits: null);
        }

        var completed = await store.CompleteAsync(
            attempt.IntervalKey,
            result.Outcome,
            timeProvider.GetUtcNow(),
            CancellationToken.None).ConfigureAwait(false);

        AccountRateLimits? refreshed = null;
        try
        {
            refreshed = await client.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            // The terminal outcome is already durable. A later full read completes refresh.
            if (exception is not OperationCanceledException)
            {
                var disposition = ClassifyFailure(exception);
                if (disposition == LiveResetFailureDisposition.ProtocolMismatch)
                {
                    safetyLatch.BlockProtocolMismatch();
                }
                else if (disposition == LiveResetFailureDisposition.Unknown)
                {
                    safetyLatch.BlockUnknownFailure();
                }
            }
        }

        if (refreshed is not null)
        {
            await store.MarkRefreshedAsync(
                refreshed.ObservedAt,
                timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
        }

        var attempts = await store.ReadAsync(CancellationToken.None).ConfigureAwait(false);
        var finalAttempt = attempts.Single(item => string.Equals(
            item.IntervalKey,
            completed.IntervalKey,
            StringComparison.Ordinal));
        return new LiveResetCycleResult(
            LiveResetCycleKind.Completed,
            evaluation,
            finalAttempt,
            result.Outcome,
            ConsumeAttempted: true,
            finalAttempt.RefreshRequired,
            refreshed);
    }

    private static bool IsSameTriggerContext(
        GuardSettings settings,
        TriggerEvaluation trigger,
        LiveAttemptSnapshot attempt) =>
        settings.AutomationEnabled
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

    private static LiveResetCycleResult CreateResult(
        LiveResetCycleKind kind,
        EvaluationResult evaluation,
        LiveAttemptSnapshot? attempt = null) => new(
            kind,
            evaluation,
            attempt,
            attempt?.Outcome,
            ConsumeAttempted: false,
            attempt?.RefreshRequired ?? false,
            RefreshedRateLimits: null);

    private static bool IsFatal(Exception exception) => exception is
        OutOfMemoryException
        or StackOverflowException
        or AccessViolationException;

    private async Task<StoredLiveAttempt> PersistStickyBlockAsync(
        LiveAttemptBlockReason reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        StoredLiveAttempt? blocked;
        var stateWriteFailed = false;
        try
        {
            blocked = await store.BlockActiveAsync(
                reason,
                now,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            blocked = null;
            stateWriteFailed = true;
        }

        safetyLatch.Block(reason);
        if (stateWriteFailed)
        {
            throw safetyLatch.CreateBlockedException();
        }

        return blocked
            ?? throw new LiveStateException("live_sticky_state_missing");
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

    private sealed class ConservativeFailureClassifier : ILiveResetFailureClassifier
    {
        public static ConservativeFailureClassifier Instance { get; } = new();

        public LiveResetFailureDisposition Classify(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            return LiveResetFailureDisposition.Unknown;
        }
    }
}
