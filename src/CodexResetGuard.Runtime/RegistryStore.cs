using Microsoft.Win32;

namespace CodexResetGuard.Runtime;

public enum CurrentUserRegistryValueKind
{
    Missing,
    String,
    NonString,
}

public readonly record struct CurrentUserRegistryValue(
    CurrentUserRegistryValueKind Kind,
    string? StringValue)
{
    public static CurrentUserRegistryValue Missing { get; } = new(
        CurrentUserRegistryValueKind.Missing,
        null);

    public static CurrentUserRegistryValue NonString { get; } = new(
        CurrentUserRegistryValueKind.NonString,
        null);

    public static CurrentUserRegistryValue FromString(string value) => new(
        CurrentUserRegistryValueKind.String,
        value ?? throw new ArgumentNullException(nameof(value)));
}

public interface ICurrentUserRegistryStore
{
    CurrentUserRegistryValue ReadValue(string subKey, string valueName);

    void SetString(string subKey, string valueName, string value);

    void DeleteValue(string subKey, string valueName);
}

public sealed class WindowsCurrentUserRegistryStore : ICurrentUserRegistryStore
{
    public CurrentUserRegistryValue ReadValue(string subKey, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: false);
        if (key is null)
        {
            return CurrentUserRegistryValue.Missing;
        }

        var missingValue = new object();
        var value = key.GetValue(
            valueName,
            missingValue,
            RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (ReferenceEquals(value, missingValue))
        {
            return CurrentUserRegistryValue.Missing;
        }

        RegistryValueKind kind;
        try
        {
            kind = key.GetValueKind(valueName);
        }
        catch (ArgumentException)
        {
            return CurrentUserRegistryValue.Missing;
        }
        catch (IOException)
        {
            var retry = key.GetValue(
                valueName,
                missingValue,
                RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (ReferenceEquals(retry, missingValue))
            {
                return CurrentUserRegistryValue.Missing;
            }

            throw;
        }

        if (kind != RegistryValueKind.String)
        {
            return CurrentUserRegistryValue.NonString;
        }

        return value is string text
            ? CurrentUserRegistryValue.FromString(text)
            : CurrentUserRegistryValue.NonString;
    }

    public void SetString(string subKey, string valueName, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKey, writable: true)
            ?? throw new IOException("registry_key_unavailable");
        key.SetValue(valueName, value, RegistryValueKind.String);
    }

    public void DeleteValue(string subKey, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
