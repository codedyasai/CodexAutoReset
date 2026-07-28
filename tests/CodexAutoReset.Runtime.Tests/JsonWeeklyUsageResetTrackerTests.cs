using System.Text.Json;
using CodexAutoReset.Core;
using CodexAutoReset.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexAutoReset.Runtime.Tests;

[TestClass]
public sealed class JsonWeeklyUsageResetTrackerTests
{
    private static readonly DateTimeOffset Now = new(
        2026,
        7,
        28,
        3,
        0,
        0,
        TimeSpan.Zero);

    [TestMethod]
    public async Task FirstObservationPersistsBaselineWithoutDetection()
    {
        using var directory = TestDirectory.Create();
        var tracker = Tracker(directory);

        var result = await tracker.ObserveAsync(
            Observation(25, Now.AddDays(2), Now),
            CancellationToken.None);

        Assert.AreEqual(
            WeeklyUsageResetTrackingStatus.BaselineEstablished,
            result.Status);
        Assert.IsNull(result.Detection);
        Assert.IsTrue(File.Exists(tracker.Path));
    }

    [TestMethod]
    public async Task PersistedBaselinePreventsDuplicateAfterRestart()
    {
        using var directory = TestDirectory.Create();
        var firstTracker = Tracker(directory);
        var previous = Observation(20, Now.AddDays(2), Now);
        _ = await firstTracker.ObserveAsync(previous, CancellationToken.None);

        var detectedObservation = Observation(
            100,
            Now.AddDays(9),
            Now.AddMinutes(1));
        var detected = await firstTracker.ObserveAsync(
            detectedObservation,
            CancellationToken.None);

        Assert.AreEqual(
            WeeklyUsageResetTrackingStatus.ResetDetected,
            detected.Status);
        Assert.AreEqual(
            WeeklyUsageResetKind.Early,
            detected.Detection?.Kind);

        var reconstructed = Tracker(directory);
        var duplicate = await reconstructed.ObserveAsync(
            detectedObservation with
            {
                ObservedAt = Now.AddMinutes(2),
            },
            CancellationToken.None);

        Assert.AreEqual(
            WeeklyUsageResetTrackingStatus.NoReset,
            duplicate.Status);
        Assert.IsNull(duplicate.Detection);
    }

    [TestMethod]
    public async Task SameResetTimeRemainingIncreaseIsPersistedAsEarlyReset()
    {
        using var directory = TestDirectory.Create();
        var tracker = Tracker(directory);
        var resetAt = Now.AddDays(2);
        _ = await tracker.ObserveAsync(
            Observation(15, resetAt, Now),
            CancellationToken.None);

        var result = await tracker.ObserveAsync(
            Observation(80, resetAt, Now.AddMinutes(1)),
            CancellationToken.None);

        Assert.AreEqual(
            WeeklyUsageResetTrackingStatus.ResetDetected,
            result.Status);
        Assert.AreEqual(WeeklyUsageResetKind.Early, result.Detection?.Kind);
    }

    [TestMethod]
    public async Task SameResetTimeIncreaseAfterDeadlineIsScheduledReset()
    {
        using var directory = TestDirectory.Create();
        var tracker = Tracker(directory);
        var resetAt = Now.AddMinutes(1);
        _ = await tracker.ObserveAsync(
            Observation(15, resetAt, Now),
            CancellationToken.None);

        var result = await tracker.ObserveAsync(
            Observation(80, resetAt, Now.AddMinutes(2)),
            CancellationToken.None);

        Assert.AreEqual(
            WeeklyUsageResetTrackingStatus.ResetDetected,
            result.Status);
        Assert.AreEqual(WeeklyUsageResetKind.Scheduled, result.Detection?.Kind);
    }

    [TestMethod]
    public async Task PendingAutomaticCreditSurvivesRestartUntilChangeIsObserved()
    {
        using var directory = TestDirectory.Create();
        var resetAt = Now.AddDays(2);
        var firstTracker = Tracker(directory);
        _ = await firstTracker.ObserveAsync(
            Observation(5, resetAt, Now),
            CancellationToken.None);

        var marked = await firstTracker.MarkAutomaticCreditSucceededAsync(
            Now.AddSeconds(10),
            CancellationToken.None);
        Assert.AreEqual(
            AutomaticCreditAttributionTrackingStatus.Recorded,
            marked);

        var reconstructed = Tracker(directory);
        var unchanged = await reconstructed.ObserveAsync(
            Observation(5, resetAt, Now.AddMinutes(1)),
            CancellationToken.None);
        Assert.AreEqual(
            WeeklyUsageResetTrackingStatus.NoReset,
            unchanged.Status);

        var afterSecondRestart = Tracker(directory);
        var reset = await afterSecondRestart.ObserveAsync(
            Observation(100, Now.AddDays(9), Now.AddMinutes(2)),
            CancellationToken.None);

        Assert.AreEqual(
            WeeklyUsageResetTrackingStatus.ResetDetected,
            reset.Status);
        Assert.AreEqual(
            WeeklyUsageResetKind.AutomaticCredit,
            reset.Detection?.Kind);

        var afterDetection = Tracker(directory);
        var laterExternalReset = await afterDetection.ObserveAsync(
            Observation(100, Now.AddDays(16), Now.AddMinutes(3)),
            CancellationToken.None);
        Assert.AreEqual(
            WeeklyUsageResetKind.Early,
            laterExternalReset.Detection?.Kind);
    }

