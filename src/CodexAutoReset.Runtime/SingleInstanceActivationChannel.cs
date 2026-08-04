using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodexAutoReset.Runtime;

public enum SingleInstanceActivationResult
{
    Activated,
    DifferentSession,
    ShuttingDown,
    Unavailable,
}

public sealed class SingleInstanceActivationChannel : IAsyncDisposable
{
    private const int RequestLength = 8;
    private const int ResponseLength = 5;
    private const byte ActivatedResponse = 1;
    private const byte DifferentSessionResponse = 2;
    private const byte ShuttingDownResponse = 3;
    private const byte InvalidRequestResponse = 4;
    private static readonly TimeSpan ConnectionIoTimeout = TimeSpan.FromSeconds(2);
    private static readonly byte[] RequestMagic = "CAR1"u8.ToArray();

    private readonly object syncRoot = new();
    private readonly string pipeName;
    private readonly int sessionId;
    private readonly CancellationTokenSource stoppingSource = new();
    private readonly Task listenerTask;
    private Func<CancellationToken, Task<bool>>? activationHandler;
    private bool activationPending;
    private bool stopping;

    private SingleInstanceActivationChannel(
        string pipeName,
        int sessionId,
        NamedPipeServerStream initialServer)
    {
        this.pipeName = pipeName;
        this.sessionId = sessionId;
        listenerTask = ListenAsync(initialServer);
    }

    public static SingleInstanceActivationChannel? TryStart(RuntimePaths paths) =>
        TryStart(paths, Process.GetCurrentProcess().SessionId);

