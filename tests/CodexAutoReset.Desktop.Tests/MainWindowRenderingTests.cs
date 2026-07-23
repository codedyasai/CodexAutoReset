using System.IO;
using System.Runtime.ExceptionServices;
using CodexAutoReset.Core;
using CodexAutoReset.Desktop;
using CodexAutoReset.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexAutoReset.Desktop.Tests;

[TestClass]
public sealed class MainWindowRenderingTests
{
    [TestMethod]
    public void MainWindow_CanShowAndCompleteInitialLayout()
    {
        Exception? failure = null;
        var completed = false;
        var thread = new Thread(() =>
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                $"CodexAutoReset-window-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            App? app = null;
            MainWindow? window = null;
            GuardMonitorService? monitor = null;
            MainWindowViewModel? viewModel = null;
            try
            {
                var settingsStore = new JsonSettingsStore(
                    Path.Combine(directory, "settings.json"));
                settingsStore.SaveAsync(GuardSettings.Default, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
                monitor = new GuardMonitorService(
                    settingsStore,
                    new NoOpCycleExecutor(),
                    GuardSettings.Default);
                viewModel = new MainWindowViewModel(
                    settingsStore,
                    new StartupService(new EmptyRegistryStore()),
                    monitor,
                    GuardSettings.Default,
                    () => null);

                app = new App();
                app.InitializeComponent();
                window = new MainWindow(viewModel);
                window.Show();
                window.UpdateLayout();
                completed = window.IsLoaded && window.ActualWidth > 0 && window.ActualHeight > 0;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                window?.CloseForExit();
                if (viewModel is not null)
                {
                    viewModel.StopAndDrainSettingsAsync().GetAwaiter().GetResult();
                }

                if (monitor is not null)
                {
                    monitor.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }

                app?.Shutdown();
                Directory.Delete(directory, recursive: true);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)), "Window layout timed out.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        Assert.IsTrue(completed, "Window did not complete its initial layout.");
    }

    private sealed class NoOpCycleExecutor : IGuardCycleExecutor
    {
        public Task<GuardCycleResult> ExecuteAsync(
            GuardSettings settings,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class EmptyRegistryStore : ICurrentUserRegistryStore
    {
        public CurrentUserRegistryValue ReadValue(string subKey, string valueName) =>
            CurrentUserRegistryValue.Missing;

        public void SetString(string subKey, string valueName, string value) =>
            throw new NotSupportedException();

        public void DeleteValue(string subKey, string valueName) =>
            throw new NotSupportedException();
    }
}
