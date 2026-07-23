using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using CodexAutoReset.Core;

namespace CodexAutoReset.AppServer;

public sealed class CodexAppServerClient : IAccountRateLimitClient
{
    private const string InitializeMethod = "initialize";
    private const string InitializedMethod = "initialized";
    private const string RateLimitsReadMethod = "account/rateLimits/read";
    private const string ResetCreditConsumeMethod =
        "account/rateLimitResetCredit/consume";
    internal const int MaximumStandardOutputFrameLength = 256 * 1_024;
    internal const int MaximumStandardErrorLineLength = 64 * 1_024;

    private static readonly TimeSpan ProcessShutdownTimeout = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string executablePath;
    private readonly string workingDirectory;
    private readonly TimeSpan requestTimeout;
    private readonly bool mutationAllowed;
    private readonly SemaphoreSlim requestGate = new(1, 1);

    private Process? process;
    private BoundedTextLineReader? stdoutReader;
    private Task? stderrDrainTask;
    private CancellationTokenSource? stderrDrainCancellation;
    private long requestId;
    private bool initialized;
    private bool consumeSchemaCompatible;
    private bool disposed;

    public CodexAppServerClient(
        string executablePath,
        string workingDirectory,
        TimeSpan? requestTimeout = null)
        : this(
            CodexExecutableLocator.CanonicalizeForReadOnly(executablePath),
            workingDirectory,
            mutationAllowed: false,
            requestTimeout)
    {
    }

    public CodexAppServerClient(
        CodexExecutableResolution executable,
        string workingDirectory,
        TimeSpan? requestTimeout = null)
        : this(
            (executable ?? throw new ArgumentNullException(nameof(executable)))
                .ExecutablePath,
            workingDirectory,
            executable.AllowsMutation,
            requestTimeout)
    {
    }

    private CodexAppServerClient(
        string executablePath,
        string workingDirectory,
        bool mutationAllowed,
        TimeSpan? requestTimeout)
    {
        this.executablePath = executablePath;
        this.workingDirectory = Path.GetFullPath(workingDirectory);
        this.mutationAllowed = mutationAllowed;
        this.requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(20);
    }

    internal static string ClientVersion { get; } = GetClientVersion();

    public async Task<AccountRateLimits> ReadAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeoutSource.CancelAfter(requestTimeout);

