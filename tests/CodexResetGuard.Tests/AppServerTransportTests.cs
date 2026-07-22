using System.Reflection;
using CodexResetGuard.AppServer;
using CodexResetGuard.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexResetGuard.Tests;

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
                $"CodexResetGuard-test-{Guid.NewGuid():N}");
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
