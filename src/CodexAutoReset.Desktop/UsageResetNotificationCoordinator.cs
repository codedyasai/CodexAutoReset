using System.Windows.Threading;
using CodexAutoReset.Core;
using CodexAutoReset.Runtime;

namespace CodexAutoReset.Desktop;

public sealed class UsageResetNotificationCoordinator : IAsyncDisposable
{
    private readonly JsonWeeklyUsageResetTracker weeklyTracker;
    private readonly JsonSupplementalNotificationTracker? supplementalTracker;
    private readonly INotificationPopupPresenter presenter;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool weeklyEnabled;
    private bool fiveHourEnabled;
    private bool creditGrantedEnabled;
    private readonly Dictionary<NotificationPreference, DateTimeOffset>
        suppressionCutoffs = [];
    private bool disposed;
    private DisplayedNotification? displayed;
    private SelectedNotification? displayedSelection;

    public UsageResetNotificationCoordinator(
        JsonWeeklyUsageResetTracker weeklyTracker,
        INotificationPopupPresenter presenter,
        JsonSupplementalNotificationTracker? supplementalTracker = null,
        TimeProvider? timeProvider = null)
    {
        this.weeklyTracker = weeklyTracker
            ?? throw new ArgumentNullException(nameof(weeklyTracker));
        this.presenter = presenter
            ?? throw new ArgumentNullException(nameof(presenter));
        this.supplementalTracker = supplementalTracker;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public UsageResetNotificationCoordinator(
        JsonWeeklyUsageResetTracker weeklyTracker,
        INotificationPopupPresenter presenter,
        TimeProvider timeProvider)
        : this(weeklyTracker, presenter, supplementalTracker: null, timeProvider)
    {
    }

    public Task InitializeAsync(bool enabled, CancellationToken cancellationToken) =>
        InitializeAsync(
            weeklyEnabled: enabled,
            fiveHourEnabled: false,
            creditGrantedEnabled: false,
            cancellationToken);

    public async Task InitializeAsync(
        bool weeklyEnabled,
        bool fiveHourEnabled,
        bool creditGrantedEnabled,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }

            this.weeklyEnabled = weeklyEnabled;
            this.fiveHourEnabled = fiveHourEnabled;
            this.creditGrantedEnabled = creditGrantedEnabled;
            var now = timeProvider.GetUtcNow();
            if (!weeklyEnabled)
            {
                var persisted = await weeklyTracker.SuppressPendingNotificationsThroughAsync(
                    now,
                    now,
                    cancellationToken).ConfigureAwait(false);
                if (!persisted)
                {
                    suppressionCutoffs[NotificationPreference.WeeklyUsageReset] = now;
                }
            }

            if (supplementalTracker is not null)
            {
                if (!fiveHourEnabled)
                {
                    var persisted = await supplementalTracker
                        .SuppressPendingNotificationsThroughAsync(
                            NotificationPreference.FiveHourUsageReset,
                            now,
                            now,
                            cancellationToken).ConfigureAwait(false);
                    if (!persisted)
                    {
                        suppressionCutoffs[
                            NotificationPreference.FiveHourUsageReset] = now;
                    }
                }

                if (!creditGrantedEnabled)
                {
                    var persisted = await supplementalTracker
                        .SuppressPendingNotificationsThroughAsync(
                            NotificationPreference.CreditGranted,
                            now,
                            now,
                            cancellationToken).ConfigureAwait(false);
                    if (!persisted)
                    {
                        suppressionCutoffs[NotificationPreference.CreditGranted] = now;
                    }
                }
            }
        }
        finally
        {
            gate.Release();
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken) =>
        SetPreferenceEnabledAsync(
            NotificationPreference.WeeklyUsageReset,
            enabled,
            cancellationToken);

    public async Task SetPreferenceEnabledAsync(
        NotificationPreference preference,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var closePopup = false;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }

            SetPreference(preference, enabled);
            if (!enabled)
            {
                suppressionCutoffs[preference] = timeProvider.GetUtcNow();
            }

