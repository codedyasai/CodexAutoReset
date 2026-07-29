using System.Text.Json;
using CodexAutoReset.Core;

namespace CodexAutoReset.AppServer;

public static class AppServerProtocolParser
{
    private const int MaximumBuckets = 100;
    private const int MaximumCreditDetails = 1_000;
    private const int MaximumUserAgentLength = 1_024;
    private const string ExpectedClientName = "codex_auto_reset";
    public static string AuditedConsumeSchemaVersion { get; } = "0.144.5";

    private static readonly string[] ExpectedServerProducts =
        ["Codex Desktop", ExpectedClientName];

    private static readonly string[] ResponseResultProperties = ["id", "result"];
    private static readonly string[] ResponseErrorProperties = ["id", "error"];
    private static readonly string[] ErrorProperties = ["code", "message", "data"];
    private static readonly string[] InitializeProperties =
        ["codexHome", "platformFamily", "platformOs", "userAgent"];
    private static readonly string[] RateLimitsResponseProperties =
        ["rateLimits", "rateLimitsByLimitId", "rateLimitResetCredits"];
    private static readonly string[] SnapshotProperties =
    [
        "credits",
        "individualLimit",
        "limitId",
        "limitName",
        "planType",
        "primary",
        "rateLimitReachedType",
        "secondary",
    ];
    private static readonly string[] WindowProperties =
        ["resetsAt", "usedPercent", "windowDurationMins"];
    private static readonly string[] CreditSummaryProperties =
        ["availableCount", "credits"];
    private static readonly string[] CreditProperties =
        ["description", "expiresAt", "grantedAt", "id", "resetType", "status", "title"];
    private static readonly string[] CreditsSnapshotProperties =
        ["balance", "hasCredits", "unlimited"];
    private static readonly string[] SpendControlProperties =
        ["limit", "remainingPercent", "resetsAt", "used"];
    private static readonly string[] ConsumeResponseProperties = ["outcome"];

    private static readonly HashSet<string> PlanTypes = new(StringComparer.Ordinal)
    {
        "free",
        "go",
        "plus",
        "pro",
        "prolite",
        "team",
        "self_serve_business_usage_based",
        "business",
        "enterprise_cbp_usage_based",
        "enterprise",
        "edu",
        "unknown",
    };

    private static readonly HashSet<string> RateLimitReachedTypes = new(StringComparer.Ordinal)
    {
        "rate_limit_reached",
        "workspace_owner_credits_depleted",
        "workspace_member_credits_depleted",
        "workspace_owner_usage_limit_reached",
        "workspace_member_usage_limit_reached",
    };

    internal static bool TryParseResponseResult(
        JsonElement response,
        long expectedId,
        out JsonElement result)
    {
        result = default;
        EnsureNoDuplicateProperties(response);
        if (!MatchesId(response, expectedId))
        {
            return false;
        }

        var hasResult = response.TryGetProperty("result", out var resultElement);
        var hasError = response.TryGetProperty("error", out var errorElement);
        if (hasResult == hasError)
        {
            throw InvalidResponse();
        }

        if (hasError)
        {
            ValidateObjectShape(
                response,
                ResponseErrorProperties,
                "id",
                "error");
            ValidateObjectShape(errorElement, ErrorProperties, "code", "message");

            if (!errorElement.TryGetProperty("code", out var codeElement)
                || !codeElement.TryGetInt64(out var parsedLongCode)
                || !errorElement.TryGetProperty("message", out var messageElement)
                || messageElement.ValueKind != JsonValueKind.String)
            {
                throw InvalidResponse();
            }

            int? remoteCode = null;
            if (parsedLongCode is >= int.MinValue and <= int.MaxValue)
            {
                remoteCode = (int)parsedLongCode;
            }

            throw new AppServerException(
                AppServerFailureCategory.RemoteError,
                remoteCode);
        }

        ValidateObjectShape(response, ResponseResultProperties, "id", "result");
        result = resultElement.Clone();
        return true;
    }

