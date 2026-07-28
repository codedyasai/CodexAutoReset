using System.IO;
using CodexAutoReset.Desktop;
using CodexAutoReset.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexAutoReset.Desktop.Tests;

[TestClass]
public sealed class CompatibilityNotificationGateTests
{
    [TestMethod]
    public void Consume_WarnsOncePerIncidentAndRearamsAfterCompatibilityRecovery()
    {
        var gate = new CompatibilityNotificationGate();

        Assert.IsFalse(gate.Consume(CodexCompatibilityState.Unknown));
        Assert.IsFalse(gate.Consume(CodexCompatibilityState.VerificationPending));
        Assert.IsTrue(gate.Consume(CodexCompatibilityState.ReadUnsupported));
        Assert.IsFalse(gate.Consume(CodexCompatibilityState.ReadUnsupported));
        Assert.IsFalse(gate.Consume(CodexCompatibilityState.MutationUnverified));
        Assert.IsFalse(gate.Consume(CodexCompatibilityState.Unknown));
        Assert.IsFalse(gate.Consume(CodexCompatibilityState.ReadUnsupported));

        Assert.IsFalse(gate.Consume(CodexCompatibilityState.Compatible));
        Assert.IsFalse(gate.Consume(CodexCompatibilityState.VerificationPending));
        Assert.IsTrue(gate.Consume(CodexCompatibilityState.MutationUnverified));
    }

    [TestMethod]
    public void Consume_RemindsOnlyAfterTwentyFourHoursWhenIncidentPersists()
    {
        var now = new DateTimeOffset(
            2026,
            7,
            28,
            0,
            0,
            0,
            TimeSpan.Zero);
        var gate = new CompatibilityNotificationGate(() => now);

        Assert.IsTrue(gate.Consume(CodexCompatibilityState.ReadUnsupported));

        now = now.AddHours(23).AddMinutes(59);
        Assert.IsFalse(gate.Consume(CodexCompatibilityState.ReadUnsupported));

        now = now.AddMinutes(1);
        Assert.IsTrue(gate.Consume(CodexCompatibilityState.ReadUnsupported));
        Assert.IsFalse(gate.Consume(CodexCompatibilityState.MutationUnverified));
    }

    [TestMethod]
    public void DurableGateSuppressesSameIncidentAcrossProcessRestart()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"CodexAutoReset-compatibility-notification-{Guid.NewGuid():N}");
        var path = Path.Combine(
            directory,
            "compatibility-notification-state.json");
        var now = new DateTimeOffset(
            2026,
            7,
            28,
            0,
            0,
            0,
            TimeSpan.Zero);
        try
        {
            var first = new CompatibilityNotificationGate(path, () => now);

            Assert.IsTrue(first.Consume(
                CodexCompatibilityState.ReadUnsupported));
            Assert.IsTrue(File.Exists(path));

            var restarted = new CompatibilityNotificationGate(path, () => now);
            Assert.IsFalse(restarted.Consume(
                CodexCompatibilityState.ReadUnsupported));

            now = now.AddHours(24);
            var nextDay = new CompatibilityNotificationGate(path, () => now);
            Assert.IsTrue(nextDay.Consume(
                CodexCompatibilityState.MutationUnverified));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    [TestMethod]
    public void CompatibleStateClearsDurableIncident()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"CodexAutoReset-compatibility-notification-{Guid.NewGuid():N}");
        var path = Path.Combine(
            directory,
            "compatibility-notification-state.json");
        var now = new DateTimeOffset(
            2026,
            7,
            28,
            0,
            0,
            0,
            TimeSpan.Zero);
        try
        {
            var gate = new CompatibilityNotificationGate(path, () => now);
            Assert.IsTrue(gate.Consume(
                CodexCompatibilityState.ReadUnsupported));

            Assert.IsFalse(gate.Consume(CodexCompatibilityState.Compatible));
            Assert.IsFalse(File.Exists(path));

            var restarted = new CompatibilityNotificationGate(path, () => now);
            Assert.IsTrue(restarted.Consume(
                CodexCompatibilityState.ReadUnsupported));
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
