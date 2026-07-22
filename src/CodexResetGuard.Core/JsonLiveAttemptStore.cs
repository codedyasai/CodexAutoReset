using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexResetGuard.Core;

public sealed class JsonLiveAttemptStore
{
    private const int MaximumRecords = 1_024;
    private const int MinimumRecentRecordRetention = 512;
    private const int MaximumDispatchCount = 32;
    private const long ExpiredResetClockSkewSeconds = 60;
    private const int MaximumIntervalKeyLength = 256;
    private const int MaximumCreditIdLength = 4_096;
    private const int MaximumProtectedCreditLength = 16_384;
    private const long MaximumDocumentBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);

    public JsonLiveAttemptStore(string path)
    {
        this.path = System.IO.Path.GetFullPath(path);
    }

    public string Path => path;

    public async Task<IReadOnlyList<LiveAttemptSnapshot>> ReadAsync(
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
            return state.Attempts.Select(ToSnapshot).ToArray();
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<StoredLiveAttempt?> ReadActiveAsync(
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
            return state.Attempts.SingleOrDefault(attempt => attempt.Phase != "terminal");
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<PrepareLiveAttemptResult> TryPrepareAsync(
        LiveAttemptCandidate candidate,
        string creditId,
        ISecretProtector protector,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(protector);
        if (string.IsNullOrWhiteSpace(creditId) || creditId.Length > MaximumCreditIdLength)
        {
            throw new LiveStateException("live_credit_invalid");
        }

        if (now == default)
        {
            throw new ArgumentOutOfRangeException(nameof(now));
        }

        ValidateCandidate(candidate, now);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
            var active = state.Attempts.SingleOrDefault(attempt => attempt.Phase != "terminal");
            if (active is not null)
            {
                return new PrepareLiveAttemptResult(
                    LivePrepareDisposition.ExistingActive,
                    active);
            }

            var existing = state.Attempts.SingleOrDefault(attempt =>
                string.Equals(
                    attempt.IntervalKey,
                    candidate.IntervalKey,
                    StringComparison.Ordinal));
            if (existing is not null)
            {
                return new PrepareLiveAttemptResult(
                    LivePrepareDisposition.ExistingTerminal,
                    existing);
            }

            if (state.Attempts.Count >= MaximumRecords)
            {
                FreeCapacityFromOldTerminalAttempt(state, now);
                if (state.Attempts.Count >= MaximumRecords)
                {
                    throw new LiveStateException("live_state_capacity_reached");
                }
            }

            var protectedCreditId = ProtectCreditId(protector, creditId);
            var attempt = new StoredLiveAttempt
            {
                IntervalKey = candidate.IntervalKey,
                TriggerLimit = "weekly",
                ThresholdPercent = candidate.ThresholdPercent,
                NormalizedDurationMinutes = candidate.NormalizedDurationMinutes,
                ResetsAt = candidate.ResetsAt,
                IdempotencyKey = Guid.NewGuid().ToString("D"),
                ProtectedCreditId = protectedCreditId,
                Phase = "pending",
                DispatchCount = 0,
                Outcome = null,
                BlockReason = null,
                RefreshRequired = false,
                PreparedAt = now,
                UpdatedAt = now,
                CompletedAt = null,
            };

            state.Attempts.Add(attempt);
            await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
            return new PrepareLiveAttemptResult(LivePrepareDisposition.Prepared, attempt);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<StoredLiveAttempt> MarkDispatchStartedAsync(
        string intervalKey,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
            var attempt = FindRequiredAttempt(state, intervalKey);
            if (!string.Equals(attempt.Phase, "pending", StringComparison.Ordinal))
            {
                throw new LiveStateException("live_attempt_not_pending");
            }

            if (attempt.DispatchCount >= MaximumDispatchCount)
            {
                attempt.Phase = "needsReview";
                attempt.BlockReason = "dispatchLimitReached";
                attempt.UpdatedAt = now;
                await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
                return attempt;
            }

            attempt.DispatchCount++;
            attempt.UpdatedAt = now;
            await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
            return attempt;
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<StoredLiveAttempt> CompleteAsync(
        string intervalKey,
        ConsumeResetCreditOutcome outcome,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new LiveStateException("live_outcome_invalid");
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
            var attempt = FindRequiredAttempt(state, intervalKey);
            if (!string.Equals(attempt.Phase, "pending", StringComparison.Ordinal)
                || attempt.DispatchCount < 1)
            {
                throw new LiveStateException("live_attempt_not_pending");
            }

            attempt.Phase = "terminal";
            attempt.Outcome = ToCode(outcome);
            attempt.BlockReason = null;
            attempt.ProtectedCreditId = null;
            attempt.RefreshRequired = true;
            attempt.UpdatedAt = now;
            attempt.CompletedAt = now;
            await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
            return attempt;
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<StoredLiveAttempt?> BlockActiveAsync(
        LiveAttemptBlockReason reason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
            var attempt = state.Attempts.SingleOrDefault(item => item.Phase != "terminal");
            if (attempt is null)
            {
                return null;
            }

            if (attempt.Phase is "needsReview" or "protocolBlocked")
            {
                return attempt;
            }

            attempt.Phase = reason == LiveAttemptBlockReason.ProtocolMismatch
                ? "protocolBlocked"
                : "needsReview";
            attempt.BlockReason = ToCode(reason);
            attempt.UpdatedAt = now;
            await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
            return attempt;
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task MarkRefreshedAsync(
        DateTimeOffset observedAt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
            var changed = false;
            foreach (var attempt in state.Attempts.Where(attempt =>
                attempt.Phase == "terminal"
                && attempt.RefreshRequired
                && attempt.CompletedAt is not null
                && observedAt >= attempt.CompletedAt))
            {
                attempt.RefreshRequired = false;
                attempt.UpdatedAt = now;
                changed = true;
            }

            if (changed)
            {
                await SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    internal static LiveAttemptSnapshot ToSnapshot(StoredLiveAttempt attempt) => new(
        attempt.IntervalKey,
        ParseTriggerLimit(attempt.TriggerLimit),
        attempt.ThresholdPercent,
        attempt.NormalizedDurationMinutes,
        attempt.ResetsAt,
        ParsePhase(attempt.Phase),
        attempt.DispatchCount,
        attempt.Outcome is null ? null : ParseOutcome(attempt.Outcome),
        attempt.BlockReason is null ? null : ParseBlockReason(attempt.BlockReason),
        attempt.RefreshRequired,
        attempt.PreparedAt,
        attempt.UpdatedAt);

    private async Task<LiveAttemptState> LoadStateAsync(
        CancellationToken cancellationToken)
    {
        EnsurePathAllowed();
        if (!File.Exists(path))
        {
            return new LiveAttemptState();
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);
            if (stream.Length > MaximumDocumentBytes)
            {
                throw InvalidState();
            }

            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            EnsureNoDuplicateMembers(document.RootElement);
            var state = document.RootElement.Deserialize<LiveAttemptState>(
                SerializerOptions);
            ValidateState(state);
            return state!;
        }
        catch (LiveStateException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw InvalidState();
        }
        catch (IOException)
        {
            throw new LiveStateException("live_state_io_error");
        }
        catch (UnauthorizedAccessException)
        {
            throw new LiveStateException("live_state_access_denied");
        }
    }

    private async Task SaveStateAsync(
        LiveAttemptState state,
        CancellationToken cancellationToken)
    {
        EnsurePathAllowed();
        ValidateState(state);
        var directory = System.IO.Path.GetDirectoryName(path) ?? throw InvalidState();
        var temporaryPath = System.IO.Path.Combine(
            directory,
            $".{System.IO.Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            Directory.CreateDirectory(directory);
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    state,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
                if (stream.Length > MaximumDocumentBytes)
                {
                    throw InvalidState();
                }
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (LiveStateException)
        {
            throw;
        }
        catch (IOException)
        {
            throw new LiveStateException("live_state_io_error");
        }
        catch (UnauthorizedAccessException)
        {
            throw new LiveStateException("live_state_access_denied");
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private void EnsurePathAllowed()
    {
        if (!string.Equals(
            System.IO.Path.GetFileName(path),
            "live-state.json",
            StringComparison.OrdinalIgnoreCase))
        {
            throw new LiveStateException("live_state_path_forbidden");
        }

        try
        {
            var directory = System.IO.Path.GetDirectoryName(path);
            if ((File.Exists(path)
                    && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                || (directory is not null
                    && Directory.Exists(directory)
                    && File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)))
            {
                throw new LiveStateException("live_state_path_forbidden");
            }
        }
        catch (LiveStateException)
        {
            throw;
        }
        catch (IOException)
        {
            throw new LiveStateException("live_state_io_error");
        }
        catch (UnauthorizedAccessException)
        {
            throw new LiveStateException("live_state_access_denied");
        }
    }

    private static string ProtectCreditId(ISecretProtector protector, string creditId)
    {
        string protectedValue;
        try
        {
            protectedValue = protector.Protect(creditId);
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            throw new LiveStateException("live_secret_protection_failed");
        }

        if (string.IsNullOrWhiteSpace(protectedValue)
            || protectedValue.Length > MaximumProtectedCreditLength
            || string.Equals(protectedValue, creditId, StringComparison.Ordinal)
            || !IsBase64(protectedValue))
        {
            throw new LiveStateException("live_secret_protection_failed");
        }

        return protectedValue;
    }

    private static void ValidateCandidate(
        LiveAttemptCandidate candidate,
        DateTimeOffset now)
    {
        if (candidate.ThresholdPercent is < GuardSettings.MinimumThreshold
                or > GuardSettings.MaximumThreshold
            || candidate.ResetsAt <= 0
            || candidate.ResetsAt
                < now.ToUnixTimeSeconds() - ExpiredResetClockSkewSeconds
            || candidate.NormalizedDurationMinutes != ExpectedDuration(TriggerLimit.Weekly)
            || !string.Equals(
                candidate.IntervalKey,
                BuildIntervalKey(
                    TriggerLimit.Weekly,
                    candidate.NormalizedDurationMinutes,
                    candidate.ResetsAt),
                StringComparison.Ordinal))
        {
            throw new LiveStateException("live_candidate_invalid");
        }
    }

    private static void FreeCapacityFromOldTerminalAttempt(
        LiveAttemptState state,
        DateTimeOffset now)
    {
        var expiredBefore = now.ToUnixTimeSeconds() - ExpiredResetClockSkewSeconds;
        var oldestRemovableCount = state.Attempts.Count - MinimumRecentRecordRetention;
        for (var index = 0; index < oldestRemovableCount; index++)
        {
            var attempt = state.Attempts[index];
            if (string.Equals(attempt.Phase, "terminal", StringComparison.Ordinal)
                && !attempt.RefreshRequired
                && attempt.CompletedAt is { } completedAt
                && completedAt <= now
                && attempt.ResetsAt < expiredBefore)
            {
                state.Attempts.RemoveAt(index);
                return;
            }
        }
    }

    private static void ValidateState(LiveAttemptState? state)
    {
        if (state is null
            || state.SchemaVersion != 1
            || state.Attempts is null
            || state.Attempts.Count > MaximumRecords)
        {
            throw InvalidState();
        }

        var intervalKeys = new HashSet<string>(StringComparer.Ordinal);
        var idempotencyKeys = new HashSet<string>(StringComparer.Ordinal);
        var nonterminalCount = 0;
        foreach (var attempt in state.Attempts)
        {
            if (attempt is null)
            {
                throw InvalidState();
            }

            var triggerLimit = ParseTriggerLimit(attempt.TriggerLimit);
            var phase = ParsePhase(attempt.Phase);
            if (string.IsNullOrWhiteSpace(attempt.IntervalKey)
                || attempt.IntervalKey.Length > MaximumIntervalKeyLength
                || !string.Equals(
                    attempt.IntervalKey,
                    BuildIntervalKey(
                        triggerLimit,
                        attempt.NormalizedDurationMinutes,
                        attempt.ResetsAt),
                    StringComparison.Ordinal)
                || attempt.ThresholdPercent is < GuardSettings.MinimumThreshold
                    or > GuardSettings.MaximumThreshold
                || attempt.NormalizedDurationMinutes != ExpectedDuration(triggerLimit)
                || attempt.ResetsAt <= 0
                || !Guid.TryParseExact(attempt.IdempotencyKey, "D", out var parsedId)
                || !string.Equals(
                    parsedId.ToString("D"),
                    attempt.IdempotencyKey,
                    StringComparison.Ordinal)
                || attempt.DispatchCount is < 0 or > MaximumDispatchCount
                || attempt.PreparedAt == default
                || attempt.UpdatedAt < attempt.PreparedAt
                || !intervalKeys.Add(attempt.IntervalKey)
                || !idempotencyKeys.Add(attempt.IdempotencyKey))
            {
                throw InvalidState();
            }

            if (phase != LiveAttemptPhase.Terminal)
            {
                nonterminalCount++;
            }

            ValidatePhaseFields(attempt, phase);
        }

        if (nonterminalCount > 1)
        {
            throw InvalidState();
        }
    }

    private static void ValidatePhaseFields(
        StoredLiveAttempt attempt,
        LiveAttemptPhase phase)
    {
        if (phase == LiveAttemptPhase.Pending)
        {
            if (!HasValidProtectedCredit(attempt.ProtectedCreditId)
                || attempt.Outcome is not null
                || attempt.BlockReason is not null
                || attempt.RefreshRequired
                || attempt.CompletedAt is not null)
            {
                throw InvalidState();
            }

            return;
        }

        if (phase == LiveAttemptPhase.Terminal)
        {
            if (attempt.ProtectedCreditId is not null
                || attempt.Outcome is null
                || attempt.BlockReason is not null
                || attempt.CompletedAt is null
                || attempt.CompletedAt < attempt.PreparedAt)
            {
                throw InvalidState();
            }

            _ = ParseOutcome(attempt.Outcome);
            return;
        }

        if (!HasValidProtectedCredit(attempt.ProtectedCreditId)
            || attempt.Outcome is not null
            || attempt.BlockReason is null
            || attempt.RefreshRequired
            || attempt.CompletedAt is not null)
        {
            throw InvalidState();
        }

        var blockReason = ParseBlockReason(attempt.BlockReason);
        if ((phase == LiveAttemptPhase.ProtocolBlocked)
                != (blockReason == LiveAttemptBlockReason.ProtocolMismatch))
        {
            throw InvalidState();
        }
    }

    private static bool HasValidProtectedCredit(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaximumProtectedCreditLength
        && IsBase64(value);

    private static bool IsBase64(string value)
    {
        if (value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        try
        {
            var bytes = Convert.FromBase64String(value);
            return bytes.Length > 0
                && string.Equals(
                    Convert.ToBase64String(bytes),
                    value,
                    StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static StoredLiveAttempt FindRequiredAttempt(
        LiveAttemptState state,
        string intervalKey) => state.Attempts.SingleOrDefault(attempt =>
            string.Equals(attempt.IntervalKey, intervalKey, StringComparison.Ordinal))
        ?? throw new LiveStateException("live_attempt_missing");

    private static string BuildIntervalKey(
        TriggerLimit triggerLimit,
        long durationMinutes,
        long resetsAt)
    {
        var trigger = triggerLimit == TriggerLimit.FiveHour ? "fiveHour" : "weekly";
        return FormattableString.Invariant(
            $"codex|{trigger}|{durationMinutes}|{resetsAt}");
    }

    private static long ExpectedDuration(TriggerLimit triggerLimit) => triggerLimit switch
    {
        TriggerLimit.FiveHour => 300,
        TriggerLimit.Weekly => 10_080,
        _ => throw InvalidState(),
    };

    private static string ToCode(ConsumeResetCreditOutcome outcome) => outcome switch
    {
        ConsumeResetCreditOutcome.Reset => "reset",
        ConsumeResetCreditOutcome.NothingToReset => "nothingToReset",
        ConsumeResetCreditOutcome.NoCredit => "noCredit",
        ConsumeResetCreditOutcome.AlreadyRedeemed => "alreadyRedeemed",
        _ => throw InvalidState(),
    };

    private static string ToCode(LiveAttemptBlockReason reason) => reason switch
    {
        LiveAttemptBlockReason.ContextChanged => "contextChanged",
        LiveAttemptBlockReason.SecretUnavailable => "secretUnavailable",
        LiveAttemptBlockReason.ProtocolMismatch => "protocolMismatch",
        LiveAttemptBlockReason.UnknownFailure => "unknownFailure",
        LiveAttemptBlockReason.DispatchLimitReached => "dispatchLimitReached",
        _ => throw InvalidState(),
    };

    private static TriggerLimit ParseTriggerLimit(string value) => value switch
    {
        "fiveHour" => TriggerLimit.FiveHour,
        "weekly" => TriggerLimit.Weekly,
        _ => throw InvalidState(),
    };

    private static LiveAttemptPhase ParsePhase(string value) => value switch
    {
        "pending" => LiveAttemptPhase.Pending,
        "terminal" => LiveAttemptPhase.Terminal,
        "needsReview" => LiveAttemptPhase.NeedsReview,
        "protocolBlocked" => LiveAttemptPhase.ProtocolBlocked,
        _ => throw InvalidState(),
    };

    private static ConsumeResetCreditOutcome ParseOutcome(string value) => value switch
    {
        "reset" => ConsumeResetCreditOutcome.Reset,
        "nothingToReset" => ConsumeResetCreditOutcome.NothingToReset,
        "noCredit" => ConsumeResetCreditOutcome.NoCredit,
        "alreadyRedeemed" => ConsumeResetCreditOutcome.AlreadyRedeemed,
        _ => throw InvalidState(),
    };

    private static LiveAttemptBlockReason ParseBlockReason(string value) => value switch
    {
        "contextChanged" => LiveAttemptBlockReason.ContextChanged,
        "secretUnavailable" => LiveAttemptBlockReason.SecretUnavailable,
        "protocolMismatch" => LiveAttemptBlockReason.ProtocolMismatch,
        "unknownFailure" => LiveAttemptBlockReason.UnknownFailure,
        "dispatchLimitReached" => LiveAttemptBlockReason.DispatchLimitReached,
        _ => throw InvalidState(),
    };

    private static void EnsureNoDuplicateMembers(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new JsonException();
                }

                EnsureNoDuplicateMembers(property.Value);
            }

            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                EnsureNoDuplicateMembers(item);
            }
        }
    }

    private static bool IsFatal(Exception exception) => exception is
        OutOfMemoryException
        or StackOverflowException
        or AccessViolationException;

    private static void TryDelete(string temporaryPath)
    {
        try
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static LiveStateException InvalidState() => new("live_state_invalid");

    private sealed record LiveAttemptState
    {
        [JsonRequired]
        public int SchemaVersion { get; init; } = 1;

        [JsonRequired]
        public List<StoredLiveAttempt> Attempts { get; init; } = [];
    }
}

internal sealed record LiveAttemptCandidate(
    string IntervalKey,
    int ThresholdPercent,
    long NormalizedDurationMinutes,
    long ResetsAt);

internal enum LivePrepareDisposition
{
    Prepared,
    ExistingActive,
    ExistingTerminal,
}

internal sealed record PrepareLiveAttemptResult(
    LivePrepareDisposition Disposition,
    StoredLiveAttempt Attempt);

internal sealed record StoredLiveAttempt
{
    [JsonRequired]
    public string IntervalKey { get; init; } = string.Empty;

    [JsonRequired]
    public string TriggerLimit { get; init; } = string.Empty;

    [JsonRequired]
    public int ThresholdPercent { get; init; }

    [JsonRequired]
    public long NormalizedDurationMinutes { get; init; }

    [JsonRequired]
    public long ResetsAt { get; init; }

    [JsonRequired]
    public string IdempotencyKey { get; init; } = string.Empty;

    [JsonRequired]
    public string? ProtectedCreditId { get; set; }

    [JsonRequired]
    public string Phase { get; set; } = string.Empty;

    [JsonRequired]
    public int DispatchCount { get; set; }

    [JsonRequired]
    public string? Outcome { get; set; }

    [JsonRequired]
    public string? BlockReason { get; set; }

    [JsonRequired]
    public bool RefreshRequired { get; set; }

    [JsonRequired]
    public DateTimeOffset PreparedAt { get; init; }

    [JsonRequired]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonRequired]
    public DateTimeOffset? CompletedAt { get; set; }
}