    public static bool ValidateInitializeResult(
        JsonElement result,
        string expectedClientVersion)
    {
        if (string.IsNullOrWhiteSpace(expectedClientVersion)
            || !IsThreePartVersion(expectedClientVersion))
        {
            throw new ArgumentException(
                "client_version_invalid",
                nameof(expectedClientVersion));
        }

        EnsureNoDuplicateProperties(result);
        ValidateObjectShape(result, InitializeProperties, InitializeProperties);
        RequireString(result, "codexHome");
        RequireString(result, "platformFamily");
        RequireString(result, "platformOs");
        var userAgent = RequireString(result, "userAgent");
        if (string.IsNullOrWhiteSpace(userAgent)
            || userAgent.Length > MaximumUserAgentLength
            || userAgent.Any(char.IsControl))
        {
            throw InvalidResponse();
        }

        var expectedClientSuffix =
            $" ({ExpectedClientName}; {expectedClientVersion})";
        if (!userAgent.EndsWith(expectedClientSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var product in ExpectedServerProducts)
        {
            var auditedServerPrefix =
                $"{product}/{AuditedConsumeSchemaVersion} ";
            if (!userAgent.StartsWith(
                    auditedServerPrefix,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var middleLength = userAgent.Length
                - auditedServerPrefix.Length
                - expectedClientSuffix.Length;
            return middleLength > 0
                && !string.IsNullOrWhiteSpace(
                    userAgent.Substring(
                        auditedServerPrefix.Length,
                        middleLength));
        }

        return false;
    }

    private static bool IsThreePartVersion(string value)
    {
        var parts = value.Split('.');
        return parts.Length == 3
            && parts.All(part =>
                part.Length > 0
                && part.All(char.IsAsciiDigit));
    }

    public static AccountRateLimits ParseRateLimits(
        JsonElement result,
        DateTimeOffset observedAt,
        bool consumeSchemaCompatible = true)
    {
        EnsureNoDuplicateProperties(result);
        ValidateObjectShape(
            result,
            RateLimitsResponseProperties,
            "rateLimits");
        if (!result.TryGetProperty("rateLimits", out var legacyElement)
            || legacyElement.ValueKind != JsonValueKind.Object)
        {
            throw InvalidResponse();
        }

        var legacy = ParseSnapshot(legacyElement);
        var byLimitId = ParseBuckets(result);
        var resetCredits = ParseResetCredits(result);

        return new AccountRateLimits(
            legacy,
            byLimitId,
            resetCredits,
            observedAt,
            consumeSchemaCompatible);
    }

    public static ConsumeResetCreditResult ParseConsumeResetCredit(
        JsonElement result)
    {
        EnsureNoDuplicateProperties(result);
        ValidateObjectShape(
            result,
            ConsumeResponseProperties,
            "outcome");
        if (!result.TryGetProperty("outcome", out var outcomeElement)
            || outcomeElement.ValueKind != JsonValueKind.String)
        {
            throw InvalidResponse();
        }

        var outcome = outcomeElement.GetString() switch
        {
            "reset" => ConsumeResetCreditOutcome.Reset,
            "nothingToReset" => ConsumeResetCreditOutcome.NothingToReset,
            "noCredit" => ConsumeResetCreditOutcome.NoCredit,
            "alreadyRedeemed" => ConsumeResetCreditOutcome.AlreadyRedeemed,
            _ => throw InvalidResponse(),
        };

        return new ConsumeResetCreditResult(outcome);
    }

    private static IReadOnlyDictionary<string, RateLimitSnapshot>? ParseBuckets(
        JsonElement result)
    {
        if (!result.TryGetProperty("rateLimitsByLimitId", out var bucketsElement)
            || bucketsElement.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (bucketsElement.ValueKind != JsonValueKind.Object)
        {
            throw InvalidResponse();
        }

        var buckets = new Dictionary<string, RateLimitSnapshot>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var property in bucketsElement.EnumerateObject())
        {
            if (buckets.Count >= MaximumBuckets
                || string.IsNullOrWhiteSpace(property.Name)
                || property.Value.ValueKind != JsonValueKind.Object)
            {
                throw InvalidResponse();
            }

            if (!buckets.TryAdd(property.Name, ParseSnapshot(property.Value)))
            {
                // Keys that differ only by casing make the Codex bucket ambiguous.
                throw InvalidResponse();
            }
        }

        return buckets;
    }

    private static ResetCreditSummary? ParseResetCredits(JsonElement result)
    {
        if (!result.TryGetProperty("rateLimitResetCredits", out var summaryElement)
            || summaryElement.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        ValidateObjectShape(
            summaryElement,
            CreditSummaryProperties,
            "availableCount");
        if (!summaryElement.TryGetProperty("availableCount", out var countElement)
            || !countElement.TryGetInt64(out var availableCount))
        {
            throw InvalidResponse();
        }

        if (!summaryElement.TryGetProperty("credits", out var creditsElement)
            || creditsElement.ValueKind == JsonValueKind.Null)
        {
            return new ResetCreditSummary(availableCount, null);
        }

        if (creditsElement.ValueKind != JsonValueKind.Array)
        {
            throw InvalidResponse();
        }

        var credits = new List<ResetCredit>();
        var creditIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var creditElement in creditsElement.EnumerateArray())
        {
            if (credits.Count >= MaximumCreditDetails
                || creditElement.ValueKind != JsonValueKind.Object)
            {
                throw InvalidResponse();
            }

            var credit = ParseCredit(creditElement);
            if (!creditIds.Add(credit.Id))
            {
                throw InvalidResponse();
            }

            credits.Add(credit);
        }

        if (availableCount >= 0 && credits.Count > availableCount)
        {
            throw InvalidResponse();
        }

        return new ResetCreditSummary(availableCount, credits);
    }

    private static ResetCredit ParseCredit(JsonElement element)
    {
        ValidateObjectShape(
            element,
            CreditProperties,
            "grantedAt",
            "id",
            "resetType",
            "status");
        var id = RequireString(element, "id");
        var resetType = RequireString(element, "resetType");
        var status = RequireString(element, "status");

        if (resetType is not ("codexRateLimits" or "unknown")
            || status is not ("available" or "redeeming" or "redeemed" or "unknown"))
        {
            throw InvalidResponse();
        }

        if (!element.TryGetProperty("grantedAt", out var grantedAtElement)
            || !grantedAtElement.TryGetInt64(out var grantedAt))
        {
            throw InvalidResponse();
        }

        return new ResetCredit(
            id,
            resetType,
            status,
            grantedAt,
            OptionalInt64(element, "expiresAt"),
            OptionalString(element, "title"),
            OptionalString(element, "description"));
    }

    private static RateLimitSnapshot ParseSnapshot(JsonElement element)
    {
        ValidateObjectShape(element, SnapshotProperties);
        ValidateCreditsSnapshot(element);
        ValidateSpendControlLimit(element);
        ValidateOptionalEnum(element, "planType", PlanTypes);
        ValidateOptionalEnum(
            element,
            "rateLimitReachedType",
            RateLimitReachedTypes);

        return new RateLimitSnapshot(
            OptionalString(element, "limitId"),
            OptionalString(element, "limitName"),
            OptionalWindow(element, "primary"),
            OptionalWindow(element, "secondary"));
    }

    private static RateLimitWindow? OptionalWindow(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var windowElement)
            || windowElement.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        ValidateObjectShape(windowElement, WindowProperties, "usedPercent");
        if (!windowElement.TryGetProperty("usedPercent", out var usedElement)
            || usedElement.ValueKind != JsonValueKind.Number
            || !usedElement.TryGetInt32(out var usedPercent))
        {
            throw InvalidResponse();
        }

        return new RateLimitWindow(
            usedPercent,
            OptionalInt64(windowElement, "windowDurationMins"),
            OptionalInt64(windowElement, "resetsAt"));
    }

    private static void ValidateCreditsSnapshot(JsonElement snapshot)
    {
        if (!snapshot.TryGetProperty("credits", out var credits)
            || credits.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        ValidateObjectShape(
            credits,
            CreditsSnapshotProperties,
            "hasCredits",
            "unlimited");
        RequireBoolean(credits, "hasCredits");
        RequireBoolean(credits, "unlimited");
        OptionalString(credits, "balance");
    }

    private static void ValidateSpendControlLimit(JsonElement snapshot)
    {
        if (!snapshot.TryGetProperty("individualLimit", out var individualLimit)
            || individualLimit.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        ValidateObjectShape(
            individualLimit,
            SpendControlProperties,
            SpendControlProperties);
        RequireString(individualLimit, "limit");
        RequireString(individualLimit, "used");
        if (!individualLimit.TryGetProperty(
                "remainingPercent",
                out var remainingPercent)
            || !remainingPercent.TryGetInt32(out _)
            || !individualLimit.TryGetProperty("resetsAt", out var resetsAt)
            || !resetsAt.TryGetInt64(out _))
        {
            throw InvalidResponse();
        }
    }

    private static void ValidateOptionalEnum(
        JsonElement element,
        string propertyName,
        IReadOnlySet<string> allowedValues)
    {
        if (!element.TryGetProperty(propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.String
            || value.GetString() is not { } parsed
            || !allowedValues.Contains(parsed))
        {
            throw InvalidResponse();
        }
    }

    private static void RequireBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw InvalidResponse();
        }
    }

    private static string RequireString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            throw InvalidResponse();
        }

        return value.GetString() ?? throw InvalidResponse();
    }

    private static string? OptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw InvalidResponse();
        }

