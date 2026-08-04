using CodexAutoReset.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO.Pipes;

namespace CodexAutoReset.Runtime.Tests;

[TestClass]
public sealed class SingleInstanceActivationChannelTests
{
    [TestMethod]
    public async Task ActivationBeforeHandlerIsDeliveredOnceHandlerIsReady()
    {
        var paths = CreatePaths();
        await using var channel =
            SingleInstanceActivationChannel.TryStart(paths, sessionId: 41);
        Assert.IsNotNull(channel);

        var result = await SingleInstanceActivationChannel
            .TryActivateExistingAsync(
                paths,
                sessionId: 41,
                TimeSpan.FromSeconds(2),
                CancellationToken.None);

        Assert.AreEqual(SingleInstanceActivationResult.Activated, result);
        var activationCount = 0;
        channel.SetActivationHandler(_ =>
        {
            activationCount++;
            return Task.FromResult(true);
        });
        Assert.AreEqual(1, activationCount);
    }

    [TestMethod]
    public async Task ConsecutiveActivationRequestsRemainAvailable()
    {
        var paths = CreatePaths();
        await using var channel =
            SingleInstanceActivationChannel.TryStart(paths, sessionId: 42);
        Assert.IsNotNull(channel);

        var activationCount = 0;
        var secondActivation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        channel.SetActivationHandler(_ =>
        {
            if (Interlocked.Increment(ref activationCount) == 2)
            {
                secondActivation.TrySetResult();
            }

            return Task.FromResult(true);
        });

        var firstResult = await SingleInstanceActivationChannel
            .TryActivateExistingAsync(
                paths,
                sessionId: 42,
                TimeSpan.FromSeconds(2),
                CancellationToken.None);
        var secondResult = await SingleInstanceActivationChannel
            .TryActivateExistingAsync(
                paths,
                sessionId: 42,
                TimeSpan.FromSeconds(2),
                CancellationToken.None);
        await secondActivation.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(SingleInstanceActivationResult.Activated, firstResult);
        Assert.AreEqual(SingleInstanceActivationResult.Activated, secondResult);
        Assert.AreEqual(2, activationCount);
    }

    [TestMethod]
    public async Task ActivationFromAnotherSessionIsRejected()
    {
        var paths = CreatePaths();
        await using var channel =
            SingleInstanceActivationChannel.TryStart(paths, sessionId: 43);
        Assert.IsNotNull(channel);

        var activationCount = 0;
        channel.SetActivationHandler(_ =>
        {
            activationCount++;
            return Task.FromResult(true);
        });
        var result = await SingleInstanceActivationChannel
            .TryActivateExistingAsync(
                paths,
                sessionId: 44,
                TimeSpan.FromSeconds(2),
                CancellationToken.None);

        Assert.AreEqual(
            SingleInstanceActivationResult.DifferentSession,
            result);
        Assert.AreEqual(0, activationCount);
    }

    [TestMethod]
    public async Task RejectedActivationIsReportedAsShuttingDown()
    {
        var paths = CreatePaths();
        await using var channel =
            SingleInstanceActivationChannel.TryStart(paths, sessionId: 49);
        Assert.IsNotNull(channel);
        channel.SetActivationHandler(_ => Task.FromResult(false));

        var result = await SingleInstanceActivationChannel
            .TryActivateExistingAsync(
                paths,
                sessionId: 49,
                TimeSpan.FromSeconds(2),
                CancellationToken.None);

        Assert.AreEqual(
            SingleInstanceActivationResult.ShuttingDown,
            result);
    }

    [TestMethod]
    public async Task MissingOrDisposedChannelReturnsUnavailable()
    {
        var paths = CreatePaths();
        var missingResult = await SingleInstanceActivationChannel
            .TryActivateExistingAsync(
                paths,
                sessionId: 45,
                TimeSpan.FromMilliseconds(150),
                CancellationToken.None);
        Assert.AreEqual(
            SingleInstanceActivationResult.Unavailable,
            missingResult);

        var channel = SingleInstanceActivationChannel.TryStart(
            paths,
            sessionId: 45);
        Assert.IsNotNull(channel);
        await channel.DisposeAsync();

        var disposedResult = await SingleInstanceActivationChannel
            .TryActivateExistingAsync(
                paths,
                sessionId: 45,
                TimeSpan.FromMilliseconds(150),
                CancellationToken.None);
        Assert.AreEqual(
            SingleInstanceActivationResult.Unavailable,
            disposedResult);
    }

    [TestMethod]
    public async Task DifferentRuntimeRootsDoNotCrossSignal()
    {
        var primaryPaths = CreatePaths();
        var otherPaths = CreatePaths();
        Assert.AreNotEqual(
            SingleInstanceActivationChannel.BuildPipeName(primaryPaths),
            SingleInstanceActivationChannel.BuildPipeName(otherPaths));

        await using var channel =
            SingleInstanceActivationChannel.TryStart(
                primaryPaths,
                sessionId: 46);
        Assert.IsNotNull(channel);
        var activationCount = 0;
        channel.SetActivationHandler(_ =>
        {
            activationCount++;
            return Task.FromResult(true);
        });

        var result = await SingleInstanceActivationChannel
            .TryActivateExistingAsync(
                otherPaths,
                sessionId: 46,
                TimeSpan.FromMilliseconds(150),
                CancellationToken.None);

        Assert.AreEqual(SingleInstanceActivationResult.Unavailable, result);
        Assert.AreEqual(0, activationCount);
    }

    [TestMethod]
    public async Task MalformedClientDoesNotStopLaterActivation()
    {
        var paths = CreatePaths();
        await using var channel =
            SingleInstanceActivationChannel.TryStart(paths, sessionId: 47);
        Assert.IsNotNull(channel);

        using (var malformedClient = new NamedPipeClientStream(
            ".",
            SingleInstanceActivationChannel.BuildPipeName(paths),
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            await malformedClient.ConnectAsync(2_000);
            await malformedClient.WriteAsync(new byte[8]);
            await malformedClient.FlushAsync();
            await malformedClient.ReadExactlyAsync(new byte[5]);
        }

        var activation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        channel.SetActivationHandler(_ =>
            Task.FromResult(activation.TrySetResult()));
        var result = await SingleInstanceActivationChannel
            .TryActivateExistingAsync(
                paths,
                sessionId: 47,
                TimeSpan.FromSeconds(2),
                CancellationToken.None);
        await activation.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(SingleInstanceActivationResult.Activated, result);
    }

    [TestMethod]
    public async Task DisposingChannelCancelsAStalledClient()
    {
        var paths = CreatePaths();
        var channel = SingleInstanceActivationChannel.TryStart(
            paths,
            sessionId: 48);
        Assert.IsNotNull(channel);

        using var stalledClient = new NamedPipeClientStream(
            ".",
            SingleInstanceActivationChannel.BuildPipeName(paths),
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await stalledClient.ConnectAsync(2_000);

        await channel.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
    }

    private static RuntimePaths CreatePaths() => RuntimePaths.ForTesting(
        Path.Combine(
            Path.GetTempPath(),
            $"CodexAutoReset-activation-test-{Guid.NewGuid():N}"));
}
