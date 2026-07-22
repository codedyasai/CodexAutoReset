using CodexResetGuard.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexResetGuard.Runtime.Tests;

[TestClass]
public sealed class WindowsRegistryStoreTests
{
    [TestMethod]
    public void MissingValueInExistingCurrentUserKeyReturnsMissing()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows registry is unavailable.");
        }

        var store = new WindowsCurrentUserRegistryStore();
        var value = store.ReadValue(
            "Software",
            $"CodexResetGuard.Tests.{Guid.NewGuid():N}");

        Assert.AreEqual(CurrentUserRegistryValueKind.Missing, value.Kind);
        Assert.IsNull(value.StringValue);
    }
}