            try
            {
                await EnsureInitializedAsync(timeoutSource.Token).ConfigureAwait(false);
                var result = await SendRateLimitsReadAsync(timeoutSource.Token)
                    .ConfigureAwait(false);
                return AppServerProtocolParser.ParseRateLimits(
                    result,
                    DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException exception)
                when (!cancellationToken.IsCancellationRequested)
            {
                await StopProcessAsync().ConfigureAwait(false);
                throw new AppServerException(
                    AppServerFailureCategory.Timeout,
                    innerException: exception);
            }
            catch (AppServerException)
            {
                await StopProcessAsync().ConfigureAwait(false);
                throw;
            }
            catch (IOException exception)
            {
                await StopProcessAsync().ConfigureAwait(false);
                throw new AppServerException(
                    AppServerFailureCategory.IoError,
                    innerException: exception);
            }
        }
        finally
        {
            requestGate.Release();
        }
    }

    public async Task<ConsumeResetCreditResult> ConsumeResetCreditAsync(
        ConsumeResetCreditRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(disposed, this);
        await requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeoutSource.CancelAfter(requestTimeout);

            try
            {
                if (!mutationAllowed)
                {
                    throw new AppServerException(
                        AppServerFailureCategory.UntrustedExecutableForMutation);
                }

                await EnsureInitializedAsync(timeoutSource.Token).ConfigureAwait(false);
                if (!consumeSchemaCompatible)
                {
                    throw new AppServerException(
                        AppServerFailureCategory.InvalidResponse);
                }

                var result = await SendResetCreditConsumeAsync(
                    request,
                    timeoutSource.Token).ConfigureAwait(false);
                return AppServerProtocolParser.ParseConsumeResetCredit(result);
            }
            catch (OperationCanceledException exception)
                when (!cancellationToken.IsCancellationRequested)
            {
                await StopProcessAsync().ConfigureAwait(false);
                throw new AppServerException(
                    AppServerFailureCategory.Timeout,
                    innerException: exception);
            }
            catch (AppServerException)
            {
                await StopProcessAsync().ConfigureAwait(false);
                throw;
            }
            catch (IOException exception)
            {
                await StopProcessAsync().ConfigureAwait(false);
                throw new AppServerException(
                    AppServerFailureCategory.IoError,
                    innerException: exception);
            }
        }
        finally
        {
            requestGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await requestGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopProcessAsync().ConfigureAwait(false);
        }
        finally
        {
            requestGate.Release();
            requestGate.Dispose();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (process is null || process.HasExited)
        {
            await StopProcessAsync().ConfigureAwait(false);
            StartProcess();
        }

        if (initialized)
        {
            return;
        }

        var id = NextRequestId();
        var request = new
        {
            method = InitializeMethod,
            id,
            @params = new
            {
                clientInfo = new
                {
                    name = "codex_auto_reset",
                    title = "CodexAutoReset",
                    version = ClientVersion,
                },
            },
        };

        await WriteMessageAsync(request, cancellationToken).ConfigureAwait(false);
        var result = await ReadResultAsync(id, cancellationToken).ConfigureAwait(false);
        consumeSchemaCompatible =
            AppServerProtocolParser.ValidateInitializeResult(result, ClientVersion);

        await WriteMessageAsync(
            new { method = InitializedMethod },
            cancellationToken).ConfigureAwait(false);
        initialized = true;
    }

    private async Task<JsonElement> SendRateLimitsReadAsync(
        CancellationToken cancellationToken)
    {
        var id = NextRequestId();
        await WriteMessageAsync(
            new { method = RateLimitsReadMethod, id },
            cancellationToken).ConfigureAwait(false);
        return await ReadResultAsync(id, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonElement> SendResetCreditConsumeAsync(
        ConsumeResetCreditRequest request,
        CancellationToken cancellationToken)
    {
        var id = NextRequestId();
        await WriteMessageAsync(
            new
            {
                method = ResetCreditConsumeMethod,
                id,
                @params = new ResetCreditConsumeParams(
                    request.IdempotencyKey,
                    request.CreditId),
            },
            cancellationToken).ConfigureAwait(false);
        return await ReadResultAsync(id, cancellationToken).ConfigureAwait(false);
    }

    private void StartProcess()
    {
        if (!File.Exists(executablePath))
        {
            throw new AppServerException(AppServerFailureCategory.ExecutableNotFound);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--listen");
        startInfo.ArgumentList.Add("stdio://");

        try
        {
            process = Process.Start(startInfo)
                ?? throw new AppServerException(AppServerFailureCategory.StartFailed);
            stdoutReader = new BoundedTextLineReader(
                process.StandardOutput,
                MaximumStandardOutputFrameLength);
            stderrDrainCancellation = new CancellationTokenSource();
            stderrDrainTask = DrainStandardErrorAsync(
                process,
                new BoundedTextLineReader(
                    process.StandardError,
                    MaximumStandardErrorLineLength),
                stderrDrainCancellation.Token);
            initialized = false;
            consumeSchemaCompatible = false;
        }
        catch (AppServerException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            throw new AppServerException(
                AppServerFailureCategory.StartFailed,
                innerException: exception);
        }
    }

    private async Task WriteMessageAsync<T>(
        T message,
        CancellationToken cancellationToken)
    {
        var activeProcess = GetActiveProcess();
        var json = JsonSerializer.Serialize(message, SerializerOptions);
        using (var document = JsonDocument.Parse(json))
        {
            ValidateOutboundMessage(document.RootElement);
        }

        await activeProcess.StandardInput.WriteLineAsync(
            json.AsMemory(),
            cancellationToken).ConfigureAwait(false);
        await activeProcess.StandardInput.FlushAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<JsonElement> ReadResultAsync(
        long expectedId,
        CancellationToken cancellationToken)
    {
        _ = GetActiveProcess();
        var activeReader = stdoutReader
            ?? throw new AppServerException(AppServerFailureCategory.ProcessExited);

        while (true)
        {
            string? line;
            try
            {
                line = await activeReader.ReadLineAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (LineLengthLimitExceededException exception)
            {
                throw new AppServerException(
                    AppServerFailureCategory.InvalidResponse,
                    innerException: exception);
            }

            if (line is null)
            {
                throw new AppServerException(AppServerFailureCategory.ProcessExited);
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException exception)
            {
                throw new AppServerException(
                    AppServerFailureCategory.InvalidResponse,
                    innerException: exception);
            }

            using (document)
            {
                if (!AppServerProtocolParser.TryParseResponseResult(
                    document.RootElement,
                    expectedId,
                    out var result))
                {
                    continue;
                }

                return result;
            }
        }
    }

    private Process GetActiveProcess()
    {
        if (process is null || process.HasExited)
        {
            throw new AppServerException(AppServerFailureCategory.ProcessExited);
        }

        return process;
    }

    private long NextRequestId() => Interlocked.Increment(ref requestId);

    internal static void ValidateOutboundMessage(JsonElement message)
    {
        AppServerProtocolParser.EnsureNoDuplicateProperties(message);
        if (message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("method", out var methodElement)
            || methodElement.ValueKind != JsonValueKind.String)
        {
            throw new AppServerException(
                AppServerFailureCategory.OutboundMethodNotAllowed);
        }

        var method = methodElement.GetString();
        if (method is not (InitializeMethod
            or InitializedMethod
            or RateLimitsReadMethod
            or ResetCreditConsumeMethod))
        {
            throw new AppServerException(
                AppServerFailureCategory.OutboundMethodNotAllowed);
        }

        if (method == ResetCreditConsumeMethod)
        {
            ValidateResetCreditConsumeMessage(message);
        }
    }

    private static void ValidateResetCreditConsumeMessage(JsonElement message)
    {
        foreach (var property in message.EnumerateObject())
        {
            if (property.Name is not ("method" or "id" or "params"))
            {
                throw new AppServerException(
                    AppServerFailureCategory.InvalidOutboundMessage);
            }
        }

        if (!message.TryGetProperty("id", out var idElement)
            || !idElement.TryGetInt64(out _)
            || !message.TryGetProperty("params", out var paramsElement)
            || paramsElement.ValueKind != JsonValueKind.Object
            || !paramsElement.TryGetProperty(
                "idempotencyKey",
                out var idempotencyKeyElement)
            || idempotencyKeyElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(idempotencyKeyElement.GetString()))
        {
            throw new AppServerException(
                AppServerFailureCategory.InvalidOutboundMessage);
        }

        if (!paramsElement.TryGetProperty("creditId", out var creditIdElement)
            || creditIdElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(creditIdElement.GetString()))
        {
            throw new AppServerException(
                AppServerFailureCategory.InvalidOutboundMessage);
        }

        foreach (var property in paramsElement.EnumerateObject())
        {
            if (property.Name is not ("idempotencyKey" or "creditId"))
            {
                throw new AppServerException(
                    AppServerFailureCategory.InvalidOutboundMessage);
            }
        }
    }

    private sealed record ResetCreditConsumeParams(
        string IdempotencyKey,
        string CreditId);

    private static async Task DrainStandardErrorAsync(
        Process activeProcess,
        BoundedTextLineReader reader,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.ReadLineAsync(cancellationToken)
                .ConfigureAwait(false) is not null)
            {
                // Intentionally discard raw stderr. It may contain private paths or remote text.
            }
        }
        catch (LineLengthLimitExceededException)
        {
            TryKillProcess(activeProcess);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            TryKillProcess(activeProcess);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void TryKillProcess(Process activeProcess)
    {
        try
        {
            if (!activeProcess.HasExited)
            {
                activeProcess.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static string GetClientVersion()
    {
        var assembly = typeof(CodexAppServerClient).Assembly;
        return assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";
    }

    private async Task StopProcessAsync()
    {
        initialized = false;
        consumeSchemaCompatible = false;
        var activeProcess = process;
        process = null;
        stdoutReader = null;
        var activeStderrDrainTask = stderrDrainTask;
        stderrDrainTask = null;
        var activeStderrDrainCancellation = stderrDrainCancellation;
        stderrDrainCancellation = null;

        activeStderrDrainCancellation?.Cancel();

        if (activeProcess is not null)
        {
            try
            {
                if (!activeProcess.HasExited)
                {
                    activeProcess.Kill(entireProcessTree: true);
                }

                await activeProcess.WaitForExitAsync()
                    .WaitAsync(ProcessShutdownTimeout)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
            catch (TimeoutException)
            {
            }
            finally
            {
                activeProcess.Dispose();
            }
        }

        if (activeStderrDrainTask is not null)
        {
            try
            {
                await activeStderrDrainTask
                    .WaitAsync(ProcessShutdownTimeout)
                    .ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
            catch (TimeoutException)
            {
            }
        }

        activeStderrDrainCancellation?.Dispose();
    }
}
