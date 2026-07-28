using System.Text;
using CodexAutoReset.AppServer;
using CodexAutoReset.Core;
using CodexAutoReset.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexAutoReset.Runtime.Tests;

[TestClass]
public sealed class CompatibilityGuardCycleExecutorTests
{
    private static readonly DateTimeOffset InitialNow =
        new(2026, 7, 28, 4, 0, 0, TimeSpan.Zero);

    private string root = null!;
    private RuntimePaths paths = null!;
    private MutableTimeProvider time = null!;

    [TestInitialize]
    public void Initialize()
    {
        root = Path.Combine(
            Path.GetTempPath(),
            $"CodexAutoReset-compat-cycle-{Guid.NewGuid():N}");
        paths = RuntimePaths.ForTesting(root);
        time = new MutableTimeProvider(InitialNow);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception exception) when (exception is
            DirectoryNotFoundException
                or IOException
                or UnauthorizedAccessException)
        {
        }
    }

    [TestMethod]
    public async Task SemanticMismatchRequiresSameSignalAfterTenSeconds()
    {
        var factory = new CompatibilityClientFactory(
            CreateMissingWeeklySnapshot());
        var executor = CreateExecutor(factory);
        var settings = GuardSettings.Default with
        {
            AutomationEnabled = true,
        };

        var first = await executor.ExecuteAsync(settings, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(9));
        var early = await executor.ExecuteAsync(settings, CancellationToken.None);

        Assert.AreEqual("protocol_verification_pending", first.ActionCode);
        Assert.AreEqual("protocol_verification_pending", early.ActionCode);
        Assert.IsFalse(File.Exists(paths.LiveSafetyBlockFile));

        time.Advance(TimeSpan.FromSeconds(1));
        var confirmed = await executor.ExecuteAsync(
            settings,
            CancellationToken.None);

        Assert.AreEqual("protocol_read_unsupported", confirmed.ActionCode);
        Assert.AreEqual(CycleActionKind.Blocked, confirmed.ActionKind);
        Assert.IsFalse(File.Exists(paths.LiveSafetyBlockFile));
        Assert.AreEqual(0, factory.ConsumeCount);
    }

    [TestMethod]
    public async Task OrdinaryReadFailureBreaksProtocolFailureSequence()
    {
        var factory = new CompatibilityClientFactory(
            CreateCompatibleSnapshot(weeklyUsedPercent: 50),
            new AppServerException(
                AppServerFailureCategory.InvalidResponse,
                operation: AppServerOperation.Read),
            new AppServerException(
                AppServerFailureCategory.Timeout,
                operation: AppServerOperation.Read),
            new AppServerException(
                AppServerFailureCategory.InvalidResponse,
                operation: AppServerOperation.Read),
            new AppServerException(
                AppServerFailureCategory.InvalidResponse,
                operation: AppServerOperation.Read));
        var executor = CreateExecutor(factory);
        var settings = GuardSettings.Default with
        {
            AutomationEnabled = true,
        };

        await Assert.ThrowsExceptionAsync<AppServerException>(
            () => executor.ExecuteAsync(settings, CancellationToken.None));
        time.Advance(TimeSpan.FromSeconds(11));
        await Assert.ThrowsExceptionAsync<AppServerException>(
            () => executor.ExecuteAsync(settings, CancellationToken.None));

        time.Advance(TimeSpan.FromSeconds(9));
        await Assert.ThrowsExceptionAsync<AppServerException>(
            () => executor.ExecuteAsync(settings, CancellationToken.None));
        Assert.IsFalse(File.Exists(paths.LiveSafetyBlockFile));

        time.Advance(TimeSpan.FromSeconds(10));
        await Assert.ThrowsExceptionAsync<AppServerException>(
            () => executor.ExecuteAsync(settings, CancellationToken.None));
        Assert.IsFalse(File.Exists(paths.LiveSafetyBlockFile));
    }

    [TestMethod]
    public async Task ConfiguredExecutableChangeStartsANewVerificationWindow()
    {
        Directory.CreateDirectory(root);
        var firstExecutable = Path.Combine(root, "first", "codex.exe");
        var secondExecutable = Path.Combine(root, "second", "codex.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(firstExecutable)!);
        Directory.CreateDirectory(Path.GetDirectoryName(secondExecutable)!);
        await File.WriteAllBytesAsync(firstExecutable, [0]);
        await File.WriteAllBytesAsync(secondExecutable, [0]);
        var factory = new CompatibilityClientFactory(
            CreateCompatibleSnapshot(weeklyUsedPercent: 50),
            new AppServerException(
                AppServerFailureCategory.InvalidResponse,
                operation: AppServerOperation.Read),
            new AppServerException(
                AppServerFailureCategory.InvalidResponse,
                operation: AppServerOperation.Read),
            new AppServerException(
                AppServerFailureCategory.InvalidResponse,
                operation: AppServerOperation.Read));
        var executor = CreateExecutor(factory);
        var firstSettings = GuardSettings.Default with
        {
            AutomationEnabled = true,
            CodexExecutablePath = firstExecutable,
        };
        var secondSettings = firstSettings with
        {
            CodexExecutablePath = secondExecutable,
        };

        await Assert.ThrowsExceptionAsync<AppServerException>(
            () => executor.ExecuteAsync(firstSettings, CancellationToken.None));
        time.Advance(TimeSpan.FromSeconds(10));
        await Assert.ThrowsExceptionAsync<AppServerException>(
            () => executor.ExecuteAsync(secondSettings, CancellationToken.None));
        Assert.IsFalse(File.Exists(paths.LiveSafetyBlockFile));

        time.Advance(TimeSpan.FromSeconds(10));
        await Assert.ThrowsExceptionAsync<AppServerException>(
            () => executor.ExecuteAsync(secondSettings, CancellationToken.None));
        Assert.IsFalse(File.Exists(paths.LiveSafetyBlockFile));
    }

    [TestMethod]
    public async Task AuditedMutationSchemaMismatchIsImmediateEvenWithReadAnomaly()
    {
        var factory = new CompatibilityClientFactory(
            CreateMissingWeeklySnapshot(consumeSchemaCompatible: false));
        var executor = CreateExecutor(factory);
        var settings = GuardSettings.Default with
        {
            AutomationEnabled = true,
        };

        var result = await executor.ExecuteAsync(settings, CancellationToken.None);

        Assert.AreEqual("mutation_schema_unverified", result.ActionCode);
        Assert.AreEqual(CycleActionKind.Blocked, result.ActionKind);
        Assert.IsFalse(File.Exists(paths.LiveSafetyBlockFile));
        Assert.AreEqual(0, factory.ConsumeCount);
    }

    [TestMethod]
    public async Task PreflightSchemaMismatchRecoversOnNextCompatibleRead()
    {
        var factory = new CompatibilityClientFactory(
            CreateCompatibleSnapshot(
                weeklyUsedPercent: 50,
                consumeSchemaCompatible: false));
        var executor = CreateExecutor(factory);
        var settings = GuardSettings.Default with
        {
            AutomationEnabled = true,
        };

        var warning = await executor.ExecuteAsync(
            settings,
            CancellationToken.None);
        factory.Snapshot = CreateCompatibleSnapshot(weeklyUsedPercent: 50);
        var recovered = await executor.ExecuteAsync(
            settings,
            CancellationToken.None);

        Assert.AreEqual("mutation_schema_unverified", warning.ActionCode);
        Assert.AreEqual("no_action", recovered.ActionCode);
        Assert.IsFalse(File.Exists(paths.LiveSafetyBlockFile));
        Assert.AreEqual(0, factory.ConsumeCount);
    }

    [TestMethod]
    public async Task MutationResponseMismatchReturnsReadableUsageAndLatchesImmediately()
    {
        var factory = new CompatibilityClientFactory(
            CreateCompatibleSnapshot(weeklyUsedPercent: 95))
        {
            ConsumeFailure = new AppServerException(
                AppServerFailureCategory.InvalidResponse,
                operation: AppServerOperation.Mutation),
        };
        var executor = CreateExecutor(factory);
        var settings = GuardSettings.Default with
        {
            AutomationEnabled = true,
        };

        var result = await executor.ExecuteAsync(settings, CancellationToken.None);

        Assert.AreEqual("mutation_schema_unverified", result.ActionCode);
        Assert.AreEqual(CycleActionKind.Blocked, result.ActionKind);
        Assert.AreEqual(5, result.Evaluation.Weekly?.RemainingPercent);
        Assert.AreEqual(1, factory.ConsumeCount);
        Assert.IsTrue(File.Exists(paths.LiveSafetyBlockFile));
    }

    [TestMethod]
    public async Task ExistingProtocolMarkerCannotDowngradeToVerificationPending()
    {
        var settings = GuardSettings.Default with
        {
            AutomationEnabled = true,
        };
        var currentRevision = string.Concat(
            typeof(GuardCycleExecutor).Assembly
                .GetName()
                .Version
                ?.ToString(3)
                ?? "0.0.0",
            "|",
            AppServerProtocolParser.AuditedConsumeSchemaVersion);
        new LiveResetSafetyLatch(
            paths.LiveSafetyBlockFile,
            currentRevision).BlockProtocolMismatch();

        var restarted = CreateExecutor(new CompatibilityClientFactory(
            CreateMissingWeeklySnapshot()));
        var afterRestart = await restarted.ExecuteAsync(
            settings,
            CancellationToken.None);

        Assert.AreEqual("protocol_read_unsupported", afterRestart.ActionCode);
        Assert.AreEqual(CycleActionKind.Blocked, afterRestart.ActionKind);
    }

    [TestMethod]
    public async Task LegacyVersionTwoMarkerIsNotClearedWithoutKnownProvenance()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(
            paths.LiveSafetyBlockFile)!);
        await File.WriteAllTextAsync(
            paths.LiveSafetyBlockFile,
            "{\"schemaVersion\":2,\"reason\":\"protocolMismatch\","
                + "\"compatibilityRevision\":\"0.2.6|0.144.5\"}");
        var executor = CreateExecutor(new CompatibilityClientFactory(
            CreateCompatibleSnapshot(weeklyUsedPercent: 50)));
        var settings = GuardSettings.Default with
        {
            AutomationEnabled = true,
        };

        var result = await executor.ExecuteAsync(
            settings,
            CancellationToken.None);

        Assert.AreEqual("live_protocol_blocked", result.ActionCode);
        Assert.AreEqual(CycleActionKind.Blocked, result.ActionKind);
        Assert.IsTrue(File.Exists(paths.LiveSafetyBlockFile));
    }

    private GuardCycleExecutor CreateExecutor(
        CompatibilityClientFactory factory) => new(
        paths,
        factory,
        new TestSecretProtector(),
        AppServerLiveResetFailureClassifier.Instance,
        time);

    private AccountRateLimits CreateMissingWeeklySnapshot(
        bool consumeSchemaCompatible = true)
    {
        var codex = new RateLimitSnapshot("codex", "Codex", null, null);
        return new AccountRateLimits(
            codex,
            new Dictionary<string, RateLimitSnapshot>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["codex"] = codex,
            },
            new ResetCreditSummary(0, []),
            time.GetUtcNow(),
            consumeSchemaCompatible);
    }

    private AccountRateLimits CreateCompatibleSnapshot(
        double weeklyUsedPercent,
        bool consumeSchemaCompatible = true)
    {
        var observedAt = time.GetUtcNow();
        var observedAtUnix = observedAt.ToUnixTimeSeconds();
        var codex = new RateLimitSnapshot(
            "codex",
            "Codex",
            null,
            new RateLimitWindow(
                weeklyUsedPercent,
                10_080,
                observedAt.AddDays(6).ToUnixTimeSeconds()));
        return new AccountRateLimits(
            codex,
            new Dictionary<string, RateLimitSnapshot>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["codex"] = codex,
            },
            new ResetCreditSummary(
                1,
                [
                    new ResetCredit(
                        "private-credit-id",
                        "codexRateLimits",
                        "available",
                        observedAtUnix - 60,
                        observedAtUnix + 86_400,
                        null,
                        null),
                ]),
            observedAt,
            consumeSchemaCompatible);
    }

    private sealed class CompatibilityClientFactory : IRateLimitClientFactory
    {
        private readonly Queue<Exception> readFailures;

        public CompatibilityClientFactory(
            AccountRateLimits snapshot,
            params Exception[] readFailures)
        {
            Snapshot = snapshot;
            this.readFailures = new Queue<Exception>(readFailures);
        }

        public AccountRateLimits Snapshot { get; set; }

        public Exception? ConsumeFailure { get; init; }

        public int ConsumeCount { get; private set; }

        public IAccountRateLimitClient Create(GuardSettings settings) =>
            new Client(this);

        private sealed class Client : IAccountRateLimitClient
        {
            private readonly CompatibilityClientFactory owner;

            public Client(CompatibilityClientFactory owner)
            {
                this.owner = owner;
            }

            public Task<AccountRateLimits> ReadAsync(
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (owner.readFailures.TryDequeue(out var exception))
                {
                    return Task.FromException<AccountRateLimits>(exception);
                }

                return Task.FromResult(owner.Snapshot with
                {
                    ObservedAt = DateTimeOffset.UtcNow,
                });
            }

            public Task<ConsumeResetCreditResult> ConsumeResetCreditAsync(
                ConsumeResetCreditRequest request,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner.ConsumeCount++;
                return owner.ConsumeFailure is { } exception
                    ? Task.FromException<ConsumeResetCreditResult>(exception)
                    : Task.FromResult(new ConsumeResetCreditResult(
                        ConsumeResetCreditOutcome.Reset));
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class TestSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"protected:{plaintext}"));

        public string Unprotect(string protectedValue)
        {
            var value = Encoding.UTF8.GetString(
                Convert.FromBase64String(protectedValue));
            return value["protected:".Length..];
        }
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset now;

        public MutableTimeProvider(DateTimeOffset now)
        {
            this.now = now;
        }

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now = now.Add(duration);
    }
}
