using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodexAutoReset.AppServer;

public enum CodexExecutableDiscoverySource
{
    ExplicitConfiguration,
    RecognizedInstallation,
    PathEnvironment,
}

public enum CodexExecutableTrust
{
    ReadOnlyUnverifiedPath,
    ReadOnlyPathDiscovery,
    TrustedExplicitConfiguration,
    TrustedRecognizedInstallation,
}

public sealed class CodexExecutableResolution
{
    internal CodexExecutableResolution(
        string executablePath,
        CodexExecutableDiscoverySource discoverySource,
        CodexExecutableTrust trust)
    {
        ExecutablePath = executablePath;
        DiscoverySource = discoverySource;
        Trust = trust;
    }

    public string ExecutablePath { get; }

    public CodexExecutableDiscoverySource DiscoverySource { get; }

    public CodexExecutableTrust Trust { get; }

    public bool AllowsMutation => Trust is
        CodexExecutableTrust.TrustedExplicitConfiguration
        or CodexExecutableTrust.TrustedRecognizedInstallation;
}

public static class CodexExecutableLocator
{
    private const string ExtendedPathPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";
    private const int MaximumCanonicalPathLength = 32_768;

    public static string Locate(string? configuredPath) =>
        Resolve(configuredPath).ExecutablePath;

    public static CodexExecutableResolution Resolve(string? configuredPath)
    {
        if (configuredPath is not null)
        {
            return ResolveExistingCandidate(
                configuredPath,
                CodexExecutableDiscoverySource.ExplicitConfiguration,
                Path.IsPathFullyQualified(configuredPath));
        }

        foreach (var standardPath in GetRecognizedInstallationPaths())
        {
            if (IsCodexExecutable(standardPath))
            {
                return ResolveExistingCandidate(
                    standardPath,
                    CodexExecutableDiscoverySource.RecognizedInstallation,
                    wasExplicitAbsolutePath: false);
            }
        }

        throw new AppServerException(AppServerFailureCategory.ExecutableNotFound);
    }

    internal static CodexExecutableResolution ResolveExistingCandidate(
        string path,
        CodexExecutableDiscoverySource discoverySource,
        bool wasExplicitAbsolutePath)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            throw new AppServerException(AppServerFailureCategory.ExecutableNotFound);
        }

        if (!IsCodexExecutable(fullPath))
        {
            throw new AppServerException(AppServerFailureCategory.ExecutableNotFound);
        }

        var canonicalizationSucceeded = TryGetCanonicalPath(
            fullPath,
            out var canonicalPath);
        var resolvesToRecognizedInstallation = canonicalizationSucceeded
            && IsRecognizedCanonicalPath(canonicalPath);

        var trust = discoverySource switch
        {
            CodexExecutableDiscoverySource.ExplicitConfiguration
                when wasExplicitAbsolutePath && canonicalizationSucceeded =>
                    CodexExecutableTrust.TrustedExplicitConfiguration,
            CodexExecutableDiscoverySource.RecognizedInstallation
                when resolvesToRecognizedInstallation =>
                    CodexExecutableTrust.TrustedRecognizedInstallation,
            CodexExecutableDiscoverySource.PathEnvironment
                when resolvesToRecognizedInstallation =>
                    CodexExecutableTrust.TrustedRecognizedInstallation,
            CodexExecutableDiscoverySource.PathEnvironment =>
                CodexExecutableTrust.ReadOnlyPathDiscovery,
            _ => CodexExecutableTrust.ReadOnlyUnverifiedPath,
        };

        // Canonicalization establishes trust, but the visible installer path is
        // the stable launch point. Its canonical target is a versioned Codex
        // package-cache path that can change during an update.
        return new CodexExecutableResolution(fullPath, discoverySource, trust);
    }

    internal static string CanonicalizeForReadOnly(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return File.Exists(fullPath)
            && TryGetCanonicalPath(fullPath, out var canonicalPath)
                ? canonicalPath
                : fullPath;
    }

    internal static IReadOnlyList<string> BuildRecognizedInstallationPaths(
        params string?[] localAppDataRoots)
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in localAppDataRoots)
        {
            if (string.IsNullOrWhiteSpace(root)
                || !Path.IsPathFullyQualified(root))
            {
                continue;
            }

            try
            {
                var path = Path.Combine(
                    Path.GetFullPath(root),
                    "Programs",
                    "OpenAI",
                    "Codex",
                    "bin",
                    "codex.exe");
                if (seen.Add(path))
                {
                    paths.Add(path);
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException
                    or NotSupportedException
                    or PathTooLongException)
            {
            }
        }

        return paths;
    }

    private static IReadOnlyList<string> GetRecognizedInstallationPaths()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        var localAppDataEnvironment = Environment.GetEnvironmentVariable(
            "LOCALAPPDATA");
        var userProfile = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile);
        var userProfileEnvironment = Environment.GetEnvironmentVariable(
            "USERPROFILE");
        var conventionalLocalAppData = string.IsNullOrWhiteSpace(userProfile)
            ? null
            : Path.Combine(userProfile, "AppData", "Local");
        var conventionalEnvironmentLocalAppData =
            string.IsNullOrWhiteSpace(userProfileEnvironment)
                ? null
                : Path.Combine(userProfileEnvironment, "AppData", "Local");

        return BuildRecognizedInstallationPaths(
            localAppData,
            localAppDataEnvironment,
            conventionalLocalAppData,
            conventionalEnvironmentLocalAppData);
    }

    private static bool IsRecognizedCanonicalPath(string canonicalPath)
    {
        foreach (var recognizedPath in GetRecognizedInstallationPaths())
        {
            if (IsCodexExecutable(recognizedPath)
                && TryGetCanonicalPath(recognizedPath, out var canonicalRecognizedPath)
                && string.Equals(
                    canonicalPath,
                    canonicalRecognizedPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetCanonicalPath(string path, out string canonicalPath)
    {
        canonicalPath = Path.GetFullPath(path);

        try
        {
            if (!OperatingSystem.IsWindows())
            {
                var target = new FileInfo(canonicalPath)
                    .ResolveLinkTarget(returnFinalTarget: true);
                canonicalPath = Path.GetFullPath(target?.FullName ?? canonicalPath);
                return true;
            }

            using SafeFileHandle handle = File.OpenHandle(
                canonicalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            var buffer = new char[512];
            while (buffer.Length <= MaximumCanonicalPathLength)
            {
                var length = GetFinalPathNameByHandle(
                    handle,
                    buffer,
                    (uint)buffer.Length,
                    0);
                if (length == 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                if (length < buffer.Length)
                {
                    canonicalPath = NormalizeExtendedPath(
                        new string(buffer, 0, checked((int)length)));
                    canonicalPath = Path.GetFullPath(canonicalPath);
                    return true;
                }

                if (length > MaximumCanonicalPathLength)
                {
                    return false;
                }

                buffer = new char[checked((int)length + 1)];
            }
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or Win32Exception
                or NotSupportedException)
        {
        }

        return false;
    }

    private static string NormalizeExtendedPath(string path)
    {
        if (path.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[ExtendedUncPrefix.Length..];
        }

        return path.StartsWith(ExtendedPathPrefix, StringComparison.Ordinal)
            ? path[ExtendedPathPrefix.Length..]
            : path;
    }

    private static bool IsCodexExecutable(string path) =>
        string.Equals(
            Path.GetFileName(path),
            "codex.exe",
            StringComparison.OrdinalIgnoreCase)
        && File.Exists(path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] char[] filePath,
        uint filePathLength,
        uint flags);
}
