using System.Globalization;
using CodexAutoReset.Core;
using CodexAutoReset.Runtime;

namespace CodexAutoReset.Desktop;

public sealed record UsageResetNotification(
    string EventId,
    string Title,
    string Body);

public static class UsageResetNotificationFormatter
{
    public static UsageResetNotification Format(
        WeeklyUsageResetDetection detection)
    {
        ArgumentNullException.ThrowIfNull(detection);

        var nextResetText = DateTimeOffset
            .FromUnixTimeSeconds(detection.NextResetsAt)
            .ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var (title, message) = detection.Kind switch
        {
            WeeklyUsageResetKind.Scheduled => (
                "정기 초기화",
                "Codex 주간 사용량이 초기화 되었습니다."),
            WeeklyUsageResetKind.Early => (
                "예정보다 이른 초기화",
                "Codex 주간 사용량 초기화를 감지했습니다. 기존 예정 시각보다 먼저 사용량이 회복되었습니다."),
            WeeklyUsageResetKind.AutomaticCredit => (
                "초기화권 자동 사용",
                "Codex 주간 사용량이 지정된 사용 한도 이하로 감소하여 초기화권이 자동 사용되었습니다."),
            _ => throw new ArgumentOutOfRangeException(
                nameof(detection),
                detection.Kind,
                "지원하지 않는 사용량 초기화 유형입니다."),
        };

        return new UsageResetNotification(
            BuildEventId(detection),
            title,
            $"{message}\n(다음 초기화일: {nextResetText})");
    }

    public static string BuildEventId(WeeklyUsageResetDetection detection)
    {
        ArgumentNullException.ThrowIfNull(detection);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)detection.Kind}:{detection.NextResetsAt}:{detection.DetectedAt.ToUniversalTime().Ticks}");
    }
}

public sealed record SupplementalNotificationContent(
    string Title,
    string Message,
    string? Detail);

public static class SupplementalNotificationFormatter
{
    public static SupplementalNotificationContent Format(
        PendingSupplementalNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (notification.Kind == SupplementalNotificationKind.CreditGranted)
        {
            var detail = notification.CreditExpirationDetailsAvailable
                ? FormatCreditExpiration(notification.AddedCreditExpirations)
                : "(만료 예정: -)";
            return new SupplementalNotificationContent(
                "초기화권 신규 지급",
                notification.AddedCreditCount == 1
                    ? "사용 가능한 Codex 초기화권 1개가 새로 지급되었습니다."
                    : $"사용 가능한 Codex 초기화권 {notification.AddedCreditCount}개가 새로 지급되었습니다.",
                detail);
        }

        if (notification.NextResetsAt is not { } nextResetsAt)
        {
            throw new ArgumentException(
                "5시간 한도 알림에 다음 갱신 시각이 없습니다.",
                nameof(notification));
        }

        var nextResetText = DateTimeOffset
            .FromUnixTimeSeconds(nextResetsAt)
            .ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var (title, message) = notification.Kind switch
        {
            SupplementalNotificationKind.FiveHourScheduledReset => (
                "5시간 한도 초기화",
                "Codex 5시간 사용량이 초기화되었습니다."),
            SupplementalNotificationKind.FiveHourEarlyReset => (
                "예정보다 이른 5시간 한도 초기화",
                "Codex 5시간 사용량이 기존 예정 시각보다 먼저 회복되었습니다."),
            SupplementalNotificationKind.FiveHourAutomaticCredit => (
                "초기화권 자동 사용",
                "Codex 5시간 사용량이 지정된 사용 한도 이하로 감소하여 초기화권이 자동 사용되었습니다."),
            _ => throw new ArgumentOutOfRangeException(
                nameof(notification),
                notification.Kind,
                "지원하지 않는 알림 유형입니다."),
        };
        return new SupplementalNotificationContent(
            title,
            message,
            $"(다음 갱신 예정: {nextResetText})");
    }

    private static string FormatCreditExpiration(
        IReadOnlyList<long?>? expirations)
    {
        if (expirations is null || expirations.Count == 0)
        {
            return "(만료 예정: -)";
        }

        var earliest = expirations
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .Order()
            .FirstOrDefault();
        if (earliest == 0)
        {
            return "(만료 예정 없음)";
        }

        var formatted = DateTimeOffset
            .FromUnixTimeSeconds(earliest)
            .ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        return expirations.Count == 1
            ? $"(만료 예정: {formatted})"
            : $"(가장 빠른 만료 예정: {formatted})";
    }
}

public sealed class UsageResetNotificationGate
{
    private readonly HashSet<string> seenEventIds = new(StringComparer.Ordinal);

    public UsageResetNotification? Consume(
        WeeklyUsageResetDetection detection,
        bool notificationsEnabled)
    {
        var notification = UsageResetNotificationFormatter.Format(detection);
        var isNewEvent = seenEventIds.Add(notification.EventId);
        return isNewEvent && notificationsEnabled ? notification : null;
    }
}
