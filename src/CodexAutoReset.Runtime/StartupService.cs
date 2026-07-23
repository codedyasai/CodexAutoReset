using System.Globalization;

namespace CodexAutoReset.Runtime;

public enum StartupStatus
{
    Disabled,
    Enabled,
    ForeignValue,
    InvalidOwnedValue,
}

public sealed record StartupState(
    StartupStatus Status,
    string? ExecutablePath);

public sealed class StartupException : Exception
{
    public StartupException(string reasonCode)
        : base(reasonCode)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}

public sealed class StartupService
{
    internal const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string OwnershipSubKey = @"Software\CodexAutoReset\Startup";
    internal const string RunValueName = "CodexAutoReset";
    internal const string OwnerValueName = "OwnerId";

    private const int MaximumCommandLength = 2048;
    private const string ExpectedExecutableName = "CodexAutoReset.exe";
    private readonly ICurrentUserRegistryStore registry;

    public StartupService(ICurrentUserRegistryStore registry)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public StartupState GetState()
    {
        var runValue = registry.ReadValue(RunSubKey, RunValueName);
        var ownerValue = registry.ReadValue(OwnershipSubKey, OwnerValueName);

        if (runValue.Kind == CurrentUserRegistryValueKind.NonString
            || ownerValue.Kind == CurrentUserRegistryValueKind.NonString)
        {
            return new StartupState(StartupStatus.ForeignValue, null);
        }

        if (runValue.Kind == CurrentUserRegistryValueKind.Missing)
        {
            return new StartupState(StartupStatus.Disabled, null);
        }

        if (!TryGetString(runValue, out var runText)
            || !TryGetString(ownerValue, out var ownerText)
            || !TryParseOwner(ownerText, out var owner))
        {
            return new StartupState(StartupStatus.ForeignValue, null);
        }

        if (!TryParseOwnedCommand(runText, out var executablePath, out var commandOwner))
        {
            return new StartupState(StartupStatus.ForeignValue, null);
        }

        if (owner != commandOwner)
        {
            return new StartupState(StartupStatus.ForeignValue, null);
        }

        return new StartupState(
            File.Exists(executablePath)
                ? StartupStatus.Enabled
                : StartupStatus.InvalidOwnedValue,
            executablePath);
    }

    public bool IsStartupLaunchAuthorized(string ownerArgument)
    {
        if (!TryParseOwner(ownerArgument, out var requestedOwner))
        {
            return false;
        }

        var ownerValue = registry.ReadValue(OwnershipSubKey, OwnerValueName);
        var runValue = registry.ReadValue(RunSubKey, RunValueName);
        return TryGetString(ownerValue, out var ownerText)
            && TryGetString(runValue, out var runText)
            && TryParseOwner(ownerText, out var storedOwner)
            && storedOwner == requestedOwner
            && TryParseOwnedCommand(runText, out _, out var commandOwner)
            && commandOwner == requestedOwner;
    }

    public StartupMutation BeginChange(bool enable, string executablePath)
    {
        var before = Capture();
        var after = PrepareChange(enable, executablePath, before);
        var mutation = new StartupMutation(this, before, after);

        try
        {
            Apply(before, after, enable);
            Verify(after);
            return mutation;
        }
        catch
        {
            mutation.Dispose();
            throw;
        }
    }

    public void ValidateChange(bool enable, string? executablePath)
    {
        var before = Capture();
        _ = PrepareChange(enable, executablePath, before);
    }

    internal static string BuildCommand(string executablePath, Guid owner)
    {
        var validatedPath = ValidateExecutablePath(
            executablePath,
            requireExists: true);
        var command = string.Create(
            CultureInfo.InvariantCulture,
            $"\"{validatedPath}\" --background --startup-owner={owner:D}");
        if (command.Length > MaximumCommandLength)
        {
            throw new StartupException("startup_command_too_long");
        }

        return command;
    }

