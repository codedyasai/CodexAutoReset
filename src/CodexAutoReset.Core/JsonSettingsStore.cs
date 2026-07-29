using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexAutoReset.Core;

public sealed class SettingsException : Exception
{
    public SettingsException(string reasonCode)
        : base(reasonCode)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}

public sealed class JsonSettingsStore
{
    private const long MaximumDocumentBytes = 64 * 1024;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string path;

    public JsonSettingsStore(string path)
    {
        this.path = System.IO.Path.GetFullPath(path);
    }

    public string Path => path;

    public async Task<GuardSettings> LoadOrCreateAsync(CancellationToken cancellationToken)
    {
        EnsurePathAllowed();

        if (!File.Exists(path))
        {
            await SaveAsync(GuardSettings.Default, cancellationToken).ConfigureAwait(false);
            return GuardSettings.Default;
        }

        return await LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<GuardSettings> LoadAsync(CancellationToken cancellationToken)
    {
        EnsurePathAllowed();

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);

            if (stream.Length > MaximumDocumentBytes)
            {
                throw new SettingsException("settings_too_large");
            }

            using var jsonDocument = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            EnsureNoDuplicateMembers(jsonDocument.RootElement);

            return DeserializeAndMap(jsonDocument.RootElement);
        }
        catch (SettingsException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new SettingsException("settings_invalid_json");
        }
        catch (IOException)
        {
            throw new SettingsException("settings_io_error");
        }
        catch (UnauthorizedAccessException)
        {
            throw new SettingsException("settings_access_denied");
        }
    }

    public async Task SaveAsync(GuardSettings settings, CancellationToken cancellationToken)
    {
        EnsurePathAllowed();
        Validate(settings);

        var directory = System.IO.Path.GetDirectoryName(path)
            ?? throw new SettingsException("settings_path_invalid");

        var temporaryPath = System.IO.Path.Combine(
            directory,
            $".{System.IO.Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        var document = SettingsDocumentV4.FromSettings(settings);

        try
        {
            Directory.CreateDirectory(directory);

            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    WriteOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);

                if (stream.Length > MaximumDocumentBytes)
                {
                    throw new SettingsException("settings_too_large");
                }
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (SettingsException)
        {
            throw;
        }
        catch (IOException)
        {
            throw new SettingsException("settings_io_error");
        }
        catch (UnauthorizedAccessException)
        {
            throw new SettingsException("settings_access_denied");
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    public static void Validate(GuardSettings settings)
    {
        if (settings.RemainingThresholdPercent is < GuardSettings.MinimumThreshold
            or > GuardSettings.MaximumThreshold)
        {
            throw new SettingsException("threshold_out_of_range");
        }

        if (settings.PollIntervalMinutes is < GuardSettings.MinimumPollIntervalMinutes
            or > GuardSettings.MaximumPollIntervalMinutes)
        {
            throw new SettingsException("poll_interval_out_of_range");
        }

        if (!Enum.IsDefined(settings.UiLanguage))
        {
            throw new SettingsException("ui_language_invalid");
        }

        if (settings.CodexExecutablePath is not null)
        {
            if (!System.IO.Path.IsPathFullyQualified(settings.CodexExecutablePath)
                || !string.Equals(
                    System.IO.Path.GetFileName(settings.CodexExecutablePath),
                    "codex.exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new SettingsException("codex_executable_path_invalid");
            }
        }
    }

    private static GuardSettings DeserializeAndMap(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("schemaVersion", out var schemaVersionElement)
            || schemaVersionElement.ValueKind != JsonValueKind.Number
            || !schemaVersionElement.TryGetInt32(out var schemaVersion))
        {
            throw new JsonException();
        }

        return schemaVersion switch
        {
            1 => ValidateAndMap(DeserializeRequired<SettingsDocumentV1>(root)),
            2 => ValidateAndMap(DeserializeRequired<SettingsDocumentV2>(root)),
            3 => ValidateAndMap(DeserializeRequired<SettingsDocumentV3>(root)),
            4 => ValidateAndMap(DeserializeRequired<SettingsDocumentV4>(root)),
            _ => throw new SettingsException("settings_schema_unsupported"),
        };
    }

    private static TDocument DeserializeRequired<TDocument>(JsonElement root)
        where TDocument : class
    {
        return root.Deserialize<TDocument>(ReadOptions)
            ?? throw new SettingsException("settings_empty");
    }

    private static GuardSettings ValidateAndMap(SettingsDocumentV1 document)
    {
        ValidateLegacyTriggerLimit(document.TriggerLimit);
        return ValidateAndMap(
            document.RemainingThresholdPercent,
            document.PollIntervalMinutes,
            document.UiLanguage,
            document.StartWithWindows,
            document.CodexExecutablePath,
            automationEnabled: false,
            notifyOnUsageReset: true);
    }

    private static GuardSettings ValidateAndMap(SettingsDocumentV2 document)
    {
        var isLive = document.ExecutionMode switch
        {
            "dryRun" => false,
            "live" => true,
            _ => throw new SettingsException("execution_mode_invalid"),
        };
        ValidateLegacyTriggerLimit(document.TriggerLimit);

        return ValidateAndMap(
            document.RemainingThresholdPercent,
            document.PollIntervalMinutes,
            document.UiLanguage,
            document.StartWithWindows,
            document.CodexExecutablePath,
            automationEnabled: isLive
                && string.Equals(document.TriggerLimit, "weekly", StringComparison.Ordinal),
            notifyOnUsageReset: true);
    }

    private static GuardSettings ValidateAndMap(SettingsDocumentV3 document) =>
        ValidateAndMap(
            document.RemainingThresholdPercent,
            document.PollIntervalMinutes,
            document.UiLanguage,
            document.StartWithWindows,
            document.CodexExecutablePath,
            document.AutomationEnabled,
            notifyOnUsageReset: true);

    private static GuardSettings ValidateAndMap(SettingsDocumentV4 document) =>
        ValidateAndMap(
            document.RemainingThresholdPercent,
            document.PollIntervalMinutes,
            document.UiLanguage,
            document.StartWithWindows,
            document.CodexExecutablePath,
            document.AutomationEnabled,
            document.NotifyOnUsageReset);

    private static GuardSettings ValidateAndMap(
        int remainingThresholdPercent,
        int pollIntervalMinutes,
        string uiLanguageValue,
        bool startWithWindows,
        string? codexExecutablePath,
        bool automationEnabled,
        bool notifyOnUsageReset)
    {
        // Version 1-4 settings previously allowed 100. Loading that value as 99
        // preserves the user's conservative intent without allowing a fully
        // recovered weekly window to remain eligible for another reset credit.
        var migratedThresholdPercent = remainingThresholdPercent == 100
            ? GuardSettings.MaximumThreshold
            : remainingThresholdPercent;
        var uiLanguage = uiLanguageValue switch
        {
            "auto" => UiLanguage.Auto,
            "ko-KR" => UiLanguage.Korean,
            "en-US" => UiLanguage.English,
            _ => throw new SettingsException("ui_language_invalid"),
        };

        var settings = new GuardSettings(
            migratedThresholdPercent,
            pollIntervalMinutes,
            uiLanguage,
            startWithWindows,
            codexExecutablePath,
            automationEnabled,
            notifyOnUsageReset);

        Validate(settings);
        return settings;
    }

    private static void ValidateLegacyTriggerLimit(string value)
    {
        if (value is not ("fiveHour" or "weekly"))
        {
            throw new SettingsException("trigger_limit_invalid");
        }
    }

    private void EnsurePathAllowed()
    {
        if (!string.Equals(
            System.IO.Path.GetFileName(path),
            "settings.json",
            StringComparison.OrdinalIgnoreCase))
        {
            throw new SettingsException("settings_path_forbidden");
        }

        try
        {
            var directory = System.IO.Path.GetDirectoryName(path);
            if ((File.Exists(path)
                    && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                || (directory is not null
                    && Directory.Exists(directory)
                    && File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)))
            {
                throw new SettingsException("settings_path_forbidden");
            }
        }
        catch (SettingsException)
        {
            throw;
        }
        catch (IOException)
        {
            throw new SettingsException("settings_io_error");
        }
        catch (UnauthorizedAccessException)
        {
            throw new SettingsException("settings_access_denied");
        }
    }

    private static void EnsureNoDuplicateMembers(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var memberNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!memberNames.Add(property.Name))
                {
                    throw new JsonException();
                }

                EnsureNoDuplicateMembers(property.Value);
            }

            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                EnsureNoDuplicateMembers(item);
            }
        }
    }

    private static void TryDeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record SettingsDocumentV1
    {
        [JsonRequired]
        public int SchemaVersion { get; init; } = 1;

        [JsonRequired]
        public int RemainingThresholdPercent { get; init; } = 7;

        [JsonRequired]
        public string TriggerLimit { get; init; } = "weekly";

        [JsonRequired]
        public int PollIntervalMinutes { get; init; } = 5;

        [JsonRequired]
        public string UiLanguage { get; init; } = "auto";

        [JsonRequired]
        public bool StartWithWindows { get; init; }

        [JsonRequired]
        public string? CodexExecutablePath { get; init; }
    }

    private sealed record SettingsDocumentV2
    {
        [JsonRequired]
        public int SchemaVersion { get; init; } = 2;

        [JsonRequired]
        public int RemainingThresholdPercent { get; init; } = 7;

        [JsonRequired]
        public string TriggerLimit { get; init; } = "weekly";

        [JsonRequired]
        public int PollIntervalMinutes { get; init; } = 5;

        [JsonRequired]
        public string UiLanguage { get; init; } = "auto";

        [JsonRequired]
        public bool StartWithWindows { get; init; }

        [JsonRequired]
        public string? CodexExecutablePath { get; init; }

        [JsonRequired]
        public string ExecutionMode { get; init; } = "dryRun";

    }

    private sealed record SettingsDocumentV3
    {
        [JsonRequired]
        public int SchemaVersion { get; init; } = 3;

        [JsonRequired]
        public int RemainingThresholdPercent { get; init; } = 7;

        [JsonRequired]
        public int PollIntervalMinutes { get; init; } = 5;

        [JsonRequired]
        public string UiLanguage { get; init; } = "auto";

        [JsonRequired]
        public bool StartWithWindows { get; init; }

        [JsonRequired]
        public string? CodexExecutablePath { get; init; }

        [JsonRequired]
        public bool AutomationEnabled { get; init; }

    }

    private sealed record SettingsDocumentV4
    {
        [JsonRequired]
        public int SchemaVersion { get; init; } = 4;

        [JsonRequired]
        public int RemainingThresholdPercent { get; init; } = 7;

        [JsonRequired]
        public int PollIntervalMinutes { get; init; } = 5;

        [JsonRequired]
        public string UiLanguage { get; init; } = "auto";

        [JsonRequired]
        public bool StartWithWindows { get; init; }

        [JsonRequired]
        public string? CodexExecutablePath { get; init; }

        [JsonRequired]
        public bool AutomationEnabled { get; init; }

        [JsonRequired]
        public bool NotifyOnUsageReset { get; init; } = true;

        public static SettingsDocumentV4 FromSettings(GuardSettings settings) => new()
        {
            SchemaVersion = 4,
            RemainingThresholdPercent = settings.RemainingThresholdPercent,
            PollIntervalMinutes = settings.PollIntervalMinutes,
            UiLanguage = settings.UiLanguage switch
            {
                CodexAutoReset.Core.UiLanguage.Korean => "ko-KR",
                CodexAutoReset.Core.UiLanguage.English => "en-US",
                _ => "auto",
            },
            StartWithWindows = settings.StartWithWindows,
            CodexExecutablePath = settings.CodexExecutablePath,
            AutomationEnabled = settings.AutomationEnabled,
            NotifyOnUsageReset = settings.NotifyOnUsageReset,
        };
    }
}