    public static Task<SingleInstanceActivationResult> TryActivateExistingAsync(
        RuntimePaths paths,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        TryActivateExistingAsync(
            paths,
            Process.GetCurrentProcess().SessionId,
            timeout,
            cancellationToken);

    public void SetActivationHandler(
        Func<CancellationToken, Task<bool>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var invokePending = false;
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(stopping, this);
            activationHandler = handler;
            invokePending = activationPending;
            activationPending = false;
        }

        if (invokePending)
        {
            _ = InvokeSafelyAsync(handler, stoppingSource.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (syncRoot)
        {
            if (stopping)
            {
                return;
            }

            stopping = true;
            activationHandler = null;
            activationPending = false;
        }

        await stoppingSource.CancelAsync().ConfigureAwait(false);
        try
        {
            await listenerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            stoppingSource.Dispose();
        }
    }

    internal static SingleInstanceActivationChannel? TryStart(
        RuntimePaths paths,
        int sessionId)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var pipeName = BuildPipeName(paths);
        try
        {
            var server = CreateServer(pipeName);
            return new SingleInstanceActivationChannel(
                pipeName,
                sessionId,
                server);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (System.Security.SecurityException)
        {
            return null;
        }
    }

    internal static async Task<SingleInstanceActivationResult>
        TryActivateExistingAsync(
            RuntimePaths paths,
            int sessionId,
            TimeSpan timeout,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var timeoutMilliseconds = (int)Math.Ceiling(timeout.TotalMilliseconds);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(timeoutMilliseconds);

        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                BuildPipeName(paths),
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(
                timeoutMilliseconds,
                timeoutSource.Token).ConfigureAwait(false);
            TryAllowServerToSetForegroundWindow(client, sessionId);

            var request = new byte[RequestLength];
            RequestMagic.CopyTo(request, 0);
            BinaryPrimitives.WriteInt32LittleEndian(
                request.AsSpan(RequestMagic.Length),
                sessionId);
            await client.WriteAsync(request, timeoutSource.Token).ConfigureAwait(false);
            await client.FlushAsync(timeoutSource.Token).ConfigureAwait(false);

            var response = new byte[ResponseLength];
            await client.ReadExactlyAsync(
                response,
                timeoutSource.Token).ConfigureAwait(false);
            var serverSessionId = BinaryPrimitives.ReadInt32LittleEndian(
                response.AsSpan(1));
            return response[0] switch
            {
                ActivatedResponse when serverSessionId == sessionId =>
                    SingleInstanceActivationResult.Activated,
                DifferentSessionResponse =>
                    SingleInstanceActivationResult.DifferentSession,
                ShuttingDownResponse =>
                    SingleInstanceActivationResult.ShuttingDown,
                _ => SingleInstanceActivationResult.Unavailable,
            };
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested)
        {
            return SingleInstanceActivationResult.Unavailable;
        }
        catch (TimeoutException)
        {
            return SingleInstanceActivationResult.Unavailable;
        }
        catch (IOException)
        {
            return SingleInstanceActivationResult.Unavailable;
        }
        catch (UnauthorizedAccessException)
        {
            return SingleInstanceActivationResult.Unavailable;
        }
        catch (System.Security.SecurityException)
        {
            return SingleInstanceActivationResult.Unavailable;
        }
    }

    internal static string BuildPipeName(RuntimePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var normalizedRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(paths.RootDirectory)).ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedRoot));
        return $"CodexAutoReset.Activation.v1.{Convert.ToHexString(hash.AsSpan(0, 16))}";
    }

    private static NamedPipeServerStream CreateServer(string pipeName) =>
        new(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous
                | PipeOptions.CurrentUserOnly
                | PipeOptions.FirstPipeInstance);

    private async Task ListenAsync(NamedPipeServerStream initialServer)
    {
        var server = initialServer;
        while (!stoppingSource.IsCancellationRequested)
        {
            using (server)
            {
                try
                {
                    await server.WaitForConnectionAsync(
                        stoppingSource.Token).ConfigureAwait(false);
                    await ProcessConnectionAsync(server).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    stoppingSource.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException)
                {
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (System.Security.SecurityException)
                {
                }
            }

            server = await CreateNextServerAsync().ConfigureAwait(false);
            if (server is null)
            {
                return;
            }
        }
    }

    private async Task<NamedPipeServerStream?> CreateNextServerAsync()
    {
        while (!stoppingSource.IsCancellationRequested)
        {
            try
            {
                return CreateServer(pipeName);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (System.Security.SecurityException)
            {
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(100),
                    stoppingSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        return null;
    }

    private async Task ProcessConnectionAsync(NamedPipeServerStream server)
    {
        using var connectionTimeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(
                stoppingSource.Token);
        connectionTimeoutSource.CancelAfter(ConnectionIoTimeout);
        var cancellationToken = connectionTimeoutSource.Token;

        var request = new byte[RequestLength];
        await server.ReadExactlyAsync(request, cancellationToken).ConfigureAwait(false);

        var responseCode = InvalidRequestResponse;
        if (request.AsSpan(0, RequestMagic.Length).SequenceEqual(RequestMagic))
        {
            var clientSessionId = BinaryPrimitives.ReadInt32LittleEndian(
                request.AsSpan(RequestMagic.Length));
            if (clientSessionId != sessionId)
            {
                responseCode = DifferentSessionResponse;
            }
            else if (IsStopping())
            {
                responseCode = ShuttingDownResponse;
            }
            else
            {
                responseCode = await TryDispatchActivationAsync(
                        cancellationToken).ConfigureAwait(false)
                    ? ActivatedResponse
                    : ShuttingDownResponse;
            }
        }

        var response = new byte[ResponseLength];
        response[0] = responseCode;
        BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(1), sessionId);
        await server.WriteAsync(response, cancellationToken).ConfigureAwait(false);
        await server.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool IsStopping()
    {
        lock (syncRoot)
        {
            return stopping;
        }
    }

    private async Task<bool> TryDispatchActivationAsync(
        CancellationToken cancellationToken)
    {
        Func<CancellationToken, Task<bool>>? handler;
        lock (syncRoot)
        {
            if (stopping)
            {
                return false;
            }

            handler = activationHandler;
            if (handler is null)
            {
                activationPending = true;
                return true;
            }
        }

        return await InvokeSafelyAsync(
            handler,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> InvokeSafelyAsync(
        Func<CancellationToken, Task<bool>> handler,
        CancellationToken cancellationToken)
    {
        try
        {
            return await handler(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception)
        {
            // Window activation is best-effort and must not stop the listener.
            return false;
        }
    }

    private static void TryAllowServerToSetForegroundWindow(
        NamedPipeClientStream client,
        int clientSessionId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            if (!GetNamedPipeServerProcessId(
                    client.SafePipeHandle,
                    out var serverProcessId)
                || serverProcessId > int.MaxValue)
            {
                return;
            }

            using var serverProcess = Process.GetProcessById(
                (int)serverProcessId);
            if (serverProcess.SessionId == clientSessionId)
            {
                _ = AllowSetForegroundWindow(serverProcessId);
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or InvalidOperationException
                or Win32Exception
                or NotSupportedException)
        {
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(
        SafePipeHandle pipe,
        out uint serverProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
