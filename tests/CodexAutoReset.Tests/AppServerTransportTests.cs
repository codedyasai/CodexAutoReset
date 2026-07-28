using System.Reflection;
using CodexAutoReset.AppServer;
using CodexAutoReset.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexAutoReset.Tests;

[TestClass]
public sealed class AppServerTransportTests
{
    [TestMethod]
    public async Task BoundedReaderReadsBufferedJsonLines()
    {
        using var input = new StringReader("{\"id\":1}\n{\"id\":2}\r\n");
        var reader = new BoundedTextLineReader(input, maximumLineLength: 32);

        Assert.AreEqual("{\"id\":1}", await reader.ReadLineAsync(CancellationToken.None));
        Assert.AreEqual("{\"id\":2}", await reader.ReadLineAsync(CancellationToken.None));
        Assert.IsNull(await reader.ReadLineAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task BoundedReaderAcceptsAnExactLimitWithoutFinalNewline()
    {
        using var input = new StringReader("12345678");
        var reader = new BoundedTextLineReader(input, maximumLineLength: 8);

        Assert.AreEqual("12345678", await reader.ReadLineAsync(CancellationToken.None));
        Assert.IsNull(await reader.ReadLineAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task BoundedReaderRejectsANewlineLessOversizedFrame()
    {
        using var input = new StringReader(new string('x', 65));
        var reader = new BoundedTextLineReader(input, maximumLineLength: 64);

        await Assert.ThrowsExceptionAsync<LineLengthLimitExceededException>(
            async () => await reader.ReadLineAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task BoundedReaderRejectsAnOversizedTerminatedFrame()
    {
        using var input = new StringReader($"{new string('x', 65)}\nnext\n");
        var reader = new BoundedTextLineReader(input, maximumLineLength: 64);

        await Assert.ThrowsExceptionAsync<LineLengthLimitExceededException>(
            async () => await reader.ReadLineAsync(CancellationToken.None));
    }

    [TestMethod]
    public void ExplicitAbsoluteCanonicalCandidateAllowsMutation()
    {
        using var temporaryExecutable = TemporaryCodexExecutable.Create();

        var resolution = CodexExecutableLocator.Resolve(temporaryExecutable.Path);

        Assert.IsTrue(Path.IsPathFullyQualified(resolution.ExecutablePath));
        Assert.AreEqual(
            CodexExecutableDiscoverySource.ExplicitConfiguration,
            resolution.DiscoverySource);
        Assert.AreEqual(
            CodexExecutableTrust.TrustedExplicitConfiguration,
            resolution.Trust);
        Assert.IsTrue(resolution.AllowsMutation);
    }

    [TestMethod]
    public void PathDiscoveredArbitraryCandidateIsReadOnly()
    {
        using var temporaryExecutable = TemporaryCodexExecutable.Create();

        var resolution = CodexExecutableLocator.ResolveExistingCandidate(
            temporaryExecutable.Path,
            CodexExecutableDiscoverySource.PathEnvironment,
            wasExplicitAbsolutePath: false);

        Assert.AreEqual(
            CodexExecutableTrust.ReadOnlyPathDiscovery,
            resolution.Trust);
        Assert.AreEqual(
            CodexExecutableDiscoverySource.PathEnvironment,
            resolution.DiscoverySource);
        Assert.IsFalse(resolution.AllowsMutation);
    }

    [TestMethod]
    public void RelativeConfiguredCandidateIsReadOnlyAfterResolution()
    {
        using var temporaryExecutable = TemporaryCodexExecutable.Create();

        var resolution = CodexExecutableLocator.ResolveExistingCandidate(
            temporaryExecutable.Path,
            CodexExecutableDiscoverySource.ExplicitConfiguration,
            wasExplicitAbsolutePath: false);

        Assert.AreEqual(
            CodexExecutableTrust.ReadOnlyUnverifiedPath,
            resolution.Trust);
        Assert.IsFalse(resolution.AllowsMutation);
    }

    [TestMethod]
    public void RecognizedCandidateKeepsTheStableVisiblePath()
    {
        using var temporaryExecutable = TemporaryCodexExecutable.Create();

        var resolution = CodexExecutableLocator.ResolveExistingCandidate(
            temporaryExecutable.Path,
            CodexExecutableDiscoverySource.RecognizedInstallation,
            wasExplicitAbsolutePath: false);

        Assert.AreEqual(
            System.IO.Path.GetFullPath(temporaryExecutable.Path),
            resolution.ExecutablePath);
        Assert.AreEqual(
            CodexExecutableTrust.ReadOnlyUnverifiedPath,
            resolution.Trust);
        Assert.IsFalse(resolution.AllowsMutation);
    }

    [TestMethod]
    public void RecognizedPathsUseSafeFallbackRootsAndDeduplicate()
    {
        var firstRoot = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "CodexAutoReset-local-app-data");
        var secondRoot = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "CodexAutoReset-fallback-local-app-data");

        var paths = CodexExecutableLocator.BuildRecognizedInstallationPaths(
            firstRoot,
            firstRoot.ToUpperInvariant(),
            null,
            "relative-path",
            secondRoot);

        Assert.AreEqual(2, paths.Count);
        Assert.AreEqual(
            System.IO.Path.Combine(
                System.IO.Path.GetFullPath(firstRoot),
                "Programs",
                "OpenAI",
                "Codex",
                "bin",
                "codex.exe"),
            paths[0]);
        Assert.AreEqual(
            System.IO.Path.Combine(
                System.IO.Path.GetFullPath(secondRoot),
                "Programs",
                "OpenAI",
                "Codex",
                "bin",
                "codex.exe"),
            paths[1]);
    }

    [TestMethod]
    public void StandalonePackageDiscoveryUsesValidatedPhysicalReleaseTarget()
    {
        using var directory = TemporaryDirectory.Create();
        var profileRoot = System.IO.Path.Combine(directory.Path, "profile");
        var releaseRoot = System.IO.Path.Combine(
            profileRoot,
            ".codex",
            "packages",
            "standalone",
            "releases",
            "0.144.5-x86_64-pc-windows-msvc");
        var executablePath = System.IO.Path.Combine(
            releaseRoot,
            "bin",
            "codex.exe");
        System.IO.Directory.CreateDirectory(
            System.IO.Path.GetDirectoryName(executablePath)!);
        File.WriteAllBytes(executablePath, [0]);

        var paths = CodexExecutableLocator.BuildStandalonePackageInstallationPaths(
            _ => releaseRoot,
            profileRoot,
            profileRoot.ToUpperInvariant());

        Assert.AreEqual(1, paths.Count);
        Assert.AreEqual(executablePath, paths[0]);
    }

    [TestMethod]
    public void StandalonePackageDiscoveryRejectsTargetOutsideReleaseRoot()
    {
        using var directory = TemporaryDirectory.Create();
        var profileRoot = System.IO.Path.Combine(directory.Path, "profile");
        var outsideRoot = System.IO.Path.Combine(directory.Path, "outside-release");
        var executablePath = System.IO.Path.Combine(
            outsideRoot,
            "bin",
            "codex.exe");
        System.IO.Directory.CreateDirectory(
            System.IO.Path.GetDirectoryName(executablePath)!);
        File.WriteAllBytes(executablePath, [0]);

        var paths = CodexExecutableLocator.BuildStandalonePackageInstallationPaths(
            _ => outsideRoot,
            profileRoot);

        Assert.AreEqual(0, paths.Count);
    }

    [TestMethod]
    public void ReparseFreeValidationRejectsNestedReparsePoint()
    {
        var root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "CodexAutoReset-profile");
        var executablePath = System.IO.Path.Combine(
            root,
            ".codex",
            "packages",
            "standalone",
            "releases",
            "0.144.5-x86_64-pc-windows-msvc",
            "bin",
            "codex.exe");

        var safe = CodexExecutableLocator.IsReparseFreePath(
            root,
            executablePath,
            requireLeafFile: true,
            path => path.EndsWith(
                    $"{System.IO.Path.DirectorySeparatorChar}bin",
                    StringComparison.OrdinalIgnoreCase)
                ? FileAttributes.Directory | FileAttributes.ReparsePoint
                : string.Equals(
                    path,
                    executablePath,
                    StringComparison.OrdinalIgnoreCase)
                    ? FileAttributes.Archive
                    : FileAttributes.Directory);

        Assert.IsFalse(safe);
    }

    [TestMethod]
    public void FilePickerUsesAccessibleCanonicalExecutable()
    {
        using var executable = TemporaryCodexExecutable.Create();

        var suggestion = CodexExecutableLocator.TryGetFilePickerExecutablePath(
            executable.Path);

        Assert.IsNotNull(suggestion);
        Assert.AreEqual(
            System.IO.Path.GetFullPath(executable.Path),
            System.IO.Path.GetFullPath(suggestion));
    }

    [TestMethod]
    public async Task LegacyStringClientCannotPerformMutation()
    {
        using var temporaryExecutable = TemporaryCodexExecutable.Create();
        await using var client = new CodexAppServerClient(
            temporaryExecutable.Path,
            temporaryExecutable.Directory);

        var exception = await Assert.ThrowsExceptionAsync<AppServerException>(
            () => client.ConsumeResetCreditAsync(
                new ConsumeResetCreditRequest("attempt", "opaque-credit"),
                CancellationToken.None));

        Assert.AreEqual(
            AppServerFailureCategory.UntrustedExecutableForMutation,
            exception.Category);
        Assert.AreEqual(AppServerOperation.Mutation, exception.Operation);
    }

    [TestMethod]
    public async Task ReadFailuresPreserveTheReadOperation()
    {
        using var temporaryExecutable = TemporaryCodexExecutable.Create();
        await using var client = new CodexAppServerClient(
            temporaryExecutable.Path,
            temporaryExecutable.Directory,
            requestTimeout: TimeSpan.FromSeconds(2));

        var exception = await Assert.ThrowsExceptionAsync<AppServerException>(
            () => client.ReadAsync(CancellationToken.None));

        Assert.AreEqual(
            AppServerFailureCategory.StartFailed,
            exception.Category);
        Assert.AreEqual(AppServerOperation.Read, exception.Operation);
    }

    [TestMethod]
    public void ClientVersionComesFromAssemblyProductMetadata()
    {
        var expected = typeof(CodexAppServerClient).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        Assert.IsFalse(string.IsNullOrWhiteSpace(expected));
        Assert.AreEqual(expected, CodexAppServerClient.ClientVersion);
    }

    private sealed class TemporaryCodexExecutable : IDisposable
    {
        private TemporaryCodexExecutable(string directory, string path)
        {
            Directory = directory;
            Path = path;
        }

        public string Directory { get; }

        public string Path { get; }

        public static TemporaryCodexExecutable Create()
        {
            var directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"CodexAutoReset-test-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, "codex.exe");
            File.WriteAllBytes(path, [0]);
            return new TemporaryCodexExecutable(directory, path);
        }

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
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
