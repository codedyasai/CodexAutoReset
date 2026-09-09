using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexAutoReset.Core;

namespace CodexAutoReset.Runtime;

public enum SupplementalNotificationKind
{
    FiveHourScheduledReset,
    FiveHourEarlyReset,
    FiveHourAutomaticCredit,
    CreditGranted,
}

public sealed record PendingSupplementalNotification(
    string EventId,
    SupplementalNotificationKind Kind,
    DateTimeOffset DetectedAt,
    long? NextResetsAt = null,
    int AddedCreditCount = 0,
    IReadOnlyList<long?>? AddedCreditExpirations = null,
    bool CreditExpirationDetailsAvailable = true);

public sealed record SupplementalTrackingResult(
    bool StateAvailable,
    IReadOnlyList<PendingSupplementalNotification> NewNotifications);

public sealed class JsonSupplementalNotificationTracker
{
    private const string RequiredFileName = "supplemental-notification-state.json";
    private const int CurrentSchemaVersion = 1;
    private const long MaximumDocumentBytes = 64 * 1024;
    private const int MaximumNotificationEventCount = 128;
    private const int MaximumPendingNotificationCount = 64;
    private const int RetainedResolvedNotificationCount = 32;
    private const int MaximumKnownCreditCount = 512;
    private const int MaximumStoredCreditExpirations = 64;
    private const double SaturationJitterFloorPercent = 99d;
    private static readonly TimeSpan RollingResetTimeSlack = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AutomaticCreditAttributionGrace = TimeSpan.FromMinutes(10);

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

    public JsonSupplementalNotificationTracker(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
    }

