using System.Text.Json;
using System.Text.Json.Serialization;
using CodexAutoReset.Core;

namespace CodexAutoReset.Runtime;

public enum WeeklyUsageResetTrackingStatus
{
    BaselineEstablished,
    NoReset,
    ResetDetected,
    ObservationIgnored,
    StateUnavailable,
}

public sealed record WeeklyUsageResetTrackingResult(
    WeeklyUsageResetTrackingStatus Status,
    WeeklyUsageResetDetection? Detection);

public enum AutomaticCreditAttributionTrackingStatus
{
    Recorded,
    Ignored,
    StateUnavailable,
}

public sealed class JsonWeeklyUsageResetTracker
{
    private const string RequiredFileName = "usage-reset-state.json";
    private const long MaximumDocumentBytes = 64 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);

    public JsonWeeklyUsageResetTracker(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = System.IO.Path.GetFullPath(path);
    }

    public string Path => path;

    public async Task<WeeklyUsageResetTrackingResult> ObserveAsync(
        WeeklyUsageObservation observation,
        WeeklyUsageResetAttribution attribution,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);

        if (!WeeklyUsageResetDetector.IsValid(observation)
            || !Enum.IsDefined(attribution))
        {
            return Ignored();
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            UsageResetStateDocument state;
            try
            {
                state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is JsonException or InvalidDataException)
            {
                return await TryRebaselineAfterInvalidStateAsync(
                    observation,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException
                && !IsFatal(exception))
            {
                return Unavailable();
            }

            var previous = state.LastObservation?.ToObservation();
            var pendingAttribution = state.PendingAutomaticCredit;
            var pendingChanged = false;
            var explicitAttributionApplies = false;
            if (attribution
                    == WeeklyUsageResetAttribution.AutomaticCreditSucceeded
                && previous is not null
                && observation.ObservedAt >= previous.ObservedAt
                && observation.ResetsAt >= previous.ResetsAt)
            {
                explicitAttributionApplies = true;
                if (observation.ObservedAt
                    < DateTimeOffset.FromUnixTimeSeconds(previous.ResetsAt))
                {
                    pendingAttribution = new StoredAutomaticCreditAttribution
                    {
                        SucceededAt = observation.ObservedAt,
                        BaselineResetsAt = previous.ResetsAt,
                    };
                    pendingChanged = true;
                }
            }

            if (pendingAttribution is not null
                && observation.ObservedAt
                    >= DateTimeOffset.FromUnixTimeSeconds(
                        pendingAttribution.BaselineResetsAt))
            {
                pendingAttribution = null;
                pendingChanged = true;
            }

            var effectiveAttribution =
                explicitAttributionApplies
                    || IsApplicable(pendingAttribution, previous, observation)
                    ? WeeklyUsageResetAttribution.AutomaticCreditSucceeded
                    : WeeklyUsageResetAttribution.None;
            var evaluation = WeeklyUsageResetDetector.Evaluate(
                previous,
                observation,
                effectiveAttribution);

            if (!evaluation.ShouldPersistObservation)
            {
                if (pendingChanged)
                {
                    try
                    {
                        await SaveAsync(
                            state with
                            {
                                PendingAutomaticCredit = pendingAttribution,
                            },
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (
                        exception is not OperationCanceledException
                        && !IsFatal(exception))
                    {
                        return Unavailable();
                    }
                }

                return Ignored();
            }

            var updatedState = state with
            {
                LastObservation = StoredWeeklyUsageObservation.FromObservation(
                    observation),
                LastDetection = evaluation.Detection is null
                    ? state.LastDetection
                    : StoredWeeklyUsageResetDetection.FromDetection(
                        evaluation.Detection),
                PendingAutomaticCredit =
                    evaluation.Detection?.Kind
                        == WeeklyUsageResetKind.AutomaticCredit
                            ? null
                            : pendingAttribution,
            };

            try
            {
                await SaveAsync(updatedState, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException
                && !IsFatal(exception))
            {
                return Unavailable();
            }

            return evaluation.Disposition switch
            {
                WeeklyUsageObservationDisposition.FirstObservation =>
                    new WeeklyUsageResetTrackingResult(
                        WeeklyUsageResetTrackingStatus.BaselineEstablished,
                        Detection: null),
                WeeklyUsageObservationDisposition.ResetDetected =>
                    new WeeklyUsageResetTrackingResult(
                        WeeklyUsageResetTrackingStatus.ResetDetected,
                        evaluation.Detection),
                _ => new WeeklyUsageResetTrackingResult(
                    WeeklyUsageResetTrackingStatus.NoReset,
                    Detection: null),
            };
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<WeeklyUsageResetTrackingResult> ObserveAsync(
        WeeklyUsageObservation observation,
        CancellationToken cancellationToken) => ObserveAsync(
            observation,
            WeeklyUsageResetAttribution.None,
            cancellationToken);

    public async Task<AutomaticCreditAttributionTrackingStatus>
        MarkAutomaticCreditSucceededAsync(
            DateTimeOffset succeededAt,
            CancellationToken cancellationToken)
    {
        if (succeededAt < DateTimeOffset.UnixEpoch)
        {
            return AutomaticCreditAttributionTrackingStatus.Ignored;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            UsageResetStateDocument state;
            try
            {
                state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException
                && !IsFatal(exception))
            {
                return AutomaticCreditAttributionTrackingStatus.StateUnavailable;
            }

            var previous = state.LastObservation?.ToObservation();
            if (previous is null
                || succeededAt < previous.ObservedAt
                || succeededAt
                    >= DateTimeOffset.FromUnixTimeSeconds(previous.ResetsAt))
            {
                return AutomaticCreditAttributionTrackingStatus.Ignored;
            }

            var updatedState = state with
            {
                PendingAutomaticCredit = new StoredAutomaticCreditAttribution
                {
                    SucceededAt = succeededAt,
                    BaselineResetsAt = previous.ResetsAt,
                },
            };

            try
            {
                await SaveAsync(updatedState, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException
                && !IsFatal(exception))
            {
                return AutomaticCreditAttributionTrackingStatus.StateUnavailable;
            }

            return AutomaticCreditAttributionTrackingStatus.Recorded;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<UsageResetStateDocument> LoadAsync(
        CancellationToken cancellationToken)
    {
        EnsurePathAllowed();
        if (!File.Exists(path))
        {
            return new UsageResetStateDocument();
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);
        if (stream.Length > MaximumDocumentBytes)
        {
            throw new InvalidDataException();
        }

        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureNoDuplicateMembers(document.RootElement);

        var state = document.RootElement.Deserialize<UsageResetStateDocument>(
            SerializerOptions);
        ValidateState(state);
        return state!;
    }

    private async Task SaveAsync(
        UsageResetStateDocument state,
        CancellationToken cancellationToken)
    {
        EnsurePathAllowed();
        ValidateState(state);

        var directory = System.IO.Path.GetDirectoryName(path)
            ?? throw new InvalidDataException();
        Directory.CreateDirectory(directory);
        EnsurePathAllowed();

        var temporaryPath = System.IO.Path.Combine(
            directory,
            $".{System.IO.Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
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
                    throw new InvalidDataException();
                }
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private async Task<WeeklyUsageResetTrackingResult>
        TryRebaselineAfterInvalidStateAsync(
            WeeklyUsageObservation observation,
            CancellationToken cancellationToken)
    {
        var directory = System.IO.Path.GetDirectoryName(path);
        if (directory is null)
        {
            return Unavailable();
        }

        var quarantinePath = System.IO.Path.Combine(
            directory,
            $".{RequiredFileName}.{Guid.NewGuid():N}.invalid");
        var quarantined = false;

        try
        {
            EnsurePathAllowed();
            if (!File.Exists(path))
            {
                return Unavailable();
            }

            File.Move(path, quarantinePath);
            quarantined = true;

            await SaveAsync(
                new UsageResetStateDocument
                {
                    LastObservation =
                        StoredWeeklyUsageObservation.FromObservation(
                            observation),
                    LastDetection = null,
                    PendingAutomaticCredit = null,
                },
                cancellationToken).ConfigureAwait(false);

            TryDelete(quarantinePath);
            return new WeeklyUsageResetTrackingResult(
                WeeklyUsageResetTrackingStatus.BaselineEstablished,
                Detection: null);
        }
        catch (OperationCanceledException)
        {
            if (quarantined)
            {
                TryRestoreQuarantinedState(quarantinePath);
            }

            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            if (quarantined)
            {
                TryRestoreQuarantinedState(quarantinePath);
            }

            return Unavailable();
        }
    }

    private void EnsurePathAllowed()
    {
        if (!string.Equals(
            System.IO.Path.GetFileName(path),
            RequiredFileName,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException();
        }

        var current = File.Exists(path)
            ? path
            : System.IO.Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException();
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }
    }

    private static void ValidateState(UsageResetStateDocument? state)
    {
        if (state is null
            || state.SchemaVersion != 1
            || state.LastObservation is not { } lastObservation
            || !WeeklyUsageResetDetector.IsValid(lastObservation.ToObservation()))
        {
            throw new InvalidDataException();
        }

        if (state.LastDetection is { } detection)
        {
            detection.Validate();
        }

        if (state.PendingAutomaticCredit is { } pending)
        {
            pending.Validate(lastObservation);
        }
    }

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

    private void TryRestoreQuarantinedState(string quarantinePath)
    {
        try
        {
            if (!File.Exists(path) && File.Exists(quarantinePath))
            {
                File.Move(quarantinePath, path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool IsFatal(Exception exception) =>
        exception is OutOfMemoryException
            or StackOverflowException
            or AccessViolationException;

    private static bool IsApplicable(
        StoredAutomaticCreditAttribution? pending,
        WeeklyUsageObservation? previous,
        WeeklyUsageObservation current) =>
        pending is not null
        && previous is not null
        && pending.BaselineResetsAt == previous.ResetsAt
        && current.ObservedAt >= pending.SucceededAt
        && current.ObservedAt
            < DateTimeOffset.FromUnixTimeSeconds(pending.BaselineResetsAt);

    private static WeeklyUsageResetTrackingResult Ignored() => new(
        WeeklyUsageResetTrackingStatus.ObservationIgnored,
        Detection: null);

    private static WeeklyUsageResetTrackingResult Unavailable() => new(
        WeeklyUsageResetTrackingStatus.StateUnavailable,
        Detection: null);

    private sealed record UsageResetStateDocument
    {
        [JsonRequired]
        public int SchemaVersion { get; init; } = 1;

        [JsonRequired]
        public StoredWeeklyUsageObservation? LastObservation { get; init; }

        [JsonRequired]
        public StoredWeeklyUsageResetDetection? LastDetection { get; init; }

        [JsonRequired]
        public StoredAutomaticCreditAttribution? PendingAutomaticCredit
        {
            get;
            init;
        }
    }

    private sealed record StoredWeeklyUsageObservation
    {
        [JsonRequired]
        public double RemainingPercent { get; init; }

        [JsonRequired]
        public long ResetsAt { get; init; }

        [JsonRequired]
        public DateTimeOffset ObservedAt { get; init; }

        public WeeklyUsageObservation ToObservation() => new(
            RemainingPercent,
            ResetsAt,
            ObservedAt);

        public static StoredWeeklyUsageObservation FromObservation(
            WeeklyUsageObservation observation) => new()
            {
                RemainingPercent = observation.RemainingPercent,
                ResetsAt = observation.ResetsAt,
                ObservedAt = observation.ObservedAt,
            };
    }

    private sealed record StoredWeeklyUsageResetDetection
    {
        [JsonRequired]
        public string Kind { get; init; } = string.Empty;

        [JsonRequired]
        public long NextResetsAt { get; init; }

        [JsonRequired]
        public DateTimeOffset DetectedAt { get; init; }

        public void Validate()
        {
            if (Kind is not ("scheduled" or "early" or "automaticCredit")
                || NextResetsAt is <= 0 or > 253_402_300_799
                || DetectedAt < DateTimeOffset.UnixEpoch)
            {
                throw new InvalidDataException();
            }
        }

        public static StoredWeeklyUsageResetDetection FromDetection(
            WeeklyUsageResetDetection detection) => new()
            {
                Kind = detection.Kind switch
                {
                    WeeklyUsageResetKind.Scheduled => "scheduled",
                    WeeklyUsageResetKind.Early => "early",
                    WeeklyUsageResetKind.AutomaticCredit => "automaticCredit",
                    _ => throw new InvalidDataException(),
                },
                NextResetsAt = detection.NextResetsAt,
                DetectedAt = detection.DetectedAt,
            };
    }

    private sealed record StoredAutomaticCreditAttribution
    {
        [JsonRequired]
        public DateTimeOffset SucceededAt { get; init; }

        [JsonRequired]
        public long BaselineResetsAt { get; init; }

        public void Validate(StoredWeeklyUsageObservation baseline)
        {
            if (SucceededAt < DateTimeOffset.UnixEpoch
                || BaselineResetsAt != baseline.ResetsAt
                || SucceededAt
                    >= DateTimeOffset.FromUnixTimeSeconds(BaselineResetsAt))
            {
                throw new InvalidDataException();
            }
        }
    }
}
