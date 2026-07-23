using CodexAutoReset.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexAutoReset.Runtime.Tests;

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
            $"CodexAutoReset.Tests.{Guid.NewGuid():N}");

        Assert.AreEqual(CurrentUserRegistryValueKind.Missing, value.Kind);
        Assert.IsNull(value.StringValue);
    }
}
