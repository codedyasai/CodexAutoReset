using CodexAutoReset.AppServer;
using CodexAutoReset.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexAutoReset.Tests;

[TestClass]
public sealed class AppServerFailureTests
{
    [TestMethod]
    public void ExistingExceptionConstructorCallsDefaultToUnknownOperation()
    {
        var exception = new AppServerException(
            AppServerFailureCategory.Timeout,
            remoteCode: null,
            innerException: new IOException());

        Assert.AreEqual(AppServerOperation.Unknown, exception.Operation);
    }

    [DataTestMethod]
    [DataRow(-32601)]
    [DataRow(-32602)]
    public void JsonRpcSchemaErrorsAreProtocolMismatches(int remoteCode)
    {
        var exception = new AppServerException(
            AppServerFailureCategory.RemoteError,
            remoteCode);

        Assert.AreEqual(
            LiveResetFailureDisposition.ProtocolMismatch,
            AppServerLiveResetFailureClassifier.Instance.Classify(exception));
    }

    [DataTestMethod]
    [DataRow(-32700)]
    [DataRow(-32600)]
    [DataRow(-32603)]
    [DataRow(-32000)]
    public void OtherRemoteErrorsRemainRetryable(int remoteCode)
    {
        var exception = new AppServerException(
            AppServerFailureCategory.RemoteError,
            remoteCode);

        Assert.AreEqual(
            LiveResetFailureDisposition.Retryable,
            AppServerLiveResetFailureClassifier.Instance.Classify(exception));
    }
}
