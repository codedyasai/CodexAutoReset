using System.IO;
using System.Windows;
using CodexAutoReset.Core;
using CodexAutoReset.Runtime;

namespace CodexAutoReset.Desktop;

public partial class App : System.Windows.Application
{
    private const string UninstallGuardMutexName =
        @"Local\CodexAutoReset-8D5D7C2C-6DE7-4B57-A788-4D8E4680B43B";

    private SingleInstanceLease? instanceLease;
    private SingleInstanceActivationChannel? activationChannel;
    private System.Threading.Mutex? uninstallGuardMutex;
    private GuardMonitorService? monitor;
    private MainWindowViewModel? viewModel;
    private TrayIconHost? tray;
    private UsageResetNotificationCoordinator?
        usageResetNotificationCoordinator;
    private bool stopping;

    protected override async void OnStartup(StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);

        if (!OperatingSystem.IsWindows())
        {
            Shutdown(2);
            return;
        }

        DesktopArguments arguments;
        try
        {
            arguments = DesktopArguments.Parse(eventArgs.Args);
        }
        catch (ArgumentException)
        {
            System.Windows.MessageBox.Show(
                "명령줄 인수가 올바르지 않습니다.",
                "CodexAutoReset",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown(2);
            return;
        }

        try
        {
            var paths = RuntimePaths.ForCurrentUser();
            instanceLease = SingleInstanceLease.TryAcquire(paths);
            if (instanceLease is null)
            {
                if (arguments.Background)
                {
                    Shutdown(4);
                    return;
                }

                var activationResult = SingleInstanceActivationResult.Unavailable;
                var activationDeadline = Environment.TickCount64 + 4_000;
                while (Environment.TickCount64 < activationDeadline)
                {
                    activationResult = await SingleInstanceActivationChannel
                        .TryActivateExistingAsync(
                            paths,
                            TimeSpan.FromMilliseconds(750),
                            CancellationToken.None);
                    if (activationResult
                        == SingleInstanceActivationResult.Activated)
                    {
                        Shutdown(0);
                        return;
                    }

                    instanceLease = SingleInstanceLease.TryAcquire(paths);
                    if (instanceLease is not null
                        || activationResult
                            == SingleInstanceActivationResult.DifferentSession)
                    {
                        break;
                    }

                    await Task.Delay(100);
                }

                if (instanceLease is null
                    && activationResult
                        != SingleInstanceActivationResult.DifferentSession)
                {
                    instanceLease = SingleInstanceLease.TryAcquire(paths);
                }

                if (instanceLease is null)
                {
                    ShowExistingInstanceActivationFailure(activationResult);
                    Shutdown(4);
                    return;
                }
            }

            uninstallGuardMutex = new System.Threading.Mutex(
                initiallyOwned: false,
                UninstallGuardMutexName);

            var startupService = new StartupService(
                new WindowsCurrentUserRegistryStore());
            if (arguments.StartupOwner is not null
                && !startupService.IsStartupLaunchAuthorized(arguments.StartupOwner))
            {
                Shutdown(2);
                return;
            }

            var settingsStore = new JsonSettingsStore(paths.SettingsFile);
            var settings = await settingsStore.LoadOrCreateAsync(CancellationToken.None);
            if (arguments.StartupOwner is not null && !settings.StartWithWindows)
            {
                Shutdown(2);
                return;
            }

            var usageResetTracker = new JsonWeeklyUsageResetTracker(
                paths.UsageResetStateFile);
            var cycleExecutor = new GuardCycleExecutor(
                paths,
                usageResetTracker);
            monitor = new GuardMonitorService(
                settingsStore,
                cycleExecutor,
                settings,
                new SafeJsonlLogger(paths.LogDirectory));

            viewModel = new MainWindowViewModel(
                settingsStore,
                startupService,
                monitor,
                settings);
            var window = new MainWindow(
                viewModel,
                SetUsageResetNotificationsEnabledAsync);
            MainWindow = window;
            activationChannel = SingleInstanceActivationChannel.TryStart(paths);
            activationChannel?.SetActivationHandler(
                cancellationToken => ActivateMainWindowAsync(
                    window,
                    cancellationToken));

            var popupPresenter = new NotificationPopupController(
                window.ShowAndActivate);
            usageResetNotificationCoordinator =
                new UsageResetNotificationCoordinator(
                    usageResetTracker,
                    popupPresenter);
            await usageResetNotificationCoordinator.InitializeAsync(
                settings.NotifyOnUsageReset,
                CancellationToken.None);

            tray = new TrayIconHost(
                viewModel,
                window,
                ShutdownSafelyAsync,
                paths.CompatibilityNotificationStateFile,
                usageResetNotificationCoordinator);
            await monitor.StartAsync();

            if (!arguments.Background)
            {
                window.ShowAndActivate();
                usageResetNotificationCoordinator.BringPendingToFront();
            }
        }
        catch (SettingsException exception)
        {
            ShowSafeStartupFailure(exception.ReasonCode);
            await ShutdownSafelyAsync(2);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or System.Security.SecurityException
                or StartupException)
        {
            var reasonCode = exception is StartupException startupException
                ? startupException.ReasonCode
                : "local_startup_failure";
            ShowSafeStartupFailure(reasonCode);
            await ShutdownSafelyAsync(3);
        }
        catch (Exception)
        {
            ShowSafeStartupFailure("unexpected_local_failure");
            await ShutdownSafelyAsync(3);
        }
    }

