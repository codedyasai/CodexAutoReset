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
    public async Task PersistedRecoveryEpisodeMergesRollingScheduleAndSaturationJitter()
    {
        using var directory = TestDirectory.Create();
        var tracker = Tracker(directory);
        var originalResetAt = Now.AddDays(2);
        var recoveredResetAt = Now.AddDays(9);
        _ = await tracker.ObserveAsync(
            Observation(20, originalResetAt, Now),
            CancellationToken.None);

        var detected = await tracker.ObserveAsync(
            Observation(100, recoveredResetAt, Now.AddMinutes(1)),
            CancellationToken.None);
        Assert.AreEqual(
            WeeklyUsageResetTrackingStatus.ResetDetected,
            detected.Status);

        tracker = Tracker(directory);
        var rolling = await tracker.ObserveAsync(
            Observation(
                100,
                recoveredResetAt.AddMinutes(1),
                Now.AddMinutes(2)),
            CancellationToken.None);
        Assert.AreEqual(WeeklyUsageResetTrackingStatus.NoReset, rolling.Status);
        Assert.IsNull(rolling.Detection);

        tracker = Tracker(directory);
        var jitterDown = await tracker.ObserveAsync(
            Observation(
                99,
                recoveredResetAt.AddMinutes(2),
                Now.AddMinutes(3)),
            CancellationToken.None);
        Assert.AreEqual(
            WeeklyUsageResetTrackingStatus.NoReset,
            jitterDown.Status);
        Assert.IsNull(jitterDown.Detection);

        tracker = Tracker(directory);
        var jitterUp = await tracker.ObserveAsync(
            Observation(
                100,
                recoveredResetAt.AddMinutes(2),
                Now.AddMinutes(4)),
            CancellationToken.None);
        Assert.AreEqual(
            WeeklyUsageResetTrackingStatus.NoReset,
            jitterUp.Status);
        Assert.IsNull(jitterUp.Detection);

        using var document = JsonDocument.Parse(
            await File.ReadAllTextAsync(tracker.Path));
        var root = document.RootElement;
        Assert.AreEqual(
            recoveredResetAt.AddMinutes(2).ToUnixTimeSeconds(),
            root.GetProperty("lastObservation").GetProperty("resetsAt")
                .GetInt64());
        Assert.AreEqual(
            recoveredResetAt.ToUnixTimeSeconds(),
            root.GetProperty("lastDetection").GetProperty("nextResetsAt")
                .GetInt64());
    }

    [TestMethod]
    public async Task SaturatedBaselineDoesNotTreatRollingScheduleAsReset()
    {
        using var directory = TestDirectory.Create();
        var tracker = Tracker(directory);
        var initialResetAt = Now.AddDays(7);
        _ = await tracker.ObserveAsync(
            Observation(100, initialResetAt, Now),
            CancellationToken.None);

        tracker = Tracker(directory);
        var rolling = await tracker.ObserveAsync(
            Observation(
                100,
                initialResetAt.AddMinutes(5),
                Now.AddMinutes(5)),
            CancellationToken.None);

        Assert.AreEqual(WeeklyUsageResetTrackingStatus.NoReset, rolling.Status);
        Assert.IsNull(rolling.Detection);
        Assert.AreEqual(
            0,
            (await tracker.LoadPendingNotificationsAsync(
                CancellationToken.None)).Count);
    }

    [TestMethod]
    public async Task MeaningfulRecoveryDuringRollingScheduleStartsNewEpisode()
    {
        using var directory = TestDirectory.Create();
        var tracker = Tracker(directory);
        var recoveredResetAt = Now.AddDays(9);
        _ = await tracker.ObserveAsync(
            Observation(20, Now.AddDays(2), Now),
            CancellationToken.None);
        _ = await tracker.ObserveAsync(
            Observation(100, recoveredResetAt, Now.AddMinutes(1)),
            CancellationToken.None);

        var usage = await tracker.ObserveAsync(
            Observation(
                50,
                recoveredResetAt.AddMinutes(1),
                Now.AddMinutes(2)),
            CancellationToken.None);
        Assert.AreEqual(WeeklyUsageResetTrackingStatus.NoReset, usage.Status);

        var recoveredAgain = await tracker.ObserveAsync(
            Observation(
                100,
                recoveredResetAt.AddMinutes(2),
                Now.AddMinutes(3)),
            CancellationToken.None);

        Assert.AreEqual(
            WeeklyUsageResetTrackingStatus.ResetDetected,
            recoveredAgain.Status);
        Assert.AreEqual(
            WeeklyUsageResetKind.Early,
            recoveredAgain.Detection?.Kind);
    }

    [TestMethod]
    public async Task LargeResetScheduleAdvanceStartsNewEpisodeAtSameRemaining()
    {
        using var directory = TestDirectory.Create();
        var tracker = Tracker(directory);
        var recoveredResetAt = Now.AddDays(9);
        _ = await tracker.ObserveAsync(
            Observation(20, Now.AddDays(2), Now),
            CancellationToken.None);
        _ = await tracker.ObserveAsync(
            Observation(100, recoveredResetAt, Now.AddMinutes(1)),
            CancellationToken.None);

        var nextReset = await tracker.ObserveAsync(
            Observation(
                100,
                recoveredResetAt.AddDays(7),
                Now.AddMinutes(2)),
            CancellationToken.None);

        Assert.AreEqual(
            WeeklyUsageResetTrackingStatus.ResetDetected,
            nextReset.Status);
        Assert.AreEqual(WeeklyUsageResetKind.Early, nextReset.Detection?.Kind);
    }

    [TestMethod]
    public async Task ScheduledResetIsNotMergedIntoRecoveryEpisode()
    {
        using var directory = TestDirectory.Create();
        var tracker = Tracker(directory);
        var originalResetAt = Now.AddMinutes(2);
        var earlyResetAt = Now.AddMinutes(4);
        _ = await tracker.ObserveAsync(
            Observation(20, originalResetAt, Now),
            CancellationToken.None);
        _ = await tracker.ObserveAsync(
            Observation(100, earlyResetAt, Now.AddMinutes(1)),
            CancellationToken.None);

        var scheduled = await tracker.ObserveAsync(
            Observation(100, Now.AddMinutes(6), Now.AddMinutes(5)),
            CancellationToken.None);

        Assert.AreEqual(
            WeeklyUsageResetTrackingStatus.ResetDetected,
            scheduled.Status);
        Assert.AreEqual(
            WeeklyUsageResetKind.Scheduled,
            scheduled.Detection?.Kind);
    }

    [TestMethod]
    public async Task AutomaticCreditIsNotMergedIntoRecoveryEpisode()
    {
        using var directory = TestDirectory.Create();
        var tracker = Tracker(directory);
        var recoveredResetAt = Now.AddDays(9);
        _ = await tracker.ObserveAsync(
            Observation(20, Now.AddDays(2), Now),
            CancellationToken.None);
        _ = await tracker.ObserveAsync(
            Observation(100, recoveredResetAt, Now.AddMinutes(1)),
            CancellationToken.None);
        _ = await tracker.ObserveAsync(
            Observation(5, recoveredResetAt, Now.AddMinutes(2)),
            CancellationToken.None);
        _ = await tracker.MarkAutomaticCreditSucceededAsync(
            Now.AddMinutes(2).AddSeconds(10),
            CancellationToken.None);

        var automatic = await tracker.ObserveAsync(
            Observation(
                100,
                recoveredResetAt.AddMinutes(1),
                Now.AddMinutes(3)),
            CancellationToken.None);

        Assert.AreEqual(
            WeeklyUsageResetTrackingStatus.ResetDetected,
            automatic.Status);
        Assert.AreEqual(
            WeeklyUsageResetKind.AutomaticCredit,
            automatic.Detection?.Kind);
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
    public async Task NullNotificationEventSelfHealsWithoutDetection()
    {
        using var directory = TestDirectory.Create();
        var path = Path.Combine(directory.Path, "usage-reset-state.json");
        await File.WriteAllTextAsync(
            path,
            $$"""
            {
              "schemaVersion": 2,
              "lastObservation": {
                "remainingPercent": 100,
                "resetsAt": {{Now.AddDays(2).ToUnixTimeSeconds()}},
                "observedAt": "{{Now:O}}"
              },
              "lastDetection": null,
              "pendingAutomaticCredit": null,
              "notificationEvents": [null]
            }
            """);
        var tracker = new JsonWeeklyUsageResetTracker(path);

        var result = await tracker.ObserveAsync(
            Observation(100, Now.AddDays(2), Now.AddMinutes(1)),
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
                "notificationEvents",
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

    [TestMethod]
    public async Task DetectionAndPendingNotificationAreCommittedTogether()
    {
        using var directory = TestDirectory.Create();
        var tracker = Tracker(directory);
        _ = await tracker.ObserveAsync(
            Observation(20, Now.AddDays(2), Now),
            CancellationToken.None);

        var result = await tracker.ObserveAsync(
            Observation(100, Now.AddDays(9), Now.AddMinutes(1)),
            WeeklyUsageResetAttribution.None,
            notificationsEnabled: true,
            CancellationToken.None);
        var pending = await tracker.LoadPendingNotificationsAsync(
            CancellationToken.None);

        Assert.AreEqual(
            WeeklyUsageResetTrackingStatus.ResetDetected,
            result.Status);
        Assert.AreEqual(1, pending.Count);
        Assert.AreEqual(result.Detection, pending[0].Detection);

        using var document = JsonDocument.Parse(
            await File.ReadAllTextAsync(tracker.Path));
        Assert.AreEqual(
            2,
            document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual(
            "pending",
            document.RootElement.GetProperty("notificationEvents")[0]
                .GetProperty("attentionState").GetString());
        var eventNames = document.RootElement
            .GetProperty("notificationEvents")[0]
            .EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(
            new[]
            {
                "attentionState",
                "detectedAt",
                "eventId",
                "kind",
                "nextResetsAt",
                "resolvedAt",
            },
            eventNames);
        var serialized = document.RootElement.GetRawText();
        Assert.IsFalse(
            serialized.Contains("title", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(
            serialized.Contains("message", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(
            serialized.Contains("Codex", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task VersionOneMigrationDoesNotReplayPastDetection()
    {
        using var directory = TestDirectory.Create();
        var path = Path.Combine(directory.Path, "usage-reset-state.json");
        var resetAt = Now.AddDays(9).ToUnixTimeSeconds();
        await File.WriteAllTextAsync(
            path,
            $$"""
            {
              "schemaVersion": 1,
              "lastObservation": {
                "remainingPercent": 100,
                "resetsAt": {{resetAt}},
                "observedAt": "{{Now.AddMinutes(1):O}}"
              },
              "lastDetection": {
                "kind": "early",
                "nextResetsAt": {{resetAt}},
                "detectedAt": "{{Now.AddMinutes(1):O}}"
              },
              "pendingAutomaticCredit": null
            }
            """);
        var tracker = new JsonWeeklyUsageResetTracker(path);

        var pending = await tracker.LoadPendingNotificationsAsync(
            CancellationToken.None);
        _ = await tracker.ObserveAsync(
            Observation(100, Now.AddDays(9), Now.AddMinutes(2)),
            CancellationToken.None);

        Assert.AreEqual(0, pending.Count);
        using var document = JsonDocument.Parse(
            await File.ReadAllTextAsync(path));
        Assert.AreEqual(
            2,
            document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual(
            0,
            document.RootElement.GetProperty("notificationEvents")
                .GetArrayLength());
    }

    [TestMethod]
    public async Task DisabledNotificationIsSuppressedAndNeverReplayed()
    {
        using var directory = TestDirectory.Create();
        var tracker = Tracker(directory);
        _ = await tracker.ObserveAsync(
            Observation(20, Now.AddDays(2), Now),
            CancellationToken.None);

        var result = await tracker.ObserveAsync(
            Observation(100, Now.AddDays(9), Now.AddMinutes(1)),
            WeeklyUsageResetAttribution.None,
            notificationsEnabled: false,
            CancellationToken.None);

        Assert.IsNotNull(result.Detection);
        Assert.AreEqual(
            0,
            (await tracker.LoadPendingNotificationsAsync(
                CancellationToken.None)).Count);
        using var document = JsonDocument.Parse(
            await File.ReadAllTextAsync(tracker.Path));
        Assert.AreEqual(
            "suppressed",
            document.RootElement.GetProperty("notificationEvents")[0]
                .GetProperty("attentionState").GetString());
    }

    [TestMethod]
    public async Task AcknowledgementPersistsAcrossTrackerRestart()
    {
        using var directory = TestDirectory.Create();
        var tracker = Tracker(directory);
        _ = await tracker.ObserveAsync(
            Observation(20, Now.AddDays(2), Now),
            CancellationToken.None);
        _ = await tracker.ObserveAsync(
            Observation(100, Now.AddDays(9), Now.AddMinutes(1)),
            CancellationToken.None);
        var pending = await tracker.LoadPendingNotificationsAsync(
            CancellationToken.None);

        var saved = await tracker.AcknowledgeNotificationAsync(
            pending[0].EventId,
            Now.AddMinutes(2),
            CancellationToken.None);
        var restored = await Tracker(directory).LoadPendingNotificationsAsync(
            CancellationToken.None);

        Assert.IsTrue(saved);
        Assert.AreEqual(0, restored.Count);
        using var document = JsonDocument.Parse(
            await File.ReadAllTextAsync(tracker.Path));
        Assert.AreEqual(
            "acknowledged",
            document.RootElement.GetProperty("notificationEvents")[0]
                .GetProperty("attentionState").GetString());
    }

    [TestMethod]
    public async Task SuppressingPendingNotificationsKeepsThemFromReappearing()
    {
        using var directory = TestDirectory.Create();
        var tracker = Tracker(directory);
        _ = await tracker.ObserveAsync(
            Observation(20, Now.AddDays(2), Now),
            CancellationToken.None);
        _ = await tracker.ObserveAsync(
            Observation(100, Now.AddDays(9), Now.AddMinutes(1)),
            CancellationToken.None);

        var saved = await tracker.SuppressPendingNotificationsAsync(
            Now.AddMinutes(2),
            CancellationToken.None);

        Assert.IsTrue(saved);
        Assert.AreEqual(
            0,
            (await Tracker(directory).LoadPendingNotificationsAsync(
                CancellationToken.None)).Count);
    }

    [TestMethod]
    public async Task SuppressionCutoffKeepsNewerPendingNotification()
    {
        using var directory = TestDirectory.Create();
        var tracker = Tracker(directory);
        _ = await tracker.ObserveAsync(
            Observation(20, Now.AddDays(2), Now),
            CancellationToken.None);
        _ = await tracker.ObserveAsync(
            Observation(100, Now.AddDays(9), Now.AddMinutes(1)),
            CancellationToken.None);
        _ = await tracker.ObserveAsync(
            Observation(50, Now.AddDays(9), Now.AddMinutes(2)),
            CancellationToken.None);
        _ = await tracker.ObserveAsync(
            Observation(
                100,
                Now.AddDays(9).AddMinutes(1),
                Now.AddMinutes(3)),
            CancellationToken.None);

        var saved =
            await tracker.SuppressPendingNotificationsThroughAsync(
                Now.AddMinutes(2),
                Now.AddMinutes(4),
                CancellationToken.None);
        var pending = await tracker.LoadPendingNotificationsAsync(
            CancellationToken.None);

        Assert.IsTrue(saved);
        Assert.AreEqual(1, pending.Count);
        Assert.AreEqual(Now.AddMinutes(3), pending[0].Detection.DetectedAt);
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
