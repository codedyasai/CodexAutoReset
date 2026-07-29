using System.Text;
using System.Text.Json.Nodes;
using CodexAutoReset.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexAutoReset.Tests;

[TestClass]
public sealed class SettingsStoreTests
{
    private const string ValidV1SettingsJson =
        """
        {
          "schemaVersion": 1,
          "remainingThresholdPercent": 7,
          "triggerLimit": "weekly",
          "pollIntervalMinutes": 5,
          "uiLanguage": "auto",
          "startWithWindows": false,
          "codexExecutablePath": null
        }
        """;

    private const string ValidV2SettingsJson =
        """
        {
          "schemaVersion": 2,
          "remainingThresholdPercent": 7,
          "triggerLimit": "weekly",
          "pollIntervalMinutes": 5,
          "uiLanguage": "auto",
          "startWithWindows": false,
          "codexExecutablePath": null,
          "executionMode": "dryRun"
        }
        """;

    private const string ValidV3SettingsJson =
        """
        {
          "schemaVersion": 3,
          "remainingThresholdPercent": 7,
          "pollIntervalMinutes": 5,
          "uiLanguage": "auto",
          "startWithWindows": false,
          "codexExecutablePath": null,
          "automationEnabled": false
        }
        """;

    private const string ValidV4SettingsJson =
        """
        {
          "schemaVersion": 4,
          "remainingThresholdPercent": 7,
          "pollIntervalMinutes": 5,
          "uiLanguage": "auto",
          "startWithWindows": false,
          "codexExecutablePath": null,
          "automationEnabled": false,
          "notifyOnUsageReset": true
        }
        """;