    private void ShowSafeStartupFailure(string reasonCode)
    {
        System.Windows.MessageBox.Show(
            $"안전하게 시작을 중단했습니다: {reasonCode}",
            "CodexAutoReset",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static void ShowExistingInstanceActivationFailure(
        SingleInstanceActivationResult activationResult)
    {
        var message = activationResult
            == SingleInstanceActivationResult.DifferentSession
            ? "CodexAutoReset가 다른 Windows 세션에서 실행 중입니다.\n\n"
                + "해당 세션에서 앱을 종료한 뒤 다시 실행해 주세요."
            : "CodexAutoReset은 실행 중이지만 기존 창과 연결하지 못했습니다.\n\n"
                + "알림 영역에 CodexAutoReset 아이콘이 있으면 더블클릭하고, "
                + "없으면 잠시 후 다시 실행해 주세요.";
        System.Windows.MessageBox.Show(
            message,
            "CodexAutoReset",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private async Task<bool> ActivateMainWindowAsync(
        MainWindow window,
        CancellationToken cancellationToken)
    {
        if (stopping || Dispatcher.HasShutdownStarted)
        {
            return false;
        }

        try
        {
            return await Dispatcher.InvokeAsync(() =>
            {
                if (stopping || Dispatcher.HasShutdownStarted)
                {
                    return false;
                }

                window.ShowAndActivate();
                usageResetNotificationCoordinator?.BringPendingToFront();
                return true;
            }, System.Windows.Threading.DispatcherPriority.Send, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    private async Task ShutdownSafelyAsync(int exitCode = 0)
    {
        if (stopping)
        {
            return;
        }

        stopping = true;
        if (activationChannel is not null)
        {
            await activationChannel.DisposeAsync();
            activationChannel = null;
        }

        tray?.Dispose();
        tray = null;

        if (usageResetNotificationCoordinator is not null)
        {
            await usageResetNotificationCoordinator.DisposeAsync();
            usageResetNotificationCoordinator = null;
        }

        if (viewModel is not null)
        {
            await viewModel.StopAndDrainSettingsAsync();
            viewModel = null;
        }

        if (monitor is not null)
        {
            await monitor.DisposeAsync();
            monitor = null;
        }

        instanceLease?.Dispose();
        instanceLease = null;
        Shutdown(exitCode);
    }

    private Task SetUsageResetNotificationsEnabledAsync(bool enabled) =>
        usageResetNotificationCoordinator?.SetEnabledAsync(
            enabled,
            CancellationToken.None)
        ?? Task.CompletedTask;

    private sealed record DesktopArguments(
        bool Background,
        string? StartupOwner)
    {
        public static DesktopArguments Parse(string[] args)
        {
            var background = false;
            string? startupOwner = null;

            foreach (var argument in args)
            {
                if (string.Equals(argument, "--background", StringComparison.Ordinal))
                {
                    if (background)
                    {
                        throw new ArgumentException("duplicate_argument");
                    }

                    background = true;
                    continue;
                }

                const string ownerPrefix = "--startup-owner=";
                if (argument.StartsWith(ownerPrefix, StringComparison.Ordinal)
                    && startupOwner is null)
                {
                    startupOwner = argument[ownerPrefix.Length..];
                    continue;
                }

                throw new ArgumentException("unsupported_argument");
            }

            if (startupOwner is not null && !background)
            {
                throw new ArgumentException("startup_owner_requires_background");
            }

            return new DesktopArguments(background, startupOwner);
        }
    }
}
