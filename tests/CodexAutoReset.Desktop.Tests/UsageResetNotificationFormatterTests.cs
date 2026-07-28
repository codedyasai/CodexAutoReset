using CodexAutoReset.Core;
using CodexAutoReset.Desktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexAutoReset.Desktop.Tests;

[TestClass]
public sealed class UsageResetNotificationFormatterTests
{
    [TestMethod]
    [DataRow(
        WeeklyUsageResetKind.Scheduled,
        "정기 초기화",
        "Codex 주간 사용량이 초기화 되었습니다.")]
    [DataRow(
        WeeklyUsageResetKind.Early,
        "예정보다 이른 초기화",
        "Codex 주간 사용량 초기화를 감지했습니다. 기존 예정 시각보다 먼저 사용량이 회복되었습니다.")]
    [DataRow(
        WeeklyUsageResetKind.AutomaticCredit,
        "앱이 초기화권을 사용한 경우",
        "Codex 주간 사용량이 지정된 사용 한도 이하로 감소하여 초기화권이 자동 사용되었습니다.")]
    public void Format_ReturnsExactKoreanNotification(
        WeeklyUsageResetKind kind,
        string expectedTitle,
        string expectedMessage)
    {
        var localNextReset = new DateTime(
            2030,
            1,
            15,
            4,
            5,
            0,
            DateTimeKind.Local);
        var nextResetsAt = new DateTimeOffset(localNextReset).ToUnixTimeSeconds();
        var detection = new WeeklyUsageResetDetection(
            kind,
            nextResetsAt,
            new DateTimeOffset(2030, 1, 14, 12, 0, 0, TimeSpan.Zero));

        var notification = UsageResetNotificationFormatter.Format(detection);

        Assert.AreEqual(expectedTitle, notification.Title);
        Assert.AreEqual(
            $"{expectedMessage}\n(다음 초기화일: 2030-01-15 04:05)",
            notification.Body);
    }

    [TestMethod]
    public void EventId_UsesKindNextResetAndDetectionInstant()
    {
        var detectedAt = new DateTimeOffset(
            2030,
            1,
            14,
            12,
            34,
            56,
            TimeSpan.FromHours(9));
        var equivalentInstant = detectedAt.ToOffset(TimeSpan.FromHours(-5));

        var first = UsageResetNotificationFormatter.Format(
            new WeeklyUsageResetDetection(
                WeeklyUsageResetKind.Early,
                1_895_000_000,
                detectedAt));
        var sameInstant = UsageResetNotificationFormatter.Format(
            new WeeklyUsageResetDetection(
                WeeklyUsageResetKind.Early,
                1_895_000_000,
                equivalentInstant));
        var differentKind = UsageResetNotificationFormatter.Format(
            new WeeklyUsageResetDetection(
                WeeklyUsageResetKind.Scheduled,
                1_895_000_000,
                detectedAt));
        var differentReset = UsageResetNotificationFormatter.Format(
            new WeeklyUsageResetDetection(
                WeeklyUsageResetKind.Early,
                1_895_000_001,
                detectedAt));
        var differentDetectionTime = UsageResetNotificationFormatter.Format(
            new WeeklyUsageResetDetection(
                WeeklyUsageResetKind.Early,
                1_895_000_000,
                detectedAt.AddSeconds(1)));

        Assert.AreEqual(first.EventId, sameInstant.EventId);
        Assert.AreNotEqual(first.EventId, differentKind.EventId);
        Assert.AreNotEqual(first.EventId, differentReset.EventId);
        Assert.AreNotEqual(first.EventId, differentDetectionTime.EventId);
    }

    [TestMethod]
    public void NotificationGate_SuppressesRepeatedTransientSnapshot()
    {
        var gate = new UsageResetNotificationGate();
        var detection = Detection(
            WeeklyUsageResetKind.Early,
            nextResetsAt: 1_895_000_000,
            detectedAtSeconds: 1_894_000_000);

        Assert.IsNotNull(gate.Consume(detection, notificationsEnabled: true));
        Assert.IsNull(gate.Consume(detection, notificationsEnabled: true));

        var anotherEvent = detection with
        {
            DetectedAt = detection.DetectedAt.AddSeconds(1),
        };
        Assert.IsNotNull(gate.Consume(
            anotherEvent,
            notificationsEnabled: true));
    }

    [TestMethod]
    public void NotificationGate_MarksDisabledEventAsSeen()
    {
        var gate = new UsageResetNotificationGate();
        var detection = Detection(
            WeeklyUsageResetKind.Scheduled,
            nextResetsAt: 1_895_000_000,
            detectedAtSeconds: 1_894_000_000);

        Assert.IsNull(gate.Consume(detection, notificationsEnabled: false));
        Assert.IsNull(gate.Consume(detection, notificationsEnabled: true));

        var nextEvent = detection with
        {
            NextResetsAt = detection.NextResetsAt + 604_800,
            DetectedAt = detection.DetectedAt.AddDays(7),
        };
        Assert.IsNotNull(gate.Consume(nextEvent, notificationsEnabled: true));
    }

    private static WeeklyUsageResetDetection Detection(
        WeeklyUsageResetKind kind,
        long nextResetsAt,
        long detectedAtSeconds) => new(
            kind,
            nextResetsAt,
            DateTimeOffset.FromUnixTimeSeconds(detectedAtSeconds));
}
