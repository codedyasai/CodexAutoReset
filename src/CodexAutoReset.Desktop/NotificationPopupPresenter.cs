using CodexAutoReset.Core;
using CodexAutoReset.Runtime;

namespace CodexAutoReset.Desktop;

public sealed record NotificationPopupRequest
{
    private NotificationPopupRequest(
        string notificationId,
        string title,
        string message,
        string? detail)
    {
        NotificationId = notificationId;
        Title = title;
        Message = message;
        Detail = detail;
    }

    public string NotificationId { get; }

    public string Title { get; }

    public string Message { get; }

    public string? Detail { get; }

    public static NotificationPopupRequest FromUsageReset(
        WeeklyUsageResetDetection detection)
    {
        ArgumentNullException.ThrowIfNull(detection);
        var notification = UsageResetNotificationFormatter.Format(detection);
        var separator = notification.Body.IndexOf('\n');
        var message = separator < 0
            ? notification.Body
            : notification.Body[..separator];
        var detail = separator < 0
            ? null
            : notification.Body[(separator + 1)..];
        return new NotificationPopupRequest(
            notification.EventId,
            notification.Title,
            message,
            detail);
    }

    public static NotificationPopupRequest FromSupplemental(
        PendingSupplementalNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var content = SupplementalNotificationFormatter.Format(notification);
        return new NotificationPopupRequest(
            notification.EventId,
            content.Title,
            content.Message,
            content.Detail);
    }
}

public interface INotificationPopupPresenter : IDisposable
{
    bool IsVisible { get; }

    void Show(
        NotificationPopupRequest request,
        Func<Task<bool>> confirmAsync);

    void BringToFrontWithoutActivation();

    void CloseAfterSuppression();
}
