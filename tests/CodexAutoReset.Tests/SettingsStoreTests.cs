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

    private const string ValidV5SettingsJson =
        """
        {
          "schemaVersion": 5,
          "weeklyRemainingThresholdPercent": 11,
          "fiveHourRemainingThresholdPercent": 23,
          "uiLanguage": "auto",
          "startWithWindows": false,
          "codexExecutablePath": null,
          "weeklyAutomationEnabled": false,
          "fiveHourAutomationEnabled": true,
          "notifyOnUsageReset": true
        }
        """;

    private const string ValidV6SettingsJson =
        """
        {
          "schemaVersion": 6,
          "weeklyRemainingThresholdPercent": 0,
          "fiveHourRemainingThresholdPercent": null,
          "uiLanguage": "auto",
          "startWithWindows": false,
          "codexExecutablePath": null,
          "weeklyAutomationEnabled": true,
          "fiveHourAutomationEnabled": false,
          "notifyOnUsageReset": true
        }
        """;

    [TestMethod]
    public async Task MissingSettingsCreateVersionSixDefaults()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "settings.json");
        var store = new JsonSettingsStore(path);

        var settings = await store.LoadOrCreateAsync(CancellationToken.None);

        Assert.AreEqual(GuardSettings.Default, settings);
        Assert.IsNull(settings.WeeklyRemainingThresholdPercent);
        Assert.IsFalse(settings.AutomationEnabled);
        Assert.IsNull(settings.FiveHourRemainingThresholdPercent);
        Assert.IsFalse(settings.FiveHourAutomationEnabled);
        Assert.IsFalse(settings.AnyAutomationEnabled);
        Assert.IsTrue(settings.NotifyOnUsageReset);
        Assert.AreEqual(
            GuardSettings.FixedPollIntervalMinutes,
            settings.PollIntervalMinutes);
        var json = await File.ReadAllTextAsync(path, Encoding.UTF8);
        StringAssert.Contains(json, "\"schemaVersion\": 6");
        StringAssert.Contains(
            json,
            "\"weeklyRemainingThresholdPercent\": null");
        StringAssert.Contains(
            json,
            "\"fiveHourRemainingThresholdPercent\": null");
        StringAssert.Contains(json, "\"weeklyAutomationEnabled\": false");
        StringAssert.Contains(json, "\"fiveHourAutomationEnabled\": false");
        StringAssert.Contains(json, "\"notifyOnUsageReset\": true");
        Assert.IsFalse(
            json.Contains("pollIntervalMinutes", StringComparison.Ordinal));
        Assert.IsFalse(
            json.Contains("remainingThresholdPercent", StringComparison.Ordinal));
        Assert.IsFalse(
            json.Contains("\"automationEnabled\"", StringComparison.Ordinal));
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
        Assert.IsFalse(settings.FiveHourAutomationEnabled);
        Assert.AreEqual(7, settings.FiveHourRemainingThresholdPercent);
        Assert.AreEqual(
            GuardSettings.FixedPollIntervalMinutes,
            settings.PollIntervalMinutes);
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
        Assert.IsFalse(settings.FiveHourAutomationEnabled);
        Assert.AreEqual(7, settings.FiveHourRemainingThresholdPercent);
        Assert.AreEqual(
            GuardSettings.FixedPollIntervalMinutes,
            settings.PollIntervalMinutes);
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public async Task VersionOneThroughFourMigrateToFixedOneMinutePoll(
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
        document["pollIntervalMinutes"] = 60;

        var settings = await LoadSettingsAsync(document.ToJsonString());

        Assert.AreEqual(
            GuardSettings.FixedPollIntervalMinutes,
            settings.PollIntervalMinutes);
        Assert.AreEqual(7, settings.FiveHourRemainingThresholdPercent);
        Assert.IsFalse(settings.FiveHourAutomationEnabled);
    }

    [DataTestMethod]
    [DataRow(3)]
    [DataRow(4)]
    public async Task VersionThreeAndFourMapLegacyValuesToWeeklyOnly(
        int schemaVersion)
    {
        var json = schemaVersion == 3
            ? ValidV3SettingsJson
            : ValidV4SettingsJson;
        var document = JsonNode.Parse(json)!.AsObject();
        document["remainingThresholdPercent"] = 41;
        document["automationEnabled"] = true;

        var settings = await LoadSettingsAsync(document.ToJsonString());

        Assert.AreEqual(41, settings.WeeklyRemainingThresholdPercent);
        Assert.AreEqual(7, settings.FiveHourRemainingThresholdPercent);
        Assert.IsTrue(settings.WeeklyAutomationEnabled);
        Assert.IsFalse(settings.FiveHourAutomationEnabled);
        Assert.AreEqual(
            GuardSettings.FixedPollIntervalMinutes,
            settings.PollIntervalMinutes);
    }

    [TestMethod]
    public async Task VersionSixSettingsRoundTripWithoutLegacyOrPollFields()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "settings.json");
        var store = new JsonSettingsStore(path);
        var enabled = GuardSettings.Default with
        {
            RemainingThresholdPercent = 13,
            FiveHourRemainingThresholdPercent = 29,
            AutomationEnabled = true,
            FiveHourAutomationEnabled = false,
            NotifyOnUsageReset = false,
        };

        await store.SaveAsync(enabled, CancellationToken.None);

        var json = await File.ReadAllTextAsync(path, Encoding.UTF8);
        StringAssert.Contains(json, "\"schemaVersion\": 6");
        StringAssert.Contains(
            json,
            "\"weeklyRemainingThresholdPercent\": 13");
        StringAssert.Contains(
            json,
            "\"fiveHourRemainingThresholdPercent\": 29");
        StringAssert.Contains(json, "\"weeklyAutomationEnabled\": true");
        StringAssert.Contains(json, "\"fiveHourAutomationEnabled\": false");
        StringAssert.Contains(json, "\"notifyOnUsageReset\": false");
        Assert.IsFalse(
            json.Contains("pollIntervalMinutes", StringComparison.Ordinal));
        Assert.IsFalse(
            json.Contains("\"remainingThresholdPercent\"", StringComparison.Ordinal));
        Assert.IsFalse(
            json.Contains("\"automationEnabled\"", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("triggerLimit", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("executionMode", StringComparison.Ordinal));
        Assert.AreEqual(enabled, await store.LoadAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task VersionSixRoundTripsBothNullableThresholdsAndRawToggles()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "settings.json");
        var store = new JsonSettingsStore(path);
        var nullable = GuardSettings.Default with
        {
            RemainingThresholdPercent = null,
            AutomationEnabled = true,
            FiveHourRemainingThresholdPercent = null,
            FiveHourAutomationEnabled = true,
            NotifyOnUsageReset = false,
        };

        await store.SaveAsync(nullable, CancellationToken.None);

        var json = await File.ReadAllTextAsync(path, Encoding.UTF8);
        StringAssert.Contains(
            json,
            "\"weeklyRemainingThresholdPercent\": null");
        StringAssert.Contains(
            json,
            "\"fiveHourRemainingThresholdPercent\": null");
        StringAssert.Contains(json, "\"weeklyAutomationEnabled\": true");
        StringAssert.Contains(json, "\"fiveHourAutomationEnabled\": true");
        var loaded = await store.LoadAsync(CancellationToken.None);
        Assert.AreEqual(nullable, loaded);
        Assert.IsTrue(loaded.WeeklyAutomationEnabled);
        Assert.IsTrue(loaded.FiveHourAutomationEnabled);
        Assert.IsFalse(loaded.IsAutomationEnabled(TriggerLimit.Weekly));
        Assert.IsFalse(loaded.IsAutomationEnabled(TriggerLimit.FiveHour));
        Assert.IsFalse(loaded.AnyAutomationEnabled);
    }

    [TestMethod]
    public async Task VersionFiveLoadsIndependentThresholdsAndToggles()
    {
        var settings = await LoadSettingsAsync(ValidV5SettingsJson);

        Assert.AreEqual(11, settings.WeeklyRemainingThresholdPercent);
        Assert.AreEqual(23, settings.FiveHourRemainingThresholdPercent);
        Assert.IsFalse(settings.WeeklyAutomationEnabled);
        Assert.IsTrue(settings.FiveHourAutomationEnabled);
        Assert.AreEqual(
            GuardSettings.FixedPollIntervalMinutes,
            settings.PollIntervalMinutes);
    }

    [TestMethod]
    public async Task VersionFiveMigratesToVersionSixWithoutChangingValues()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "settings.json");
        await File.WriteAllTextAsync(path, ValidV5SettingsJson);
        var store = new JsonSettingsStore(path);

        var settings = await store.LoadAsync(CancellationToken.None);
        await store.SaveAsync(settings, CancellationToken.None);

        Assert.AreEqual(11, settings.WeeklyRemainingThresholdPercent);
        Assert.AreEqual(23, settings.FiveHourRemainingThresholdPercent);
        Assert.IsFalse(settings.WeeklyAutomationEnabled);
        Assert.IsTrue(settings.FiveHourAutomationEnabled);
        var json = await File.ReadAllTextAsync(path, Encoding.UTF8);
        StringAssert.Contains(json, "\"schemaVersion\": 6");
        StringAssert.Contains(
            json,
            "\"weeklyRemainingThresholdPercent\": 11");
        StringAssert.Contains(
            json,
            "\"fiveHourRemainingThresholdPercent\": 23");
        StringAssert.Contains(json, "\"weeklyAutomationEnabled\": false");
        StringAssert.Contains(json, "\"fiveHourAutomationEnabled\": true");
        Assert.AreEqual(settings, await store.LoadAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task VersionSixLoadsUnsetFiveHourThreshold()
    {
        var settings = await LoadSettingsAsync(ValidV6SettingsJson);

        Assert.AreEqual(0, settings.WeeklyRemainingThresholdPercent);
        Assert.IsNull(settings.FiveHourRemainingThresholdPercent);
        Assert.IsTrue(settings.WeeklyAutomationEnabled);
        Assert.IsFalse(settings.FiveHourAutomationEnabled);
    }

    [TestMethod]
    public void UnsetFiveHourThresholdDisablesEffectiveAutomation()
    {
        var settings = GuardSettings.Default with
        {
            AutomationEnabled = false,
            FiveHourRemainingThresholdPercent = null,
            FiveHourAutomationEnabled = true,
        };

        Assert.IsTrue(settings.FiveHourAutomationEnabled);
        Assert.IsFalse(settings.IsAutomationEnabled(TriggerLimit.FiveHour));
        Assert.IsFalse(settings.AnyAutomationEnabled);
        Assert.IsNull(
            settings.GetRemainingThresholdPercent(TriggerLimit.FiveHour));
    }

    [TestMethod]
    public async Task VersionThreeMigratesUsageResetNotificationsEnabled()
    {
        var settings = await LoadSettingsAsync(ValidV3SettingsJson);

        Assert.IsTrue(settings.NotifyOnUsageReset);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(7)]
    [DataRow(99)]
    public void WeeklyThresholdBoundaryValuesAreValid(int threshold) =>
        JsonSettingsStore.Validate(GuardSettings.Default with
        {
            RemainingThresholdPercent = threshold,
        });

    [DataTestMethod]
    [DataRow(-1)]
    [DataRow(100)]
    [DataRow(101)]
    public void WeeklyThresholdOutsideRangeIsRejected(int threshold)
    {
        var exception = Assert.ThrowsException<SettingsException>(() =>
            JsonSettingsStore.Validate(GuardSettings.Default with
            {
                RemainingThresholdPercent = threshold,
            }));
        Assert.AreEqual("threshold_out_of_range", exception.ReasonCode);
    }

    [DataTestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(7)]
    [DataRow(99)]
    public void FiveHourThresholdBoundaryValuesAreValid(int threshold) =>
        JsonSettingsStore.Validate(GuardSettings.Default with
        {
            FiveHourRemainingThresholdPercent = threshold,
        });

    [TestMethod]
    public void UnsetFiveHourThresholdIsValid() =>
        JsonSettingsStore.Validate(GuardSettings.Default with
        {
            FiveHourRemainingThresholdPercent = null,
        });

    [DataTestMethod]
    [DataRow(-1)]
    [DataRow(100)]
    [DataRow(101)]
    public void FiveHourThresholdOutsideRangeIsRejected(int threshold)
    {
        var exception = Assert.ThrowsException<SettingsException>(() =>
            JsonSettingsStore.Validate(GuardSettings.Default with
            {
                FiveHourRemainingThresholdPercent = threshold,
            }));
        Assert.AreEqual(
            "five_hour_threshold_out_of_range",
            exception.ReasonCode);
    }

    [DataTestMethod]
    [DataRow(
        "weeklyRemainingThresholdPercent",
        "threshold_out_of_range")]
    [DataRow(
        "fiveHourRemainingThresholdPercent",
        "five_hour_threshold_out_of_range")]
    public async Task VersionFiveDoesNotMigrateOutOfRangeThresholds(
        string propertyName,
        string expectedReason)
    {
        var document = JsonNode.Parse(ValidV5SettingsJson)!.AsObject();
        document[propertyName] = 100;

        var exception = await LoadInvalidSettingsAsync(
            document.ToJsonString());

        Assert.AreEqual(expectedReason, exception.ReasonCode);
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

    [DataTestMethod]
    [DataRow("schemaVersion")]
    [DataRow("weeklyRemainingThresholdPercent")]
    [DataRow("fiveHourRemainingThresholdPercent")]
    [DataRow("uiLanguage")]
    [DataRow("startWithWindows")]
    [DataRow("codexExecutablePath")]
    [DataRow("weeklyAutomationEnabled")]
    [DataRow("fiveHourAutomationEnabled")]
    [DataRow("notifyOnUsageReset")]
    public async Task MissingVersionFiveFieldFailsClosed(string propertyName)
    {
        var document = JsonNode.Parse(ValidV5SettingsJson)!.AsObject();
        Assert.IsTrue(document.Remove(propertyName));

        var exception = await LoadInvalidSettingsAsync(document.ToJsonString());

        Assert.AreEqual("settings_invalid_json", exception.ReasonCode);
    }

    [DataTestMethod]
    [DataRow("schemaVersion")]
    [DataRow("weeklyRemainingThresholdPercent")]
    [DataRow("fiveHourRemainingThresholdPercent")]
    [DataRow("uiLanguage")]
    [DataRow("startWithWindows")]
    [DataRow("codexExecutablePath")]
    [DataRow("weeklyAutomationEnabled")]
    [DataRow("fiveHourAutomationEnabled")]
    [DataRow("notifyOnUsageReset")]
    public async Task MissingVersionSixFieldFailsClosed(string propertyName)
    {
        var document = JsonNode.Parse(ValidV6SettingsJson)!.AsObject();
        Assert.IsTrue(document.Remove(propertyName));

        var exception = await LoadInvalidSettingsAsync(document.ToJsonString());

        Assert.AreEqual("settings_invalid_json", exception.ReasonCode);
    }

    [DataTestMethod]
    [DataRow("pollIntervalMinutes", "1")]
    [DataRow("remainingThresholdPercent", "7")]
    [DataRow("automationEnabled", "false")]
    [DataRow("triggerLimit", "\"weekly\"")]
    [DataRow("executionMode", "\"live\"")]
    [DataRow("unexpected", "true")]
    public async Task VersionFiveRejectsLegacyPollAndUnknownProperties(
        string propertyName,
        string jsonValue)
    {
        var document = JsonNode.Parse(ValidV5SettingsJson)!.AsObject();
        document[propertyName] = JsonNode.Parse(jsonValue);

        var exception = await LoadInvalidSettingsAsync(document.ToJsonString());

        Assert.AreEqual("settings_invalid_json", exception.ReasonCode);
    }

    [DataTestMethod]
    [DataRow("pollIntervalMinutes", "1")]
    [DataRow("remainingThresholdPercent", "7")]
    [DataRow("automationEnabled", "false")]
    [DataRow("triggerLimit", "\"weekly\"")]
    [DataRow("executionMode", "\"live\"")]
    [DataRow("unexpected", "true")]
    public async Task VersionSixRejectsLegacyPollAndUnknownProperties(
        string propertyName,
        string jsonValue)
    {
        var document = JsonNode.Parse(ValidV6SettingsJson)!.AsObject();
        document[propertyName] = JsonNode.Parse(jsonValue);

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
