using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
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

                var connectionCard = (Border)window.FindName("CodexConnectionCard");
                var usageCard = (Border)window.FindName("UsageOverviewCard");
                var settingsCard = (Border)window.FindName("SettingsCard");
                var refreshButton = (Button)window.FindName("RefreshButton");
                var saveButton = (Button)window.FindName("SaveButton");
                var buttons = FindVisualChildren<Button>(window).ToArray();

                Assert.AreEqual(0, Grid.GetRow(connectionCard));
                Assert.AreEqual(1, Grid.GetRow(usageCard));
                Assert.AreEqual(2, Grid.GetRow(settingsCard));
                Assert.IsTrue(settingsCard.IsAncestorOf(saveButton));
                Assert.AreEqual("새로고침", refreshButton.ToolTip);
                Assert.AreEqual("주간 사용량 새로고침", AutomationProperties.GetName(refreshButton));
                Assert.AreEqual(
                    "\uE72C",
                    ((TextBlock)refreshButton.Content).Text);
                Assert.AreEqual(
                    1,
                    buttons.Count(button =>
                        AutomationProperties.GetName(button).Contains(
                            "새로고침",
                            StringComparison.Ordinal)));
                Assert.IsFalse(buttons.Any(button =>
                    string.Equals(GetButtonText(button), "지금 새로고침", StringComparison.Ordinal)));
                Assert.IsFalse(buttons.Any(button =>
                    string.Equals(GetButtonText(button), "창 숨기기", StringComparison.Ordinal)));

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

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static string? GetButtonText(Button button) => button.Content switch
    {
        string text => text,
        TextBlock textBlock => textBlock.Text,
        _ => null,
    };

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
