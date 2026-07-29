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
    public async Task QueuedNotificationsAreShownOneAtATime()
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
        var firstEventId = presenter.Requests[0].NotificationId;

        Assert.IsTrue(await presenter.ConfirmAsync());
        await coordinator.RefreshAsync(CancellationToken.None);

        Assert.AreEqual(2, presenter.ShowCount);
        Assert.IsTrue(presenter.IsVisible);
        Assert.AreNotEqual(
            firstEventId,
            presenter.Requests[1].NotificationId);
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
