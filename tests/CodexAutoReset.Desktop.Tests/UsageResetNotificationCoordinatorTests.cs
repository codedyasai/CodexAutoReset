using System.IO;
using CodexAutoReset.Core;
using CodexAutoReset.Desktop;
using CodexAutoReset.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexAutoReset.Desktop.Tests;

[TestClass]
public sealed class UsageResetNotificationCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(
        2026,
        7,
        29,
        4,
        0,
        0,
        TimeSpan.Zero);

    [TestMethod]
    public async Task RepeatedRefreshShowsOnePersistentPopup()
    {
        using var directory = TestDirectory.Create();
        var tracker = await CreateTrackerWithPendingAsync(directory);
        var presenter = new FakePresenter();
        await using var coordinator = new UsageResetNotificationCoordinator(
            tracker,
            presenter);

        await coordinator.InitializeAsync(
            enabled: true,
            CancellationToken.None);
        await coordinator.RefreshAsync(CancellationToken.None);
        await coordinator.RefreshAsync(CancellationToken.None);

        Assert.AreEqual(1, presenter.ShowCount);
        Assert.IsTrue(presenter.IsVisible);
    }

    [TestMethod]
    public async Task MissingPresenterWindowRetriesPendingNotification()
    {
        using var directory = TestDirectory.Create();
        var tracker = await CreateTrackerWithPendingAsync(directory);
        var presenter = new FakePresenter(showAsVisible: false);
        await using var coordinator = new UsageResetNotificationCoordinator(
            tracker,
            presenter);

        await coordinator.InitializeAsync(
            enabled: true,
            CancellationToken.None);
        await coordinator.RefreshAsync(CancellationToken.None);

        Assert.AreEqual(2, presenter.ShowCount);
        Assert.AreEqual(
            presenter.Requests[0].NotificationId,
            presenter.Requests[1].NotificationId);
    }

    [TestMethod]
    public async Task ConfirmationPersistsBeforePopupCloses()
    {
        using var directory = TestDirectory.Create();
        var tracker = await CreateTrackerWithPendingAsync(directory);
        var presenter = new FakePresenter();
        await using var coordinator = new UsageResetNotificationCoordinator(
            tracker,
            presenter);
        await coordinator.InitializeAsync(
            enabled: true,
            CancellationToken.None);

        var persisted = await presenter.ConfirmAsync();

        Assert.IsTrue(persisted);
        Assert.IsFalse(presenter.IsVisible);
        Assert.AreEqual(
            0,
            (await new JsonWeeklyUsageResetTracker(tracker.Path)
                .LoadPendingNotificationsAsync(CancellationToken.None)).Count);
    }

    [TestMethod]
    public async Task TransientStateReadFailureKeepsVisiblePopupConfirmable()
    {
        using var directory = TestDirectory.Create();
        var tracker = await CreateTrackerWithPendingAsync(directory);
        var presenter = new FakePresenter();
        await using var coordinator = new UsageResetNotificationCoordinator(
            tracker,
            presenter);
        await coordinator.InitializeAsync(
            enabled: true,
            CancellationToken.None);

        using (var lockedState = new FileStream(
            tracker.Path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            await coordinator.RefreshAsync(CancellationToken.None);
            Assert.IsTrue(presenter.IsVisible);
            Assert.AreEqual(1, presenter.ShowCount);
        }

        Assert.IsTrue(await presenter.ConfirmAsync());
        Assert.IsFalse(presenter.IsVisible);
        Assert.AreEqual(
            0,
            (await tracker.LoadPendingNotificationsAsync(
                CancellationToken.None)).Count);
    }

    [TestMethod]
    public async Task DisablingSuppressesVisibleAndQueuedNotifications()
    {
        using var directory = TestDirectory.Create();
        var tracker = await CreateTrackerWithPendingAsync(directory);
        var presenter = new FakePresenter();
        await using var coordinator = new UsageResetNotificationCoordinator(
            tracker,
            presenter);
        await coordinator.InitializeAsync(
            enabled: true,
            CancellationToken.None);

        await coordinator.SetEnabledAsync(
            enabled: false,
            CancellationToken.None);
        await coordinator.SetEnabledAsync(
            enabled: true,
            CancellationToken.None);

        Assert.IsFalse(presenter.IsVisible);
        Assert.AreEqual(1, presenter.ShowCount);
        Assert.AreEqual(
            0,
            (await tracker.LoadPendingNotificationsAsync(
                CancellationToken.None)).Count);

        _ = await tracker.ObserveAsync(
            new WeeklyUsageObservation(
                50,
                Now.AddDays(9).ToUnixTimeSeconds(),
                Now.AddMinutes(4)),
            CancellationToken.None);
        _ = await tracker.ObserveAsync(
            new WeeklyUsageObservation(
                100,
                Now.AddDays(9).AddMinutes(2).ToUnixTimeSeconds(),
                Now.AddMinutes(5)),
            CancellationToken.None);
        await coordinator.RefreshAsync(CancellationToken.None);

        Assert.AreEqual(2, presenter.ShowCount);
        Assert.IsTrue(presenter.IsVisible);
    }

    [TestMethod]
    public async Task SuppressionWriteFailureKeepsPopupVisibleUntilRetrySucceeds()
    {
        using var directory = TestDirectory.Create();
        var tracker = await CreateTrackerWithPendingAsync(directory);
        var presenter = new FakePresenter();
        var timeProvider = new MutableTimeProvider(Now.AddMinutes(2));
        await using var coordinator = new UsageResetNotificationCoordinator(
            tracker,
            presenter,
            timeProvider);
        await coordinator.InitializeAsync(
            enabled: true,
            CancellationToken.None);

        using (var lockedState = new FileStream(
            tracker.Path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            await coordinator.SetEnabledAsync(
                enabled: false,
                CancellationToken.None);
            Assert.IsTrue(presenter.IsVisible);

            timeProvider.UtcNow = Now.AddMinutes(3);
            await coordinator.SetEnabledAsync(
                enabled: true,
                CancellationToken.None);
            Assert.AreEqual(1, presenter.ShowCount);
            Assert.IsTrue(presenter.IsVisible);
        }

        _ = await tracker.ObserveAsync(
            new WeeklyUsageObservation(
                50,
                Now.AddDays(9).ToUnixTimeSeconds(),
                Now.AddMinutes(4)),
            CancellationToken.None);
        _ = await tracker.ObserveAsync(
            new WeeklyUsageObservation(
                100,
                Now.AddDays(9).AddMinutes(2).ToUnixTimeSeconds(),
                Now.AddMinutes(5)),
            CancellationToken.None);
        timeProvider.UtcNow = Now.AddMinutes(6);
        await coordinator.RefreshAsync(CancellationToken.None);

        Assert.AreEqual(2, presenter.ShowCount);
        Assert.IsTrue(presenter.IsVisible);
        var pending = await tracker.LoadPendingNotificationsAsync(
            CancellationToken.None);
        Assert.AreEqual(1, pending.Count);
        Assert.AreEqual(
            presenter.Requests[1].NotificationId,
            pending[0].EventId);
    }

    [TestMethod]
    public async Task ProgrammaticDisposeDoesNotAcknowledgeNotification()
    {
        using var directory = TestDirectory.Create();
        var tracker = await CreateTrackerWithPendingAsync(directory);
        var firstPresenter = new FakePresenter();
        var firstCoordinator = new UsageResetNotificationCoordinator(
            tracker,
            firstPresenter);
        await firstCoordinator.InitializeAsync(
            enabled: true,
            CancellationToken.None);

        await firstCoordinator.DisposeAsync();

        var secondPresenter = new FakePresenter();
        await using var secondCoordinator =
            new UsageResetNotificationCoordinator(
                new JsonWeeklyUsageResetTracker(tracker.Path),
                secondPresenter);
        await secondCoordinator.InitializeAsync(
            enabled: true,
            CancellationToken.None);

        Assert.IsTrue(secondPresenter.IsVisible);
        Assert.AreEqual(1, secondPresenter.ShowCount);
    }

    [TestMethod]
    public async Task NewestEarlyNotificationReplacesOlderPendingNotifications()
    {
        using var directory = TestDirectory.Create();
        var tracker = await CreateTrackerWithPendingAsync(directory);
        _ = await tracker.ObserveAsync(
            new WeeklyUsageObservation(
                50,
                Now.AddDays(9).ToUnixTimeSeconds(),
                Now.AddMinutes(2)),
            CancellationToken.None);
        _ = await tracker.ObserveAsync(
            new WeeklyUsageObservation(
                100,
                Now.AddDays(9).AddMinutes(1).ToUnixTimeSeconds(),
                Now.AddMinutes(3)),
            CancellationToken.None);
        var presenter = new FakePresenter();
        await using var coordinator = new UsageResetNotificationCoordinator(
            tracker,
            presenter);
        await coordinator.InitializeAsync(
            enabled: true,
            CancellationToken.None);

        Assert.AreEqual(1, presenter.ShowCount);
        Assert.AreEqual(1, presenter.Requests.Count);
        Assert.AreEqual(1, (await tracker.LoadPendingNotificationsAsync(CancellationToken.None)).Count);

        Assert.IsTrue(await presenter.ConfirmAsync());
        await coordinator.RefreshAsync(CancellationToken.None);

        Assert.AreEqual(1, presenter.ShowCount);
        Assert.IsFalse(presenter.IsVisible);
    }

    [TestMethod]
    [DataRow(true, true, true)]
    [DataRow(true, false, true)]
    [DataRow(false, true, false)]
    public async Task SimultaneousResetsRespectPreferencesAndWeeklyPriority(
        bool weeklyEnabled, bool fiveHourEnabled, bool expectWeekly)
    {
        using var directory = TestDirectory.Create();
        var weekly = await CreateTrackerWithPendingAsync(directory);
        var supplemental = new JsonSupplementalNotificationTracker(
            Path.Combine(directory.Path, "supplemental-notification-state.json"));
        await ObserveFiveHourAsync(supplemental, 40, Now.AddHours(5), Now);
        await ObserveFiveHourAsync(supplemental, 100, Now.AddHours(10), Now.AddMinutes(1));
        var presenter = new FakePresenter();
        await using var coordinator = new UsageResetNotificationCoordinator(weekly, presenter, supplemental);
        await coordinator.InitializeAsync(weeklyEnabled, fiveHourEnabled, false, CancellationToken.None);
        var weeklyId = (await weekly.LoadPendingNotificationsAsync(CancellationToken.None)).FirstOrDefault()?.EventId;
        Assert.AreEqual(expectWeekly, presenter.Requests.Single().NotificationId == weeklyId);
        Assert.IsTrue(await presenter.ConfirmAsync());
        await coordinator.RefreshAsync(CancellationToken.None);
        Assert.AreEqual(1, presenter.ShowCount);
    }

    [TestMethod]
    public async Task EarlyResetSurvivesLaterScheduledResetButNewEarlyReplacesIt()
    {
        using var directory = TestDirectory.Create();
        var weekly = await CreateTrackerWithPendingAsync(directory);
        var supplemental = new JsonSupplementalNotificationTracker(
            Path.Combine(directory.Path, "supplemental-notification-state.json"));
        var presenter = new FakePresenter();
        await using var coordinator = new UsageResetNotificationCoordinator(weekly, presenter, supplemental);
        await coordinator.InitializeAsync(true, true, false, CancellationToken.None);
        await ObserveFiveHourAsync(supplemental, 40, Now.AddHours(5), Now);
        await ObserveFiveHourAsync(supplemental, 100, Now.AddHours(10), Now.AddHours(5));
        await coordinator.RefreshAsync(CancellationToken.None);
        Assert.AreEqual(1, presenter.ShowCount);
        Assert.AreEqual(0, (await supplemental.LoadPendingNotificationsAsync(CancellationToken.None)).Count);
        await ObserveFiveHourAsync(supplemental, 40, Now.AddHours(10), Now.AddHours(6));
        await ObserveFiveHourAsync(supplemental, 100, Now.AddHours(11), Now.AddHours(7));
        await coordinator.RefreshAsync(CancellationToken.None);
        Assert.AreEqual(2, presenter.ShowCount);
        StringAssert.Contains(presenter.Requests.Last().Title, "5시간");
        Assert.AreEqual(0, (await weekly.LoadPendingNotificationsAsync(CancellationToken.None)).Count);
    }

    [TestMethod]
    public async Task LatestScheduledResetReplacesVisibleResetAndDoesNotReturnAfterRestart()
    {
        using var directory = TestDirectory.Create();
        var weekly = new JsonWeeklyUsageResetTracker(Path.Combine(directory.Path, "usage-reset-state.json"));
        var supplemental = new JsonSupplementalNotificationTracker(Path.Combine(directory.Path, "supplemental-notification-state.json"));
        await ObserveFiveHourAsync(supplemental, 40, Now.AddHours(5), Now);
        await ObserveFiveHourAsync(supplemental, 100, Now.AddHours(10), Now.AddHours(5));
        var presenter = new FakePresenter();
        await using var coordinator = new UsageResetNotificationCoordinator(weekly, presenter, supplemental);
        await coordinator.InitializeAsync(true, true, false, CancellationToken.None);
        await ObserveFiveHourAsync(supplemental, 40, Now.AddHours(10), Now.AddHours(6));
        await ObserveFiveHourAsync(supplemental, 100, Now.AddHours(15), Now.AddHours(10));
        await coordinator.RefreshAsync(CancellationToken.None);
        Assert.AreEqual(2, presenter.ShowCount);
        Assert.AreNotEqual(presenter.Requests[0].NotificationId, presenter.Requests[1].NotificationId);
        Assert.AreEqual(1, (await new JsonSupplementalNotificationTracker(Path.Combine(directory.Path, "supplemental-notification-state.json"))
            .LoadPendingNotificationsAsync(CancellationToken.None)).Count);
        Assert.IsTrue(await presenter.ConfirmAsync());
        await coordinator.RefreshAsync(CancellationToken.None);
        Assert.AreEqual(2, presenter.ShowCount);
    }

    private static async Task ObserveFiveHourAsync(
        JsonSupplementalNotificationTracker tracker, double remaining,
        DateTimeOffset resetsAt, DateTimeOffset observedAt)
    {
        _ = await tracker.ObserveAsync(
            new WindowReading(100 - remaining, remaining, 300, 300, resetsAt.ToUnixTimeSeconds()),
            new ResetCreditSummary(0, []), observedAt, WeeklyUsageResetAttribution.None,
            true, true, CancellationToken.None);
    }

    [TestMethod]
    public async Task CreditGrantPopupUsesItsIndependentPreference()
    {
        using var directory = TestDirectory.Create();
        var weeklyTracker = new JsonWeeklyUsageResetTracker(
            Path.Combine(directory.Path, "usage-reset-state.json"));
        var supplementalTracker = new JsonSupplementalNotificationTracker(
            Path.Combine(
                directory.Path,
                "supplemental-notification-state.json"));
        _ = await supplementalTracker.ObserveAsync(
            fiveHour: null,
            new ResetCreditSummary(0, []),
            Now,
            WeeklyUsageResetAttribution.None,
            fiveHourNotificationsEnabled: true,
            creditNotificationsEnabled: true,
            CancellationToken.None);
        _ = await supplementalTracker.ObserveAsync(
            fiveHour: null,
            new ResetCreditSummary(
                1,
                [
                    new ResetCredit(
                        "new-credit",
                        "codexRateLimits",
                        "available",
                        Now.ToUnixTimeSeconds(),
                        Now.AddDays(7).ToUnixTimeSeconds(),
                        null,
                        null),
                ]),
            Now.AddMinutes(1),
            WeeklyUsageResetAttribution.None,
            fiveHourNotificationsEnabled: true,
            creditNotificationsEnabled: true,
            CancellationToken.None);
        var presenter = new FakePresenter();
        await using var coordinator = new UsageResetNotificationCoordinator(
            weeklyTracker,
            presenter,
            supplementalTracker);

        await coordinator.InitializeAsync(
            weeklyEnabled: false,
            fiveHourEnabled: false,
            creditGrantedEnabled: true,
            CancellationToken.None);

        Assert.AreEqual(1, presenter.ShowCount);
        StringAssert.Contains(presenter.Requests.Single().Title, "초기화권 신규 지급");
        Assert.IsTrue(await presenter.ConfirmAsync());
        Assert.AreEqual(
            0,
            (await supplementalTracker.LoadPendingNotificationsAsync(
                CancellationToken.None)).Count);
    }

    [TestMethod]
    public async Task FiveHourResetPopupUsesItsIndependentPreference()
    {
        using var directory = TestDirectory.Create();
        var weeklyTracker = new JsonWeeklyUsageResetTracker(
            Path.Combine(directory.Path, "usage-reset-state.json"));
        var supplementalTracker = new JsonSupplementalNotificationTracker(
            Path.Combine(
                directory.Path,
                "supplemental-notification-state.json"));
        var resetAt = Now.AddHours(5);
        _ = await supplementalTracker.ObserveAsync(
            new WindowReading(
                UsedPercent: 60,
                RemainingPercent: 40,
                ReportedDurationMinutes: 300,
                NormalizedDurationMinutes: 300,
                ResetsAt: resetAt.ToUnixTimeSeconds()),
            new ResetCreditSummary(0, []),
            Now,
            WeeklyUsageResetAttribution.None,
            fiveHourNotificationsEnabled: true,
            creditNotificationsEnabled: true,
            CancellationToken.None);
        _ = await supplementalTracker.ObserveAsync(
            new WindowReading(
                UsedPercent: 0,
                RemainingPercent: 100,
                ReportedDurationMinutes: 300,
                NormalizedDurationMinutes: 300,
                ResetsAt: resetAt.AddHours(5).ToUnixTimeSeconds()),
            new ResetCreditSummary(0, []),
            Now.AddMinutes(1),
            WeeklyUsageResetAttribution.None,
            fiveHourNotificationsEnabled: true,
            creditNotificationsEnabled: true,
            CancellationToken.None);
        var presenter = new FakePresenter();
        await using var coordinator = new UsageResetNotificationCoordinator(
            weeklyTracker,
            presenter,
            supplementalTracker);

        await coordinator.InitializeAsync(
            weeklyEnabled: false,
            fiveHourEnabled: true,
            creditGrantedEnabled: false,
            CancellationToken.None);

        Assert.AreEqual(1, presenter.ShowCount);
        StringAssert.Contains(presenter.Requests.Single().Title, "5시간");
    }

    private static async Task<JsonWeeklyUsageResetTracker>
        CreateTrackerWithPendingAsync(TestDirectory directory)
    {
        var tracker = new JsonWeeklyUsageResetTracker(
            Path.Combine(directory.Path, "usage-reset-state.json"));
        _ = await tracker.ObserveAsync(
            new WeeklyUsageObservation(
                20,
                Now.AddDays(2).ToUnixTimeSeconds(),
                Now),
            CancellationToken.None);
        _ = await tracker.ObserveAsync(
            new WeeklyUsageObservation(
                100,
                Now.AddDays(9).ToUnixTimeSeconds(),
                Now.AddMinutes(1)),
            CancellationToken.None);
        return tracker;
    }

    private sealed class FakePresenter : INotificationPopupPresenter
    {
        private readonly bool showAsVisible;
        private Func<Task<bool>>? confirmAsync;

        public FakePresenter(bool showAsVisible = true)
        {
            this.showAsVisible = showAsVisible;
        }

        public bool IsVisible { get; private set; }

        public int ShowCount { get; private set; }

        public List<NotificationPopupRequest> Requests { get; } = [];

        public void Show(
            NotificationPopupRequest request,
            Func<Task<bool>> confirmAsync)
        {
            ArgumentNullException.ThrowIfNull(request);
            this.confirmAsync = confirmAsync;
            IsVisible = showAsVisible;
            ShowCount++;
            Requests.Add(request);
        }

        public void CloseAfterSuppression()
        {
            IsVisible = false;
            confirmAsync = null;
        }

        public void BringToFrontWithoutActivation()
        {
        }

        public async Task<bool> ConfirmAsync()
        {
            var callback = confirmAsync
                ?? throw new InvalidOperationException();
            var persisted = await callback();
            if (persisted)
            {
                IsVisible = false;
                confirmAsync = null;
            }

            return persisted;
        }

        public void Dispose() => CloseAfterSuppression();
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        public MutableTimeProvider(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; set; }

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class TestDirectory : IDisposable
    {
        private TestDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TestDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "CodexAutoReset.Desktop.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TestDirectory(path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
