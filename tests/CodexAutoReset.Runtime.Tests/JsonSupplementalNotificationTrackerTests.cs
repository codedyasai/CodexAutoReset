using CodexAutoReset.Core;
using CodexAutoReset.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexAutoReset.Runtime.Tests;

[TestClass]
public sealed class JsonSupplementalNotificationTrackerTests
{
    private static readonly DateTimeOffset Now =
        new(2030, 1, 10, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task FirstObservationEstablishesBaselinesWithoutNotifications()
    {
        using var directory = new TemporaryDirectory();
        var tracker = CreateTracker(directory.Path);

        var result = await tracker.ObserveAsync(
            Reading(40, Now.AddHours(5)),
            Credits(Credit("existing", Now.AddDays(7))),
            Now,
            WeeklyUsageResetAttribution.None,
            fiveHourNotificationsEnabled: true,
            creditNotificationsEnabled: true,
            CancellationToken.None);

        Assert.IsTrue(result.StateAvailable);
        Assert.AreEqual(0, result.NewNotifications.Count);
        Assert.AreEqual(
            0,
            (await tracker.LoadPendingNotificationsAsync(CancellationToken.None)).Count);
    }

    [TestMethod]
    public async Task FiveHourRecoveryAndNewCreditCreateIndependentNotifications()
    {
        using var directory = new TemporaryDirectory();
        var tracker = CreateTracker(directory.Path);
        var resetAt = Now.AddHours(5);
        await ObserveBaselineAsync(tracker, resetAt, "existing");

        var result = await tracker.ObserveAsync(
            Reading(85, resetAt),
            Credits(
                Credit("existing", Now.AddDays(7)),
                Credit("new", Now.AddDays(3))),
            Now.AddMinutes(1),
            WeeklyUsageResetAttribution.None,
            fiveHourNotificationsEnabled: true,
            creditNotificationsEnabled: true,
            CancellationToken.None);

        Assert.IsTrue(result.StateAvailable);
        Assert.AreEqual(2, result.NewNotifications.Count);
        var pending = await tracker.LoadPendingNotificationsAsync(
            CancellationToken.None);
        Assert.IsTrue(pending.Any(item =>
            item.Kind == SupplementalNotificationKind.FiveHourEarlyReset));
        var credit = pending.Single(item =>
            item.Kind == SupplementalNotificationKind.CreditGranted);
        Assert.AreEqual(1, credit.AddedCreditCount);
        Assert.AreEqual(
            Now.AddDays(3).ToUnixTimeSeconds(),
            credit.AddedCreditExpirations!.Single());
    }

    [TestMethod]
    public async Task CreditIdentityReplacementIsDetectedEvenWhenCountIsUnchanged()
    {
        using var directory = new TemporaryDirectory();
        var tracker = CreateTracker(directory.Path);
        await tracker.ObserveAsync(
            fiveHour: null,
            Credits(Credit("first", Now.AddDays(2))),
            Now,
            WeeklyUsageResetAttribution.None,
            fiveHourNotificationsEnabled: true,
            creditNotificationsEnabled: true,
            CancellationToken.None);

        await tracker.ObserveAsync(
            fiveHour: null,
            Credits(Credit("second", Now.AddDays(4))),
            Now.AddMinutes(1),
            WeeklyUsageResetAttribution.None,
            fiveHourNotificationsEnabled: true,
            creditNotificationsEnabled: true,
            CancellationToken.None);

        var pending = await tracker.LoadPendingNotificationsAsync(
            CancellationToken.None);
        var notification = pending.Single();
        Assert.AreEqual(
            SupplementalNotificationKind.CreditGranted,
            notification.Kind);
        Assert.AreEqual(1, notification.AddedCreditCount);
    }

    [TestMethod]
    public async Task CountIncreaseStillAlertsWhenCreditDetailsAreUnavailable()
    {
        using var directory = new TemporaryDirectory();
        var tracker = CreateTracker(directory.Path);
        await tracker.ObserveAsync(
            null,
            new ResetCreditSummary(1, null),
            Now,
            WeeklyUsageResetAttribution.None,
            true,
            true,
            CancellationToken.None);

        var result = await tracker.ObserveAsync(
            null,
            new ResetCreditSummary(3, null),
            Now.AddMinutes(1),
            WeeklyUsageResetAttribution.None,
            true,
            true,
            CancellationToken.None);

        var notification = result.NewNotifications.Single();
        Assert.AreEqual(
            SupplementalNotificationKind.CreditGranted,
            notification.Kind);
        Assert.AreEqual(2, notification.AddedCreditCount);
        Assert.IsTrue(notification.AddedCreditExpirations!.All(value => value is null));
    }

    [TestMethod]
    public async Task PartialCreditDetailsDoNotClaimUnknownExpirationsAreMissing()
    {
        using var directory = new TemporaryDirectory();
        var tracker = CreateTracker(directory.Path);
        await tracker.ObserveAsync(
            null,
            new ResetCreditSummary(0, []),
            Now,
            WeeklyUsageResetAttribution.None,
            true,
            true,
            CancellationToken.None);

        var result = await tracker.ObserveAsync(
            null,
            new ResetCreditSummary(
                2,
                [Credit("only-detail", Now.AddDays(2))]),
            Now.AddMinutes(1),
            WeeklyUsageResetAttribution.None,
            true,
            true,
            CancellationToken.None);

        var notification = result.NewNotifications.Single();
        Assert.AreEqual(2, notification.AddedCreditCount);
        Assert.IsFalse(notification.CreditExpirationDetailsAvailable);
    }

    [TestMethod]
    public async Task PreviouslySeenCreditDoesNotAlertWhenItReappears()
    {
        using var directory = new TemporaryDirectory();
        var tracker = CreateTracker(directory.Path);
        await tracker.ObserveAsync(
            null,
            Credits(Credit("known", Now.AddDays(7))),
            Now,
            WeeklyUsageResetAttribution.None,
            true,
            true,
            CancellationToken.None);
        await tracker.ObserveAsync(
            null,
            Credits(),
            Now.AddMinutes(1),
            WeeklyUsageResetAttribution.None,
            true,
            true,
            CancellationToken.None);
        var result = await tracker.ObserveAsync(
            null,
            Credits(Credit("known", Now.AddDays(7))),
            Now.AddMinutes(2),
            WeeklyUsageResetAttribution.None,
            true,
            true,
            CancellationToken.None);

        Assert.AreEqual(0, result.NewNotifications.Count);
        Assert.AreEqual(
            0,
            (await tracker.LoadPendingNotificationsAsync(CancellationToken.None)).Count);
    }

    [TestMethod]
    public async Task DisabledPreferencesSuppressEventsButKeepFutureDetectionAccurate()
    {
        using var directory = new TemporaryDirectory();
        var tracker = CreateTracker(directory.Path);
        var resetAt = Now.AddHours(5);
        await ObserveBaselineAsync(tracker, resetAt, "first");

        await tracker.ObserveAsync(
            Reading(80, resetAt),
            Credits(
                Credit("first", Now.AddDays(7)),
                Credit("second", Now.AddDays(8))),
            Now.AddMinutes(1),
            WeeklyUsageResetAttribution.None,
            fiveHourNotificationsEnabled: false,
            creditNotificationsEnabled: false,
            CancellationToken.None);
        Assert.AreEqual(
            0,
            (await tracker.LoadPendingNotificationsAsync(CancellationToken.None)).Count);

        var nextResetAt = resetAt.AddHours(5);
        var enabled = await tracker.ObserveAsync(
            Reading(100, nextResetAt),
            Credits(
                Credit("first", Now.AddDays(7)),
                Credit("second", Now.AddDays(8)),
                Credit("third", Now.AddDays(9))),
            resetAt.AddMinutes(1),
            WeeklyUsageResetAttribution.None,
            fiveHourNotificationsEnabled: true,
            creditNotificationsEnabled: true,
            CancellationToken.None);

        Assert.AreEqual(2, enabled.NewNotifications.Count);
        Assert.IsTrue(enabled.NewNotifications.Any(item =>
            item.Kind == SupplementalNotificationKind.FiveHourScheduledReset));
        Assert.IsTrue(enabled.NewNotifications.Any(item =>
            item.Kind == SupplementalNotificationKind.CreditGranted));
    }

    [TestMethod]
    public async Task AutomaticCreditAttributionProducesFiveHourAutomaticUseNotification()
    {
        using var directory = new TemporaryDirectory();
        var tracker = CreateTracker(directory.Path);
        var resetAt = Now.AddHours(5);
        await ObserveBaselineAsync(tracker, resetAt, "existing");

        var result = await tracker.ObserveAsync(
            Reading(100, resetAt.AddHours(5)),
            Credits(),
            Now.AddMinutes(1),
            WeeklyUsageResetAttribution.AutomaticCreditSucceeded,
            fiveHourNotificationsEnabled: true,
            creditNotificationsEnabled: true,
            CancellationToken.None);

        Assert.AreEqual(
            SupplementalNotificationKind.FiveHourAutomaticCredit,
            result.NewNotifications.Single().Kind);
    }

    private static async Task ObserveBaselineAsync(
        JsonSupplementalNotificationTracker tracker,
        DateTimeOffset resetAt,
        string creditId) => await tracker.ObserveAsync(
            Reading(40, resetAt),
            Credits(Credit(creditId, Now.AddDays(7))),
            Now,
            WeeklyUsageResetAttribution.None,
            fiveHourNotificationsEnabled: true,
            creditNotificationsEnabled: true,
            CancellationToken.None);

    private static JsonSupplementalNotificationTracker CreateTracker(string directory) =>
        new(Path.Combine(directory, "supplemental-notification-state.json"));

    private static WindowReading Reading(
        double remaining,
        DateTimeOffset resetsAt) => new(
            UsedPercent: 100 - remaining,
            RemainingPercent: remaining,
            ReportedDurationMinutes: 300,
            NormalizedDurationMinutes: 300,
            ResetsAt: resetsAt.ToUnixTimeSeconds());

    private static ResetCreditSummary Credits(params ResetCredit[] credits) =>
        new(credits.LongLength, credits);

    private static ResetCredit Credit(string id, DateTimeOffset expiresAt) => new(
        id,
        "codexRateLimits",
        "available",
        Now.AddDays(-1).ToUnixTimeSeconds(),
        expiresAt.ToUnixTimeSeconds(),
        null,
        null);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"CodexAutoReset-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