    [TestMethod]
    public async Task PendingAutomaticCreditExpiresAtOldScheduledReset()
    {
        using var directory = TestDirectory.Create();
        var resetAt = Now.AddMinutes(5);
        var tracker = Tracker(directory);
        _ = await tracker.ObserveAsync(
            Observation(5, resetAt, Now),
            CancellationToken.None);
        _ = await tracker.MarkAutomaticCreditSucceededAsync(
            Now.AddMinutes(1),
            CancellationToken.None);

        var result = await tracker.ObserveAsync(
            Observation(100, Now.AddDays(7), resetAt.AddSeconds(1)),
            CancellationToken.None);

        Assert.AreEqual(
            WeeklyUsageResetTrackingStatus.ResetDetected,
            result.Status);
        Assert.AreEqual(WeeklyUsageResetKind.Scheduled, result.Detection?.Kind);
    }

    [TestMethod]
    public async Task CombinedAttributionWaitsForObservedChange()
    {
        using var directory = TestDirectory.Create();
        var resetAt = Now.AddDays(2);
        var tracker = Tracker(directory);
        _ = await tracker.ObserveAsync(
            Observation(5, resetAt, Now),
            CancellationToken.None);

        var unchanged = await tracker.ObserveAsync(
            Observation(5, resetAt, Now.AddMinutes(1)),
            WeeklyUsageResetAttribution.AutomaticCreditSucceeded,
            CancellationToken.None);
        Assert.AreEqual(
            WeeklyUsageResetTrackingStatus.NoReset,
            unchanged.Status);

        var detected = await tracker.ObserveAsync(
            Observation(100, Now.AddDays(9), Now.AddMinutes(2)),
            CancellationToken.None);
        Assert.AreEqual(
            WeeklyUsageResetKind.AutomaticCredit,
            detected.Detection?.Kind);
    }

    [TestMethod]
    public async Task ExplicitAutomaticCreditOverridesCrossedScheduledDeadline()
    {
        using var directory = TestDirectory.Create();
        var tracker = Tracker(directory);
        var resetAt = Now.AddMinutes(1);
        _ = await tracker.ObserveAsync(
            Observation(5, resetAt, Now),
            CancellationToken.None);

        var detected = await tracker.ObserveAsync(
            Observation(100, Now.AddDays(7), Now.AddMinutes(2)),
            WeeklyUsageResetAttribution.AutomaticCreditSucceeded,
            CancellationToken.None);

        Assert.AreEqual(
            WeeklyUsageResetTrackingStatus.ResetDetected,
            detected.Status);
        Assert.AreEqual(
            WeeklyUsageResetKind.AutomaticCredit,
            detected.Detection?.Kind);
    }

    [TestMethod]
    public async Task ResetTimeRegressionDoesNotReplacePersistedBaseline()
    {
        using var directory = TestDirectory.Create();
        var tracker = Tracker(directory);
        var baselineResetAt = Now.AddDays(2);
        _ = await tracker.ObserveAsync(
            Observation(20, baselineResetAt, Now),
            CancellationToken.None);

        var regression = await tracker.ObserveAsync(
            Observation(100, Now.AddDays(1), Now.AddMinutes(1)),
            CancellationToken.None);
        Assert.AreEqual(
            WeeklyUsageResetTrackingStatus.ObservationIgnored,
            regression.Status);

        var reconstructed = Tracker(directory);
        var recovered = await reconstructed.ObserveAsync(
            Observation(20, baselineResetAt, Now.AddMinutes(2)),
            CancellationToken.None);
        Assert.AreEqual(
            WeeklyUsageResetTrackingStatus.NoReset,
            recovered.Status);
        Assert.IsNull(recovered.Detection);
    }

