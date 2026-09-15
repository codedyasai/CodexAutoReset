using System.Text.Json;
using System.Text.Json.Nodes;
using CodexAutoReset.AppServer;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexAutoReset.Tests;

[TestClass]
public sealed class CodexSchemaVerifierTests
{
    private static JsonObject RequestBundle() => new()
    {
        ["definitions"] = new JsonObject
        {
            ["RequestId"] = JsonNode.Parse(CodexSchemaVerifier.ExpectedRequestId),
            ["ConsumeAccountRateLimitResetCreditParams"] = JsonNode.Parse(CodexSchemaVerifier.ExpectedParams),
        },
        ["oneOf"] = new JsonArray(JsonNode.Parse(CodexSchemaVerifier.ExpectedRequest)),
    };

    private static bool Verify(JsonNode parameters, JsonNode response, JsonNode requests)
    {
        using var p = JsonDocument.Parse(parameters.ToJsonString());
        using var r = JsonDocument.Parse(response.ToJsonString());
        using var q = JsonDocument.Parse(requests.ToJsonString());
        return CodexSchemaVerifier.MatchesContract(p.RootElement, r.RootElement, q.RootElement);
    }

    [TestMethod]
    public void VerifiedContractAllowsDocumentationAndOrderChanges()
    {
        var p = JsonNode.Parse(CodexSchemaVerifier.ExpectedParams)!;
        var r = JsonNode.Parse(CodexSchemaVerifier.ExpectedResponse)!;
        var q = RequestBundle();
        p["description"] = "Documentation only";
        p["title"] = "New display title";
        p["$schema"] = "http://json-schema.org/draft-07/schema#";
        p["properties"]!["creditId"]!["type"] = new JsonArray("null", "string");
        r["properties"]!["outcome"]!["description"] = "Display text";
        q["oneOf"]![0]!["required"] = new JsonArray("params", "method", "id");
        Assert.IsTrue(Verify(p, r, q));
    }

    [DataTestMethod]
    [DataRow("new_required")]
    [DataRow("credit_type")]
    [DataRow("idempotency_missing")]
    [DataRow("new_outcome")]
    [DataRow("outcome_missing")]
    [DataRow("new_property_named_title")]
    [DataRow("missing_method")]
    [DataRow("duplicate_method")]
    [DataRow("method_alias")]
    [DataRow("wrong_params_ref")]
    [DataRow("different_linked_params")]
    [DataRow("wrong_request_id")]
    [DataRow("new_constraint")]
    public void ChangedMutationContractFailsClosed(string change)
    {
        var p = JsonNode.Parse(CodexSchemaVerifier.ExpectedParams)!;
        var r = JsonNode.Parse(CodexSchemaVerifier.ExpectedResponse)!;
        var q = RequestBundle();
        switch (change)
        {
            case "new_required": p["required"]!.AsArray().Add("newConsent"); break;
            case "credit_type": p["properties"]!["creditId"]!["type"] = "integer"; break;
            case "idempotency_missing": p["properties"]!.AsObject().Remove("idempotencyKey"); break;
            case "new_outcome":
                r["definitions"]!["ConsumeAccountRateLimitResetCreditOutcome"]!["oneOf"]!.AsArray()
                    .Add(JsonNode.Parse("""{"type":"string","enum":["queued"]}""")); break;
            case "outcome_missing": r["required"] = new JsonArray(); break;
            case "new_property_named_title": p["properties"]!["title"] = JsonNode.Parse("""{"type":"string"}"""); break;
            case "missing_method": q["oneOf"] = new JsonArray(); break;
            case "duplicate_method": q["oneOf"]!.AsArray().Add(q["oneOf"]![0]!.DeepClone()); break;
            case "method_alias": q["oneOf"]![0]!["properties"]!["method"]!["enum"]!.AsArray().Add("another/method"); break;
            case "wrong_params_ref": q["oneOf"]![0]!["properties"]!["params"]!["$ref"] = "#/definitions/Other"; break;
            case "different_linked_params": q["definitions"]!["ConsumeAccountRateLimitResetCreditParams"]!["required"] = new JsonArray(); break;
            case "wrong_request_id": q["definitions"]!["RequestId"] = JsonNode.Parse("""{"type":"boolean"}"""); break;
            case "new_constraint": p["properties"]!["idempotencyKey"]!["maxLength"] = 5; break;
        }
        Assert.IsFalse(Verify(p, r, q));
    }

    [TestMethod]
    public void DuplicateSchemaKeysFailClosed()
    {
        using var p = JsonDocument.Parse(CodexSchemaVerifier.ExpectedParams.Replace(
            "\"required\":[\"idempotencyKey\"]", "\"required\":[],\"required\":[\"idempotencyKey\"]"));
        using var r = JsonDocument.Parse(CodexSchemaVerifier.ExpectedResponse);
        using var q = JsonDocument.Parse(RequestBundle().ToJsonString());
        Assert.IsFalse(CodexSchemaVerifier.MatchesContract(p.RootElement, r.RootElement, q.RootElement));
    }

    [TestMethod]
    public void EmptyMalformedContractFailsClosed()
    {
        using var empty = JsonDocument.Parse("{}");
        Assert.IsFalse(CodexSchemaVerifier.MatchesContract(empty.RootElement, empty.RootElement, empty.RootElement));
    }

    [TestMethod]
    public void NewAlphaVersionCanBeIdentifiedWithoutBeingAutomaticallyTrusted()
    {
        using var response = JsonDocument.Parse("""
            {"codexHome":"synthetic-path","platformFamily":"windows","platformOs":"windows",
            "userAgent":"codex_auto_reset/0.154.0-alpha.6.2 (Windows) unknown (codex_auto_reset; 0.4.2)"}
            """);
        Assert.AreEqual("0.154.0-alpha.6.2", AppServerProtocolParser.GetInitializedServerVersion(response.RootElement, "0.4.2"));
        Assert.IsFalse(AppServerProtocolParser.ValidateInitializeResult(response.RootElement, "0.4.2"));
        Assert.IsNull(AppServerProtocolParser.GetInitializedServerVersion(response.RootElement, "0.4.1"));
    }

    [TestMethod]
    public async Task UnavailableExecutableCannotBecomeVerified()
    {
        using var directory = TemporaryDirectory.Create();
        Assert.IsFalse(await CodexSchemaVerifier.VerifyAsync(Path.Combine(directory.Path, "codex.exe"),
            "0.154.0-alpha.6.2", CancellationToken.None));
    }

    [TestMethod]
    public async Task InvalidExecutableCannotBecomeVerified()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "codex.exe");
        await File.WriteAllTextAsync(path, "Not an executable");
        Assert.IsFalse(await CodexSchemaVerifier.VerifyAsync(path, "0.154.0-alpha.6.2", CancellationToken.None));
    }

    [TestMethod]
    public async Task CacheIdentityChangesWithFileOrVersion()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "codex.exe");
        await File.WriteAllTextAsync(path, "first");
        var original = CodexSchemaVerifier.GetStamp(path, "1.0.0");
        Assert.AreNotEqual(original, CodexSchemaVerifier.GetStamp(path, "1.0.1"));
        await File.WriteAllTextAsync(path, "replacement");
        Assert.AreNotEqual(original, CodexSchemaVerifier.GetStamp(path, "1.0.0"));
    }
}