        return value.GetString();
    }

    private static long? OptionalInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (!value.TryGetInt64(out var parsed))
        {
            throw InvalidResponse();
        }

        return parsed;
    }

    private static bool MatchesId(JsonElement root, long expectedId)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("id", out var idElement))
        {
            return false;
        }

        return (idElement.TryGetInt64(out var numericId) && numericId == expectedId)
            || (idElement.ValueKind == JsonValueKind.String
                && long.TryParse(
                    idElement.GetString(),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var stringId)
                && stringId == expectedId);
    }

    private static void ValidateObjectShape(
        JsonElement element,
        IReadOnlyCollection<string> allowedProperties,
        params string[] requiredProperties)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw InvalidResponse();
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name)
                || !allowedProperties.Contains(property.Name, StringComparer.Ordinal))
            {
                throw InvalidResponse();
            }
        }

        if (requiredProperties.Any(required => !seen.Contains(required)))
        {
            throw InvalidResponse();
        }
    }

    internal static void EnsureNoDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw InvalidResponse();
                }

                EnsureNoDuplicateProperties(property.Value);
            }

            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                EnsureNoDuplicateProperties(item);
            }
        }
    }

    private static AppServerException InvalidResponse() =>
        new(AppServerFailureCategory.InvalidResponse);
}
