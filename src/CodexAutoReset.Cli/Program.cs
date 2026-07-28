using CodexAutoReset.AppServer;
using CodexAutoReset.Core;
using CodexAutoReset.Runtime;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("CodexAutoReset.Tests")]

namespace CodexAutoReset.Cli;

internal static class Program
{
    private const string InstanceLockFileName = "instance.lock";
    internal static string CompatibilityRevision { get; } = string.Concat(
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
        "|",
        AppServerProtocolParser.AuditedConsumeSchemaVersion);

    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await RunAsync(args).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception)
        {
            // Never let a runtime stack trace disclose local paths or remote text.
            Console.Error.WriteLine("Stopped safely: unexpected_local_failure");
            return 3;
        }
    }

    internal static async Task<int> RunAsync(
        string[] args,
        string? appDataRootOverride = null)
    {
        CommandLineOptions options;
        try
        {
            options = CommandLineOptions.Parse(args);
        }
        catch (ArgumentException)
        {
            Console.Error.WriteLine("Invalid command-line arguments. Use --help.");
            return 2;
        }
        if (options.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("CodexAutoReset currently supports Windows only.");
            return 2;
        }

        var appDataRoot = appDataRootOverride ?? GetAppDataRoot();
        using var singleInstance = TryAcquireInstanceLock(appDataRoot);
        if (singleInstance is null)
        {
            Console.Error.WriteLine("CodexAutoReset is already running.");
            return 4;
        }

        using var cancellationSource = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationSource.Cancel();
        };

        var settingsPath = Path.Combine(appDataRoot, "settings.json");
        var settingsStore = new JsonSettingsStore(settingsPath);
        if (options.AutomationEnabledCommand is { } automationEnabled)
        {
            return await SaveAutomationEnabledAsync(
                settingsStore,
                automationEnabled,
                cancellationSource.Token).ConfigureAwait(false);
        }

        var settingsExisted = File.Exists(settingsPath);

        GuardSettings initialSettings;
        try
        {
            initialSettings = await settingsStore.LoadOrCreateAsync(
                cancellationSource.Token).ConfigureAwait(false);
        }
        catch (SettingsException exception)
        {
            Console.Error.WriteLine($"Stopped safely: {exception.ReasonCode}");
            return 2;
        }

        var localizer = new ConsoleLocalizer(initialSettings.UiLanguage);
        Console.WriteLine(localizer.Header(
            initialSettings.AutomationEnabled && !options.Diagnostics));
        if (options.Diagnostics)
        {
            Console.WriteLine(localizer.DiagnosticsReadOnly);
        }

        if (!settingsExisted)
        {
            Console.WriteLine(localizer.SettingsCreated);
        }

        var liveAttemptStore = new JsonLiveAttemptStore(
            Path.Combine(appDataRoot, "live-state.json"));
        var logger = new SafeJsonlLogger(Path.Combine(appDataRoot, "Logs"));
        var engine = new ResetDecisionEngine();
        var secretProtector = new DpapiSecretProtector();
        var failureClassifier = AppServerLiveResetFailureClassifier.Instance;
        var liveSafetyLatch = CreateLiveSafetyLatch(appDataRoot);

        IAccountRateLimitClient? usageSource = null;
        LiveResetCoordinator? liveCoordinator = null;
        CodexExecutableResolution? activeExecutable = null;
        var exitCode = 0;

        try
        {
            while (!cancellationSource.IsCancellationRequested)
            {
                GuardSettings settings;
                try
                {
                    settings = await settingsStore.LoadAsync(
                        cancellationSource.Token).ConfigureAwait(false);
                    localizer = new ConsoleLocalizer(settings.UiLanguage);
                }
                catch (SettingsException exception)
                {
                    exitCode = 2;
                    Console.Error.WriteLine(localizer.Failure(exception.ReasonCode));
                    if (!options.Diagnostics)
                    {
                        await TryLogFailureAsync(
                            logger,
                            "settings",
                            exception.ReasonCode,
                            cancellationSource.Token).ConfigureAwait(false);
                    }

                    if (options.Once)
                    {
                        break;
                    }

                    await DelaySafelyAsync(
                        TimeSpan.FromMinutes(1),
                        cancellationSource.Token).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    var executable = CodexExecutableLocator.Resolve(
                        settings.CodexExecutablePath);
                    if (usageSource is null
                        || !CanReuseClient(activeExecutable, executable))
                    {
                        if (usageSource is not null)
                        {
                            await usageSource.DisposeAsync().ConfigureAwait(false);
                        }

                        usageSource = new CodexAppServerClient(
                            executable,
                            Environment.CurrentDirectory);
                        liveCoordinator = null;
                        activeExecutable = executable;
                    }

                    var activeUsageSource = usageSource
                        ?? throw new AppServerException(AppServerFailureCategory.StartFailed);
                    var allowAutomation = IsAutomationExecutionAllowed(
                        settings,
                        options.Diagnostics);
                    if (allowAutomation)
                    {
                        liveCoordinator ??= new LiveResetCoordinator(
                            engine,
                            liveAttemptStore,
                            secretProtector,
                            activeUsageSource,
                            failureClassifier,
                            safetyLatch: liveSafetyLatch);
                    }

                    var snapshot = await activeUsageSource.ReadAsync(
                        cancellationSource.Token).ConfigureAwait(false);
                    if (options.Diagnostics)
                    {
                        PrintProtocolDiagnostics(snapshot);
                    }
                    else if (allowAutomation)
                    {
                        await ProcessAutomationSnapshotAsync(
                            liveCoordinator
                                ?? throw new LiveStateException(
                                    "live_coordinator_unavailable"),
                            engine,
                            logger,
                            localizer,
                            settings,
                            snapshot,
                            DateTimeOffset.UtcNow,
                            cancellationSource.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        var evaluation = engine.Evaluate(
                            settings,
                            snapshot,
                            DateTimeOffset.UtcNow);
                        await LogReadOnlyStatusAsync(
                            logger,
                            settings,
                            evaluation,
                            cancellationSource.Token).ConfigureAwait(false);
                        PrintReadOnlyStatus(
                            localizer,
                            settings,
                            evaluation);
                    }

                    exitCode = 0;
                }
                catch (OperationCanceledException)
                    when (cancellationSource.IsCancellationRequested)
                {
                    break;
                }
                catch (AppServerException exception)
                {
                    exitCode = 3;
                    var liveFailureDisposition = failureClassifier.Classify(exception);
                    if (!Enum.IsDefined(liveFailureDisposition))
                    {
                        liveFailureDisposition = LiveResetFailureDisposition.Unknown;
                    }

                    if (settings.AutomationEnabled
                        && !options.Diagnostics
                        && liveFailureDisposition
                            != LiveResetFailureDisposition.Retryable)
                    {
                        if (liveCoordinator is not null)
                        {
                            await PreserveOrBlockPendingAsync(
                                liveCoordinator,
                                liveFailureDisposition).ConfigureAwait(false);
                        }
                        else
                        {
                            LatchFailure(liveSafetyLatch, liveFailureDisposition);
                        }
                    }

                    var category = ToCode(exception.Category);
                    Console.Error.WriteLine(localizer.Failure(category));
                    if (settings.AutomationEnabled
                        && !options.Diagnostics)
                    {
                        Console.Error.WriteLine(localizer.LiveFailureState(
                            liveFailureDisposition));
                    }

                    if (!options.Diagnostics)
                    {
                        await TryLogFailureAsync(
                            logger,
                            "app_server",
                            category,
                            cancellationSource.Token).ConfigureAwait(false);
                    }
                    if (usageSource is not null)
                    {
                        await usageSource.DisposeAsync().ConfigureAwait(false);
                        usageSource = null;
                        liveCoordinator = null;
                        activeExecutable = null;
                    }
                }
                catch (LiveStateException exception)
                {
                    exitCode = 3;
                    if (settings.AutomationEnabled
                        && !options.Diagnostics)
                    {
                        liveSafetyLatch.BlockUnknownFailure();
                    }

                    Console.Error.WriteLine(localizer.Failure(exception.ReasonCode));
                    Console.Error.WriteLine(localizer.LiveFailureState(
                        LiveResetFailureDisposition.Unknown));
                    if (!options.Diagnostics)
                    {
                        await TryLogFailureAsync(
                            logger,
                            "live_state",
                            exception.ReasonCode,
                            cancellationSource.Token).ConfigureAwait(false);
                    }
                }
                catch (InvalidDataException)
                {
                    exitCode = 3;
                    if (settings.AutomationEnabled
                        && !options.Diagnostics)
                    {
                        liveSafetyLatch.BlockUnknownFailure();
                    }

                    Console.Error.WriteLine(localizer.Failure("state_invalid"));
                    if (settings.AutomationEnabled
                        && !options.Diagnostics)
                    {
                        Console.Error.WriteLine(localizer.LiveFailureState(
                            LiveResetFailureDisposition.Unknown));
                    }

                    if (!options.Diagnostics)
                    {
                        await TryLogFailureAsync(
                            logger,
                            "state",
                            "state_invalid",
                            cancellationSource.Token).ConfigureAwait(false);
                    }
                }
                catch (IOException)
                {
                    exitCode = 3;
                    if (settings.AutomationEnabled
                        && !options.Diagnostics)
                    {
                        liveSafetyLatch.BlockUnknownFailure();
                    }

                    Console.Error.WriteLine(localizer.Failure("local_io_error"));
                    if (settings.AutomationEnabled
                        && !options.Diagnostics)
                    {
                        Console.Error.WriteLine(localizer.LiveFailureState(
                            LiveResetFailureDisposition.Unknown));
                    }

                    if (!options.Diagnostics)
                    {
                        await TryLogFailureAsync(
                            logger,
                            "local_storage",
                            "local_io_error",
                            cancellationSource.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (!IsFatal(exception))
                {
                    exitCode = 3;
                    if (settings.AutomationEnabled
                        && !options.Diagnostics)
                    {
                        liveSafetyLatch.BlockUnknownFailure();
                    }

                    Console.Error.WriteLine(localizer.Failure(
                        "unexpected_local_failure"));
                    if (settings.AutomationEnabled
                        && !options.Diagnostics)
                    {
                        Console.Error.WriteLine(localizer.LiveFailureState(
                            LiveResetFailureDisposition.Unknown));
                    }

                    if (!options.Diagnostics)
                    {
                        await TryLogFailureAsync(
                            logger,
                            "monitor",
                            "unexpected_local_failure",
                            cancellationSource.Token).ConfigureAwait(false);
                    }
                }

                if (options.Once)
                {
                    break;
                }

                await DelaySafelyAsync(
                    TimeSpan.FromMinutes(settings.PollIntervalMinutes),
                    cancellationSource.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            if (usageSource is not null)
            {
                await usageSource.DisposeAsync().ConfigureAwait(false);
            }
        }

        if (cancellationSource.IsCancellationRequested)
        {
            Console.WriteLine(localizer.Stopping);
        }

        return exitCode;
    }

    internal static bool IsAutomationExecutionAllowed(
        GuardSettings settings,
        bool diagnostics)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.AutomationEnabled && !diagnostics;
    }

    internal static bool CanReuseClient(
        CodexExecutableResolution? current,
        CodexExecutableResolution candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return current is not null
            && string.Equals(
                current.ExecutablePath,
                candidate.ExecutablePath,
                StringComparison.OrdinalIgnoreCase)
            && current.DiscoverySource == candidate.DiscoverySource
            && current.Trust == candidate.Trust;
    }

    private static async Task<int> SaveAutomationEnabledAsync(
        JsonSettingsStore settingsStore,
        bool automationEnabled,
        CancellationToken cancellationToken)
    {
        try
        {
            var settings = await settingsStore.LoadOrCreateAsync(
                cancellationToken).ConfigureAwait(false);
            await settingsStore.SaveAsync(
                settings with { AutomationEnabled = automationEnabled },
                cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"automationEnabled={automationEnabled.ToString().ToLowerInvariant()}");
            return 0;
        }
        catch (SettingsException exception)
        {
            Console.Error.WriteLine($"Stopped safely: {exception.ReasonCode}");
            return 2;
        }
    }

    private static Task LogReadOnlyStatusAsync(
        SafeJsonlLogger logger,
        GuardSettings settings,
        EvaluationResult evaluation,
        CancellationToken cancellationToken)
    {
        return logger.WriteAsync(
            new SafeLogEvent(
                DateTimeOffset.UtcNow,
                "poll",
                evaluation.Decision.Kind == DecisionKind.Blocked
                    ? "evaluation_blocked"
                    : "automation_disabled",
                ToCode(evaluation.Decision.Reason),
                "weekly",
                evaluation.Decision.TriggerWindow?.RemainingPercent,
                settings.RemainingThresholdPercent,
                evaluation.AvailableCreditCount,
                DuplicateSuppressed: null,
                ComponentCategory: "monitor"),
            cancellationToken);
    }

    internal static async Task<LiveResetCycleResult> ProcessAutomationSnapshotAsync(
        LiveResetCoordinator coordinator,
        ResetDecisionEngine engine,
        SafeJsonlLogger logger,
        ConsoleLocalizer localizer,
        GuardSettings settings,
        AccountRateLimits snapshot,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(localizer);

        var result = await coordinator.ExecuteAsync(
            settings,
            snapshot,
            now,
            cancellationToken).ConfigureAwait(false);

        PrintLiveEvaluation(localizer, engine, settings, result, now);
        await TryLogLiveResultAsync(
            logger,
            settings,
            result,
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async Task TryLogLiveResultAsync(
        SafeJsonlLogger logger,
        GuardSettings settings,
        LiveResetCycleResult result,
        CancellationToken cancellationToken)
    {
        var reason = result.RequiresRefresh
            ? "refresh_pending"
            : result.Attempt?.BlockReason is { } blockReason
                ? ToCode(blockReason)
                : result.ProcessBlockReason is { } processBlockReason
                    ? ToCode(processBlockReason)
                : ToCode(result.Evaluation.Decision.Reason);
        var outcome = result.Outcome is { } consumeOutcome
            ? ToWireOutcome(consumeOutcome)
            : ToCode(result.Kind);
        var auditCancellationToken = result.Outcome is not null
            ? CancellationToken.None
            : cancellationToken;

        try
        {
            await logger.WriteAsync(
                new SafeLogEvent(
                    DateTimeOffset.UtcNow,
                    result.ConsumeAttempted ? "live_consume" : "live_poll",
                    outcome,
                    reason,
                    "weekly",
                    result.Evaluation.Decision.TriggerWindow?.RemainingPercent,
                    settings.RemainingThresholdPercent,
                    result.Evaluation.AvailableCreditCount,
                    result.Kind == LiveResetCycleKind.DuplicateSuppressed,
                    "coordinator"),
                auditCancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException)
        {
            // A terminal result remains valid even if its sanitized audit write fails.
        }
    }

    internal static LiveResetSafetyLatch CreateLiveSafetyLatch(
        string appDataRoot) => new(
            Path.Combine(appDataRoot, "live-safety-block.json"),
            CompatibilityRevision);

    internal static async Task PreserveOrBlockPendingAsync(
        LiveResetCoordinator coordinator,
        LiveResetFailureDisposition disposition)
    {
        if (disposition == LiveResetFailureDisposition.Retryable)
        {
            return;
        }

        try
        {
            var shouldBlock = true;
            try
            {
                var attempts = await coordinator.ReadAttemptsAsync(
                    CancellationToken.None).ConfigureAwait(false);
                shouldBlock = attempts.Any(
                    attempt => attempt.Phase != LiveAttemptPhase.Terminal);
            }
            catch (Exception readException) when (!IsFatal(readException))
            {
                // A state read failure cannot prove that no mutation is pending.
                // Fall through to the existing fail-closed block operation.
            }

            if (!shouldBlock)
            {
                return;
            }

            await coordinator.BlockPendingAsync(
                disposition == LiveResetFailureDisposition.ProtocolMismatch
                    ? LiveAttemptBlockReason.ProtocolMismatch
                    : LiveAttemptBlockReason.UnknownFailure,
                DateTimeOffset.UtcNow,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception blockException) when (
            blockException is not (
                OutOfMemoryException
                or StackOverflowException
                or AccessViolationException))
        {
            // Keep the existing durable record unchanged if classification cannot persist.
        }
    }

    internal static void LatchFailure(
        LiveResetSafetyLatch latch,
        LiveResetFailureDisposition disposition)
    {
        if (disposition == LiveResetFailureDisposition.Unknown)
        {
            latch.BlockUnknownFailure();
        }
    }

    private static void PrintReadOnlyStatus(
        ConsoleLocalizer localizer,
        GuardSettings settings,
        EvaluationResult evaluation)
    {
        Console.WriteLine();
        Console.WriteLine(DateTimeOffset.Now.ToString("u"));
        PrintWindow(localizer, localizer.Weekly, evaluation.Weekly);
        Console.WriteLine(
            $"{localizer.Threshold}: {settings.RemainingThresholdPercent}%");
        Console.WriteLine(
            $"{localizer.Credits}: {evaluation.AvailableCreditCount?.ToString() ?? localizer.Unknown}");
        Console.WriteLine(
            $"{localizer.Decision}: {localizer.AutomationDisabled} ({evaluation.Decision.Reason})");
    }

    private static void PrintLiveEvaluation(
        ConsoleLocalizer localizer,
        ResetDecisionEngine engine,
        GuardSettings settings,
        LiveResetCycleResult result,
        DateTimeOffset now)
    {
        var evaluation = result.Evaluation;
        Console.WriteLine();
        Console.WriteLine(DateTimeOffset.Now.ToString("u"));
        PrintWindow(localizer, localizer.Weekly, evaluation.Weekly);
        Console.WriteLine(
            $"{localizer.Threshold}: {settings.RemainingThresholdPercent}%");
        Console.WriteLine(
            $"{localizer.Credits}: {evaluation.AvailableCreditCount?.ToString() ?? localizer.Unknown}");
        Console.WriteLine(
            $"{localizer.Decision}: {result.Kind} ({evaluation.Decision.Reason})");

        switch (result.Kind)
        {
            case LiveResetCycleKind.Completed when result.Outcome is { } outcome:
                Console.WriteLine(localizer.LiveOutcome(ToWireOutcome(outcome)));
                Console.WriteLine(localizer.ServerDeterminedScope);
                break;
            case LiveResetCycleKind.DuplicateSuppressed:
                Console.WriteLine(localizer.LiveDuplicateSuppressed);
                if (result.Outcome is { } priorOutcome)
                {
                    Console.WriteLine(localizer.LiveOutcome(
                        ToWireOutcome(priorOutcome)));
                }

                break;
            case LiveResetCycleKind.Blocked:
                Console.WriteLine(localizer.LiveBlocked(
                    result.Attempt?.BlockReason is { } reason
                        ? ToCode(reason)
                        : result.ProcessBlockReason is { } processReason
                            ? ToCode(processReason)
                        : ToCode(evaluation.Decision.Reason)));
                break;
            case LiveResetCycleKind.NoAction:
                Console.WriteLine(localizer.LiveNoAction);
                break;
            case LiveResetCycleKind.AutomationDisabled:
                Console.WriteLine(localizer.LiveNotEnabled);
                break;
        }

        if (result.RefreshedRateLimits is { } refreshed)
        {
            var refreshedTrigger = engine.EvaluateTrigger(settings, refreshed, now);
            Console.WriteLine(localizer.RefreshCompleted);
            PrintWindow(localizer, localizer.Weekly, refreshedTrigger.Weekly);
            Console.WriteLine(
                $"{localizer.Credits}: {refreshed.ResetCredits?.AvailableCount.ToString() ?? localizer.Unknown}");
        }
        else if (result.RequiresRefresh)
        {
            Console.WriteLine(localizer.RefreshPending);
        }
    }

    private static void PrintWindow(
        ConsoleLocalizer localizer,
        string label,
        WindowReading? reading)
    {
        if (reading is null)
        {
            Console.WriteLine($"{label}: {localizer.Unknown}");
            return;
        }

        var resetTime = DateTimeOffset.FromUnixTimeSeconds(reading.ResetsAt)
            .ToLocalTime();
        Console.WriteLine(
            $"{label}: {localizer.Remaining} {reading.RemainingPercent:F0}% | "
            + $"{localizer.ResetsAt} {resetTime:g}");
    }

    private static void PrintProtocolDiagnostics(AccountRateLimits snapshot)
    {
        Console.WriteLine("Protocol diagnostics (no account or credit identifiers):");
        PrintSnapshotDiagnostics("legacy", snapshot.LegacyRateLimits);

        if (snapshot.RateLimitsByLimitId is not null)
        {
            var otherBucketNumber = 0;
            foreach (var pair in snapshot.RateLimitsByLimitId
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                var label = string.Equals(
                    pair.Key,
                    "codex",
                    StringComparison.OrdinalIgnoreCase)
                    ? "bucket:codex"
                    : $"bucket:other_{++otherBucketNumber}";
                PrintSnapshotDiagnostics(
                    label,
                    pair.Value);
            }
        }

        Console.WriteLine(
            $"  reset-credit count: {snapshot.ResetCredits?.AvailableCount.ToString() ?? "unknown"}");
    }

    private static void PrintSnapshotDiagnostics(
        string label,
        RateLimitSnapshot snapshot)
    {
        Console.WriteLine(
            $"  {label} primary duration={FormatDuration(snapshot.Primary)} "
            + $"used={FormatUsed(snapshot.Primary)}; "
            + $"secondary duration={FormatDuration(snapshot.Secondary)} "
            + $"used={FormatUsed(snapshot.Secondary)}");
    }

    private static string FormatDuration(RateLimitWindow? window) =>
        window?.WindowDurationMins?.ToString() ?? "null";

    private static string FormatUsed(RateLimitWindow? window) =>
        window is null ? "null" : $"{window.UsedPercent:F0}%";

    private static async Task TryLogFailureAsync(
        SafeJsonlLogger logger,
        string component,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            await logger.WriteAsync(
                new SafeLogEvent(
                    DateTimeOffset.UtcNow,
                    "failure",
                    "blocked",
                    reason,
                    ComponentCategory: component),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException)
        {
            // A logging failure must not expose raw exception text or enable an action.
        }
    }

    private static async Task DelaySafelyAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static bool IsFatal(Exception exception) => exception is
        OutOfMemoryException
        or StackOverflowException
        or AccessViolationException;

    private static string GetAppDataRoot()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("Local application data is unavailable.");
        }

        return Path.Combine(localAppData, "CodexResetGuard");
    }

    private static FileStream? TryAcquireInstanceLock(string appDataRoot)
    {
        Directory.CreateDirectory(appDataRoot);
        try
        {
            return new FileStream(
                Path.Combine(appDataRoot, InstanceLockFileName),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string ToCode<T>(T value)
        where T : struct, Enum => string.Concat(
            value.ToString().Select((character, index) =>
                char.IsUpper(character) && index > 0
                    ? $"_{char.ToLowerInvariant(character)}"
                    : char.ToLowerInvariant(character).ToString()));

    private static string ToWireOutcome(ConsumeResetCreditOutcome outcome) => outcome switch
    {
        ConsumeResetCreditOutcome.Reset => "reset",
        ConsumeResetCreditOutcome.NothingToReset => "nothingToReset",
        ConsumeResetCreditOutcome.NoCredit => "noCredit",
        ConsumeResetCreditOutcome.AlreadyRedeemed => "alreadyRedeemed",
        _ => "unknown",
    };

    private static void PrintHelp()
    {
        Console.WriteLine("CodexAutoReset");
        Console.WriteLine("  --once           Read and evaluate one snapshot, then exit.");
        Console.WriteLine("  --diagnostics    Print sanitized window durations for protocol checks.");
        Console.WriteLine("  --enable-automation   Enable automatic weekly reset and exit without contacting Codex.");
        Console.WriteLine("  --disable-automation  Disable automatic reset and exit without contacting Codex.");
        Console.WriteLine("  --help            Show this help.");
    }

    internal sealed record CommandLineOptions(
        bool Once,
        bool ShowHelp,
        bool Diagnostics,
        bool? AutomationEnabledCommand)
    {
        public static CommandLineOptions Parse(string[] args)
        {
            var once = false;
            var help = false;
            var diagnostics = false;
            bool? automationEnabledCommand = null;

            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--once":
                        once = true;
                        break;
                    case "--help" or "-h":
                        help = true;
                        break;
                    case "--diagnostics":
                        diagnostics = true;
                        break;
                    case "--enable-automation":
                        if (automationEnabledCommand is not null)
                        {
                            throw new ArgumentException("Only one automation command is allowed.");
                        }

                        automationEnabledCommand = true;
                        break;
                    case "--disable-automation":
                        if (automationEnabledCommand is not null)
                        {
                            throw new ArgumentException("Only one automation command is allowed.");
                        }

                        automationEnabledCommand = false;
                        break;
                    default:
                        throw new ArgumentException("Unsupported command-line argument.");
                }
            }

            if (automationEnabledCommand is not null
                && (once || help || diagnostics || args.Length != 1))
            {
                throw new ArgumentException("An automation command must be used alone.");
            }

            return new CommandLineOptions(
                once,
                help,
                diagnostics,
                automationEnabledCommand);
        }
    }

}
