using CodexAutoReset.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexAutoReset.Tests;

[TestClass]
public sealed class LiveResetSafetyLatchTests
{
    [TestMethod]
    public async Task DurableMarkerSurvivesNewLatchWithoutSensitiveFields()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "live-safety-block.json");
        var first = new LiveResetSafetyLatch(path);

        first.BlockProtocolMismatch();

        var second = new LiveResetSafetyLatch(path);
        Assert.AreEqual(
            LiveAttemptBlockReason.ProtocolMismatch,
            second.BlockReason);
        var marker = await File.ReadAllTextAsync(path);
        Assert.AreEqual(
            "{\"schemaVersion\":1,\"reason\":\"protocolMismatch\"}",
            marker);
    }

    [TestMethod]
    public async Task RevisionAwareProtocolMarkerUsesStrictVersionThreeSchema()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "live-safety-block.json");
        var first = new LiveResetSafetyLatch(path, "0.144.5");

        first.BlockProtocolMismatch();

        var second = new LiveResetSafetyLatch(path, "0.144.5");
        Assert.AreEqual(
            LiveAttemptBlockReason.ProtocolMismatch,
            second.BlockReason);
        var marker = await File.ReadAllTextAsync(path);
        Assert.AreEqual(
            "{\"schemaVersion\":3,\"reason\":\"protocolMismatch\","
                + "\"compatibilityRevision\":\"0.144.5\","
                + "\"origin\":\"mutationAmbiguous\"}",
            marker);
    }

    [TestMethod]
    public async Task BlockOverloadCanProvideCompatibilityRevision()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "live-safety-block.json");
        var latch = new LiveResetSafetyLatch(path);

        latch.BlockProtocolMismatch("audit-7");

        var marker = await File.ReadAllTextAsync(path);
        StringAssert.Contains(
            marker,
            "\"compatibilityRevision\":\"audit-7\"");
    }

    [TestMethod]
    public void MutationAmbiguousMarkerCannotBeAutomaticallyCleared()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "live-safety-block.json");
        var blocked = new LiveResetSafetyLatch(path, "audit-1");
        blocked.BlockProtocolMismatch();
        var upgraded = new LiveResetSafetyLatch(path, "audit-2");

        var cleared = upgraded.TryClearProtocolMismatch(
            compatibilityValidationSucceeded: true,
            hasUnresolvedAttempt: false);

        Assert.IsFalse(cleared);
        Assert.AreEqual(
            LiveAttemptBlockReason.ProtocolMismatch,
            upgraded.BlockReason);
        Assert.IsTrue(File.Exists(path));
        Assert.IsFalse(File.Exists(path + ".tmp"));
        Assert.AreEqual(
            LiveAttemptBlockReason.ProtocolMismatch,
            new LiveResetSafetyLatch(path, "audit-2").BlockReason);
    }

    [TestMethod]
    public void ExplicitRevisionClearOverloadCannotClearMutationMarker()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "live-safety-block.json");
        var blocked = new LiveResetSafetyLatch(path);
        blocked.BlockProtocolMismatch("audit-1");
        var upgraded = new LiveResetSafetyLatch(path);

        var cleared = upgraded.TryClearProtocolMismatch(
            "audit-2",
            compatibilityValidationSucceeded: true,
            hasUnresolvedAttempt: false);

        Assert.IsFalse(cleared);
        Assert.AreEqual(
            LiveAttemptBlockReason.ProtocolMismatch,
            upgraded.BlockReason);
        Assert.IsTrue(File.Exists(path));
    }

    [DataTestMethod]
    [DataRow("audit-1", true, false)]
    [DataRow("audit-2", false, false)]
    [DataRow("audit-2", true, true)]
    public void ClearRequiresChangedRevisionValidatedCompatibilityAndNoAttempt(
        string currentRevision,
        bool compatibilityValidationSucceeded,
        bool hasUnresolvedAttempt)
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "live-safety-block.json");
        var blocked = new LiveResetSafetyLatch(path, "audit-1");
        blocked.BlockProtocolMismatch();
        var upgraded = new LiveResetSafetyLatch(path, currentRevision);

        var cleared = upgraded.TryClearProtocolMismatch(
            compatibilityValidationSucceeded,
            hasUnresolvedAttempt);

        Assert.IsFalse(cleared);
        Assert.AreEqual(
            LiveAttemptBlockReason.ProtocolMismatch,
            upgraded.BlockReason);
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public async Task LegacyVersionOneMarkerCannotBeAutomaticallyCleared()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "live-safety-block.json");
        await File.WriteAllTextAsync(
            path,
            "{\"schemaVersion\":1,\"reason\":\"protocolMismatch\"}");
        var latch = new LiveResetSafetyLatch(path, "audit-2");

        var cleared = latch.TryClearProtocolMismatch(
            compatibilityValidationSucceeded: true,
            hasUnresolvedAttempt: false);

        Assert.IsFalse(cleared);
        Assert.AreEqual(
            LiveAttemptBlockReason.ProtocolMismatch,
            latch.BlockReason);
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public async Task LegacyVersionTwoMarkerRemainsProtocolBlocked()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "live-safety-block.json");
        await File.WriteAllTextAsync(
            path,
            "{\"schemaVersion\":2,\"reason\":\"protocolMismatch\","
                + "\"compatibilityRevision\":\"audit-1\"}");
        var latch = new LiveResetSafetyLatch(path, "audit-2");

        var cleared = latch.TryClearProtocolMismatch(
            compatibilityValidationSucceeded: true,
            hasUnresolvedAttempt: false);

        Assert.IsFalse(cleared);
        Assert.AreEqual(
            LiveAttemptBlockReason.ProtocolMismatch,
            latch.BlockReason);
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public void UnknownFailureCannotBeAutomaticallyCleared()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "live-safety-block.json");
        var blocked = new LiveResetSafetyLatch(path, "audit-1");
        blocked.BlockUnknownFailure();
        var upgraded = new LiveResetSafetyLatch(path, "audit-2");

        var cleared = upgraded.TryClearProtocolMismatch(
            compatibilityValidationSucceeded: true,
            hasUnresolvedAttempt: false);

        Assert.IsFalse(cleared);
        Assert.AreEqual(
            LiveAttemptBlockReason.UnknownFailure,
            upgraded.BlockReason);
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public void ChangedMarkerFailsClosedDuringClear()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "live-safety-block.json");
        var blocked = new LiveResetSafetyLatch(path, "audit-1");
        blocked.BlockProtocolMismatch();
        var upgraded = new LiveResetSafetyLatch(path, "audit-2");
        File.Delete(path);
        Directory.CreateDirectory(path);

        var cleared = upgraded.TryClearProtocolMismatch(
            compatibilityValidationSucceeded: true,
            hasUnresolvedAttempt: false);

        Assert.IsFalse(cleared);
        Assert.AreEqual(
            LiveAttemptBlockReason.ProtocolMismatch,
            upgraded.BlockReason);
        Assert.IsTrue(Directory.Exists(path));
    }

    [DataTestMethod]
    [DataRow("{malformed")]
    [DataRow("{\"schemaVersion\":1,\"reason\":\"other\"}")]
    [DataRow("{\"schemaVersion\":1,\"reason\":\"protocolMismatch\",\"extra\":1}")]
    [DataRow("{\"schemaVersion\":1,\"schemaVersion\":1,\"reason\":\"protocolMismatch\"}")]
    [DataRow("{\"schemaVersion\":2,\"reason\":\"protocolMismatch\"}")]
    [DataRow("{\"schemaVersion\":2,\"reason\":\"unknownFailure\",\"compatibilityRevision\":\"audit-1\"}")]
    [DataRow("{\"schemaVersion\":2,\"reason\":\"protocolMismatch\",\"compatibilityRevision\":\"audit-1\",\"extra\":1}")]
    [DataRow("{\"schemaVersion\":3,\"reason\":\"protocolMismatch\",\"compatibilityRevision\":\"audit-1\"}")]
    [DataRow("{\"schemaVersion\":3,\"reason\":\"protocolMismatch\",\"compatibilityRevision\":\"audit-1\",\"origin\":\"readMismatch\"}")]
    [DataRow("{\"schemaVersion\":3,\"reason\":\"unknownFailure\",\"compatibilityRevision\":\"audit-1\",\"origin\":\"mutationAmbiguous\"}")]
    [DataRow("{\"schemaVersion\":3,\"reason\":\"protocolMismatch\",\"compatibilityRevision\":\"audit-1\",\"origin\":\"mutationAmbiguous\",\"extra\":1}")]
    public async Task InvalidMarkerIsPermanentBlockEvidence(string content)
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "live-safety-block.json");
        await File.WriteAllTextAsync(path, content);

        var latch = new LiveResetSafetyLatch(path);

        Assert.AreEqual(LiveAttemptBlockReason.UnknownFailure, latch.BlockReason);
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("contains space")]
    [DataRow("line\nbreak")]
    public void InvalidCompatibilityRevisionIsRejected(string revision)
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "live-safety-block.json");

        var exception = Assert.ThrowsException<ArgumentException>(
            () => new LiveResetSafetyLatch(path, revision));

        StringAssert.StartsWith(
            exception.Message,
            "live_compatibility_revision_invalid");
        Assert.AreEqual("currentCompatibilityRevision", exception.ParamName);
    }

    [TestMethod]
    public async Task LeftoverTemporaryMarkerIsBlockEvidence()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "live-safety-block.json");
        await File.WriteAllTextAsync(path + ".tmp", "partial");

        var latch = new LiveResetSafetyLatch(path);

        Assert.AreEqual(LiveAttemptBlockReason.UnknownFailure, latch.BlockReason);
    }
}
