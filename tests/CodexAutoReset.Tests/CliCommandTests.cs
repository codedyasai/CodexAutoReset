using System.Globalization;
using System.Text;
using CodexAutoReset.AppServer;
using CodexAutoReset.Cli;
using CodexAutoReset.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexAutoReset.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CliCommandTests
{
    [TestMethod]
    public async Task EnableAutomationPreservesExistingSettingsAndOnlyPrintsState()
    {
        using var directory = TemporaryDirectory.Create();
        var executablePath = Path.Combine(directory.Path, "codex.exe");
        await File.WriteAllTextAsync(executablePath, "not an executable");
        var store = new JsonSettingsStore(Path.Combine(directory.Path, "settings.json"));
        var original = GuardSettings.Default with
        {
            RemainingThresholdPercent = 42,
            PollIntervalMinutes = 17,
            UiLanguage = UiLanguage.Korean,
            StartWithWindows = true,
            CodexExecutablePath = executablePath,
            AutomationEnabled = false,
        };
        await store.SaveAsync(original, CancellationToken.None);

        var result = await RunCliAsync(directory.Path, "--enable-automation");

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual("automationEnabled=true", result.StandardOutput.Trim());
        Assert.AreEqual(string.Empty, result.StandardError);
        Assert.AreEqual(
            original with { AutomationEnabled = true },
            await store.LoadAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task DisableAutomationPreservesExistingSettingsAndOnlyPrintsState()
    {
        using var directory = TemporaryDirectory.Create();
        var store = new JsonSettingsStore(Path.Combine(directory.Path, "settings.json"));
        var original = GuardSettings.Default with
        {
            RemainingThresholdPercent = 88,
            PollIntervalMinutes = 29,
            AutomationEnabled = true,
        };
        await store.SaveAsync(original, CancellationToken.None);

        var result = await RunCliAsync(directory.Path, "--disable-automation");

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual("automationEnabled=false", result.StandardOutput.Trim());
        Assert.AreEqual(string.Empty, result.StandardError);
        Assert.AreEqual(
            original with { AutomationEnabled = false },
            await store.LoadAsync(CancellationToken.None));
    }

    [DataTestMethod]
    [DynamicData(nameof(InvalidModeCommandCombinations))]
    public void AutomationCommandCombinationFailsClosed(string[] args)
    {
        Assert.ThrowsException<ArgumentException>(() =>
            Program.CommandLineOptions.Parse(args));
    }

    [TestMethod]
    public async Task InvalidAutomationCommandCombinationDoesNotModifySettings()
    {
        using var directory = TemporaryDirectory.Create();
        var store = new JsonSettingsStore(Path.Combine(directory.Path, "settings.json"));
        var original = GuardSettings.Default;
        await store.SaveAsync(original, CancellationToken.None);

        var result = await RunCliAsync(
            directory.Path,
            "--enable-automation",
            "--once");

        Assert.AreEqual(2, result.ExitCode);
        Assert.AreEqual(string.Empty, result.StandardOutput);
        Assert.AreEqual(
            "Invalid command-line arguments. Use --help.",
            result.StandardError.Trim());
        Assert.AreEqual(original, await store.LoadAsync(CancellationToken.None));
    }

    [TestMethod]
    public void DiagnosticsAlwaysDisablesAutomation()
    {
        var liveSettings = GuardSettings.Default with
        {
            AutomationEnabled = true,
        };

        Assert.IsTrue(Program.IsAutomationExecutionAllowed(liveSettings, diagnostics: false));
        Assert.IsFalse(Program.IsAutomationExecutionAllowed(liveSettings, diagnostics: true));
        Assert.IsFalse(Program.IsAutomationExecutionAllowed(
            liveSettings with { AutomationEnabled = false },
            diagnostics: false));
    }

    [TestMethod]
    public void ClientReuseRequiresSamePathDiscoverySourceAndTrust()
    {
        const string path = @"C:\Program Files\Codex\codex.exe";
        var trustedExplicit = new CodexExecutableResolution(
            path,
            CodexExecutableDiscoverySource.ExplicitConfiguration,
            CodexExecutableTrust.TrustedExplicitConfiguration);
        var same = new CodexExecutableResolution(
            path.ToUpperInvariant(),
            CodexExecutableDiscoverySource.ExplicitConfiguration,
            CodexExecutableTrust.TrustedExplicitConfiguration);
        var pathDiscovered = new CodexExecutableResolution(
            path,
            CodexExecutableDiscoverySource.PathEnvironment,
            CodexExecutableTrust.ReadOnlyPathDiscovery);
        var differentSourceSameTrust = new CodexExecutableResolution(
            path,
            CodexExecutableDiscoverySource.RecognizedInstallation,
            CodexExecutableTrust.TrustedExplicitConfiguration);

        Assert.IsTrue(Program.CanReuseClient(trustedExplicit, same));
        Assert.IsFalse(Program.CanReuseClient(trustedExplicit, pathDiscovered));
        Assert.IsFalse(Program.CanReuseClient(
            trustedExplicit,
            differentSourceSameTrust));
        Assert.IsFalse(Program.CanReuseClient(null, trustedExplicit));
    }

    [DataTestMethod]
    [DataRow("InvalidResponse", "ProtocolMismatch")]
    [DataRow("ExecutableNotFound", "Retryable")]
    [DataRow("StartFailed", "Retryable")]
    [DataRow("ProcessExited", "Retryable")]
    [DataRow("Timeout", "Retryable")]
    [DataRow("RemoteError", "Retryable")]
    [DataRow("IoError", "Retryable")]
    [DataRow("OutboundMethodNotAllowed", "Unknown")]
    [DataRow("InvalidOutboundMessage", "Unknown")]
    [DataRow("UntrustedExecutableForMutation", "Unknown")]
    public void AppServerLiveFailureClassificationIsConservative(
        string categoryName,
        string expectedName)
    {
        var category = Enum.Parse<AppServerFailureCategory>(categoryName);
        var expected = Enum.Parse<LiveResetFailureDisposition>(expectedName);

        var actual = AppServerLiveResetFailureClassifier.Instance.Classify(
            new AppServerException(category));

        Assert.AreEqual(expected, actual);
    }

    [DataTestMethod]
    [DataRow("Reset", "reset")]
    [DataRow("NothingToReset", "nothingToReset")]
    [DataRow("NoCredit", "noCredit")]
    [DataRow("AlreadyRedeemed", "alreadyRedeemed")]
    public async Task LiveCyclePrintsAndPersistsExactTerminalOutcome(
        string outcomeName,
        string wireOutcome)
    {
        using var directory = TemporaryDirectory.Create();
        var now = DateTimeOffset.UtcNow;
        var outcome = Enum.Parse<ConsumeResetCreditOutcome>(outcomeName);
        var initial = CreateLiveSnapshot(now, weeklyUsedPercent: 93, creditCount: 1);
        var refreshed = CreateLiveSnapshot(
            now.AddMinutes(1),
            weeklyUsedPercent: 0,
            creditCount: 0);
        var client = new FakeAccountRateLimitClient(outcome, refreshed);
        var store = new JsonLiveAttemptStore(
            Path.Combine(directory.Path, "live-state.json"));
        var engine = new ResetDecisionEngine();
        var coordinator = new LiveResetCoordinator(
            engine,
            store,
            new FakeSecretProtector(),
            client,
            new FakeFailureClassifier());
        var logger = new SafeJsonlLogger(Path.Combine(directory.Path, "Logs"));
        var settings = GuardSettings.Default with
        {
            UiLanguage = UiLanguage.English,
            AutomationEnabled = true,
        };

        var captured = await CaptureConsoleAsync(() =>
            Program.ProcessAutomationSnapshotAsync(
                coordinator,
                engine,
                logger,
                new ConsoleLocalizer(UiLanguage.English),
                settings,
                initial,
                now,
                CancellationToken.None));

        Assert.AreEqual(LiveResetCycleKind.Completed, captured.Result.Kind);
        Assert.AreEqual(outcome, captured.Result.Outcome);
        Assert.IsFalse(captured.Result.RequiresRefresh);
        Assert.AreEqual(1, client.ConsumeRequests.Count);
        Assert.AreEqual(1, client.ReadCount);
        StringAssert.Contains(captured.StandardOutput, $"Reset outcome: {wireOutcome}");
        StringAssert.Contains(
            captured.StandardOutput,
            "The weekly limit is the trigger; the server determines the actual reset scope.");
        StringAssert.Contains(captured.StandardOutput, "Completed a full usage refresh");
        Assert.AreEqual(string.Empty, captured.StandardError);

        var attempt = (await store.ReadAsync(CancellationToken.None)).Single();
        Assert.AreEqual(LiveAttemptPhase.Terminal, attempt.Phase);
        Assert.AreEqual(outcome, attempt.Outcome);
        Assert.IsFalse(attempt.RefreshRequired);
        Assert.IsTrue(Guid.TryParseExact(
            client.ConsumeRequests[0].IdempotencyKey,
            "D",
            out _));
        Assert.AreEqual("test-credit", client.ConsumeRequests[0].CreditId);

        var log = await ReadOnlyLogAsync(directory.Path);
        StringAssert.Contains(log, $"\"outcome\":\"{wireOutcome}\"");
        Assert.IsFalse(log.Contains("test-credit", StringComparison.Ordinal));
        Assert.IsFalse(captured.StandardOutput.Contains(
            "test-credit",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RefreshFailureDoesNotUndoOrHideTerminalOutcome()
    {
        using var directory = TemporaryDirectory.Create();
        var now = DateTimeOffset.UtcNow;
        var client = new FakeAccountRateLimitClient(
            ConsumeResetCreditOutcome.Reset,
            refreshException: new IOException("test-only local failure"));
        var store = new JsonLiveAttemptStore(
            Path.Combine(directory.Path, "live-state.json"));
        var engine = new ResetDecisionEngine();
        var coordinator = new LiveResetCoordinator(
            engine,
            store,
            new FakeSecretProtector(),
            client,
            new FakeFailureClassifier());
        var logger = new SafeJsonlLogger(Path.Combine(directory.Path, "Logs"));
        var settings = GuardSettings.Default with
        {
            UiLanguage = UiLanguage.English,
            AutomationEnabled = true,
        };

        var captured = await CaptureConsoleAsync(() =>
            Program.ProcessAutomationSnapshotAsync(
                coordinator,
                engine,
                logger,
                new ConsoleLocalizer(UiLanguage.English),
                settings,
                CreateLiveSnapshot(now, weeklyUsedPercent: 93, creditCount: 1),
                now,
                CancellationToken.None));

        Assert.AreEqual(LiveResetCycleKind.Completed, captured.Result.Kind);
        Assert.AreEqual(ConsumeResetCreditOutcome.Reset, captured.Result.Outcome);
        Assert.IsTrue(captured.Result.RequiresRefresh);
        Assert.IsNull(captured.Result.RefreshedRateLimits);
        StringAssert.Contains(captured.StandardOutput, "Reset outcome: reset");
        StringAssert.Contains(captured.StandardOutput, "terminal outcome is durable");

        var attempt = (await store.ReadAsync(CancellationToken.None)).Single();
        Assert.AreEqual(LiveAttemptPhase.Terminal, attempt.Phase);
        Assert.AreEqual(ConsumeResetCreditOutcome.Reset, attempt.Outcome);
        Assert.IsTrue(attempt.RefreshRequired);

        var log = await ReadOnlyLogAsync(directory.Path);
        StringAssert.Contains(log, "\"outcome\":\"reset\"");
        StringAssert.Contains(log, "\"reasonCode\":\"refresh_pending\"");
    }

    [TestMethod]
    public async Task TerminalOutcomeAuditIgnoresCallerCancellationAfterDispatch()
    {
        using var directory = TemporaryDirectory.Create();
        using var cancellationSource = new CancellationTokenSource();
        var now = DateTimeOffset.UtcNow;
        var client = new FakeAccountRateLimitClient(
            ConsumeResetCreditOutcome.Reset,
            CreateLiveSnapshot(now.AddMinutes(1), weeklyUsedPercent: 0, creditCount: 0),
            onConsume: cancellationSource.Cancel);
        var engine = new ResetDecisionEngine();
        var coordinator = new LiveResetCoordinator(
            engine,
            new JsonLiveAttemptStore(Path.Combine(directory.Path, "live-state.json")),
            new FakeSecretProtector(),
            client,
            new FakeFailureClassifier());

        var captured = await CaptureConsoleAsync(() =>
            Program.ProcessAutomationSnapshotAsync(
                coordinator,
                engine,
                new SafeJsonlLogger(Path.Combine(directory.Path, "Logs")),
                new ConsoleLocalizer(UiLanguage.English),
                GuardSettings.Default with
                {
                    AutomationEnabled = true,
                    UiLanguage = UiLanguage.English,
                },
                CreateLiveSnapshot(now, weeklyUsedPercent: 93, creditCount: 1),
                now,
                cancellationSource.Token));

        Assert.AreEqual(ConsumeResetCreditOutcome.Reset, captured.Result.Outcome);
        var log = await ReadOnlyLogAsync(directory.Path);
        StringAssert.Contains(log, "\"outcome\":\"reset\"");
    }

    [TestMethod]
    public async Task WeeklyWindowMustMeetThresholdBeforeAutomaticConsume()
    {
        using var directory = TemporaryDirectory.Create();
        var now = DateTimeOffset.UtcNow;
        var snapshot = CreateLiveSnapshot(
            now,
            weeklyUsedPercent: 80,
            creditCount: 1);
        var client = new FakeAccountRateLimitClient(
            ConsumeResetCreditOutcome.Reset,
            CreateLiveSnapshot(now.AddMinutes(1), 0, 0));
        var engine = new ResetDecisionEngine();
        var coordinator = new LiveResetCoordinator(
            engine,
            new JsonLiveAttemptStore(Path.Combine(directory.Path, "live-state.json")),
            new FakeSecretProtector(),
            client,
            new FakeFailureClassifier());
        var settings = GuardSettings.Default with
        {
            AutomationEnabled = true,
            UiLanguage = UiLanguage.English,
        };

        var captured = await CaptureConsoleAsync(() =>
            Program.ProcessAutomationSnapshotAsync(
                coordinator,
                engine,
                new SafeJsonlLogger(Path.Combine(directory.Path, "Logs")),
                new ConsoleLocalizer(UiLanguage.English),
                settings,
                snapshot,
                now,
                CancellationToken.None));

        Assert.AreEqual(LiveResetCycleKind.NoAction, captured.Result.Kind);
        Assert.AreEqual(0, client.ConsumeRequests.Count);
        Assert.AreEqual(0, client.ReadCount);
        StringAssert.Contains(captured.StandardOutput, "The trigger is not currently met");
    }

    [TestMethod]
    public async Task PendingAttemptIsReconciledWithSameIdempotencyKeyBeforeNewWork()
    {
        using var directory = TemporaryDirectory.Create();
        var now = DateTimeOffset.UtcNow;
        var snapshot = CreateLiveSnapshot(now, weeklyUsedPercent: 93, creditCount: 1);
        var store = new JsonLiveAttemptStore(
            Path.Combine(directory.Path, "live-state.json"));
        var protector = new FakeSecretProtector();
        var engine = new ResetDecisionEngine();
        var firstClient = new FakeAccountRateLimitClient(
            ConsumeResetCreditOutcome.Reset,
            consumeException: new IOException("ambiguous test dispatch"));
        var firstCoordinator = new LiveResetCoordinator(
            engine,
            store,
            protector,
            firstClient,
            new FakeFailureClassifier());
        var logger = new SafeJsonlLogger(Path.Combine(directory.Path, "Logs"));
        var settings = GuardSettings.Default with
        {
            AutomationEnabled = true,
            UiLanguage = UiLanguage.English,
        };

        await Assert.ThrowsExceptionAsync<IOException>(() =>
            Program.ProcessAutomationSnapshotAsync(
                firstCoordinator,
                engine,
                logger,
                new ConsoleLocalizer(UiLanguage.English),
                settings,
                snapshot,
                now,
                CancellationToken.None));

        var pending = (await store.ReadAsync(CancellationToken.None)).Single();
        Assert.AreEqual(LiveAttemptPhase.Pending, pending.Phase);
        Assert.AreEqual(1, pending.DispatchCount);
        var originalKey = firstClient.ConsumeRequests.Single().IdempotencyKey;

        var secondClient = new FakeAccountRateLimitClient(
            ConsumeResetCreditOutcome.AlreadyRedeemed,
            CreateLiveSnapshot(now.AddMinutes(1), weeklyUsedPercent: 0, creditCount: 0));
        var secondCoordinator = new LiveResetCoordinator(
            engine,
            store,
            protector,
            secondClient,
            new FakeFailureClassifier());

        var reconciled = await CaptureConsoleAsync(() =>
            Program.ProcessAutomationSnapshotAsync(
                secondCoordinator,
                engine,
                logger,
                new ConsoleLocalizer(UiLanguage.English),
                settings,
                snapshot,
                now.AddSeconds(1),
                CancellationToken.None));

        Assert.AreEqual(LiveResetCycleKind.Completed, reconciled.Result.Kind);
        Assert.AreEqual(
            ConsumeResetCreditOutcome.AlreadyRedeemed,
            reconciled.Result.Outcome);
        Assert.AreEqual(
            originalKey,
            secondClient.ConsumeRequests.Single().IdempotencyKey);
        var terminal = (await store.ReadAsync(CancellationToken.None)).Single();
        Assert.AreEqual(LiveAttemptPhase.Terminal, terminal.Phase);
        Assert.AreEqual(2, terminal.DispatchCount);

        var duplicateClient = new FakeAccountRateLimitClient(
            ConsumeResetCreditOutcome.Reset,
            CreateLiveSnapshot(now.AddMinutes(2), weeklyUsedPercent: 0, creditCount: 0));
        var duplicateCoordinator = new LiveResetCoordinator(
            engine,
            store,
            protector,
            duplicateClient,
            new FakeFailureClassifier());
        var duplicate = await CaptureConsoleAsync(() =>
            Program.ProcessAutomationSnapshotAsync(
                duplicateCoordinator,
                engine,
                logger,
                new ConsoleLocalizer(UiLanguage.English),
                settings,
                snapshot,
                now.AddSeconds(2),
                CancellationToken.None));

        Assert.AreEqual(LiveResetCycleKind.DuplicateSuppressed, duplicate.Result.Kind);
        Assert.AreEqual(0, duplicateClient.ConsumeRequests.Count);
        StringAssert.Contains(duplicate.StandardOutput, "Reset outcome: alreadyRedeemed");
    }

    public static IEnumerable<object[]> InvalidModeCommandCombinations
    {
        get
        {
            yield return [new[] { "--enable-automation", "--disable-automation" }];
            yield return [new[] { "--enable-automation", "--enable-automation" }];
            yield return [new[] { "--disable-automation", "--disable-automation" }];
            yield return [new[] { "--enable-automation", "--once" }];
            yield return [new[] { "--disable-automation", "--diagnostics" }];
            yield return [new[] { "--enable-automation", "--help" }];
        }
    }

    private static async Task<CliResult> RunCliAsync(
        string appDataRoot,
        params string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);

        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exitCode = await Program.RunAsync(args, appDataRoot);
            return new CliResult(exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static async Task<CapturedResult<T>> CaptureConsoleAsync<T>(
        Func<Task<T>> action)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);

        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var result = await action();
            return new CapturedResult<T>(
                result,
                output.ToString(),
                error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static AccountRateLimits CreateLiveSnapshot(
        DateTimeOffset observedAt,
        int weeklyUsedPercent,
        long creditCount)
    {
        var snapshot = new RateLimitSnapshot(
            "codex",
            null,
            new RateLimitWindow(
                20,
                300,
                observedAt.AddHours(4).ToUnixTimeSeconds()),
            new RateLimitWindow(
                weeklyUsedPercent,
                10_080,
                observedAt.AddDays(5).ToUnixTimeSeconds()));
        IReadOnlyList<ResetCredit> credits = creditCount > 0
            ?
            [
                new ResetCredit(
                    "test-credit",
                    "codexRateLimits",
                    "available",
                    observedAt.AddDays(-1).ToUnixTimeSeconds(),
                    observedAt.AddDays(2).ToUnixTimeSeconds(),
                    null,
                    null),
            ]
            : [];

        return new AccountRateLimits(
            snapshot,
            new Dictionary<string, RateLimitSnapshot>
            {
                ["codex"] = snapshot,
            },
            new ResetCreditSummary(creditCount, credits),
            observedAt);
    }

    private static async Task<string> ReadOnlyLogAsync(string root)
    {
        var file = Directory.GetFiles(
            Path.Combine(root, "Logs"),
            "*.jsonl").Single();
        return await File.ReadAllTextAsync(file);
    }

    private sealed class FakeAccountRateLimitClient : IAccountRateLimitClient
    {
        private readonly ConsumeResetCreditOutcome outcome;
        private readonly AccountRateLimits? refreshed;
        private readonly Exception? refreshException;
        private readonly Exception? consumeException;
        private readonly Action? onConsume;

        public FakeAccountRateLimitClient(
            ConsumeResetCreditOutcome outcome,
            AccountRateLimits? refreshed = null,
            Exception? refreshException = null,
            Exception? consumeException = null,
            Action? onConsume = null)
        {
            this.outcome = outcome;
            this.refreshed = refreshed;
            this.refreshException = refreshException;
            this.consumeException = consumeException;
            this.onConsume = onConsume;
        }

        public List<ConsumeResetCreditRequest> ConsumeRequests { get; } = [];

        public int ReadCount { get; private set; }

        public Task<AccountRateLimits> ReadAsync(CancellationToken cancellationToken)
        {
            ReadCount++;
            if (refreshException is not null)
            {
                return Task.FromException<AccountRateLimits>(refreshException);
            }

            return Task.FromResult(
                refreshed ?? throw new InvalidOperationException("Missing fake refresh."));
        }

        public Task<ConsumeResetCreditResult> ConsumeResetCreditAsync(
            ConsumeResetCreditRequest request,
            CancellationToken cancellationToken)
        {
            ConsumeRequests.Add(request);
            onConsume?.Invoke();
            if (consumeException is not null)
            {
                return Task.FromException<ConsumeResetCreditResult>(consumeException);
            }

            return Task.FromResult(new ConsumeResetCreditResult(outcome));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => Convert.ToBase64String(
            Encoding.UTF8.GetBytes(plaintext));

        public string Unprotect(string protectedValue) => Encoding.UTF8.GetString(
            Convert.FromBase64String(protectedValue));
    }

    private sealed class FakeFailureClassifier : ILiveResetFailureClassifier
    {
        public LiveResetFailureDisposition Classify(Exception exception) =>
            LiveResetFailureDisposition.Retryable;
    }

    private sealed record CliResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    private sealed record CapturedResult<T>(
        T Result,
        string StandardOutput,
        string StandardError);
}
