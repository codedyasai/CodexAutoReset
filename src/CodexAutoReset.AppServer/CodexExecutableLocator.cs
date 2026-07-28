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
    private const string CodexExecutableFileName = "codex.exe";
    private const string ExtendedPathPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";
    private const int MaximumCanonicalPathLength = 32_768;

    public static string Locate(string? configuredPath) =>
        Resolve(configuredPath).ExecutablePath;

    public static CodexExecutableResolution Resolve(string? configuredPath)
    {
        if (configuredPath is not null)
        {
            try
            {
                return ResolveExistingCandidate(
                    configuredPath,
                    CodexExecutableDiscoverySource.ExplicitConfiguration,
                    Path.IsPathFullyQualified(configuredPath));
            }
            catch (AppServerException exception) when (
                exception.Category == AppServerFailureCategory.ExecutableNotFound
                && IsKnownInstallerPathShape(configuredPath))
            {
                // Codex updates replace the physical standalone release directory.
                // Recover only known Codex installer paths; arbitrary configured
                // paths must still fail instead of silently changing executables.
            }
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

    public static string? TryGetFilePickerExecutablePath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            try
            {
                var fullPath = Path.GetFullPath(configuredPath);
                if (IsCodexExecutable(fullPath)
                    && TryGetCanonicalPath(fullPath, out var canonicalPath)
                    && IsCodexExecutable(canonicalPath))
                {
                    return canonicalPath;
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException
                    or NotSupportedException
                    or PathTooLongException
                    or System.Security.SecurityException)
            {
            }
        }

        try
        {
            return Resolve(configuredPath: null).ExecutablePath;
        }
        catch (AppServerException exception) when (
            exception.Category == AppServerFailureCategory.ExecutableNotFound)
        {
            return null;
        }
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
                    CodexExecutableFileName);
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

    internal static IReadOnlyList<string> BuildStandalonePackageInstallationPaths(
        params string?[] userProfileRoots) =>
        BuildStandalonePackageInstallationPaths(
            TryResolveImmediateDirectoryLinkTarget,
            userProfileRoots);

    internal static IReadOnlyList<string> BuildStandalonePackageInstallationPaths(
        Func<string, string?> resolveImmediateDirectoryLinkTarget,
        params string?[] userProfileRoots)
    {
        ArgumentNullException.ThrowIfNull(resolveImmediateDirectoryLinkTarget);

        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in userProfileRoots)
        {
            if (string.IsNullOrWhiteSpace(root)
                || !Path.IsPathFullyQualified(root))
            {
                continue;
            }

            try
            {
                var fullRoot = Path.GetFullPath(root);
                var standaloneRoot = Path.Combine(
                    fullRoot,
                    ".codex",
                    "packages",
                    "standalone");
                var currentPath = Path.Combine(standaloneRoot, "current");
                var target = resolveImmediateDirectoryLinkTarget(currentPath);
                if (target is null
                    || !TryBuildStandalonePhysicalExecutablePath(
                        fullRoot,
                        standaloneRoot,
                        target,
                        out var executablePath)
                    || !seen.Add(executablePath))
                {
                    continue;
                }

                paths.Add(executablePath);
            }
            catch (Exception exception) when (
                exception is ArgumentException
                    or IOException
                    or NotSupportedException
                    or PathTooLongException
                    or UnauthorizedAccessException
                    or System.Security.SecurityException)
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

        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddRecognizedPaths(
            paths,
            seen,
            BuildStandalonePackageInstallationPaths(
                userProfile,
                userProfileEnvironment));

        foreach (var legacyPath in BuildRecognizedInstallationPaths(
            localAppData,
            localAppDataEnvironment,
            conventionalLocalAppData,
            conventionalEnvironmentLocalAppData))
        {
            var localRoot = TryGetLegacyLocalAppDataRoot(legacyPath);
            if (localRoot is not null
                && IsReparseFreePath(
                    localRoot,
                    legacyPath,
                    requireLeafFile: true)
                && seen.Add(legacyPath))
            {
                paths.Add(legacyPath);
            }
        }

        return paths;
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

    private static string? TryResolveImmediateDirectoryLinkTarget(string path)
    {
        try
        {
            return new DirectoryInfo(path)
                .ResolveLinkTarget(returnFinalTarget: false)
                ?.FullName;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or NotSupportedException
                or PathTooLongException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static bool TryBuildStandalonePhysicalExecutablePath(
        string userProfileRoot,
        string standaloneRoot,
        string currentTarget,
        out string executablePath)
    {
        executablePath = string.Empty;

        string fullUserProfileRoot;
        string fullStandaloneRoot;
        string releasesRoot;
        string targetRoot;
        try
        {
            fullUserProfileRoot = TrimEndingDirectorySeparator(
                Path.GetFullPath(userProfileRoot));
            fullStandaloneRoot = TrimEndingDirectorySeparator(
                Path.GetFullPath(standaloneRoot));
            releasesRoot = TrimEndingDirectorySeparator(
                Path.Combine(fullStandaloneRoot, "releases"));
            targetRoot = TrimEndingDirectorySeparator(
                Path.GetFullPath(currentTarget));
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return false;
        }

        if (!string.Equals(
                Path.GetDirectoryName(targetRoot),
                releasesRoot,
                StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(Path.GetFileName(targetRoot)))
        {
            return false;
        }

        var candidate = Path.Combine(
            targetRoot,
            "bin",
            CodexExecutableFileName);
        if (!IsReparseFreePath(
                fullUserProfileRoot,
                candidate,
                requireLeafFile: true)
            || !IsCodexExecutable(candidate)
            || !TryGetCanonicalPath(candidate, out var canonicalPath)
            || !string.Equals(
                TrimEndingDirectorySeparator(canonicalPath),
                TrimEndingDirectorySeparator(candidate),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        executablePath = candidate;
        return true;
    }

    private static bool IsReparseFreePath(
        string trustedRoot,
        string candidatePath,
        bool requireLeafFile) =>
        IsReparseFreePath(
            trustedRoot,
            candidatePath,
            requireLeafFile,
            File.GetAttributes);

    internal static bool IsReparseFreePath(
        string trustedRoot,
        string candidatePath,
        bool requireLeafFile,
        Func<string, FileAttributes> getAttributes)
    {
        ArgumentNullException.ThrowIfNull(getAttributes);

        string root;
        string candidate;
        try
        {
            root = TrimEndingDirectorySeparator(Path.GetFullPath(trustedRoot));
            candidate = Path.GetFullPath(candidatePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or NotSupportedException
                or PathTooLongException)
        {
            return false;
        }

        var relative = Path.GetRelativePath(root, candidate);
        if (relative == "."
            || relative == ".."
            || relative.StartsWith(
                string.Concat("..", Path.DirectorySeparatorChar),
                StringComparison.Ordinal)
            || Path.IsPathFullyQualified(relative))
        {
            return false;
        }

        var current = root;
        var segments = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        try
        {
            for (var index = 0; index < segments.Length; index++)
            {
                current = Path.Combine(current, segments[index]);
                var attributes = getAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }

                var isLeaf = index == segments.Length - 1;
                if (isLeaf
                    && requireLeafFile
                    && (attributes & FileAttributes.Directory) != 0)
                {
                    return false;
                }
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or NotSupportedException
                or PathTooLongException
                or UnauthorizedAccessException
                or System.Security.SecurityException)
        {
            return false;
        }

        return true;
    }

    private static string? TryGetLegacyLocalAppDataRoot(string executablePath)
    {
        var suffix = Path.Combine(
            "Programs",
            "OpenAI",
            "Codex",
            "bin",
            CodexExecutableFileName);
        if (!executablePath.EndsWith(
                suffix,
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rootLength = executablePath.Length - suffix.Length;
        return rootLength > 0
            ? executablePath[..rootLength]
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : null;
    }

    private static bool IsKnownInstallerPathShape(string path)
    {
        if (!Path.IsPathFullyQualified(path)
            || !string.Equals(
                Path.GetFileName(path),
                CodexExecutableFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return normalized.Contains(
                Path.Combine("Programs", "OpenAI", "Codex", "bin"),
                StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(
                Path.Combine(".codex", "packages", "standalone", "releases"),
                StringComparison.OrdinalIgnoreCase);
    }

    private static void AddRecognizedPaths(
        List<string> destination,
        HashSet<string> seen,
        IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (seen.Add(candidate))
            {
                destination.Add(candidate);
            }
        }
    }

    private static string TrimEndingDirectorySeparator(string path) =>
        Path.TrimEndingDirectorySeparator(path);

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
            CodexExecutableFileName,
            StringComparison.OrdinalIgnoreCase)
        && File.Exists(path);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] char[] filePath,
        uint filePathLength,
        uint flags);
}
