using CodexAutoReset.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexAutoReset.Tests;

[TestClass]
public sealed class SafeLoggerTests
{
    [TestMethod]
    public async Task LoggerWritesOnlyStructuredAllowListedFields()
    {
        using var directory = TemporaryDirectory.Create();
        var logger = new SafeJsonlLogger(directory.Path);

        await logger.WriteAsync(
            new SafeLogEvent(
                DateTimeOffset.UtcNow,
                "poll",
                "automation_disabled",
                "threshold_reached",
                "weekly",
                7,
                7,
                2,
                false,
                "monitor"),
            CancellationToken.None);

        var file = Directory.GetFiles(directory.Path, "*.jsonl").Single();
        var json = await File.ReadAllTextAsync(file);
        StringAssert.Contains(json, "\"eventType\":\"poll\"");
        StringAssert.Contains(json, "\"outcome\":\"automation_disabled\"");
        Assert.IsFalse(json.Contains("would_consume", StringComparison.Ordinal));
        StringAssert.Contains(json, "\"availableCreditCount\":2");
        Assert.IsFalse(json.Contains("creditId", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("email", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(json.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task LoggerDeletesExpiredCurrentAndLegacyLogsOnly()
    {
        using var directory = TemporaryDirectory.Create();
        var now = new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
        var currentExpired = Path.Combine(
            directory.Path,
            "codex-auto-reset-2026-07-01.jsonl");
        var legacyExpired = Path.Combine(
            directory.Path,
            "codex-reset-guard-2026-07-02.jsonl");
        var currentRecent = Path.Combine(
            directory.Path,
            "codex-auto-reset-2026-07-10.jsonl");
        var unrelatedExpired = Path.Combine(
            directory.Path,
            "unrelated-2026-07-01.jsonl");

        foreach (var path in new[]
                 {
                     currentExpired,
                     legacyExpired,
                     currentRecent,
                     unrelatedExpired,
                 })
        {
            await File.WriteAllTextAsync(path, "{}\n");
        }

        File.SetLastWriteTimeUtc(currentExpired, now.UtcDateTime.AddDays(-15));
        File.SetLastWriteTimeUtc(legacyExpired, now.UtcDateTime.AddDays(-15));
        File.SetLastWriteTimeUtc(currentRecent, now.UtcDateTime.AddDays(-13));
        File.SetLastWriteTimeUtc(unrelatedExpired, now.UtcDateTime.AddDays(-15));

        var logger = new SafeJsonlLogger(directory.Path, retentionDays: 14);
        await logger.WriteAsync(
            new SafeLogEvent(now, "poll", "automation_disabled"),
            CancellationToken.None);

        Assert.IsFalse(File.Exists(currentExpired));
        Assert.IsFalse(File.Exists(legacyExpired));
        Assert.IsTrue(File.Exists(currentRecent));
        Assert.IsTrue(File.Exists(unrelatedExpired));
    }

    [DataTestMethod]
    [DataRow("dry_run", "automation_disabled")]
    [DataRow("poll", "would_consume")]
    [DataRow("poll", "dry_run_would_consume")]
    public async Task LoggerRejectsRemovedDryRunVocabulary(
        string eventType,
        string outcome)
    {
        using var directory = TemporaryDirectory.Create();
        var logger = new SafeJsonlLogger(directory.Path);

        await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            logger.WriteAsync(
                new SafeLogEvent(DateTimeOffset.UtcNow, eventType, outcome),
                CancellationToken.None));
    }

    [DataTestMethod]
    [DataRow("user@example.com")]
    [DataRow("tokenlikevalue1234567890")]
    [DataRow("future_reason_code")]
    public async Task LoggerRejectsEveryNonAllowListedReason(string reasonCode)
    {
        using var directory = TemporaryDirectory.Create();
        var logger = new SafeJsonlLogger(directory.Path);

        await Assert.ThrowsExceptionAsync<ArgumentException>(() =>
            logger.WriteAsync(
                new SafeLogEvent(
                    DateTimeOffset.UtcNow,
                    "failure",
                    "blocked",
                    reasonCode),
                CancellationToken.None));
    }

    [DataTestMethod]
    [DataRow("live_safety_block_persist_failed")]
    [DataRow("live_sticky_state_missing")]
    [DataRow("live_needs_review")]
    [DataRow("live_protocol_blocked")]
    [DataRow("executable_became_unavailable")]
    public async Task LoggerAcceptsFixedReasonCodes(string reasonCode)
    {
        using var directory = TemporaryDirectory.Create();
        var logger = new SafeJsonlLogger(directory.Path);

        await logger.WriteAsync(
            new SafeLogEvent(
                DateTimeOffset.UtcNow,
                "failure",
                "blocked",
                reasonCode,
                ComponentCategory: "live_state"),
            CancellationToken.None);

        var json = await File.ReadAllTextAsync(
            Directory.GetFiles(directory.Path, "*.jsonl").Single());
        StringAssert.Contains(json, $"\"reasonCode\":\"{reasonCode}\"");
    }

    [DataTestMethod]
    [DataRow("live_recovery_pending", "threshold_reached")]
    [DataRow("usage_reset_settling", "threshold_reached")]
    [DataRow("usage_reset_state_unavailable", "threshold_reached")]
    [DataRow("scheduled_reset_imminent", "scheduled_reset_imminent")]
    public async Task LoggerAcceptsSafetyOutcomes(
        string outcome,
        string reasonCode)
    {
        using var directory = TemporaryDirectory.Create();
        var logger = new SafeJsonlLogger(directory.Path);

        await logger.WriteAsync(
            new SafeLogEvent(
                DateTimeOffset.UtcNow,
                "poll",
                outcome,
                reasonCode,
                "weekly",
                5,
                7,
                1,
                false,
                "desktop_monitor"),
            CancellationToken.None);

        var json = await File.ReadAllTextAsync(
            Directory.GetFiles(directory.Path, "*.jsonl").Single());
        StringAssert.Contains(json, $"\"outcome\":\"{outcome}\"");
        StringAssert.Contains(json, $"\"reasonCode\":\"{reasonCode}\"");
    }

    [DataTestMethod]
    [DataRow("weekly")]
    [DataRow("fiveHour")]
    [DataRow("account")]
    public async Task LoggerAcceptsKnownDualWindowTriggerScopes(
        string triggerLimit)
    {
        using var directory = TemporaryDirectory.Create();
        var logger = new SafeJsonlLogger(directory.Path);

        await logger.WriteAsync(
            new SafeLogEvent(
                DateTimeOffset.UtcNow,
                "poll",
                "automation_disabled",
                TriggerLimit: triggerLimit),
            CancellationToken.None);

        var json = await File.ReadAllTextAsync(
            Directory.GetFiles(directory.Path, "*.jsonl").Single());
        StringAssert.Contains(json, $"\"triggerLimit\":\"{triggerLimit}\"");
    }

    [DataTestMethod]
    [DataRow(-0.1)]
    [DataRow(100.1)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    public async Task LoggerRejectsInvalidRemainingPercent(double remainingPercent)
    {
        using var directory = TemporaryDirectory.Create();
        var logger = new SafeJsonlLogger(directory.Path);

        await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(() =>
            logger.WriteAsync(
                new SafeLogEvent(
                    DateTimeOffset.UtcNow,
                    "poll",
                    "blocked",
                    RemainingPercent: remainingPercent),
                CancellationToken.None));
    }
}
