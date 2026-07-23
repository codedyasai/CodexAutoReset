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

    [DataTestMethod]
    [DataRow("{malformed")]
    [DataRow("{\"schemaVersion\":1,\"reason\":\"other\"}")]
    [DataRow("{\"schemaVersion\":1,\"reason\":\"protocolMismatch\",\"extra\":1}")]
    [DataRow("{\"schemaVersion\":1,\"schemaVersion\":1,\"reason\":\"protocolMismatch\"}")]
    public async Task InvalidMarkerIsPermanentBlockEvidence(string content)
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "live-safety-block.json");
        await File.WriteAllTextAsync(path, content);

        var latch = new LiveResetSafetyLatch(path);

        Assert.AreEqual(LiveAttemptBlockReason.UnknownFailure, latch.BlockReason);
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
