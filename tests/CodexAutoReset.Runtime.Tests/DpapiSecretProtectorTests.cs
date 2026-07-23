using System.Security.Cryptography;
using CodexAutoReset.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexAutoReset.Runtime.Tests;

[TestClass]
public sealed class DpapiSecretProtectorTests
{
    [TestMethod]
    public void ProtectAndUnprotect_RoundTripsWithoutPlaintextDisclosure()
    {
        var protector = new DpapiSecretProtector();
        const string plaintext = "credit-id-private-value";

        var protectedValue = protector.Protect(plaintext);

        Assert.AreNotEqual(plaintext, protectedValue);
        Assert.AreEqual(plaintext, protector.Unprotect(protectedValue));
        _ = Convert.FromBase64String(protectedValue);
    }

    [TestMethod]
    public void Protect_UsesRandomizedDpapiCiphertext()
    {
        var protector = new DpapiSecretProtector();

        var first = protector.Protect("same-credit-id");
        var second = protector.Protect("same-credit-id");

        Assert.AreNotEqual(first, second);
    }

    [DataTestMethod]
    [DataRow("not-base64")]
    [DataRow(" YQ==")]
    [DataRow("YQ==\r\n")]
    public void Unprotect_RejectsNonCanonicalBase64(string protectedValue)
    {
        var protector = new DpapiSecretProtector();

        Assert.ThrowsException<CryptographicException>(
            () => protector.Unprotect(protectedValue));
    }
}