    internal static bool TryParseOwnedCommand(
        string command,
        out string executablePath,
        out Guid owner)
    {
        executablePath = string.Empty;
        owner = Guid.Empty;

        if (string.IsNullOrEmpty(command)
            || command.Length > MaximumCommandLength
            || command.Any(char.IsControl)
            || command[0] != '"')
        {
            return false;
        }

        var closingQuote = command.IndexOf('"', 1);
        if (closingQuote < 2)
        {
            return false;
        }

        var suffix = command[(closingQuote + 1)..];
        const string expectedPrefix = " --background --startup-owner=";
        if (!suffix.StartsWith(expectedPrefix, StringComparison.Ordinal)
            || suffix.Length != expectedPrefix.Length + 36)
        {
            return false;
        }

        var path = command[1..closingQuote];
        try
        {
            path = ValidateExecutablePath(
                path,
                requireExists: false);
        }
        catch (StartupException)
        {
            return false;
        }

        if (!Guid.TryParseExact(suffix[expectedPrefix.Length..], "D", out owner)
            || owner == Guid.Empty)
        {
            return false;
        }

        executablePath = path;
        return true;
    }

    private static RegistrySnapshot PrepareEnable(
        string executablePath,
        RegistrySnapshot before)
    {
        RejectNonString(before);
        var hasRun = TryGetString(before.RunValue, out var currentRun);
        var hasOwner = TryGetString(before.OwnerValue, out var currentOwner);

        Guid owner;
        if (hasRun)
        {
            if (!hasOwner
                || !TryParseOwner(currentOwner, out owner)
                || !TryParseOwnedCommand(currentRun, out _, out var commandOwner)
                || owner != commandOwner)
            {
                throw new StartupException("startup_foreign_value");
            }
        }
        else if (!hasOwner || !TryParseOwner(currentOwner, out owner))
        {
            owner = Guid.NewGuid();
        }

        var command = BuildCommand(executablePath, owner);
        var ownerText = owner.ToString("D");
        return new RegistrySnapshot(
            CurrentUserRegistryValue.FromString(command),
            CurrentUserRegistryValue.FromString(ownerText));
    }

    private static RegistrySnapshot PrepareChange(
        bool enable,
        string? executablePath,
        RegistrySnapshot before) => enable
        ? PrepareEnable(executablePath ?? string.Empty, before)
        : PrepareDisable(before);

    private static RegistrySnapshot PrepareDisable(RegistrySnapshot before)
    {
        RejectNonString(before);
        if (before.RunValue.Kind == CurrentUserRegistryValueKind.Missing)
        {
            if (TryGetString(before.OwnerValue, out var ownerText)
                && TryParseOwner(ownerText, out _))
            {
                return new RegistrySnapshot(
                    CurrentUserRegistryValue.Missing,
                    CurrentUserRegistryValue.Missing);
            }

            return before;
        }

        if (!TryGetString(before.RunValue, out var runText)
            || !TryGetString(before.OwnerValue, out var storedOwner)
            || !TryParseOwner(storedOwner, out var owner)
            || !TryParseOwnedCommand(runText, out _, out var commandOwner)
            || owner != commandOwner)
        {
            throw new StartupException("startup_foreign_value");
        }

        return new RegistrySnapshot(
            CurrentUserRegistryValue.Missing,
            CurrentUserRegistryValue.Missing);
    }

    private RegistrySnapshot Capture() => new(
        registry.ReadValue(RunSubKey, RunValueName),
        registry.ReadValue(OwnershipSubKey, OwnerValueName));

    private void Apply(
        RegistrySnapshot before,
        RegistrySnapshot after,
        bool enable)
    {
        if (enable)
        {
            WriteValueIfChanged(
                OwnershipSubKey,
                OwnerValueName,
                before.OwnerValue,
                after.OwnerValue);
            WriteValueIfChanged(
                RunSubKey,
                RunValueName,
                before.RunValue,
                after.RunValue);
        }
        else
        {
            WriteValueIfChanged(
                RunSubKey,
                RunValueName,
                before.RunValue,
                after.RunValue);
            WriteValueIfChanged(
                OwnershipSubKey,
                OwnerValueName,
                before.OwnerValue,
                after.OwnerValue);
        }
    }