    [TestMethod]
    public async Task CorruptStateSelfHealsAsBaselineWithoutDetection()
    {
        using var directory = TestDirectory.Create();
        var path = Path.Combine(directory.Path, "usage-reset-state.json");
        const string corruptContent = """{"schemaVersion":1,"unexpected":true}""";
        await File.WriteAllTextAsync(path, corruptContent);
        var tracker = new JsonWeeklyUsageResetTracker(path);

        var result = await tracker.ObserveAsync(
            Observation(100, Now.AddDays(2), Now),
            CancellationToken.None);

        Assert.AreEqual(
            WeeklyUsageResetTrackingStatus.BaselineEstablished,
            result.Status);
        Assert.IsNull(result.Detection);
        Assert.AreNotEqual(corruptContent, await File.ReadAllTextAsync(path));

        var reconstructed = new JsonWeeklyUsageResetTracker(path);
        var duplicate = await reconstructed.ObserveAsync(
            Observation(100, Now.AddDays(2), Now.AddMinutes(1)),
            CancellationToken.None);
        Assert.AreEqual(
            WeeklyUsageResetTrackingStatus.NoReset,
            duplicate.Status);
        Assert.IsNull(duplicate.Detection);
    }

    [TestMethod]
    public async Task OversizedStateSelfHealsWithoutDetection()
    {
        using var directory = TestDirectory.Create();
        var path = Path.Combine(directory.Path, "usage-reset-state.json");
        await File.WriteAllTextAsync(path, new string('x', 64 * 1024 + 1));
        var tracker = new JsonWeeklyUsageResetTracker(path);

        var result = await tracker.ObserveAsync(
            Observation(100, Now.AddDays(2), Now),
            CancellationToken.None);

        Assert.AreEqual(
            WeeklyUsageResetTrackingStatus.BaselineEstablished,
            result.Status);
        Assert.IsNull(result.Detection);
        Assert.IsTrue(new FileInfo(path).Length < 64 * 1024);
    }

    [TestMethod]
    public async Task UnsupportedSchemaSelfHealsWithoutDetection()
    {
        using var directory = TestDirectory.Create();
        var path = Path.Combine(directory.Path, "usage-reset-state.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "schemaVersion": 99,
              "lastObservation": {
                "remainingPercent": 5,
                "resetsAt": 1800000000,
                "observedAt": "2026-07-28T03:00:00+00:00"
              },
              "lastDetection": null,
              "pendingAutomaticCredit": null
            }
            """);
        var tracker = new JsonWeeklyUsageResetTracker(path);

        var result = await tracker.ObserveAsync(
            Observation(100, Now.AddDays(2), Now),
            CancellationToken.None);

        Assert.AreEqual(
            WeeklyUsageResetTrackingStatus.BaselineEstablished,
            result.Status);
        Assert.IsNull(result.Detection);
    }

    [TestMethod]
    public async Task ForbiddenPathFailsSoftWithoutNotification()
    {
        using var directory = TestDirectory.Create();
        var forbiddenPath = Path.Combine(directory.Path, "other-name.json");
        var tracker = new JsonWeeklyUsageResetTracker(forbiddenPath);

        var result = await tracker.ObserveAsync(
            Observation(100, Now.AddDays(2), Now),
            CancellationToken.None);

        Assert.AreEqual(
            WeeklyUsageResetTrackingStatus.StateUnavailable,
            result.Status);
        Assert.IsNull(result.Detection);
        Assert.IsFalse(File.Exists(forbiddenPath));
    }

    [TestMethod]
    public async Task StateDocumentContainsOnlyNonIdentifyingUsageFields()
    {
        using var directory = TestDirectory.Create();
        var tracker = Tracker(directory);
        _ = await tracker.ObserveAsync(
            Observation(25, Now.AddDays(2), Now),
            CancellationToken.None);

        using var document = JsonDocument.Parse(
            await File.ReadAllTextAsync(tracker.Path));
        var rootNames = document.RootElement
            .EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(
            new[]
            {
                "lastDetection",
                "lastObservation",
                "pendingAutomaticCredit",
                "schemaVersion",
            },
            rootNames);

        var serialized = document.RootElement.GetRawText();
        Assert.IsFalse(
            serialized.Contains(
                Environment.UserName,
                StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(
            serialized.Contains("account", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(
            serialized.Contains("creditId", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(
            serialized.Contains("path", StringComparison.OrdinalIgnoreCase));
    }

    private static JsonWeeklyUsageResetTracker Tracker(TestDirectory directory) =>
        new(Path.Combine(directory.Path, "usage-reset-state.json"));

    private static WeeklyUsageObservation Observation(
        double remainingPercent,
        DateTimeOffset resetsAt,
        DateTimeOffset observedAt) => new(
            remainingPercent,
            resetsAt.ToUnixTimeSeconds(),
            observedAt);

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
                "CodexAutoReset.Runtime.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TestDirectory(path);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
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