    public async Task<SupplementalTrackingResult> ObserveAsync(
        WindowReading? fiveHour,
        ResetCreditSummary? resetCredits,
        DateTimeOffset observedAt,
        WeeklyUsageResetAttribution attribution,
        bool fiveHourNotificationsEnabled,
        bool creditNotificationsEnabled,
        CancellationToken cancellationToken)
    {
        if (observedAt < DateTimeOffset.UnixEpoch || !Enum.IsDefined(attribution))
        {
            return Unavailable();
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SupplementalState state;
            try
            {
                state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (UnsupportedSupplementalSchemaException)
            {
                return Unavailable();
            }
            catch (Exception exception) when (
                exception is JsonException or InvalidDataException)
            {
                return await TryRebaselineAfterInvalidStateAsync(
                    fiveHour,
                    resetCredits,
                    observedAt,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException && !IsFatal(exception))
            {
                return Unavailable();
            }

            var events = state.NotificationEvents.ToList();
            var newNotifications = new List<PendingSupplementalNotification>();
            var updated = ObserveFiveHour(
                state,
                events,
                newNotifications,
                fiveHour,
                observedAt,
                attribution,
                fiveHourNotificationsEnabled);
            updated = ObserveCredits(
                updated,
                events,
                newNotifications,
                resetCredits,
                observedAt,
                creditNotificationsEnabled);
            updated = updated with
            {
                NotificationEvents = PruneNotificationEvents(events),
            };

            try
            {
                await SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException && !IsFatal(exception))
            {
                return Unavailable();
            }

            return new SupplementalTrackingResult(true, newNotifications);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task MarkAutomaticCreditSucceededAsync(
        DateTimeOffset succeededAt,
        CancellationToken cancellationToken)
    {
        if (succeededAt < DateTimeOffset.UnixEpoch)
        {
            return;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                var state = await LoadAsync(cancellationToken).ConfigureAwait(false);
                var previous = state.LastFiveHourObservation?.ToObservation();
                if (previous is null
                    || succeededAt < previous.ObservedAt
                    || succeededAt >= DateTimeOffset.FromUnixTimeSeconds(previous.ResetsAt))
                {
                    return;
                }

                await SaveAsync(
                    state with
                    {
                        PendingAutomaticCredit = new StoredAutomaticCreditAttribution
                        {
                            SucceededAt = succeededAt,
                            BaselineResetsAt = previous.ResetsAt,
                        },
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException && !IsFatal(exception))
            {
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<PendingSupplementalNotification>>
        LoadPendingNotificationsAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                var state = await LoadAsync(cancellationToken).ConfigureAwait(false);
                return state.NotificationEvents
                    .Where(item => item.AttentionState == StoredAttentionState.Pending)
                    .Select(item => item.ToPending())
                    .ToArray();
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException && !IsFatal(exception))
            {
                return [];
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<bool> AcknowledgeNotificationAsync(
        string eventId,
        DateTimeOffset acknowledgedAt,
        CancellationToken cancellationToken) => ResolveNotificationsAsync(
            notification => string.Equals(
                notification.EventId,
                eventId,
                StringComparison.Ordinal),
            StoredAttentionState.Acknowledged,
            acknowledgedAt,
            cancellationToken);

    public Task<bool> SuppressNotificationsAsync(
        IReadOnlySet<string> eventIds,
        DateTimeOffset suppressedAt,
        CancellationToken cancellationToken) => ResolveNotificationsAsync(
            notification => eventIds.Contains(notification.EventId),
            StoredAttentionState.Suppressed,
            suppressedAt,
            cancellationToken);

    public Task<bool> SuppressPendingNotificationsThroughAsync(
        NotificationPreference preference,
        DateTimeOffset detectedOnOrBefore,
        DateTimeOffset suppressedAt,
        CancellationToken cancellationToken)
    {
        if (preference is not (
                NotificationPreference.FiveHourUsageReset
                or NotificationPreference.CreditGranted))
        {
            throw new ArgumentOutOfRangeException(nameof(preference));
        }

        return ResolveNotificationsAsync(
            notification => notification.DetectedAt <= detectedOnOrBefore
                && MatchesPreference(notification.Kind, preference),
            StoredAttentionState.Suppressed,
            suppressedAt,
            cancellationToken);
    }

    private static SupplementalState ObserveFiveHour(
        SupplementalState state,
        List<StoredNotification> events,
        List<PendingSupplementalNotification> newNotifications,
        WindowReading? reading,
        DateTimeOffset observedAt,
        WeeklyUsageResetAttribution attribution,
        bool notificationsEnabled)
    {
        if (reading is null)
        {
            return state;
        }

        var current = new WeeklyUsageObservation(
            reading.RemainingPercent,
            reading.ResetsAt,
            observedAt);
        if (!WeeklyUsageResetDetector.IsValid(current))
        {
            return state;
        }

        var previous = state.LastFiveHourObservation?.ToObservation();
        var pending = state.PendingAutomaticCredit;
        var explicitAttributionApplies = attribution
                == WeeklyUsageResetAttribution.AutomaticCreditSucceeded
            && previous is not null
            && current.ObservedAt >= previous.ObservedAt
            && current.ResetsAt >= previous.ResetsAt;
        if (explicitAttributionApplies
            && current.ObservedAt < DateTimeOffset.FromUnixTimeSeconds(previous!.ResetsAt))
        {
            pending = new StoredAutomaticCreditAttribution
            {
                SucceededAt = current.ObservedAt,
                BaselineResetsAt = previous.ResetsAt,
            };
        }

        if (pending is not null
            && current.ObservedAt >= DateTimeOffset
                .FromUnixTimeSeconds(pending.BaselineResetsAt)
                .Add(AutomaticCreditAttributionGrace))
        {
            pending = null;
        }

        var effectiveAttribution = explicitAttributionApplies
                || IsApplicable(pending, previous, current)
            ? WeeklyUsageResetAttribution.AutomaticCreditSucceeded
            : WeeklyUsageResetAttribution.None;
        var evaluation = WeeklyUsageResetDetector.Evaluate(
            previous,
            current,
            effectiveAttribution);
        if (!evaluation.ShouldPersistObservation)
        {
            return state with { PendingAutomaticCredit = pending };
        }

        var candidate = evaluation.Detection;
        if (ShouldMergeSaturatedRollingAdvance(previous, current, candidate)
            || ShouldMergeWithPersistedRecoveryEpisode(
                state.LastFiveHourDetection,
                previous,
                current,
                candidate))
        {
            candidate = null;
        }

        StoredFiveHourDetection? storedDetection = null;
        if (candidate is not null)
        {
            storedDetection = StoredFiveHourDetection.FromDetection(candidate);
            var notification = StoredNotification.FromFiveHour(
                candidate,
                notificationsEnabled);
            if (TryAppend(events, notification))
            {
                if (notificationsEnabled)
                {
                    newNotifications.Add(notification.ToPending());
                }
            }
        }

        return state with
        {
            LastFiveHourObservation = StoredObservation.FromObservation(current),
            LastFiveHourDetection = storedDetection ?? state.LastFiveHourDetection,
            PendingAutomaticCredit = candidate?.Kind
                    == WeeklyUsageResetKind.AutomaticCredit
                ? null
                : pending,
        };
    }

    private static SupplementalState ObserveCredits(
        SupplementalState state,
        List<StoredNotification> events,
        List<PendingSupplementalNotification> newNotifications,
        ResetCreditSummary? summary,
        DateTimeOffset observedAt,
        bool notificationsEnabled)
    {
        if (summary?.Credits is not { } credits)
        {
            if (summary is null || summary.AvailableCount < 0)
            {
                return state;
            }

            if (state.LastAvailableCreditCount is not { } previousSummaryCount)
            {
                return state with
                {
                    LastAvailableCreditCount = summary.AvailableCount,
                };
            }

            var countOnlyAdded = summary.AvailableCount > previousSummaryCount
                ? checked((int)Math.Min(
                    summary.AvailableCount - previousSummaryCount,
                    int.MaxValue))
                : 0;
            if (countOnlyAdded > 0)
            {
                var notification = StoredNotification.FromCreditGrant(
                    countOnlyAdded,
                    Enumerable.Repeat<long?>(
                        null,
                        Math.Min(countOnlyAdded, MaximumStoredCreditExpirations))
                        .ToArray(),
                    observedAt,
                    notificationsEnabled,
                    identitySeed: $"count:{previousSummaryCount}:{summary.AvailableCount}",
                    expirationDetailsAvailable: false);
                if (TryAppend(events, notification) && notificationsEnabled)
                {
                    newNotifications.Add(notification.ToPending());
                }
            }

            return state with
            {
                LastAvailableCreditCount = summary.AvailableCount,
            };
        }

        if (summary.AvailableCount < 0)
        {
            return state;
        }

        var nowUnix = observedAt.ToUnixTimeSeconds();
        var available = credits
            .Where(credit => !string.IsNullOrWhiteSpace(credit.Id))
            .Where(credit => string.Equals(
                credit.ResetType,
                "codexRateLimits",
                StringComparison.Ordinal))
            .Where(credit => string.Equals(
                credit.Status,
                "available",
                StringComparison.Ordinal))
            .Where(credit => credit.ExpiresAt is null || credit.ExpiresAt > nowUnix)
            .Select(credit => new ObservedCredit(
                Fingerprint(credit.Id),
                NormalizeExpiration(credit.ExpiresAt)))
            .DistinctBy(credit => credit.Fingerprint, StringComparer.Ordinal)
            .ToArray();
        var known = state.KnownCreditFingerprints;
        if (known is null)
        {
            return state with
            {
                LastAvailableCreditCount = summary.AvailableCount,
                KnownCreditFingerprints = available
                    .Select(credit => credit.Fingerprint)
                    .TakeLast(MaximumKnownCreditCount)
                    .ToList(),
            };
        }

        var knownSet = known.ToHashSet(StringComparer.Ordinal);
        var added = available
            .Where(credit => !knownSet.Contains(credit.Fingerprint))
            .ToArray();
        foreach (var credit in available)
        {
            if (knownSet.Add(credit.Fingerprint))
            {
                known.Add(credit.Fingerprint);
            }
        }

        if (known.Count > MaximumKnownCreditCount)
        {
            known = known.TakeLast(MaximumKnownCreditCount).ToList();
        }

        var addedCount = added.Length;
        var addedExpirations = added
            .Select(credit => credit.ExpiresAt)
            .ToList();
        var detailsAreIncomplete = available.LongLength < summary.AvailableCount;
        if (detailsAreIncomplete
            && state.LastAvailableCreditCount is { } previousCount
            && summary.AvailableCount > previousCount)
        {
            var countIncrease = checked((int)Math.Min(
                summary.AvailableCount - previousCount,
                int.MaxValue));
            while (addedExpirations.Count
                < Math.Min(countIncrease, MaximumStoredCreditExpirations))
            {
                addedExpirations.Add(null);
            }

            addedCount = Math.Max(addedCount, countIncrease);
        }

        if (addedCount > 0)
        {
            var notification = StoredNotification.FromCreditGrant(
                addedCount,
                addedExpirations,
                observedAt,
                notificationsEnabled,
                identitySeed: string.Join(
                    ":",
                    added.Select(item => item.Fingerprint)
                        .Order(StringComparer.Ordinal)),
                expirationDetailsAvailable:
                    !detailsAreIncomplete
                    && addedExpirations.Count == addedCount);
            if (TryAppend(events, notification) && notificationsEnabled)
            {
                newNotifications.Add(notification.ToPending());
            }
        }

        return state with
        {
            LastAvailableCreditCount = summary.AvailableCount,
            KnownCreditFingerprints = known,
        };
    }

    private async Task<bool> ResolveNotificationsAsync(
        Func<StoredNotification, bool> shouldResolve,
        StoredAttentionState state,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shouldResolve);
        if (resolvedAt < DateTimeOffset.UnixEpoch)
        {
            throw new ArgumentOutOfRangeException(nameof(resolvedAt));
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                var document = await LoadAsync(cancellationToken).ConfigureAwait(false);
                var changed = false;
                var events = document.NotificationEvents.Select(notification =>
                {
                    if (notification.AttentionState != StoredAttentionState.Pending
                        || !shouldResolve(notification))
                    {
                        return notification;
                    }

                    changed = true;
                    return notification with
                    {
                        AttentionState = state,
                        ResolvedAt = resolvedAt < notification.DetectedAt
                            ? notification.DetectedAt
                            : resolvedAt,
                    };
                }).ToList();
                if (!changed)
                {
                    return true;
                }

                await SaveAsync(
                    document with
                    {
                        NotificationEvents = PruneNotificationEvents(events),
                    },
                    cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException && !IsFatal(exception))
            {
                return false;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<SupplementalState> LoadAsync(CancellationToken cancellationToken)
    {
        EnsurePathAllowed();
        if (!File.Exists(path))
        {
            return new SupplementalState();
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            useAsync: true);
        if (stream.Length > MaximumDocumentBytes)
        {
            throw new InvalidDataException();
        }

        using var json = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        EnsureNoDuplicateMembers(json.RootElement);
        if (!json.RootElement.TryGetProperty("schemaVersion", out var schema)
            || !schema.TryGetInt32(out var schemaVersion)
            || schemaVersion != CurrentSchemaVersion)
        {
            throw new UnsupportedSupplementalSchemaException();
        }

        var state = json.RootElement.Deserialize<SupplementalState>(SerializerOptions);
        ValidateState(state);
        return state!;
    }

    private async Task SaveAsync(
        SupplementalState state,
        CancellationToken cancellationToken)
    {
        EnsurePathAllowed();
        ValidateState(state);
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidDataException();
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{RequiredFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
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

    private async Task<SupplementalTrackingResult> TryRebaselineAfterInvalidStateAsync(
        WindowReading? fiveHour,
        ResetCreditSummary? resetCredits,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is null)
        {
            return Unavailable();
        }

        var quarantine = Path.Combine(
            directory,
            $".{RequiredFileName}.{Guid.NewGuid():N}.invalid");
        try
        {
            File.Move(path, quarantine);
            var baseline = BuildBaseline(fiveHour, resetCredits, observedAt);
            await SaveAsync(baseline, cancellationToken).ConfigureAwait(false);
            TryDelete(quarantine);
        }
        catch (OperationCanceledException)
        {
            TryRestore(quarantine);
            throw;
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            TryRestore(quarantine);
        }

        return Unavailable();
    }

    private static SupplementalState BuildBaseline(
        WindowReading? fiveHour,
        ResetCreditSummary? resetCredits,
        DateTimeOffset observedAt)
    {
        StoredObservation? observation = null;
        if (fiveHour is not null)
        {
            var current = new WeeklyUsageObservation(
                fiveHour.RemainingPercent,
                fiveHour.ResetsAt,
                observedAt);
            if (WeeklyUsageResetDetector.IsValid(current))
            {
                observation = StoredObservation.FromObservation(current);
            }
        }

        List<string>? fingerprints = null;
        if (resetCredits?.Credits is { } credits)
        {
            var nowUnix = observedAt.ToUnixTimeSeconds();
            fingerprints = credits
                .Where(credit => !string.IsNullOrWhiteSpace(credit.Id))
                .Where(credit => credit.ResetType == "codexRateLimits")
                .Where(credit => credit.Status == "available")
                .Where(credit => credit.ExpiresAt is null || credit.ExpiresAt > nowUnix)
                .Select(credit => Fingerprint(credit.Id))
                .Distinct(StringComparer.Ordinal)
                .TakeLast(MaximumKnownCreditCount)
                .ToList();
        }

        return new SupplementalState
        {
            LastFiveHourObservation = observation,
            LastAvailableCreditCount = resetCredits?.AvailableCount is >= 0
                ? resetCredits.AvailableCount
                : null,
            KnownCreditFingerprints = fingerprints,
        };
    }

    private static bool TryAppend(
        List<StoredNotification> events,
        StoredNotification notification)
    {
        if (events.Any(item => item.EventId == notification.EventId))
        {
            return true;
        }

        if (notification.AttentionState == StoredAttentionState.Pending
            && events.Count(item => item.AttentionState == StoredAttentionState.Pending)
                >= MaximumPendingNotificationCount)
        {
            return false;
        }

        events.Add(notification);
        return true;
    }

    private static List<StoredNotification> PruneNotificationEvents(
        IReadOnlyList<StoredNotification> events) => events
        .Where(item => item.AttentionState == StoredAttentionState.Pending)
        .Concat(events
            .Where(item => item.AttentionState != StoredAttentionState.Pending)
            .TakeLast(RetainedResolvedNotificationCount))
        .OrderBy(item => item.DetectedAt)
        .TakeLast(MaximumNotificationEventCount)
        .ToList();

    private static bool MatchesPreference(
        SupplementalNotificationKind kind,
        NotificationPreference preference) => preference switch
        {
            NotificationPreference.FiveHourUsageReset =>
                kind != SupplementalNotificationKind.CreditGranted,
            NotificationPreference.CreditGranted =>
                kind == SupplementalNotificationKind.CreditGranted,
            _ => false,
        };

    private static bool IsApplicable(
        StoredAutomaticCreditAttribution? pending,
        WeeklyUsageObservation? previous,
        WeeklyUsageObservation current) => pending is not null
        && previous is not null
        && pending.BaselineResetsAt == previous.ResetsAt
        && current.ObservedAt >= pending.SucceededAt
        && current.ObservedAt < DateTimeOffset
            .FromUnixTimeSeconds(pending.BaselineResetsAt)
            .Add(AutomaticCreditAttributionGrace);

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

        var resetAdvance = current.ResetsAt - previous.ResetsAt;
        var elapsed = current.ObservedAt - previous.ObservedAt;
        return elapsed >= TimeSpan.Zero
            && resetAdvance <= elapsed.TotalSeconds + RollingResetTimeSlack.TotalSeconds;
    }

    private static bool ShouldMergeWithPersistedRecoveryEpisode(
        StoredFiveHourDetection? episode,
        WeeklyUsageObservation? previous,
        WeeklyUsageObservation current,
        WeeklyUsageResetDetection? candidate)
    {
        if (episode is null
            || previous is null
            || candidate?.Kind != WeeklyUsageResetKind.Early
            || current.ObservedAt < episode.DetectedAt)
        {
            return false;
        }

        var resetAdvance = current.ResetsAt - episode.NextResetsAt;
        var elapsed = current.ObservedAt - episode.DetectedAt;
        if (resetAdvance < 0
            || resetAdvance > elapsed.TotalSeconds + RollingResetTimeSlack.TotalSeconds)
        {
            return false;
        }

        var increased = current.RemainingPercent > previous.RemainingPercent;
        var jitter = previous.RemainingPercent >= SaturationJitterFloorPercent
            && current.RemainingPercent >= SaturationJitterFloorPercent
            && increased;
        return current.ResetsAt > previous.ResetsAt ? !increased || jitter : jitter;
    }

    private void EnsurePathAllowed()
    {
        if (!string.Equals(
                Path.GetFileName(path),
                RequiredFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException();
        }

        var current = File.Exists(path) ? path : Path.GetDirectoryName(path);
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

    private static void ValidateState(SupplementalState? state)
    {
        if (state is null
            || state.SchemaVersion != CurrentSchemaVersion
            || state.NotificationEvents is null
            || state.NotificationEvents.Count > MaximumNotificationEventCount
            || state.NotificationEvents.Count(item =>
                item.AttentionState == StoredAttentionState.Pending)
                > MaximumPendingNotificationCount
            || state.KnownCreditFingerprints?.Count > MaximumKnownCreditCount
            || state.LastAvailableCreditCount < 0)
        {
            throw new InvalidDataException();
        }

        if (state.LastFiveHourObservation is { } observation
            && !WeeklyUsageResetDetector.IsValid(observation.ToObservation()))
        {
            throw new InvalidDataException();
        }

        if (state.LastFiveHourDetection is { } detection)
        {
            detection.Validate();
        }

        if (state.PendingAutomaticCredit is { } pending)
        {
            if (state.LastFiveHourObservation is null)
            {
                throw new InvalidDataException();
            }

            pending.Validate(state.LastFiveHourObservation);
        }

        if (state.KnownCreditFingerprints is { } fingerprints
            && (fingerprints.Any(value => value.Length != 64
                    || value.Any(character => !Uri.IsHexDigit(character)))
                || fingerprints.Distinct(StringComparer.Ordinal).Count()
                    != fingerprints.Count))
        {
            throw new InvalidDataException();
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var notification in state.NotificationEvents)
        {
            notification.Validate();
            if (!ids.Add(notification.EventId))
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
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                EnsureNoDuplicateMembers(item);
            }
        }
    }

    private static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static long? NormalizeExpiration(long? expiresAt)
    {
        if (expiresAt is null)
        {
            return null;
        }

        try
        {
            _ = DateTimeOffset.FromUnixTimeSeconds(expiresAt.Value);
            return expiresAt;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string BuildFiveHourEventId(
        WeeklyUsageResetDetection detection) => string.Create(
            CultureInfo.InvariantCulture,
            $"fiveHour:{(int)detection.Kind}:{detection.NextResetsAt}:{detection.DetectedAt.ToUniversalTime().Ticks}");

    private static string BuildCreditEventId(
        string identitySeed,
        DateTimeOffset detectedAt)
    {
        var digest = Fingerprint(identitySeed)[..16];
        return string.Create(
            CultureInfo.InvariantCulture,
            $"creditGranted:{detectedAt.ToUniversalTime().Ticks}:{digest}");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void TryRestore(string quarantine)
    {
        try
        {
            if (!File.Exists(path) && File.Exists(quarantine))
            {
                File.Move(quarantine, path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool IsFatal(Exception exception) => exception is
        OutOfMemoryException or StackOverflowException or AccessViolationException;

    private static SupplementalTrackingResult Unavailable() => new(false, []);

    private sealed record ObservedCredit(string Fingerprint, long? ExpiresAt);

    private sealed class UnsupportedSupplementalSchemaException : Exception
    {
    }

    private sealed record SupplementalState
    {
        [JsonRequired]
        public int SchemaVersion { get; init; } = CurrentSchemaVersion;

        public StoredObservation? LastFiveHourObservation { get; init; }

        public StoredFiveHourDetection? LastFiveHourDetection { get; init; }

        public StoredAutomaticCreditAttribution? PendingAutomaticCredit { get; init; }

        public long? LastAvailableCreditCount { get; init; }

        public List<string>? KnownCreditFingerprints { get; init; }

        [JsonRequired]
        public List<StoredNotification> NotificationEvents { get; init; } = [];
    }

    private sealed record StoredObservation
    {
        [JsonRequired]
        public double RemainingPercent { get; init; }

        [JsonRequired]
        public long ResetsAt { get; init; }

        [JsonRequired]
        public DateTimeOffset ObservedAt { get; init; }

        public WeeklyUsageObservation ToObservation() =>
            new(RemainingPercent, ResetsAt, ObservedAt);

        public static StoredObservation FromObservation(
            WeeklyUsageObservation observation) => new()
            {
                RemainingPercent = observation.RemainingPercent,
                ResetsAt = observation.ResetsAt,
                ObservedAt = observation.ObservedAt,
            };
    }

    private sealed record StoredFiveHourDetection
    {
        [JsonRequired]
        public WeeklyUsageResetKind Kind { get; init; }

        [JsonRequired]
        public long NextResetsAt { get; init; }

        [JsonRequired]
        public DateTimeOffset DetectedAt { get; init; }

        public void Validate()
        {
            if (!Enum.IsDefined(Kind)
                || NextResetsAt is <= 0 or > 253_402_300_799
                || DetectedAt < DateTimeOffset.UnixEpoch)
            {
                throw new InvalidDataException();
            }
        }

        public static StoredFiveHourDetection FromDetection(
            WeeklyUsageResetDetection detection) => new()
            {
                Kind = detection.Kind,
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

        public void Validate(StoredObservation observation)
        {
            if (SucceededAt < DateTimeOffset.UnixEpoch
                || BaselineResetsAt != observation.ResetsAt
                || SucceededAt >= DateTimeOffset.FromUnixTimeSeconds(BaselineResetsAt))
            {
                throw new InvalidDataException();
            }
        }
    }

    private enum StoredAttentionState
    {
        Pending,
        Acknowledged,
        Suppressed,
    }

    private sealed record StoredNotification
    {
        [JsonRequired]
        public string EventId { get; init; } = string.Empty;

        [JsonRequired]
        public SupplementalNotificationKind Kind { get; init; }

        [JsonRequired]
        public DateTimeOffset DetectedAt { get; init; }

        public long? NextResetsAt { get; init; }

        public int AddedCreditCount { get; init; }

        public List<long?>? AddedCreditExpirations { get; init; }

        public bool CreditExpirationDetailsAvailable { get; init; } = true;

        [JsonRequired]
        public StoredAttentionState AttentionState { get; init; }

        public DateTimeOffset? ResolvedAt { get; init; }

        public PendingSupplementalNotification ToPending() => new(
            EventId,
            Kind,
            DetectedAt,
            NextResetsAt,
            AddedCreditCount,
            AddedCreditExpirations,
            CreditExpirationDetailsAvailable);

        public static StoredNotification FromFiveHour(
            WeeklyUsageResetDetection detection,
            bool enabled) => new()
            {
                EventId = BuildFiveHourEventId(detection),
                Kind = detection.Kind switch
                {
                    WeeklyUsageResetKind.Scheduled =>
                        SupplementalNotificationKind.FiveHourScheduledReset,
                    WeeklyUsageResetKind.Early =>
                        SupplementalNotificationKind.FiveHourEarlyReset,
                    WeeklyUsageResetKind.AutomaticCredit =>
                        SupplementalNotificationKind.FiveHourAutomaticCredit,
                    _ => throw new InvalidDataException(),
                },
                DetectedAt = detection.DetectedAt,
                NextResetsAt = detection.NextResetsAt,
                AttentionState = enabled
                    ? StoredAttentionState.Pending
                    : StoredAttentionState.Suppressed,
                ResolvedAt = enabled ? null : detection.DetectedAt,
            };

        public static StoredNotification FromCreditGrant(
            int addedCreditCount,
            IReadOnlyList<long?> expirations,
            DateTimeOffset detectedAt,
            bool enabled,
            string identitySeed,
            bool expirationDetailsAvailable) => new()
            {
                EventId = BuildCreditEventId(identitySeed, detectedAt),
                Kind = SupplementalNotificationKind.CreditGranted,
                DetectedAt = detectedAt,
                AddedCreditCount = addedCreditCount,
                AddedCreditExpirations = expirations
                    .OrderBy(value => value ?? long.MaxValue)
                    .ToList(),
                CreditExpirationDetailsAvailable = expirationDetailsAvailable,
                AttentionState = enabled
                    ? StoredAttentionState.Pending
                    : StoredAttentionState.Suppressed,
                ResolvedAt = enabled ? null : detectedAt,
            };

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(EventId)
                || !Enum.IsDefined(Kind)
                || DetectedAt < DateTimeOffset.UnixEpoch
                || !Enum.IsDefined(AttentionState)
                || AttentionState == StoredAttentionState.Pending && ResolvedAt is not null
                || AttentionState != StoredAttentionState.Pending
                    && (ResolvedAt is null || ResolvedAt < DetectedAt))
            {
                throw new InvalidDataException();
            }

            var isCredit = Kind == SupplementalNotificationKind.CreditGranted;
            if (isCredit
                ? NextResetsAt is not null
                    || AddedCreditCount <= 0
                    || AddedCreditExpirations is null
                    || AddedCreditExpirations.Count > AddedCreditCount
                    || AddedCreditExpirations.Count
                        > MaximumStoredCreditExpirations
                    || AddedCreditExpirations.Any(value => value is not null
                        && (value <= 0 || value > 253_402_300_799))
                : NextResetsAt is null
                    || NextResetsAt <= 0
                    || NextResetsAt > 253_402_300_799
                    || AddedCreditCount != 0
                    || AddedCreditExpirations is not null)
            {
                throw new InvalidDataException();
            }
        }
    }
}