            if (suppressionCutoffs.TryGetValue(preference, out var cutoff))
            {
                var now = timeProvider.GetUtcNow();
                var persisted = await SuppressPreferenceAsync(
                    preference,
                    cutoff,
                    now,
                    cancellationToken).ConfigureAwait(false);
                if (persisted)
                {
                    suppressionCutoffs.Remove(preference);
                    if (displayed?.Preference == preference)
                    {
                        displayed = null;
                        closePopup = true;
                    }
                }
            }
        }
        finally
        {
            gate.Release();
        }

        if (closePopup)
        {
            presenter.CloseAfterSuppression();
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public void RequestRefresh()
    {
        if (!disposed)
        {
            _ = RefreshIgnoringFailureAsync();
        }
    }

    public void BringPendingToFront()
    {
        if (disposed)
        {
            return;
        }

        presenter.BringToFrontWithoutActivation();
        RequestRefresh();
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        SelectedNotification? next = null;
        var closePopup = false;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }

            foreach (var pendingSuppression in suppressionCutoffs.ToArray())
            {
                var persisted = await SuppressPreferenceAsync(
                    pendingSuppression.Key,
                    pendingSuppression.Value,
                    timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);
                if (persisted)
                {
                    suppressionCutoffs.Remove(pendingSuppression.Key);
                    if (displayed?.Preference == pendingSuppression.Key)
                    {
                        displayed = null;
                        closePopup = true;
                    }
                }
            }

            next = await SelectNextAsync(cancellationToken).ConfigureAwait(false);
            if (next is not null)
            {
                displayedSelection = next;
                displayed = new DisplayedNotification(
                    next.Request.NotificationId,
                    next.Source,
                    next.Preference);
            }
        }
        finally
        {
            gate.Release();
        }

        if (closePopup)
        {
            presenter.CloseAfterSuppression();
        }

        if (next is not null)
        {
            presenter.Show(
                next.Request,
                () => ConfirmAsync(next.Request.NotificationId));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            displayed = null;
            presenter.Dispose();
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<SelectedNotification?> SelectNextAsync(
        CancellationToken cancellationToken)
    {
        if (suppressionCutoffs.Count > 0)
        {
            return null;
        }

        var candidates = new List<SelectedNotification>();
        if (weeklyEnabled)
        {
            var weekly = await weeklyTracker.LoadPendingNotificationsAsync(
                cancellationToken).ConfigureAwait(false);
            candidates.AddRange(weekly.Select(item => new SelectedNotification(
                NotificationPopupRequest.FromUsageReset(item.Detection),
                NotificationSource.Weekly,
                NotificationPreference.WeeklyUsageReset,
                item.Detection.DetectedAt,
                item.Detection.Kind == WeeklyUsageResetKind.Early)));
        }

        if (supplementalTracker is not null
            && (fiveHourEnabled || creditGrantedEnabled))
        {
            var supplemental = await supplementalTracker
                .LoadPendingNotificationsAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (var item in supplemental)
            {
                var preference = item.Kind == SupplementalNotificationKind.CreditGranted
                    ? NotificationPreference.CreditGranted
                    : NotificationPreference.FiveHourUsageReset;
                if (IsPreferenceEnabled(preference))
                {
                    candidates.Add(new SelectedNotification(
                        NotificationPopupRequest.FromSupplemental(item),
                        NotificationSource.Supplemental,
                        preference,
                        item.DetectedAt,
                        item.Kind == SupplementalNotificationKind.FiveHourEarlyReset));
                }
            }
        }

        if (displayed is not null
            && displayedSelection is { } visible
            && IsPreferenceEnabled(visible.Preference)
            && !candidates.Any(item => item.Request.NotificationId == visible.Request.NotificationId))
        {
            // A temporarily unreadable state file must not displace an unconfirmed alert.
            candidates.Add(visible);
        }

        var resets = candidates
            .Where(item => item.Preference != NotificationPreference.CreditGranted)
            .OrderByDescending(item => item.IsEarly)
            .ThenByDescending(item => item.DetectedAt)
            .ThenBy(item => item.Preference == NotificationPreference.WeeklyUsageReset ? 0 : 1)
            .ThenBy(item => item.Request.NotificationId, StringComparer.Ordinal)
            .ToArray();
        foreach (var group in resets.Skip(1).GroupBy(item => item.Source))
        {
            var ids = group.Select(item => item.Request.NotificationId)
                .ToHashSet(StringComparer.Ordinal);
            var persisted = group.Key == NotificationSource.Weekly
                ? await weeklyTracker.SuppressNotificationsAsync(
                    ids, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false)
                : await supplementalTracker!.SuppressNotificationsAsync(
                    ids, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            if (!persisted)
            {
                return null;
            }
        }

        // Credit grants retain their independent acknowledgement queue.
        var next = resets.FirstOrDefault() ?? candidates
            .OrderBy(item => item.DetectedAt)
            .ThenBy(item => item.Request.NotificationId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (next is null || displayed?.EventId == next.Request.NotificationId && presenter.IsVisible)
        {
            return null;
        }

        return next;
    }

    private async Task<bool> ConfirmAsync(string eventId)
    {
        var acknowledged = false;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed
                || displayed is not { } current
                || !string.Equals(current.EventId, eventId, StringComparison.Ordinal))
            {
                return false;
            }

            acknowledged = current.Source == NotificationSource.Weekly
                ? await weeklyTracker.AcknowledgeNotificationAsync(
                    eventId,
                    timeProvider.GetUtcNow(),
                    CancellationToken.None).ConfigureAwait(false)
                : supplementalTracker is not null
                    && await supplementalTracker.AcknowledgeNotificationAsync(
                        eventId,
                        timeProvider.GetUtcNow(),
                        CancellationToken.None).ConfigureAwait(false);
            if (acknowledged)
            {
                displayed = null;
            }
        }
        finally
        {
            gate.Release();
        }

        if (acknowledged)
        {
            ScheduleRefresh();
        }

        return acknowledged;
    }

    private Task<bool> SuppressPreferenceAsync(
        NotificationPreference preference,
        DateTimeOffset cutoff,
        DateTimeOffset now,
        CancellationToken cancellationToken) => preference switch
        {
            NotificationPreference.WeeklyUsageReset =>
                weeklyTracker.SuppressPendingNotificationsThroughAsync(
                    cutoff,
                    now,
                    cancellationToken),
            NotificationPreference.FiveHourUsageReset
                or NotificationPreference.CreditGranted
                    when supplementalTracker is not null =>
                supplementalTracker.SuppressPendingNotificationsThroughAsync(
                    preference,
                    cutoff,
                    now,
                    cancellationToken),
            _ => Task.FromResult(true),
        };

    private void SetPreference(NotificationPreference preference, bool enabled)
    {
        switch (preference)
        {
            case NotificationPreference.WeeklyUsageReset:
                weeklyEnabled = enabled;
                break;
            case NotificationPreference.FiveHourUsageReset:
                fiveHourEnabled = enabled;
                break;
            case NotificationPreference.CreditGranted:
                creditGrantedEnabled = enabled;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(preference));
        }
    }

    private bool IsPreferenceEnabled(NotificationPreference preference) =>
        preference switch
        {
            NotificationPreference.WeeklyUsageReset => weeklyEnabled,
            NotificationPreference.FiveHourUsageReset => fiveHourEnabled,
            NotificationPreference.CreditGranted => creditGrantedEnabled,
            _ => false,
        };

    private void ScheduleRefresh()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null
            && !dispatcher.HasShutdownStarted
            && !dispatcher.HasShutdownFinished)
        {
            _ = dispatcher.BeginInvoke(RequestRefresh, DispatcherPriority.ContextIdle);
        }
        else
        {
            _ = Task.Run(RefreshIgnoringFailureAsync);
        }
    }

    private async Task RefreshIgnoringFailureAsync()
    {
        try
        {
            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
        }
    }

    private enum NotificationSource
    {
        Weekly,
        Supplemental,
    }

    private sealed record DisplayedNotification(
        string EventId,
        NotificationSource Source,
        NotificationPreference Preference);

    private sealed record SelectedNotification(
        NotificationPopupRequest Request,
        NotificationSource Source,
        NotificationPreference Preference,
        DateTimeOffset DetectedAt,
        bool IsEarly);
}