    [TestMethod]
    public async Task MissingSettingsCreateVersionFourDefaults()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "settings.json");
        var store = new JsonSettingsStore(path);

        var settings = await store.LoadOrCreateAsync(CancellationToken.None);

        Assert.AreEqual(GuardSettings.Default, settings);
        Assert.IsFalse(settings.AutomationEnabled);
        Assert.IsTrue(settings.NotifyOnUsageReset);
        var json = await File.ReadAllTextAsync(path, Encoding.UTF8);
        StringAssert.Contains(json, "\"schemaVersion\": 4");
        StringAssert.Contains(json, "\"automationEnabled\": false");
        StringAssert.Contains(json, "\"notifyOnUsageReset\": true");
        Assert.IsFalse(json.Contains("triggerLimit", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("executionMode", StringComparison.Ordinal));
        Assert.AreEqual(settings, await store.LoadAsync(CancellationToken.None));
    }

    [DataTestMethod]
    [DataRow("weekly")]
    [DataRow("fiveHour")]
    public async Task VersionOneAlwaysMigratesDisabled(string triggerLimit)
    {
        var document = JsonNode.Parse(ValidV1SettingsJson)!.AsObject();
        document["triggerLimit"] = triggerLimit;

        var settings = await LoadSettingsAsync(document.ToJsonString());

        Assert.IsFalse(settings.AutomationEnabled);
    }

    [DataTestMethod]
    [DataRow("weekly", "live", true)]
    [DataRow("weekly", "dryRun", false)]
    [DataRow("fiveHour", "live", false)]
    [DataRow("fiveHour", "dryRun", false)]
    public async Task VersionTwoOnlyWeeklyLiveMigratesEnabled(
        string triggerLimit,
        string executionMode,
        bool expectedEnabled)
    {
        var document = JsonNode.Parse(ValidV2SettingsJson)!.AsObject();
        document["triggerLimit"] = triggerLimit;
        document["executionMode"] = executionMode;

        var settings = await LoadSettingsAsync(document.ToJsonString());

        Assert.AreEqual(expectedEnabled, settings.AutomationEnabled);
    }

    [TestMethod]
    public async Task VersionFourSettingsRoundTripWithoutLegacyFields()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "settings.json");
        var store = new JsonSettingsStore(path);
        var enabled = GuardSettings.Default with
        {
            AutomationEnabled = true,
            NotifyOnUsageReset = false,
        };

        await store.SaveAsync(enabled, CancellationToken.None);

        var json = await File.ReadAllTextAsync(path, Encoding.UTF8);
        StringAssert.Contains(json, "\"schemaVersion\": 4");
        StringAssert.Contains(json, "\"automationEnabled\": true");
        StringAssert.Contains(json, "\"notifyOnUsageReset\": false");
        Assert.IsFalse(json.Contains("triggerLimit", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("executionMode", StringComparison.Ordinal));
        Assert.AreEqual(enabled, await store.LoadAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task VersionThreeMigratesUsageResetNotificationsEnabled()
    {
        var settings = await LoadSettingsAsync(ValidV3SettingsJson);

        Assert.IsTrue(settings.NotifyOnUsageReset);
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(7)]
    [DataRow(99)]
    public void ThresholdBoundaryValuesAreValid(int threshold) =>
        JsonSettingsStore.Validate(GuardSettings.Default with
        {
            RemainingThresholdPercent = threshold,
        });

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(100)]
    [DataRow(101)]
    public void ThresholdOutsideRangeIsRejected(int threshold)
    {
        var exception = Assert.ThrowsException<SettingsException>(() =>
            JsonSettingsStore.Validate(GuardSettings.Default with
            {
                RemainingThresholdPercent = threshold,
            }));
        Assert.AreEqual("threshold_out_of_range", exception.ReasonCode);
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public async Task VersionOneThroughFourThresholdOneHundredMigratesToNinetyNine(
        int schemaVersion)
    {
        var json = schemaVersion switch
        {
            1 => ValidV1SettingsJson,
            2 => ValidV2SettingsJson,
            3 => ValidV3SettingsJson,
            4 => ValidV4SettingsJson,
            _ => throw new AssertFailedException(),
        };
        var document = JsonNode.Parse(json)!.AsObject();
        document["remainingThresholdPercent"] = 100;

        var settings = await LoadSettingsAsync(document.ToJsonString());

        Assert.AreEqual(99, settings.RemainingThresholdPercent);
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(5)]
    [DataRow(60)]
    public void PollIntervalBoundaryValuesAreValid(int minutes) =>
        JsonSettingsStore.Validate(GuardSettings.Default with
        {
            PollIntervalMinutes = minutes,
        });

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(61)]
    public void PollIntervalOutsideRangeIsRejected(int minutes)
    {
        var exception = Assert.ThrowsException<SettingsException>(() =>
            JsonSettingsStore.Validate(GuardSettings.Default with
            {
                PollIntervalMinutes = minutes,
            }));
        Assert.AreEqual("poll_interval_out_of_range", exception.ReasonCode);
    }

    [DataTestMethod]
    [DataRow("triggerLimit", "\"weekly\"")]
    [DataRow("executionMode", "\"live\"")]
    [DataRow("unexpected", "true")]
    public async Task VersionThreeRejectsLegacyAndUnknownProperties(
        string propertyName,
        string jsonValue)
    {
        var document = JsonNode.Parse(ValidV3SettingsJson)!.AsObject();
        document[propertyName] = JsonNode.Parse(jsonValue);

        var exception = await LoadInvalidSettingsAsync(document.ToJsonString());

        Assert.AreEqual("settings_invalid_json", exception.ReasonCode);
    }

    [TestMethod]
    public async Task VersionOneRejectsVersionTwoExecutionModeField()
    {
        var document = JsonNode.Parse(ValidV1SettingsJson)!.AsObject();
        document["executionMode"] = "dryRun";

        var exception = await LoadInvalidSettingsAsync(document.ToJsonString());

        Assert.AreEqual("settings_invalid_json", exception.ReasonCode);
    }

    [DataTestMethod]
    [DataRow("schemaVersion")]
    [DataRow("remainingThresholdPercent")]
    [DataRow("pollIntervalMinutes")]
    [DataRow("uiLanguage")]
    [DataRow("startWithWindows")]
    [DataRow("codexExecutablePath")]
    [DataRow("automationEnabled")]
    public async Task MissingVersionThreeFieldFailsClosed(string propertyName)
    {
        var document = JsonNode.Parse(ValidV3SettingsJson)!.AsObject();
        Assert.IsTrue(document.Remove(propertyName));

        var exception = await LoadInvalidSettingsAsync(document.ToJsonString());

        Assert.AreEqual("settings_invalid_json", exception.ReasonCode);
    }

    [DataTestMethod]
    [DataRow("schemaVersion")]
    [DataRow("remainingThresholdPercent")]
    [DataRow("pollIntervalMinutes")]
    [DataRow("uiLanguage")]
    [DataRow("startWithWindows")]
    [DataRow("codexExecutablePath")]
    [DataRow("automationEnabled")]
    [DataRow("notifyOnUsageReset")]
    public async Task MissingVersionFourFieldFailsClosed(string propertyName)
    {
        var document = JsonNode.Parse(ValidV4SettingsJson)!.AsObject();
        Assert.IsTrue(document.Remove(propertyName));

        var exception = await LoadInvalidSettingsAsync(document.ToJsonString());

        Assert.AreEqual("settings_invalid_json", exception.ReasonCode);
    }

    [TestMethod]
    public async Task MissingConfiguredExecutableStillLoadsSoTheUserCanRecover()
    {
        using var directory = TemporaryDirectory.Create();
        var settingsPath = Path.Combine(directory.Path, "settings.json");
        var missingCodexPath = Path.Combine(directory.Path, "missing", "codex.exe");
        var settings = GuardSettings.Default with
        {
            CodexExecutablePath = missingCodexPath,
        };
        var store = new JsonSettingsStore(settingsPath);

        await store.SaveAsync(settings, CancellationToken.None);
        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.AreEqual(missingCodexPath, loaded.CodexExecutablePath);
    }

    [DataTestMethod]
    [DataRow("daily", "live", "trigger_limit_invalid")]
    [DataRow("weekly", "LIVE", "execution_mode_invalid")]
    [DataRow("weekly", "", "execution_mode_invalid")]
    public async Task InvalidLegacyMigrationFieldFailsClosed(
        string triggerLimit,
        string executionMode,
        string expectedReason)
    {
        var document = JsonNode.Parse(ValidV2SettingsJson)!.AsObject();
        document["triggerLimit"] = triggerLimit;
        document["executionMode"] = executionMode;

        var exception = await LoadInvalidSettingsAsync(document.ToJsonString());

        Assert.AreEqual(expectedReason, exception.ReasonCode);
    }

    [TestMethod]
    public async Task DuplicatePropertyFailsClosed()
    {
        var json = ValidV3SettingsJson.Replace(
            "\"schemaVersion\": 3,",
            "\"schemaVersion\": 3, \"schemaVersion\": 3,",
            StringComparison.Ordinal);

        var exception = await LoadInvalidSettingsAsync(json);

        Assert.AreEqual("settings_invalid_json", exception.ReasonCode);
    }

    [TestMethod]
    public async Task OversizedDocumentFailsClosed()
    {
        var exception = await LoadInvalidSettingsAsync(new string(' ', (64 * 1024) + 1));
        Assert.AreEqual("settings_too_large", exception.ReasonCode);
    }

    [DataTestMethod]
    [DataRow("auth.json")]
    [DataRow("AUTH.JSON")]
    [DataRow("custom.json")]
    public async Task NonSettingsFileNameIsRejectedBeforeCreate(string fileName)
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, fileName);
        var store = new JsonSettingsStore(path);

        var exception = await Assert.ThrowsExceptionAsync<SettingsException>(
            () => store.LoadOrCreateAsync(CancellationToken.None));

        Assert.AreEqual("settings_path_forbidden", exception.ReasonCode);
        Assert.IsFalse(File.Exists(path));
    }

    private static async Task<GuardSettings> LoadSettingsAsync(string json)
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "settings.json");
        await File.WriteAllTextAsync(path, json);
        return await new JsonSettingsStore(path).LoadAsync(CancellationToken.None);
    }

    private static async Task<SettingsException> LoadInvalidSettingsAsync(string json)
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "settings.json");
        await File.WriteAllTextAsync(path, json);
        return await Assert.ThrowsExceptionAsync<SettingsException>(() =>
            new JsonSettingsStore(path).LoadAsync(CancellationToken.None));
    }
}
