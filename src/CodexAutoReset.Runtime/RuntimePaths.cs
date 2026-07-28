namespace CodexAutoReset.Runtime;

public sealed record RuntimePaths
{
    private RuntimePaths(string rootDirectory)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        SettingsFile = Path.Combine(RootDirectory, "settings.json");
        LiveStateFile = Path.Combine(RootDirectory, "live-state.json");
        LiveSafetyBlockFile = Path.Combine(RootDirectory, "live-safety-block.json");
        UsageResetStateFile = Path.Combine(RootDirectory, "usage-reset-state.json");
        CompatibilityNotificationStateFile = Path.Combine(
            RootDirectory,
            "compatibility-notification-state.json");
        InstanceLockFile = Path.Combine(RootDirectory, "instance.lock");
        LogDirectory = Path.Combine(RootDirectory, "Logs");
    }

    public string RootDirectory { get; }

    public string SettingsFile { get; }

    public string LiveStateFile { get; }

    public string LiveSafetyBlockFile { get; }

    public string UsageResetStateFile { get; }

    public string CompatibilityNotificationStateFile { get; }

    public string InstanceLockFile { get; }

    public string LogDirectory { get; }

    public static RuntimePaths ForCurrentUser()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("local_app_data_unavailable");
        }

        return new RuntimePaths(Path.Combine(localAppData, "CodexResetGuard"));
    }

    internal static RuntimePaths ForTesting(string rootDirectory) => new(rootDirectory);
}
