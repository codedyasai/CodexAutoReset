using System.Globalization;
using CodexAutoReset.Core;

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
                "앱이 초기화권을 사용한 경우",
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
