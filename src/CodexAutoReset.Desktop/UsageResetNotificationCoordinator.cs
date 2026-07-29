using System.Windows.Threading;
using CodexAutoReset.Runtime;

namespace CodexAutoReset.Desktop;

public sealed class UsageResetNotificationCoordinator : IAsyncDisposable
{
    private readonly JsonWeeklyUsageResetTracker tracker;
    private readonly INotificationPopupPresenter presenter;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool desiredNotificationsEnabled;
    private bool notificationsEnabled;
    private bool suppressionRetryRequired;
    private DateTimeOffset? suppressionCutoffAt;
    private bool disposed;
    private string? displayedEventId;

    public UsageResetNotificationCoordinator(
        JsonWeeklyUsageResetTracker tracker,
        INotificationPopupPresenter presenter,
        TimeProvider? timeProvider = null)
    {
        this.tracker = tracker
            ?? throw new ArgumentNullException(nameof(tracker));
        this.presenter = presenter
            ?? throw new ArgumentNullException(nameof(presenter));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task InitializeAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        await SetEnabledAsync(enabled, cancellationToken);
    }

    public async Task SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        NotificationPopupRequest? request = null;
        var closePopup = false;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }

            var wasDesiredEnabled = desiredNotificationsEnabled;
            desiredNotificationsEnabled = enabled;
            if (!enabled)
            {
                notificationsEnabled = false;
                var suppressionPersisted =
                    await PersistSuppressionAsync(
                        advanceCutoff: true,
                        cancellationToken).ConfigureAwait(false);
                suppressionRetryRequired = !suppressionPersisted;
                if (suppressionPersisted)
                {
                    displayedEventId = null;
                    closePopup = true;
                }
            }
            else
            {
                if (suppressionRetryRequired)
                {
                    var suppressionPersisted =
                        await PersistSuppressionAsync(
                            advanceCutoff: !wasDesiredEnabled,
                            cancellationToken).ConfigureAwait(false);
                    if (!suppressionPersisted)
                    {
                        notificationsEnabled = false;
                        return;
                    }

                    suppressionRetryRequired = false;
                    displayedEventId = null;
                    closePopup = true;
                }

                notificationsEnabled = true;
                request = await SelectNextRequestAsync(cancellationToken)
                    .ConfigureAwait(false);
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

        if (request is not null)
        {
            await ShowIfCurrentAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public void RequestRefresh()
    {
        if (disposed)
        {
            return;
        }

        _ = RefreshIgnoringFailureAsync();
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
        NotificationPopupRequest? request = null;
        var closePopup = false;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }

            if (!desiredNotificationsEnabled)
            {
                notificationsEnabled = false;
                var suppressionPersisted =
                    await PersistSuppressionAsync(
                        advanceCutoff: true,
                        cancellationToken).ConfigureAwait(false);
                suppressionRetryRequired = !suppressionPersisted;
                if (suppressionPersisted)
                {
                    displayedEventId = null;
                    closePopup = true;
                }
            }
            else
            {
                if (suppressionRetryRequired)
                {
                    var suppressionPersisted =
                        await PersistSuppressionAsync(
                            advanceCutoff: false,
                            cancellationToken).ConfigureAwait(false);
                    if (!suppressionPersisted)
                    {
                        notificationsEnabled = false;
                        return;
                    }

                    suppressionRetryRequired = false;
                    displayedEventId = null;
                    closePopup = true;
                }

                notificationsEnabled = true;
                request = await SelectNextRequestAsync(cancellationToken)
                    .ConfigureAwait(false);
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

        if (request is not null)
        {
            await ShowIfCurrentAsync(request, cancellationToken)
                .ConfigureAwait(false);
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
            displayedEventId = null;
            presenter.Dispose();
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<NotificationPopupRequest?> SelectNextRequestAsync(
        CancellationToken cancellationToken)
    {
        var pending = await tracker.LoadPendingNotificationsAsync(
            cancellationToken).ConfigureAwait(false);
        if (displayedEventId is not null
            && pending.Any(notification => string.Equals(
                notification.EventId,
                displayedEventId,
                StringComparison.Ordinal)))
        {
            if (presenter.IsVisible)
            {
                return null;
            }

            displayedEventId = null;
        }

        displayedEventId = null;
        var next = pending.FirstOrDefault();
        if (next is null)
        {
            return null;
        }

        displayedEventId = next.EventId;
        return NotificationPopupRequest.FromUsageReset(next.Detection);
    }

    private async Task<bool> PersistSuppressionAsync(
        bool advanceCutoff,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (advanceCutoff || suppressionCutoffAt is null)
        {
            suppressionCutoffAt = now;
        }

        var persisted =
            await tracker.SuppressPendingNotificationsThroughAsync(
                suppressionCutoffAt.Value,
                now,
                cancellationToken).ConfigureAwait(false);
        if (persisted)
        {
            suppressionCutoffAt = null;
        }

        return persisted;
    }

    private async Task ShowIfCurrentAsync(
        NotificationPopupRequest request,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!disposed
                && notificationsEnabled
                && string.Equals(
                    displayedEventId,
                    request.NotificationId,
                    StringComparison.Ordinal))
            {
                presenter.Show(
                    request,
                    () => ConfirmAsync(request.NotificationId));
            }
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> ConfirmAsync(string eventId)
    {
        var acknowledged = false;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed
                || !string.Equals(
                    displayedEventId,
                    eventId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            acknowledged = await tracker.AcknowledgeNotificationAsync(
                eventId,
                timeProvider.GetUtcNow(),
                CancellationToken.None).ConfigureAwait(false);
            if (acknowledged)
            {
                displayedEventId = null;
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

    private void ScheduleRefresh()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null
            && !dispatcher.HasShutdownStarted
            && !dispatcher.HasShutdownFinished)
        {
            _ = dispatcher.BeginInvoke(
                RequestRefresh,
                DispatcherPriority.ContextIdle);
            return;
        }

        _ = Task.Run(RefreshIgnoringFailureAsync);
    }

    private async Task RefreshIgnoringFailureAsync()
    {
        try
        {
            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (
            OutOfMemoryException
                or StackOverflowException
                or AccessViolationException))
        {
        }
    }
}
