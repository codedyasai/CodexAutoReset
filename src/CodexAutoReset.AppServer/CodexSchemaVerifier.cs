using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodexAutoReset.AppServer;

internal static class CodexSchemaVerifier
{
    internal const string ExpectedParams = """
        {"type":"object","properties":{"creditId":{"type":["string","null"]},
        "idempotencyKey":{"type":"string"}},"required":["idempotencyKey"]}
        """;
    internal const string ExpectedResponse = """
        {"type":"object","properties":{"outcome":{"$ref":"#/definitions/ConsumeAccountRateLimitResetCreditOutcome"}},
        "required":["outcome"],"definitions":{"ConsumeAccountRateLimitResetCreditOutcome":{"oneOf":[
        {"type":"string","enum":["reset"]},{"type":"string","enum":["nothingToReset"]},
        {"type":"string","enum":["noCredit"]},{"type":"string","enum":["alreadyRedeemed"]}]}}}
        """;
    internal const string ExpectedRequest = """
        {"type":"object","properties":{"id":{"$ref":"#/definitions/RequestId"},
        "method":{"type":"string","enum":["account/rateLimitResetCredit/consume"]},
        "params":{"$ref":"#/definitions/ConsumeAccountRateLimitResetCreditParams"}},"required":["id","method","params"]}
        """;
    internal const string ExpectedRequestId = """
        {"anyOf":[{"type":"string"},{"type":"integer","format":"int64"}]}
        """;

    private static readonly ConcurrentDictionary<ExecutableStamp, bool> Verified = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    internal readonly record struct ExecutableStamp(string Path, long Length, long LastWriteTicks, string Version);

    internal static ExecutableStamp GetStamp(string path, string version)
    {
        var canonicalPath = CodexExecutableLocator.CanonicalizeForReadOnly(path);
        var file = new FileInfo(canonicalPath);
        return new ExecutableStamp(canonicalPath, file.Length, file.LastWriteTimeUtc.Ticks, version);
    }

    public static async Task<bool> VerifyAsync(string executablePath, string serverVersion, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            var stamp = GetStamp(executablePath, serverVersion);
            if (Verified.ContainsKey(stamp))
            {
                return true;
            }

            await Gate.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                if (Verified.ContainsKey(stamp))
                {
                    return true;
                }

                var directory = Directory.CreateTempSubdirectory("CodexAutoReset-schema-");
                try
                {
                    // Bind the generated contract to the version reported by the active server.
                    var version = await RunAsync(stamp.Path, directory.FullName, ["--version"], timeout.Token).ConfigureAwait(false);
                    if (!string.Equals(version.Trim(), $"codex-cli {serverVersion}", StringComparison.Ordinal))
                    {
                        return false;
                    }

                    await RunAsync(stamp.Path, directory.FullName,
                        ["app-server", "generate-json-schema", "--out", directory.FullName, "--experimental"], timeout.Token).ConfigureAwait(false);
                    using var parameters = ReadSchema(directory.FullName, "v2", "ConsumeAccountRateLimitResetCreditParams.json");
                    using var response = ReadSchema(directory.FullName, "v2", "ConsumeAccountRateLimitResetCreditResponse.json");
                    using var requests = ReadSchema(directory.FullName, "ClientRequest.json");
                    if (!MatchesContract(parameters.RootElement, response.RootElement, requests.RootElement)
                        || stamp != GetStamp(executablePath, serverVersion))
                    {
                        return false;
                    }

                    if (Verified.Count >= 32)
                    {
                        Verified.Clear();
                    }

                    Verified[stamp] = true;
                    return true;
                }
                finally
                {
                    // This directory was created exclusively for this schema probe.
                    try { directory.Delete(recursive: true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            finally { Gate.Release(); }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception
            or AppServerException or ArgumentException)
        {
            return false;
        }
    }

    private static JsonDocument ReadSchema(string directory, params string[] parts)
    {
        var path = Path.Combine([directory, .. parts]);
        var file = new FileInfo(path);
        if (file.Length > 4 * 1_024 * 1_024 || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException();
        }

        using var stream = file.OpenRead();
        return JsonDocument.Parse(stream);
    }

    internal static bool MatchesContract(JsonElement parameters, JsonElement response, JsonElement requests)
    {
        try
        {
            AppServerProtocolParser.EnsureNoDuplicateProperties(parameters);
            AppServerProtocolParser.EnsureNoDuplicateProperties(response);
            AppServerProtocolParser.EnsureNoDuplicateProperties(requests);
            if (!Equivalent(parameters, ExpectedParams) || !Equivalent(response, ExpectedResponse))
            {
                return false;
            }

            var definitions = requests.GetProperty("definitions");
            if (!Equivalent(definitions.GetProperty("ConsumeAccountRateLimitResetCreditParams"), ExpectedParams)
                || !Equivalent(definitions.GetProperty("RequestId"), ExpectedRequestId))
            {
                return false;
            }

            var matches = requests.GetProperty("oneOf").EnumerateArray().Where(candidate =>
                candidate.TryGetProperty("properties", out var properties)
                && properties.TryGetProperty("method", out var method)
                && method.TryGetProperty("enum", out var names)
                && names.ValueKind == JsonValueKind.Array
                && names.EnumerateArray().Any(name => name.ValueKind == JsonValueKind.String
                    && name.GetString() == "account/rateLimitResetCredit/consume")).ToArray();
            return matches.Length == 1 && Equivalent(matches[0], ExpectedRequest);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or AppServerException)
        {
            return false;
        }
    }

    private static bool Equivalent(JsonElement actual, string expected)
    {
        using var document = JsonDocument.Parse(expected);
        return Normalize(actual) == Normalize(document.RootElement);
    }

    // Ignore documentation and ordering, not constraints, defaults, new fields or outcomes.
    // A property named "title" is a data field inside a property map, not an annotation.
    private static string Normalize(JsonElement element, bool propertyMap = false) => element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject()
            .Where(property => propertyMap || property.Name is not ("description" or "title" or "$schema"))
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => JsonSerializer.Serialize(property.Name) + ":"
                + Normalize(property.Value, property.Name is "properties" or "definitions" or "$defs"))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray()
            .Select(item => Normalize(item)).Order(StringComparer.Ordinal)) + "]",
        _ => element.GetRawText(),
    };

    private static async Task<string> RunAsync(string executable, string directory, string[] arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) { startInfo.ArgumentList.Add(argument); }
        using var process = Process.Start(startInfo) ?? throw new IOException();
        try
        {
            var output = ReadBoundedAsync(process.StandardOutput, cancellationToken);
            var error = ReadBoundedAsync(process.StandardError, cancellationToken);
            await Task.WhenAll(output, error, process.WaitForExitAsync(cancellationToken)).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0) { throw new IOException(); }
            return await output.ConfigureAwait(false);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (TimeoutException) { }
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var buffer = new char[1_024];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) { return text.ToString(); }
            if (text.Length + count > 16 * 1_024) { throw new InvalidDataException(); }
            text.Append(buffer, 0, count);
        }
    }
}
