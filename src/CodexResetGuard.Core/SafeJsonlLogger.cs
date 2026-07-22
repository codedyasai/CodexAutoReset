using System.Text.Json;

namespace CodexResetGuard.Core;

public sealed record SafeLogEvent(
    DateTimeOffset Timestamp,
    string EventType,
    string Outcome,
    string? ReasonCode = null,
    string? TriggerLimit = null,
    double? RemainingPercent = null,
    int? ThresholdPercent = null,
    long? AvailableCreditCount = null,
    bool? DuplicateSuppressed = null,
    string? ComponentCategory = null);

public sealed class SafeJsonlLogger
{
    private const string FilePrefix = "codex-reset-guard-";

    private static readonly HashSet<string> AllowedEventTypes = new(
        [
            "failure",
            "live",
            "live_consume",
            "live_poll",
            "poll",
        ],
        StringComparer.Ordinal);

    private static readonly HashSet<string> AllowedOutcomes = new(
        [
            "alreadyRedeemed",
            "automation_disabled",
            "blocked",
            "completed",
            "duplicate_suppressed",
            "evaluation_blocked",
            "legacy_trigger_unsupported",
            "live_already_redeemed",
            "live_blocked",
            "live_context_changed",
            "live_dispatch_limit",
            "live_needs_review",
            "live_no_credit",
            "live_no_credit_refresh_pending",
            "live_nothing_refresh_pending",
            "live_nothing_to_reset",
            "live_protocol_blocked",
            "live_redeemed_refresh_pending",
            "live_retry_pending",
            "live_reset",
            "live_reset_refresh_pending",
            "live_secret_unavailable",
            "noCredit",
            "no_action",
            "nothingToReset",
            "reset",
        ],
        StringComparer.Ordinal);

    private static readonly HashSet<string> AllowedReasons = new(
        [
            "above_threshold",
            "ambiguous_legacy_bucket",
            "cancelled",
            "codex_bucket_mismatch",
            "codex_bucket_missing",
            "codex_executable_path_invalid",
            "context_changed",
            "credit_details_unavailable",
            "credit_summary_unavailable",
            "dispatch_limit_reached",
            "execution_mode_invalid",
            "executable_not_found",
            "invalid_credit_count",
            "invalid_outbound_message",
            "invalid_reset_time",
            "invalid_response",
            "invalid_used_percent",
            "io_error",
            "legacy_trigger_unsupported",
            "live_attempt_missing",
            "live_attempt_not_pending",
            "live_candidate_invalid",
            "live_coordinator_unavailable",
            "live_credit_invalid",
            "live_outcome_invalid",
            "live_needs_review",
            "live_protocol_blocked",
            "live_safety_block_persist_failed",
            "live_secret_protection_failed",
            "live_secret_unavailable",
            "live_sticky_state_missing",
            "live_state_access_denied",
            "live_state_capacity_reached",
            "live_state_failure",
            "live_state_invalid",
            "live_state_io_error",
            "live_state_path_forbidden",
            "local_access_denied",
            "local_io_error",
            "local_runtime_failure",
            "no_credits",
            "no_eligible_credit",
            "outbound_method_not_allowed",
            "poll_interval_out_of_range",
            "process_exited",
            "protocol_mismatch",
            "refresh_pending",
            "remote_error",
            "secret_unavailable",
            "selected_window_ambiguous",
            "selected_window_missing",
            "settings_access_denied",
            "settings_empty",
            "settings_invalid_json",
            "settings_io_error",
            "settings_path_forbidden",
            "settings_path_invalid",
            "settings_schema_unsupported",
            "settings_too_large",
            "start_failed",
            "state_invalid",
            "threshold_out_of_range",
            "threshold_reached",
            "timeout",
            "trigger_limit_invalid",
            "ui_language_invalid",
            "untrusted_executable_for_mutation",
            "unexpected_local_failure",
            "unknown_failure",
        ],
        StringComparer.Ordinal);

    private static readonly HashSet<string> AllowedTriggerLimits = new(
        ["weekly"],
        StringComparer.Ordinal);

    private static readonly HashSet<string> AllowedComponentCategories = new(
        [
            "app_server",
            "coordinator",
            "desktop_monitor",
            "live_state",
            "local_storage",
            "monitor",
            "settings",
            "state",
        ],
        StringComparer.Ordinal);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string logDirectory;
    private readonly int retentionDays;
    private readonly SemaphoreSlim gate = new(1, 1);

    public SafeJsonlLogger(string logDirectory, int retentionDays = 14)
    {
        if (retentionDays < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionDays));
        }

        this.logDirectory = Path.GetFullPath(logDirectory);
        this.retentionDays = retentionDays;
    }

    public async Task WriteAsync(
        SafeLogEvent logEvent,
        CancellationToken cancellationToken)
    {
        Validate(logEvent);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(logDirectory);
            DeleteExpiredFiles(logEvent.Timestamp.UtcDateTime);

            var filePath = Path.Combine(
                logDirectory,
                $"{FilePrefix}{logEvent.Timestamp.UtcDateTime:yyyy-MM-dd}.jsonl");
            var json = JsonSerializer.Serialize(logEvent, SerializerOptions);

            await using var stream = new FileStream(
                filePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);
            await using var writer = new StreamWriter(stream);
            await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static void Validate(SafeLogEvent logEvent)
    {
        if (logEvent.Timestamp == default)
        {
            throw new ArgumentException("A log timestamp is required.", nameof(logEvent));
        }

        ValidateAllowed(
            logEvent.EventType,
            AllowedEventTypes,
            nameof(logEvent.EventType));
        ValidateAllowed(
            logEvent.Outcome,
            AllowedOutcomes,
            nameof(logEvent.Outcome));

        if (logEvent.ReasonCode is not null)
        {
            ValidateAllowed(
                logEvent.ReasonCode,
                AllowedReasons,
                nameof(logEvent.ReasonCode));
        }

        if (logEvent.TriggerLimit is not null)
        {
            ValidateAllowed(
                logEvent.TriggerLimit,
                AllowedTriggerLimits,
                nameof(logEvent.TriggerLimit));
        }

        if (logEvent.ComponentCategory is not null)
        {
            ValidateAllowed(
                logEvent.ComponentCategory,
                AllowedComponentCategories,
                nameof(logEvent.ComponentCategory));
        }

        if (logEvent.RemainingPercent is { } remaining
            && (!double.IsFinite(remaining) || remaining is < 0 or > 100))
        {
            throw new ArgumentOutOfRangeException(nameof(logEvent.RemainingPercent));
        }

        if (logEvent.ThresholdPercent is { } threshold
            && threshold is < GuardSettings.MinimumThreshold
                or > GuardSettings.MaximumThreshold)
        {
            throw new ArgumentOutOfRangeException(nameof(logEvent.ThresholdPercent));
        }
    }

    private static void ValidateAllowed(
        string value,
        IReadOnlySet<string> allowedValues,
        string parameterName)
    {
        if (!allowedValues.Contains(value))
        {
            throw new ArgumentException("The log code is not allow-listed.", parameterName);
        }
    }

    private void DeleteExpiredFiles(DateTime utcNow)
    {
        var cutoff = utcNow.AddDays(-retentionDays);
        foreach (var filePath in Directory.EnumerateFiles(
            logDirectory,
            $"{FilePrefix}*.jsonl",
            SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(filePath) < cutoff)
                {
                    File.Delete(filePath);
                }
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
