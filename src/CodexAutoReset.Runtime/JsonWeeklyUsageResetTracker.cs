using System.Globalization;
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

public sealed record PendingUsageResetNotification(
    string EventId,
    WeeklyUsageResetDetection Detection);

public enum AutomaticCreditAttributionTrackingStatus
{
    Recorded,
    Ignored,
    StateUnavailable,
}

public sealed class JsonWeeklyUsageResetTracker
{
    private const string RequiredFileName = "usage-reset-state.json";
    private const int CurrentSchemaVersion = 2;
    private const long MaximumDocumentBytes = 64 * 1024;
    private const int MaximumNotificationEventCount = 128;
    private const int MaximumPendingNotificationCount = 64;
    private const int RetainedResolvedNotificationCount = 32;
    private const double SaturationJitterFloorPercent = 99d;
    private static readonly TimeSpan RollingResetTimeSlack =
        TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false),
        },
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
        bool notificationsEnabled,
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

            var detection = evaluation.Detection;
            if (ShouldMergeSaturatedRollingAdvance(
                    previous,
                    observation,
                    detection)
                || ShouldMergeWithPersistedRecoveryEpisode(
                    state.LastDetection,
                    previous,
                    observation,
                    detection))
            {
                detection = null;
            }

            var notificationEvents = state.NotificationEvents.ToList();
            if (detection is not null)
            {
                if (!TryAppendNotificationEvent(
                        notificationEvents,
                        detection,
                        notificationsEnabled))
                {
                    return Unavailable();
                }
            }

            var updatedState = state with
            {
                LastObservation = StoredWeeklyUsageObservation.FromObservation(
                    observation),
                LastDetection = detection is null
                    ? state.LastDetection
                    : StoredWeeklyUsageResetDetection.FromDetection(
                        detection),
                PendingAutomaticCredit =
                    detection?.Kind
                        == WeeklyUsageResetKind.AutomaticCredit
                            ? null
                            : pendingAttribution,
                NotificationEvents = PruneNotificationEvents(
                    notificationEvents),
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
                WeeklyUsageObservationDisposition.ResetDetected
                    when detection is not null =>
                    new WeeklyUsageResetTrackingResult(
                        WeeklyUsageResetTrackingStatus.ResetDetected,
                        detection),
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
        WeeklyUsageResetAttribution attribution,
        CancellationToken cancellationToken) => ObserveAsync(
            observation,
            attribution,
            notificationsEnabled: true,
            cancellationToken);

    public Task<WeeklyUsageResetTrackingResult> ObserveAsync(
        WeeklyUsageObservation observation,
        CancellationToken cancellationToken) => ObserveAsync(
            observation,
            WeeklyUsageResetAttribution.None,
            notificationsEnabled: true,
            cancellationToken);

    public async Task<IReadOnlyList<PendingUsageResetNotification>>
        LoadPendingNotificationsAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                var state = await LoadAsync(cancellationToken)
                    .ConfigureAwait(false);
                return state.NotificationEvents
                    .Where(notification =>
                        notification.AttentionState
                            == StoredNotificationAttentionState.Pending)
                    .Select(notification => notification.ToPending())
                    .ToArray();
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException
                && !IsFatal(exception))
            {
                return [];
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> AcknowledgeNotificationAsync(
        string eventId,
        DateTimeOffset acknowledgedAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        if (acknowledgedAt < DateTimeOffset.UnixEpoch)
        {
            throw new ArgumentOutOfRangeException(nameof(acknowledgedAt));
        }

        return await ResolveNotificationsAsync(
            notification => string.Equals(
                notification.EventId,
                eventId,
                StringComparison.Ordinal),
            StoredNotificationAttentionState.Acknowledged,
            acknowledgedAt,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> SuppressPendingNotificationsAsync(
        DateTimeOffset suppressedAt,
        CancellationToken cancellationToken)
    {
        if (suppressedAt < DateTimeOffset.UnixEpoch)
        {
            throw new ArgumentOutOfRangeException(nameof(suppressedAt));
        }

        return ResolveNotificationsAsync(
            notification =>
                notification.AttentionState
                    == StoredNotificationAttentionState.Pending,
            StoredNotificationAttentionState.Suppressed,
            suppressedAt,
            cancellationToken);
    }

    public Task<bool> SuppressPendingNotificationsThroughAsync(
        DateTimeOffset detectedOnOrBefore,
        DateTimeOffset suppressedAt,
        CancellationToken cancellationToken)
    {
        if (detectedOnOrBefore < DateTimeOffset.UnixEpoch)
        {
            throw new ArgumentOutOfRangeException(
                nameof(detectedOnOrBefore));
        }

        if (suppressedAt < DateTimeOffset.UnixEpoch)
        {
            throw new ArgumentOutOfRangeException(nameof(suppressedAt));
        }

        return ResolveNotificationsAsync(
            notification =>
                notification.AttentionState
                    == StoredNotificationAttentionState.Pending
                && notification.DetectedAt <= detectedOnOrBefore,
            StoredNotificationAttentionState.Suppressed,
            suppressedAt,
            cancellationToken);
    }

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

    private async Task<bool> ResolveNotificationsAsync(
        Func<StoredUsageResetNotification, bool> shouldResolve,
        StoredNotificationAttentionState resolvedState,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                var state = await LoadAsync(cancellationToken)
                    .ConfigureAwait(false);
                var changed = false;
                var notificationEvents = state.NotificationEvents
                    .Select(notification =>
                    {
                        if (notification.AttentionState
                                != StoredNotificationAttentionState.Pending
                            || !shouldResolve(notification))
                        {
                            return notification;
                        }

                        changed = true;
                        return notification with
                        {
                            AttentionState = resolvedState,
                            ResolvedAt = resolvedAt < notification.DetectedAt
                                ? notification.DetectedAt
                                : resolvedAt,
                        };
                    })
                    .ToList();
                if (!changed)
                {
                    return true;
                }

                await SaveAsync(
                    state with
                    {
                        NotificationEvents = PruneNotificationEvents(
                            notificationEvents),
                    },
                    cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException
                && !IsFatal(exception))
            {
                return false;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool TryAppendNotificationEvent(
        List<StoredUsageResetNotification> notificationEvents,
        WeeklyUsageResetDetection detection,
        bool notificationsEnabled)
    {
        var eventId = BuildNotificationEventId(detection);
        if (notificationEvents.Any(notification => string.Equals(
                notification.EventId,
                eventId,
                StringComparison.Ordinal)))
        {
            return true;
        }

        if (notificationsEnabled
            && notificationEvents.Count(notification =>
                notification.AttentionState
                    == StoredNotificationAttentionState.Pending)
                >= MaximumPendingNotificationCount)
        {
            return false;
        }

        notificationEvents.Add(
            StoredUsageResetNotification.FromDetection(
                detection,
                notificationsEnabled
                    ? StoredNotificationAttentionState.Pending
                    : StoredNotificationAttentionState.Suppressed,
                notificationsEnabled ? null : detection.DetectedAt));
        return true;
    }

    private static List<StoredUsageResetNotification>
        PruneNotificationEvents(
            IReadOnlyList<StoredUsageResetNotification> notificationEvents)
    {
        var pending = notificationEvents
            .Where(notification =>
                notification.AttentionState
                    == StoredNotificationAttentionState.Pending);
        var resolved = notificationEvents
            .Where(notification =>
                notification.AttentionState
                    != StoredNotificationAttentionState.Pending)
            .TakeLast(RetainedResolvedNotificationCount);
        return pending
            .Concat(resolved)
            .OrderBy(notification => notification.DetectedAt)
            .Take(MaximumNotificationEventCount)
            .ToList();
    }

    private static string BuildNotificationEventId(
        WeeklyUsageResetDetection detection) => string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)detection.Kind}:{detection.NextResetsAt}:"
                + $"{detection.DetectedAt.ToUniversalTime().Ticks}");

    private static UsageResetStateDocument MigrateVersionOne(
        UsageResetStateDocumentV1? state)
    {
        if (state is null)
        {
            throw new InvalidDataException();
        }

        return new UsageResetStateDocument
        {
            LastObservation = state.LastObservation,
            LastDetection = state.LastDetection,
            PendingAutomaticCredit = state.PendingAutomaticCredit,
            NotificationEvents = [],
        };
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

        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty(
                "schemaVersion",
                out var schemaElement)
            || !schemaElement.TryGetInt32(out var schemaVersion))
        {
            throw new InvalidDataException();
        }

        var state = schemaVersion switch
        {
            1 => MigrateVersionOne(
                document.RootElement.Deserialize<UsageResetStateDocumentV1>(
                    SerializerOptions)),
            CurrentSchemaVersion =>
                document.RootElement.Deserialize<UsageResetStateDocument>(
                    SerializerOptions),
            _ => throw new InvalidDataException(),
        };
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
            || state.SchemaVersion != CurrentSchemaVersion
            || state.LastObservation is not { } lastObservation
            || !WeeklyUsageResetDetector.IsValid(lastObservation.ToObservation())
            || state.NotificationEvents is null
            || state.NotificationEvents.Count > MaximumNotificationEventCount
            || state.NotificationEvents.Any(notification =>
                notification is null)
            || state.NotificationEvents.Count(notification =>
                notification?.AttentionState
                    == StoredNotificationAttentionState.Pending)
                > MaximumPendingNotificationCount)
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

        var eventIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var notification in state.NotificationEvents)
        {
            if (notification is null)
            {
                throw new InvalidDataException();
            }

            notification.Validate();
            if (!eventIds.Add(notification.EventId))
            {
                throw new InvalidDataException();
            }
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

    private static bool ShouldMergeWithPersistedRecoveryEpisode(
        StoredWeeklyUsageResetDetection? episode,
        WeeklyUsageObservation? previous,
        WeeklyUsageObservation current,
        WeeklyUsageResetDetection? candidate)
    {
        if (episode is null
            || previous is null
            || candidate?.Kind != WeeklyUsageResetKind.Early
            || current.ObservedAt < episode.DetectedAt
            || !IsWithinRollingResetSchedule(episode, current))
        {
            return false;
        }

        var remainingIncreased =
            current.RemainingPercent > previous.RemainingPercent;
        if (current.ResetsAt > previous.ResetsAt)
        {
            return !remainingIncreased
                || IsSaturationJitter(previous, current);
        }

        return current.ResetsAt == previous.ResetsAt
            && IsSaturationJitter(previous, current);
    }

    private static bool ShouldMergeSaturatedRollingAdvance(
        WeeklyUsageObservation? previous,
        WeeklyUsageObservation current,
        WeeklyUsageResetDetection? candidate)
    {
        if (previous is null
            || candidate?.Kind != WeeklyUsageResetKind.Early
            || previous.RemainingPercent < SaturationJitterFloorPercent
            || current.RemainingPercent < SaturationJitterFloorPercent
            || current.ResetsAt <= previous.ResetsAt)
        {
            return false;
        }

        var resetAdvanceSeconds = current.ResetsAt - previous.ResetsAt;
        var observationElapsed = current.ObservedAt - previous.ObservedAt;
        return observationElapsed >= TimeSpan.Zero
            && resetAdvanceSeconds
                <= observationElapsed.TotalSeconds
                    + RollingResetTimeSlack.TotalSeconds;
    }

    private static bool IsWithinRollingResetSchedule(
        StoredWeeklyUsageResetDetection episode,
        WeeklyUsageObservation current)
    {
        var resetAdvanceSeconds = current.ResetsAt - episode.NextResetsAt;
        if (resetAdvanceSeconds < 0)
        {
            return false;
        }

        var elapsed = current.ObservedAt - episode.DetectedAt;
        return resetAdvanceSeconds
            <= elapsed.TotalSeconds + RollingResetTimeSlack.TotalSeconds;
    }

    private static bool IsSaturationJitter(
        WeeklyUsageObservation previous,
        WeeklyUsageObservation current) =>
        previous.RemainingPercent >= SaturationJitterFloorPercent
        && current.RemainingPercent >= SaturationJitterFloorPercent
        && current.RemainingPercent > previous.RemainingPercent;

    private static WeeklyUsageResetTrackingResult Ignored() => new(
        WeeklyUsageResetTrackingStatus.ObservationIgnored,
        Detection: null);

    private static WeeklyUsageResetTrackingResult Unavailable() => new(
        WeeklyUsageResetTrackingStatus.StateUnavailable,
        Detection: null);

    private sealed record UsageResetStateDocument
    {
        [JsonRequired]
        public int SchemaVersion { get; init; } = CurrentSchemaVersion;

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

        [JsonRequired]
        public List<StoredUsageResetNotification> NotificationEvents
        {
            get;
            init;
        } = [];
    }

    private sealed record UsageResetStateDocumentV1
    {
        [JsonRequired]
        public int SchemaVersion { get; init; }

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

    private enum StoredNotificationAttentionState
    {
        Pending,
        Acknowledged,
        Suppressed,
    }

    private sealed record StoredUsageResetNotification
    {
        [JsonRequired]
        public string EventId { get; init; } = string.Empty;

        [JsonRequired]
        public string Kind { get; init; } = string.Empty;

        [JsonRequired]
        public long NextResetsAt { get; init; }

        [JsonRequired]
        public DateTimeOffset DetectedAt { get; init; }

        [JsonRequired]
        public StoredNotificationAttentionState AttentionState { get; init; }

        [JsonRequired]
        public DateTimeOffset? ResolvedAt { get; init; }

        public PendingUsageResetNotification ToPending()
        {
            var detection = ToDetection();
            return new PendingUsageResetNotification(EventId, detection);
        }

        public void Validate()
        {
            var detection = ToDetection();
            if (!string.Equals(
                    EventId,
                    BuildNotificationEventId(detection),
                    StringComparison.Ordinal)
                || !Enum.IsDefined(AttentionState)
                || AttentionState == StoredNotificationAttentionState.Pending
                    && ResolvedAt is not null
                || AttentionState != StoredNotificationAttentionState.Pending
                    && (ResolvedAt is null
                        || ResolvedAt < detection.DetectedAt))
            {
                throw new InvalidDataException();
            }
        }

        public static StoredUsageResetNotification FromDetection(
            WeeklyUsageResetDetection detection,
            StoredNotificationAttentionState attentionState,
            DateTimeOffset? resolvedAt) => new()
            {
                EventId = BuildNotificationEventId(detection),
                Kind = detection.Kind switch
                {
                    WeeklyUsageResetKind.Scheduled => "scheduled",
                    WeeklyUsageResetKind.Early => "early",
                    WeeklyUsageResetKind.AutomaticCredit => "automaticCredit",
                    _ => throw new InvalidDataException(),
                },
                NextResetsAt = detection.NextResetsAt,
                DetectedAt = detection.DetectedAt,
                AttentionState = attentionState,
                ResolvedAt = resolvedAt,
            };

        private WeeklyUsageResetDetection ToDetection()
        {
            var kind = Kind switch
            {
                "scheduled" => WeeklyUsageResetKind.Scheduled,
                "early" => WeeklyUsageResetKind.Early,
                "automaticCredit" => WeeklyUsageResetKind.AutomaticCredit,
                _ => throw new InvalidDataException(),
            };
            var detection = new WeeklyUsageResetDetection(
                kind,
                NextResetsAt,
                DetectedAt);
            if (!Enum.IsDefined(detection.Kind)
                || detection.NextResetsAt is <= 0 or > 253_402_300_799
                || detection.DetectedAt < DateTimeOffset.UnixEpoch)
            {
                throw new InvalidDataException();
            }

            return detection;
        }
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