    private void WriteValueIfChanged(
        string subKey,
        string valueName,
        CurrentUserRegistryValue before,
        CurrentUserRegistryValue after)
    {
        if (before != after)
        {
            WriteValue(subKey, valueName, after);
        }
    }

    private void Verify(RegistrySnapshot expected)
    {
        if (Capture() != expected)
        {
            throw new StartupException("startup_verification_failed");
        }
    }

    private void RestoreIfUnchanged(
        RegistrySnapshot expectedCurrent,
        RegistrySnapshot restore)
    {
        RestoreValueIfEquals(
            RunSubKey,
            RunValueName,
            expectedCurrent.RunValue,
            restore.RunValue);
        RestoreValueIfEquals(
            OwnershipSubKey,
            OwnerValueName,
            expectedCurrent.OwnerValue,
            restore.OwnerValue);
    }

    private void RestoreValueIfEquals(
        string subKey,
        string valueName,
        CurrentUserRegistryValue expectedCurrent,
        CurrentUserRegistryValue restoreValue)
    {
        if (expectedCurrent == restoreValue)
        {
            return;
        }

        if (registry.ReadValue(subKey, valueName) != expectedCurrent)
        {
            return;
        }

        WriteValue(subKey, valueName, restoreValue);
    }

    private void WriteValue(
        string subKey,
        string valueName,
        CurrentUserRegistryValue value)
    {
        if (value.Kind == CurrentUserRegistryValueKind.Missing)
        {
            registry.DeleteValue(subKey, valueName);
        }
        else if (TryGetString(value, out var text))
        {
            registry.SetString(subKey, valueName, text);
        }
        else
        {
            throw new StartupException("startup_foreign_value");
        }
    }

    private static void RejectNonString(RegistrySnapshot snapshot)
    {
        if (snapshot.RunValue.Kind == CurrentUserRegistryValueKind.NonString
            || snapshot.OwnerValue.Kind == CurrentUserRegistryValueKind.NonString)
        {
            throw new StartupException("startup_foreign_value");
        }
    }

    private static bool TryGetString(
        CurrentUserRegistryValue value,
        out string text)
    {
        if (value.Kind == CurrentUserRegistryValueKind.String
            && value.StringValue is not null)
        {
            text = value.StringValue;
            return true;
        }

        text = string.Empty;
        return false;
    }

    private static bool TryParseOwner(string? value, out Guid owner) =>
        Guid.TryParseExact(value, "D", out owner) && owner != Guid.Empty;

    private static string ValidateExecutablePath(
        string executablePath,
        bool requireExists)
    {
        var executableName = Path.GetFileName(executablePath);
        if (string.IsNullOrWhiteSpace(executablePath)
            || executablePath.Length > MaximumCommandLength
            || executablePath.Any(character => char.IsControl(character) || character == '"')
            || !Path.IsPathFullyQualified(executablePath)
            || !string.Equals(
                executableName,
                ExpectedExecutableName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new StartupException("startup_executable_invalid");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(executablePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new StartupException("startup_executable_invalid");
        }

        if (requireExists && !File.Exists(fullPath))
        {
            throw new StartupException("startup_executable_missing");
        }

        return fullPath;
    }

    internal sealed record RegistrySnapshot(
        CurrentUserRegistryValue RunValue,
        CurrentUserRegistryValue OwnerValue);

    public sealed class StartupMutation : IDisposable
    {
        private readonly StartupService service;
        private readonly RegistrySnapshot before;
        private readonly RegistrySnapshot after;
        private bool committed;
        private bool disposed;

        internal StartupMutation(
            StartupService service,
            RegistrySnapshot before,
            RegistrySnapshot after)
        {
            this.service = service;
            this.before = before;
            this.after = after;
        }

        public void Commit()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            committed = true;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (!committed)
            {
                service.RestoreIfUnchanged(after, before);
            }
        }
    }
}
