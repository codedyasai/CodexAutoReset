using System.Security.Cryptography;
using System.Text;
using CodexAutoReset.Core;

namespace CodexAutoReset.Runtime;

public sealed class DpapiSecretProtector : ISecretProtector
{
    private const int MaximumPlaintextBytes = 16 * 1024;
    private const int MaximumProtectedTextLength = 64 * 1024;

    private static readonly byte[] PurposeEntropy =
        Encoding.UTF8.GetBytes("CodexResetGuard/live-reset-credit/v1");

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public string Protect(string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("dpapi_windows_required");
        }

        byte[] plaintextBytes;
        try
        {
            plaintextBytes = StrictUtf8.GetBytes(plaintext);
        }
        catch (EncoderFallbackException exception)
        {
            throw new CryptographicException("secret_text_invalid", exception);
        }

        if (plaintextBytes.Length > MaximumPlaintextBytes)
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
            throw new ArgumentException("secret_text_too_large", nameof(plaintext));
        }

        try
        {
            var protectedBytes = ProtectedData.Protect(
                plaintextBytes,
                PurposeEntropy,
                DataProtectionScope.CurrentUser);
            try
            {
                return Convert.ToBase64String(protectedBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }

    public string Unprotect(string protectedValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedValue);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("dpapi_windows_required");
        }

        if (protectedValue.Length > MaximumProtectedTextLength
            || protectedValue.Any(char.IsWhiteSpace))
        {
            throw new CryptographicException("protected_secret_invalid");
        }

        byte[] protectedBytes;
        try
        {
            protectedBytes = Convert.FromBase64String(protectedValue);
        }
        catch (FormatException exception)
        {
            throw new CryptographicException("protected_secret_invalid", exception);
        }

        if (protectedBytes.Length == 0
            || !string.Equals(
                Convert.ToBase64String(protectedBytes),
                protectedValue,
                StringComparison.Ordinal))
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            throw new CryptographicException("protected_secret_invalid");
        }

        try
        {
            var plaintextBytes = ProtectedData.Unprotect(
                protectedBytes,
                PurposeEntropy,
                DataProtectionScope.CurrentUser);
            try
            {
                if (plaintextBytes.Length == 0
                    || plaintextBytes.Length > MaximumPlaintextBytes)
                {
                    throw new CryptographicException("secret_text_invalid");
                }

                var plaintext = StrictUtf8.GetString(plaintextBytes);
                if (string.IsNullOrWhiteSpace(plaintext))
                {
                    throw new CryptographicException("secret_text_invalid");
                }

                return plaintext;
            }
            catch (DecoderFallbackException exception)
            {
                throw new CryptographicException("secret_text_invalid", exception);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintextBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }
}
