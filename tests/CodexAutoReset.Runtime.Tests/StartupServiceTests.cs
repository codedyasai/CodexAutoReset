using CodexAutoReset.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexAutoReset.Runtime.Tests;

[TestClass]
public sealed class StartupServiceTests
{
    private string temporaryDirectory = null!;
    private string executablePath = null!;

    [TestInitialize]
    public void Initialize()
    {
        temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            $"CodexAutoReset.Runtime.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        executablePath = Path.Combine(temporaryDirectory, "CodexAutoReset.exe");
        File.WriteAllBytes(executablePath, [0]);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [TestMethod]
    public void BeginChange_EnableCreatesOwnedQuotedCommand()
    {
        var registry = new FakeRegistryStore();
        var service = new StartupService(registry);

        using (var mutation = service.BeginChange(enable: true, executablePath))
        {
            mutation.Commit();
        }

        var state = service.GetState();
        Assert.AreEqual(StartupStatus.Enabled, state.Status);
        Assert.AreEqual(Path.GetFullPath(executablePath), state.ExecutablePath);

        var command = registry.GetString(
            StartupService.RunSubKey,
            StartupService.RunValueName);
        StringAssert.StartsWith(command, $"\"{Path.GetFullPath(executablePath)}\" --background");
        Assert.IsTrue(StartupService.TryParseOwnedCommand(
            command!,
            out _,
            out var commandOwner));
        Assert.AreEqual(
            commandOwner.ToString("D"),
            registry.GetString(
                StartupService.OwnershipSubKey,
                StartupService.OwnerValueName));
    }

    [TestMethod]
    public void TryParseOwnedCommand_RejectsLegacyExecutable()
    {
        var owner = Guid.NewGuid();
        var legacyPath = Path.Combine(temporaryDirectory, "CodexResetGuard.exe");
        var command = $"\"{legacyPath}\" --background --startup-owner={owner:D}";

        Assert.IsFalse(StartupService.TryParseOwnedCommand(
            command,
            out var parsedPath,
            out var parsedOwner));
        Assert.AreEqual(string.Empty, parsedPath);
        Assert.AreEqual(Guid.Empty, parsedOwner);
    }

    [TestMethod]
    public void BeginChange_EnableUpdatesOnlyOwnedValue()
    {
        var registry = new FakeRegistryStore();
        var service = new StartupService(registry);
        Commit(service.BeginChange(enable: true, executablePath));
        var owner = registry.GetString(
            StartupService.OwnershipSubKey,
            StartupService.OwnerValueName);

        var secondDirectory = Path.Combine(temporaryDirectory, "moved");
        Directory.CreateDirectory(secondDirectory);
        var secondPath = Path.Combine(secondDirectory, "CodexAutoReset.exe");
        File.WriteAllBytes(secondPath, [0]);
        Commit(service.BeginChange(enable: true, secondPath));

        Assert.AreEqual(owner, registry.GetString(
            StartupService.OwnershipSubKey,
            StartupService.OwnerValueName));
        Assert.AreEqual(Path.GetFullPath(secondPath), service.GetState().ExecutablePath);
    }

    [TestMethod]
    public void BeginChange_EnableRefusesForeignRunValue()
    {
        var registry = new FakeRegistryStore();
        registry.SetString(
            StartupService.RunSubKey,
            StartupService.RunValueName,
            "foreign-command.exe");
        var service = new StartupService(registry);

        var exception = Assert.ThrowsException<StartupException>(
            () => service.BeginChange(enable: true, executablePath));

        Assert.AreEqual("startup_foreign_value", exception.ReasonCode);
        Assert.AreEqual(
            "foreign-command.exe",
            registry.GetString(StartupService.RunSubKey, StartupService.RunValueName));
    }

    [TestMethod]
    public void BeginChange_DisableDeletesOwnedValue()
    {
        var registry = new FakeRegistryStore();
        var service = new StartupService(registry);
        Commit(service.BeginChange(enable: true, executablePath));

        Commit(service.BeginChange(enable: false, executablePath));

        Assert.AreEqual(StartupStatus.Disabled, service.GetState().Status);
        Assert.IsNull(registry.GetString(
            StartupService.RunSubKey,
            StartupService.RunValueName));
        Assert.IsNull(registry.GetString(
            StartupService.OwnershipSubKey,
            StartupService.OwnerValueName));
    }

    [TestMethod]
    public void BeginChange_DisablePreservesForeignValue()
    {
        var registry = new FakeRegistryStore();
        registry.SetString(
            StartupService.RunSubKey,
            StartupService.RunValueName,
            "foreign-command.exe");
        var service = new StartupService(registry);

        var exception = Assert.ThrowsException<StartupException>(
            () => service.BeginChange(enable: false, executablePath));

        Assert.AreEqual("startup_foreign_value", exception.ReasonCode);
        Assert.AreEqual(
            "foreign-command.exe",
            registry.GetString(StartupService.RunSubKey, StartupService.RunValueName));
    }

    [TestMethod]
    public void Mutation_RollsBackWhenNotCommitted()
    {
        var registry = new FakeRegistryStore();
        var service = new StartupService(registry);

        service.BeginChange(enable: true, executablePath).Dispose();

        Assert.AreEqual(StartupStatus.Disabled, service.GetState().Status);
        Assert.IsNull(registry.GetString(
            StartupService.OwnershipSubKey,
            StartupService.OwnerValueName));
    }

    [TestMethod]
    public void Mutation_DoesNotOverwriteConcurrentForeignChange()
    {
        var registry = new FakeRegistryStore();
        var service = new StartupService(registry);
        var mutation = service.BeginChange(enable: true, executablePath);
        registry.SetString(
            StartupService.RunSubKey,
            StartupService.RunValueName,
            "foreign-command.exe");

        mutation.Dispose();

        Assert.AreEqual(
            "foreign-command.exe",
            registry.GetString(StartupService.RunSubKey, StartupService.RunValueName));
    }

    [TestMethod]
    public void BeginChange_PartialWriteFailurePreservesConcurrentForeignValue()
    {
        var registry = new FakeRegistryStore
        {
            ReplaceRunWithForeignThenThrow = true,
        };
        var service = new StartupService(registry);

        Assert.ThrowsException<IOException>(
            () => service.BeginChange(enable: true, executablePath));

        Assert.AreEqual(
            "foreign-command.exe",
            registry.GetString(StartupService.RunSubKey, StartupService.RunValueName));
        Assert.IsNull(registry.GetString(
            StartupService.OwnershipSubKey,
            StartupService.OwnerValueName));
    }

    [TestMethod]
    public void BeginChange_VerificationReadFailureRollsBackWrites()
    {
        var registry = new FakeRegistryStore
        {
            ThrowOnReadNumber = 3,
        };
        var service = new StartupService(registry);

        Assert.ThrowsException<IOException>(
            () => service.BeginChange(enable: true, executablePath));

        Assert.IsNull(registry.GetString(
            StartupService.RunSubKey,
            StartupService.RunValueName));
        Assert.IsNull(registry.GetString(
            StartupService.OwnershipSubKey,
            StartupService.OwnerValueName));
    }

    [DataTestMethod]
    [DataRow(true, true)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(false, false)]
    public void BeginChange_RefusesForeignNonStringValue(
        bool enable,
        bool runValue)
    {
        var registry = new FakeRegistryStore();
        registry.SeedNonString(
            runValue ? StartupService.RunSubKey : StartupService.OwnershipSubKey,
            runValue ? StartupService.RunValueName : StartupService.OwnerValueName);
        var service = new StartupService(registry);

        var exception = Assert.ThrowsException<StartupException>(
            () => service.BeginChange(enable, executablePath));

        Assert.AreEqual("startup_foreign_value", exception.ReasonCode);
        Assert.AreEqual(0, registry.WriteCount);
        Assert.AreEqual(StartupStatus.ForeignValue, service.GetState().Status);
    }

    [TestMethod]
    public void ValidateChange_PerformsEnablePreflightWithoutMutation()
    {
        var registry = new FakeRegistryStore();
        var service = new StartupService(registry);

        service.ValidateChange(enable: true, executablePath);

        Assert.AreEqual(0, registry.WriteCount);
        Assert.AreEqual(StartupStatus.Disabled, service.GetState().Status);
    }

    [TestMethod]
    public void ValidateChange_RejectsUnsafeEnableWithoutMutation()
    {
        var registry = new FakeRegistryStore();
        var service = new StartupService(registry);

        var missing = Assert.ThrowsException<StartupException>(() =>
            service.ValidateChange(enable: true, executablePath: null));
        registry.SeedNonString(
            StartupService.RunSubKey,
            StartupService.RunValueName);
        var foreign = Assert.ThrowsException<StartupException>(() =>
            service.ValidateChange(enable: true, executablePath));

        Assert.AreEqual("startup_executable_invalid", missing.ReasonCode);
        Assert.AreEqual("startup_foreign_value", foreign.ReasonCode);
        Assert.AreEqual(0, registry.WriteCount);
    }

    [TestMethod]
    public void BuildCommand_RejectsMissingOrUnsafeExecutable()
    {
        Assert.ThrowsException<StartupException>(
            () => StartupService.BuildCommand(
                Path.Combine(temporaryDirectory, "missing.exe"),
                Guid.NewGuid()));
        Assert.ThrowsException<StartupException>(
            () => StartupService.BuildCommand(
                Path.Combine(temporaryDirectory, "unsafe\".exe"),
                Guid.NewGuid()));

        var legacyPath = Path.Combine(temporaryDirectory, "CodexResetGuard.exe");
        File.WriteAllBytes(legacyPath, [0]);
        Assert.ThrowsException<StartupException>(
            () => StartupService.BuildCommand(legacyPath, Guid.NewGuid()));
    }

    [TestMethod]
    public void IsStartupLaunchAuthorized_RequiresMatchingStoredOwner()
    {
        var registry = new FakeRegistryStore();
        var service = new StartupService(registry);
        Commit(service.BeginChange(enable: true, executablePath));
        var owner = registry.GetString(
            StartupService.OwnershipSubKey,
            StartupService.OwnerValueName)!;

        Assert.IsTrue(service.IsStartupLaunchAuthorized(owner));
        Assert.IsFalse(service.IsStartupLaunchAuthorized(Guid.NewGuid().ToString("D")));
        Assert.IsFalse(service.IsStartupLaunchAuthorized("not-a-guid"));
    }

    private static void Commit(StartupService.StartupMutation mutation)
    {
        using (mutation)
        {
            mutation.Commit();
        }
    }

    private sealed class FakeRegistryStore : ICurrentUserRegistryStore
    {
        private readonly Dictionary<(string Key, string Name), CurrentUserRegistryValue> values = new();
        private int readCount;

        public bool ReplaceRunWithForeignThenThrow { get; init; }

        public int ThrowOnReadNumber { get; init; }

        public int WriteCount { get; private set; }

        public CurrentUserRegistryValue ReadValue(string subKey, string valueName)
        {
            readCount++;
            if (readCount == ThrowOnReadNumber)
            {
                throw new IOException("simulated");
            }

            return values.GetValueOrDefault(
                (subKey, valueName),
                CurrentUserRegistryValue.Missing);
        }

        public string? GetString(string subKey, string valueName)
        {
            var value = ReadValue(subKey, valueName);
            return value.Kind == CurrentUserRegistryValueKind.String
                ? value.StringValue
                : null;
        }

        public void SetString(string subKey, string valueName, string value)
        {
            WriteCount++;
            if (ReplaceRunWithForeignThenThrow
                && subKey == StartupService.RunSubKey
                && valueName == StartupService.RunValueName)
            {
                values[(subKey, valueName)] = CurrentUserRegistryValue.FromString(
                    "foreign-command.exe");
                throw new IOException("simulated");
            }

            values[(subKey, valueName)] = CurrentUserRegistryValue.FromString(value);
        }

        public void DeleteValue(string subKey, string valueName)
        {
            WriteCount++;
            values.Remove((subKey, valueName));
        }

        public void SeedNonString(string subKey, string valueName) =>
            values[(subKey, valueName)] = CurrentUserRegistryValue.NonString;
    }
}
