using CodexResetGuard.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexResetGuard.Runtime.Tests;

[TestClass]
public sealed class RuntimePathsAndLeaseTests
{
    [TestMethod]
    public void Paths_UseFixedFileNames()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var paths = RuntimePaths.ForTesting(root);

        Assert.AreEqual(Path.Combine(root, "settings.json"), paths.SettingsFile);
        Assert.AreEqual(Path.Combine(root, "live-state.json"), paths.LiveStateFile);
        Assert.AreEqual(
            Path.Combine(root, "live-safety-block.json"),
            paths.LiveSafetyBlockFile);
        Assert.AreEqual(Path.Combine(root, "instance.lock"), paths.InstanceLockFile);
        Assert.AreEqual(Path.Combine(root, "Logs"), paths.LogDirectory);
    }

    [TestMethod]
    public void Lease_PreventsSecondInstanceUntilDisposed()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"CodexResetGuard.Runtime.Tests-{Guid.NewGuid():N}");
        var paths = RuntimePaths.ForTesting(root);

        try
        {
            using var first = SingleInstanceLease.TryAcquire(paths);
            Assert.IsNotNull(first);
            Assert.IsNull(SingleInstanceLease.TryAcquire(paths));

            first.Dispose();
            using var second = SingleInstanceLease.TryAcquire(paths);
            Assert.IsNotNull(second);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
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
