using System.Text.Json;
using CodexAutoReset.AppServer;
using CodexAutoReset.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexAutoReset.Tests;

[TestClass]
public sealed class ProtocolParserTests
{
    [TestMethod]
    public void LiveConsumeSchemaIsPinnedToGeneratedCodexVersion()
    {
        Assert.AreEqual(
            "0.144.5",
            AppServerProtocolParser.AuditedConsumeSchemaVersion);
    }

    [TestMethod]
    public void StableSchemaShapedResponseParses()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "rateLimits": {
                "limitId": "codex",
                "primary": {
                  "usedPercent": 25,
                  "windowDurationMins": 300,
                  "resetsAt": 2000000000
                },
                "secondary": {
                  "usedPercent": 93,
                  "windowDurationMins": 10080,
                  "resetsAt": 2000000001
                }
              },
              "rateLimitsByLimitId": {
                "codex": {
                  "limitId": "codex",
                  "primary": {
                    "usedPercent": 25,
                    "windowDurationMins": 300,
                    "resetsAt": 2000000000
                  }
                }
              },
              "rateLimitResetCredits": {
                "availableCount": 2,
                "credits": [
                  {
                    "id": "opaque-credit",
                    "resetType": "codexRateLimits",
                    "status": "available",
                    "grantedAt": 1900000000,
                    "expiresAt": 2100000000,
                    "title": "Full reset",
                    "description": "Ready"
                  }
                ]
              }
            }
            """);

        var parsed = AppServerProtocolParser.ParseRateLimits(
            document.RootElement,
            DateTimeOffset.UtcNow);

        Assert.AreEqual("codex", parsed.LegacyRateLimits.LimitId);
        Assert.AreEqual(25d, parsed.LegacyRateLimits.Primary!.UsedPercent);
        Assert.AreEqual(2L, parsed.ResetCredits!.AvailableCount);
        Assert.AreEqual("opaque-credit", parsed.ResetCredits.Credits![0].Id);
        Assert.IsTrue(parsed.RateLimitsByLimitId!.ContainsKey("codex"));
    }

    [TestMethod]
    public void RateLimitsExposeConsumeSchemaCompatibility()
    {
        using var document = JsonDocument.Parse("{\"rateLimits\":{}}");

        var compatibleByDefault = AppServerProtocolParser.ParseRateLimits(
            document.RootElement,
            DateTimeOffset.UtcNow);
        var incompatible = AppServerProtocolParser.ParseRateLimits(
            document.RootElement,
            DateTimeOffset.UtcNow,
            consumeSchemaCompatible: false);

        Assert.IsTrue(compatibleByDefault.ConsumeSchemaCompatible);
        Assert.IsFalse(incompatible.ConsumeSchemaCompatible);
    }

    [TestMethod]
    public void StableUnusedSnapshotFieldsAreValidatedAndAccepted()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "rateLimits": {
                "limitId": "codex",
                "limitName": "Codex",
                "planType": "plus",
                "rateLimitReachedType": "rate_limit_reached",
                "credits": {
                  "balance": "12.34",
                  "hasCredits": true,
                  "unlimited": false
                },
                "individualLimit": {
                  "limit": "100.00",
                  "remainingPercent": 75,
                  "resetsAt": 2000000000,
                  "used": "25.00"
                }
              }
            }
            """);

        var parsed = AppServerProtocolParser.ParseRateLimits(
            document.RootElement,
            DateTimeOffset.UtcNow);

        Assert.AreEqual("codex", parsed.LegacyRateLimits.LimitId);
    }

    [DataTestMethod]
    [DataRow("{\"rateLimits\":{},\"unexpected\":true}")]
    [DataRow("{\"rateLimits\":{\"unexpected\":true}}")]
    [DataRow("{\"rateLimits\":{\"planType\":\"future_plan\"}}")]
    [DataRow("{\"rateLimits\":{\"rateLimitReachedType\":7}}")]
    [DataRow("{\"rateLimits\":{\"credits\":{\"hasCredits\":true,\"unlimited\":\"no\"}}}")]
    [DataRow("{\"rateLimits\":{\"individualLimit\":{\"limit\":\"1\",\"remainingPercent\":1.5,\"resetsAt\":2,\"used\":\"0\"}}}")]
    [DataRow("{\"rateLimits\":{},\"rateLimitResetCredits\":{\"availableCount\":1,\"unexpected\":true}}")]
    [DataRow("{\"rateLimits\":{},\"rateLimitResetCredits\":{\"availableCount\":1,\"credits\":[{\"id\":\"id\",\"resetType\":\"future\",\"status\":\"available\",\"grantedAt\":1}]}}")]
    public void UnknownOrMalformedStableFieldsFailClosed(string json)
    {
        using var document = JsonDocument.Parse(json);

        var exception = Assert.ThrowsException<AppServerException>(() =>
            AppServerProtocolParser.ParseRateLimits(
                document.RootElement,
                DateTimeOffset.UtcNow));

        Assert.AreEqual(AppServerFailureCategory.InvalidResponse, exception.Category);
    }

    [DataTestMethod]
    [DataRow("{\"rateLimits\":{},\"rateLimits\":{}}")]
    [DataRow("{\"rateLimits\":{\"primary\":{\"usedPercent\":1,\"usedPercent\":2}}}")]
    public void DuplicateResponseMembersFailClosed(string json)
    {
        using var document = JsonDocument.Parse(json);

        var exception = Assert.ThrowsException<AppServerException>(() =>
            AppServerProtocolParser.ParseRateLimits(
                document.RootElement,
                DateTimeOffset.UtcNow));

        Assert.AreEqual(AppServerFailureCategory.InvalidResponse, exception.Category);
    }

    [TestMethod]
    public void DuplicateCreditIdentifiersFailClosed()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "rateLimits": {},
              "rateLimitResetCredits": {
                "availableCount": 2,
                "credits": [
                  {
                    "id": "same-credit",
                    "resetType": "codexRateLimits",
                    "status": "available",
                    "grantedAt": 1
                  },
                  {
                    "id": "same-credit",
                    "resetType": "codexRateLimits",
                    "status": "available",
                    "grantedAt": 2
                  }
                ]
              }
            }
            """);

        var exception = Assert.ThrowsException<AppServerException>(() =>
            AppServerProtocolParser.ParseRateLimits(
                document.RootElement,
                DateTimeOffset.UtcNow));

        Assert.AreEqual(AppServerFailureCategory.InvalidResponse, exception.Category);
    }

    [TestMethod]
    public void CreditDetailsCannotExceedAvailableCount()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "rateLimits": {},
              "rateLimitResetCredits": {
                "availableCount": 0,
                "credits": [
                  {
                    "id": "credit",
                    "resetType": "codexRateLimits",
                    "status": "available",
                    "grantedAt": 1
                  }
                ]
              }
            }
            """);

        var exception = Assert.ThrowsException<AppServerException>(() =>
            AppServerProtocolParser.ParseRateLimits(
                document.RootElement,
                DateTimeOffset.UtcNow));

        Assert.AreEqual(AppServerFailureCategory.InvalidResponse, exception.Category);
    }

    [TestMethod]
    public void NullCreditDetailsRemainUnknownRatherThanEmpty()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "rateLimits": {},
              "rateLimitResetCredits": {
                "availableCount": 4,
                "credits": null
              }
            }
            """);

        var parsed = AppServerProtocolParser.ParseRateLimits(
            document.RootElement,
            DateTimeOffset.UtcNow);

        Assert.AreEqual(4L, parsed.ResetCredits!.AvailableCount);
        Assert.IsNull(parsed.ResetCredits.Credits);
    }

    [TestMethod]
    public void MissingRequiredRateLimitsFailsClosed()
    {
        using var document = JsonDocument.Parse("{\"rateLimitResetCredits\": null}");

        var exception = Assert.ThrowsException<AppServerException>(() =>
            AppServerProtocolParser.ParseRateLimits(
                document.RootElement,
                DateTimeOffset.UtcNow));

        Assert.AreEqual(AppServerFailureCategory.InvalidResponse, exception.Category);
    }

    [DataTestMethod]
    [DataRow("25.5")]
    [DataRow("25.0")]
    [DataRow("2147483648")]
    [DataRow("\"25\"")]
    public void UsedPercentRequiresStableSchemaInt32(string usedPercentJson)
    {
        using var document = JsonDocument.Parse(
            $$"""
            {
              "rateLimits": {
                "primary": {
                  "usedPercent": {{usedPercentJson}}
                }
              }
            }
            """);

        var exception = Assert.ThrowsException<AppServerException>(() =>
            AppServerProtocolParser.ParseRateLimits(
                document.RootElement,
                DateTimeOffset.UtcNow));

        Assert.AreEqual(AppServerFailureCategory.InvalidResponse, exception.Category);
    }

    [TestMethod]
    public void CaseVariantDuplicateBucketNamesFailClosed()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "rateLimits": {},
              "rateLimitsByLimitId": {
                "codex": {},
                "CODEX": {}
              }
            }
            """);

        var exception = Assert.ThrowsException<AppServerException>(() =>
            AppServerProtocolParser.ParseRateLimits(
                document.RootElement,
                DateTimeOffset.UtcNow));

        Assert.AreEqual(AppServerFailureCategory.InvalidResponse, exception.Category);
    }

    [TestMethod]
    public void InitializeResponseRequiresAllStableFields()
    {
        using var valid = JsonDocument.Parse(
            """
            {
              "codexHome": "private-path-not-logged",
              "platformFamily": "windows",
              "platformOs": "windows",
              "userAgent": "Codex Desktop/0.144.5 (Windows 10.0.26200; x86_64) unknown (codex_auto_reset; 0.1.0)"
            }
            """);
        Assert.IsTrue(
            AppServerProtocolParser.ValidateInitializeResult(
                valid.RootElement,
                "0.1.0"));

        using var invalid = JsonDocument.Parse(
            "{\"userAgent\":\"Codex\"}");
        var exception = Assert.ThrowsException<AppServerException>(() =>
            AppServerProtocolParser.ValidateInitializeResult(
                invalid.RootElement,
                "0.1.0"));
        Assert.AreEqual(AppServerFailureCategory.InvalidResponse, exception.Category);
    }

    [TestMethod]
    public void InitializeResponsePinsLiveConsumeToAuditedServerVersion()
    {
        using var future = JsonDocument.Parse(
            """
            {
              "codexHome": "private-path-not-logged",
              "platformFamily": "windows",
              "platformOs": "windows",
              "userAgent": "Codex Desktop/0.145.0 (Windows 10.0.26200; x86_64) unknown (codex_auto_reset; 0.1.0)"
            }
            """);

        Assert.IsFalse(
            AppServerProtocolParser.ValidateInitializeResult(
                future.RootElement,
                "0.1.0"));
    }

    [DataTestMethod]
    [DataRow("Codex Desktop/0.144.50 (Windows) unknown (codex_auto_reset; 0.1.0)")]
    [DataRow("xCodex Desktop/0.144.5 (Windows) unknown (codex_auto_reset; 0.1.0)")]
    [DataRow("Codex Desktop/0.144.5 (codex_auto_reset; 0.1.0)")]
    [DataRow("Codex Desktop/0.144.5     (codex_auto_reset; 0.1.0)")]
    [DataRow("Codex Desktop/0.144.5 (Windows) unknown (codex_auto_reset; 0.1.0)x")]
    public void InitializeResponseRequiresExactAuditedConsumeMarker(
        string userAgent)
    {
        using var response = CreateInitializeResponse(userAgent);

        Assert.IsFalse(
            AppServerProtocolParser.ValidateInitializeResult(
                response.RootElement,
                "0.1.0"));
    }

    [DataTestMethod]
    [DataRow("codex-cli/0.144.5")]
    [DataRow("codex_auto_reset/0.144.5")]
    [DataRow("Codex Desktop/0.144.5")]
    [DataRow("Codex Desktop/0.144.5 (Windows) unknown (other_client; 0.1.0)")]
    [DataRow("Codex Desktop/0.144.5 (Windows) unknown (codex_auto_reset; 0.2.0)")]
    public void InitializeResponseKeepsUnexpectedUserAgentReadOnly(
        string userAgent)
    {
        using var response = CreateInitializeResponse(userAgent);

        Assert.IsFalse(
            AppServerProtocolParser.ValidateInitializeResult(
                response.RootElement,
                "0.1.0"));
    }

    [TestMethod]
    public void InitializeResponseRejectsUnsafeUserAgent()
    {
        var json = JsonSerializer.Serialize(new
        {
            codexHome = "private-path-not-logged",
            platformFamily = "windows",
            platformOs = "windows",
            userAgent = "Codex Desktop/0.144.5 (codex_auto_reset; 0.1.0)\u0001",
        });
        using var response = JsonDocument.Parse(json);

        var exception = Assert.ThrowsException<AppServerException>(() =>
            AppServerProtocolParser.ValidateInitializeResult(
                response.RootElement,
                "0.1.0"));
        Assert.AreEqual(AppServerFailureCategory.InvalidResponse, exception.Category);
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public void InitializeResponseRejectsBlankUserAgent(string userAgent)
    {
        using var response = CreateInitializeResponse(userAgent);

        var exception = Assert.ThrowsException<AppServerException>(() =>
            AppServerProtocolParser.ValidateInitializeResult(
                response.RootElement,
                "0.1.0"));
        Assert.AreEqual(AppServerFailureCategory.InvalidResponse, exception.Category);
    }

    [TestMethod]
    public void InitializeResponseRejectsOversizedUserAgent()
    {
        using var response = CreateInitializeResponse(new string('a', 1_025));

        var exception = Assert.ThrowsException<AppServerException>(() =>
            AppServerProtocolParser.ValidateInitializeResult(
                response.RootElement,
                "0.1.0"));
        Assert.AreEqual(AppServerFailureCategory.InvalidResponse, exception.Category);
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("0.1")]
    [DataRow("0.1.0.0")]
    [DataRow("0.1.x")]
    public void InitializeResponseRejectsInvalidExpectedClientVersion(
        string clientVersion)
    {
        using var response = CreateInitializeResponse(
            "Codex Desktop/0.144.5 (Windows) unknown (codex_auto_reset; 0.1.0)");

        Assert.ThrowsException<ArgumentException>(() =>
            AppServerProtocolParser.ValidateInitializeResult(
                response.RootElement,
                clientVersion));
    }

    private static JsonDocument CreateInitializeResponse(string userAgent)
    {
        return JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            codexHome = "private-path-not-logged",
            platformFamily = "windows",
            platformOs = "windows",
            userAgent,
        }));
    }

    [TestMethod]
    public void JsonRpcResultAndErrorAreMutuallyExclusive()
    {
        using var response = JsonDocument.Parse(
            """
            {
              "id": 7,
              "result": { "accepted": true },
              "error": null
            }
            """);

        var exception = Assert.ThrowsException<AppServerException>(() =>
            AppServerProtocolParser.TryParseResponseResult(
                response.RootElement,
                7,
                out _));

        Assert.AreEqual(AppServerFailureCategory.InvalidResponse, exception.Category);
    }

    [TestMethod]
    public void JsonRpcErrorObjectCannotBeAcceptedAsResult()
    {
        using var response = JsonDocument.Parse(
            """
            {
              "id": 7,
              "error": { "code": -32603, "message": "remote text" }
            }
            """);

        var exception = Assert.ThrowsException<AppServerException>(() =>
            AppServerProtocolParser.TryParseResponseResult(
                response.RootElement,
                7,
                out _));

        Assert.AreEqual(AppServerFailureCategory.RemoteError, exception.Category);
        Assert.AreEqual(-32603, exception.RemoteCode);
    }

    [DataTestMethod]
    [DataRow("reset", nameof(ConsumeResetCreditOutcome.Reset))]
    [DataRow("nothingToReset", nameof(ConsumeResetCreditOutcome.NothingToReset))]
    [DataRow("noCredit", nameof(ConsumeResetCreditOutcome.NoCredit))]
    [DataRow("alreadyRedeemed", nameof(ConsumeResetCreditOutcome.AlreadyRedeemed))]
    public void StableConsumeOutcomesParse(
        string wireOutcome,
        string expectedOutcomeName)
    {
        using var document = JsonDocument.Parse(
            $$"""{"outcome":"{{wireOutcome}}"}""");

        var parsed = AppServerProtocolParser.ParseConsumeResetCredit(
            document.RootElement);

        Assert.AreEqual(
            Enum.Parse<ConsumeResetCreditOutcome>(expectedOutcomeName),
            parsed.Outcome);
    }

    [DataTestMethod]
    [DataRow("{}")]
    [DataRow("{\"outcome\":null}")]
    [DataRow("{\"outcome\":1}")]
    [DataRow("{\"outcome\":true}")]
    [DataRow("{\"outcome\":{}}")]
    [DataRow("{\"outcome\":\"RESET\"}")]
    [DataRow("{\"outcome\":\"futureOutcome\"}")]
    [DataRow("[]")]
    [DataRow("null")]
    public void MissingWrongOrUnknownConsumeOutcomeFailsClosed(string json)
    {
        using var document = JsonDocument.Parse(json);

        var exception = Assert.ThrowsException<AppServerException>(() =>
            AppServerProtocolParser.ParseConsumeResetCredit(document.RootElement));

        Assert.AreEqual(AppServerFailureCategory.InvalidResponse, exception.Category);
    }

    [DataTestMethod]
    [DataRow("{\"outcome\":\"reset\",\"unexpected\":true}")]
    [DataRow("{\"outcome\":\"reset\",\"outcome\":\"noCredit\"}")]
    public void ConsumeOutcomeShapeMustBeExact(string json)
    {
        using var document = JsonDocument.Parse(json);

        var exception = Assert.ThrowsException<AppServerException>(() =>
            AppServerProtocolParser.ParseConsumeResetCredit(document.RootElement));

        Assert.AreEqual(AppServerFailureCategory.InvalidResponse, exception.Category);
    }

    [DataTestMethod]
    [DataRow("{\"id\":7,\"result\":{},\"unexpected\":true}")]
    [DataRow("{\"id\":7,\"id\":7,\"result\":{}}")]
    [DataRow("{\"id\":7,\"error\":{\"code\":-1}}")]
    [DataRow("{\"id\":7,\"error\":{\"code\":-1,\"message\":7}}")]
    public void JsonRpcEnvelopeShapeMismatchFailsClosed(string json)
    {
        using var document = JsonDocument.Parse(json);

        var exception = Assert.ThrowsException<AppServerException>(() =>
            AppServerProtocolParser.TryParseResponseResult(
                document.RootElement,
                7,
                out _));

        Assert.AreEqual(AppServerFailureCategory.InvalidResponse, exception.Category);
    }

    [TestMethod]
    public void OutboundWriterAllowlistIsExact()
    {
        string[] allowedMessages =
        [
            "{\"method\":\"initialize\"}",
            "{\"method\":\"initialized\"}",
            "{\"method\":\"account/rateLimits/read\"}",
            """
            {
              "method": "account/rateLimitResetCredit/consume",
              "id": 1,
              "params": {
                "idempotencyKey": "logical-attempt-1",
                "creditId": "opaque-credit"
              }
            }
            """,
        ];

        foreach (var json in allowedMessages)
        {
            using var message = JsonDocument.Parse(json);
            CodexAppServerClient.ValidateOutboundMessage(message.RootElement);
        }

        string[] rejectedMethods =
        [
            "Initialize",
            "initialized ",
            "account/rateLimits/read/",
            "account/rateLimits",
            "account/rateLimitResetCredit/Consume",
            "",
        ];

        foreach (var method in rejectedMethods)
        {
            using var message = JsonDocument.Parse($"{{\"method\":\"{method}\"}}");
            var exception = Assert.ThrowsException<AppServerException>(() =>
                CodexAppServerClient.ValidateOutboundMessage(message.RootElement));
            Assert.AreEqual(
                AppServerFailureCategory.OutboundMethodNotAllowed,
                exception.Category);
        }
    }

    [DataTestMethod]
    [DataRow(
        "{\"method\":\"account/rateLimitResetCredit/consume\",\"params\":{\"idempotencyKey\":\"attempt\"}}")]
    [DataRow(
        "{\"method\":\"account/rateLimitResetCredit/consume\",\"id\":1}")]
    [DataRow(
        "{\"method\":\"account/rateLimitResetCredit/consume\",\"id\":1,\"params\":null}")]
    [DataRow(
        "{\"method\":\"account/rateLimitResetCredit/consume\",\"id\":1,\"params\":{}}")]
    [DataRow(
        "{\"method\":\"account/rateLimitResetCredit/consume\",\"id\":1,\"params\":{\"idempotencyKey\":null}}")]
    [DataRow(
        "{\"method\":\"account/rateLimitResetCredit/consume\",\"id\":1,\"params\":{\"idempotencyKey\":7}}")]
    [DataRow(
        "{\"method\":\"account/rateLimitResetCredit/consume\",\"id\":1,\"params\":{\"idempotencyKey\":\" \"}}")]
    [DataRow(
        "{\"method\":\"account/rateLimitResetCredit/consume\",\"id\":1,\"params\":{\"idempotencyKey\":\"attempt\"}}")]
    [DataRow(
        "{\"method\":\"account/rateLimitResetCredit/consume\",\"id\":1,\"params\":{\"idempotencyKey\":\"attempt\",\"creditId\":null}}")]
    [DataRow(
        "{\"method\":\"account/rateLimitResetCredit/consume\",\"id\":1,\"params\":{\"idempotencyKey\":\"attempt\",\"creditId\":7}}")]
    [DataRow(
        "{\"method\":\"account/rateLimitResetCredit/consume\",\"id\":1,\"params\":{\"idempotencyKey\":\"attempt\",\"creditId\":\"\"}}")]
    [DataRow(
        "{\"method\":\"account/rateLimitResetCredit/consume\",\"id\":1,\"params\":{\"idempotencyKey\":\"attempt\",\"unexpected\":true}}")]
    [DataRow(
        "{\"method\":\"account/rateLimitResetCredit/consume\",\"id\":1,\"params\":{\"idempotencyKey\":\"attempt\"},\"unexpected\":true}")]
    public void ConsumeOutboundParamsMustBeTypedAndComplete(string json)
    {
        using var message = JsonDocument.Parse(json);

        var exception = Assert.ThrowsException<AppServerException>(() =>
            CodexAppServerClient.ValidateOutboundMessage(message.RootElement));

        Assert.AreEqual(
            AppServerFailureCategory.InvalidOutboundMessage,
            exception.Category);
    }

    [DataTestMethod]
    [DataRow("{\"method\":\"account/rateLimitResetCredit/consume\",\"method\":\"account/rateLimitResetCredit/consume\",\"id\":1,\"params\":{\"idempotencyKey\":\"attempt\"}}")]
    [DataRow("{\"method\":\"account/rateLimitResetCredit/consume\",\"id\":1,\"params\":{\"idempotencyKey\":\"first\",\"idempotencyKey\":\"second\"}}")]
    [DataRow("{\"method\":\"account/rateLimitResetCredit/consume\",\"id\":1,\"params\":{\"idempotencyKey\":\"attempt\",\"creditId\":\"one\",\"creditId\":\"two\"}}")]
    public void OutboundDuplicateMembersFailClosed(string json)
    {
        using var message = JsonDocument.Parse(json);

        var exception = Assert.ThrowsException<AppServerException>(() =>
            CodexAppServerClient.ValidateOutboundMessage(message.RootElement));

        Assert.AreEqual(AppServerFailureCategory.InvalidResponse, exception.Category);
    }

    [TestMethod]
    public void ConsumeOutboundCreditIdIsRequiredAndExplicit()
    {
        using var message = JsonDocument.Parse("""
        {
          "method": "account/rateLimitResetCredit/consume",
          "id": 1,
          "params": {
            "idempotencyKey": "attempt",
            "creditId": "opaque-credit"
          }
        }
        """);

        CodexAppServerClient.ValidateOutboundMessage(message.RootElement);
    }

    [TestMethod]
    public void ConsumeRequestRequiresNonBlankIdempotencyKey()
    {
        Assert.ThrowsException<ArgumentNullException>(() =>
            new ConsumeResetCreditRequest(null!, "opaque-credit"));
        Assert.ThrowsException<ArgumentException>(() =>
            new ConsumeResetCreditRequest(string.Empty, "opaque-credit"));
        Assert.ThrowsException<ArgumentException>(() =>
            new ConsumeResetCreditRequest("  ", "opaque-credit"));
    }

    [TestMethod]
    public void ConsumeRequestRequiresExplicitNonBlankCreditId()
    {
        var explicitCredit = new ConsumeResetCreditRequest(
            "attempt-two",
            "opaque-credit");

        Assert.AreEqual("opaque-credit", explicitCredit.CreditId);
        Assert.ThrowsException<ArgumentNullException>(() =>
            new ConsumeResetCreditRequest("attempt-one", null!));
        Assert.ThrowsException<ArgumentException>(() =>
            new ConsumeResetCreditRequest("attempt-three", " "));
        Assert.AreEqual(
            nameof(ConsumeResetCreditRequest),
            explicitCredit.ToString());
    }

    [TestMethod]
    public void ClientPublicSurfaceHasTypedConsumeAndNoArbitrarySendMethod()
    {
        var declaredPublicMethods = typeof(CodexAppServerClient)
            .GetMethods(System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly)
            .Select(method => method.Name)
            .ToArray();

        CollectionAssert.AreEquivalent(
            new[] { "ReadAsync", "ConsumeResetCreditAsync", "DisposeAsync" },
            declaredPublicMethods);
        Assert.IsFalse(declaredPublicMethods.Any(name =>
            name.Contains("Send", StringComparison.OrdinalIgnoreCase)));
    }
}
