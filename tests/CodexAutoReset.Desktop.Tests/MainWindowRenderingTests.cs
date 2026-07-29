using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
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
                var overallStatusPanel =
                    (Border)window.FindName("OverallStatusPanel");
                var compatibilityWarningIcon =
                    (TextBlock)window.FindName("CompatibilityWarningIcon");
                var overallStatusTitle =
                    (TextBlock)window.FindName("OverallStatusTitleText");
                var overallStatusBody =
                    (TextBlock)window.FindName("OverallStatusBodyText");
                var appHeaderGrid = (Grid)window.FindName("AppHeaderGrid");
                var codexConnectionPathBox =
                    (Border)window.FindName("CodexConnectionPathBox");
                var codexConnectionPathPanel =
                    (Grid)window.FindName("CodexConnectionPathPanel");
                var settingsSplitGrid = (Grid)window.FindName("SettingsSplitGrid");
                var settingsAutomationPanel =
                    (StackPanel)window.FindName("SettingsAutomationPanel");
                var settingsNumericPanel =
                    (StackPanel)window.FindName("SettingsNumericPanel");
                var startWithWindows =
                    (CheckBox)window.FindName("StartWithWindowsCheckBox");
                var automationToggle =
                    (CheckBox)window.FindName("AutomationToggle");
                var usageResetNotificationToggle =
                    (CheckBox)window.FindName("UsageResetNotificationToggle");
                var thresholdInput = (TextBox)window.FindName("ThresholdInput");
                var pollIntervalInput = (TextBox)window.FindName("PollIntervalInput");
                var refreshButton = (Button)window.FindName("RefreshButton");
                var buttons = FindVisualChildren<Button>(window).ToArray();
                var automaticFindButton = buttons.Single(button =>
                    string.Equals(GetButtonText(button), "자동 찾기", StringComparison.Ordinal));
                var directFindButton = buttons.Single(button =>
                    string.Equals(
                        GetButtonText(button),
                        "Codex.exe 직접 찾기",
                        StringComparison.Ordinal));
                var boundTextPaths = FindVisualChildren<TextBlock>(window)
                    .Select(textBlock =>
                        BindingOperations.GetBinding(textBlock, TextBlock.TextProperty)
                            ?.Path?.Path)
                    .Where(path => path is not null)
                    .ToArray();

                Assert.AreEqual(0, Grid.GetRow(connectionCard));
                Assert.AreEqual(1, Grid.GetRow(usageCard));
                Assert.AreEqual(2, Grid.GetRow(settingsCard));
                Assert.AreEqual(2, appHeaderGrid.ColumnDefinitions.Count);
                Assert.IsFalse(boundTextPaths.Contains(
                    "AutomationStateText",
                    StringComparer.Ordinal));
                Assert.IsFalse(boundTextPaths.Contains(
                    "CodexExecutableModeText",
                    StringComparer.Ordinal));
                Assert.IsTrue(boundTextPaths.Contains(
                    "OverallStatusTitle",
                    StringComparer.Ordinal));
                Assert.IsTrue(boundTextPaths.Contains(
                    "OverallStatus",
                    StringComparer.Ordinal));
                Assert.IsTrue(FindVisualChildren<TextBlock>(window).Any(textBlock =>
                    string.Equals(
                        textBlock.Text,
                        "마지막 정상 확인",
                        StringComparison.Ordinal)));
                Assert.IsTrue(codexConnectionPathBox.IsAncestorOf(
                    codexConnectionPathPanel));
                Assert.IsTrue(codexConnectionPathPanel.IsAncestorOf(
                    automaticFindButton));
                Assert.IsTrue(codexConnectionPathPanel.IsAncestorOf(
                    directFindButton));
                Assert.IsTrue(
                    automaticFindButton.TranslatePoint(
                        new Point(),
                        codexConnectionPathPanel).X
                    < directFindButton.TranslatePoint(
                        new Point(),
                        codexConnectionPathPanel).X);
                Assert.AreEqual(4, settingsSplitGrid.ColumnDefinitions.Count);
                Assert.AreEqual(0, Grid.GetColumn(settingsAutomationPanel));
                Assert.AreEqual(3, Grid.GetColumn(settingsNumericPanel));
                Assert.IsTrue(settingsAutomationPanel.IsAncestorOf(
                    startWithWindows));
                Assert.IsTrue(settingsAutomationPanel.IsAncestorOf(
                    automationToggle));
                Assert.IsTrue(settingsAutomationPanel.IsAncestorOf(
                    usageResetNotificationToggle));
                Assert.IsTrue(
                    startWithWindows.TranslatePoint(
                        new Point(),
                        settingsAutomationPanel).Y
                    < automationToggle.TranslatePoint(
                        new Point(),
                        settingsAutomationPanel).Y);
                Assert.IsTrue(
                    automationToggle.TranslatePoint(
                        new Point(),
                        settingsAutomationPanel).Y
                    < usageResetNotificationToggle.TranslatePoint(
                        new Point(),
                        settingsAutomationPanel).Y);
                Assert.IsTrue(settingsNumericPanel.IsAncestorOf(thresholdInput));
                Assert.IsTrue(settingsNumericPanel.IsAncestorOf(pollIntervalInput));
                Assert.IsTrue(
                    thresholdInput.TranslatePoint(new Point(), settingsNumericPanel).Y
                    < pollIntervalInput.TranslatePoint(
                        new Point(),
                        settingsNumericPanel).Y);
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
                Assert.IsFalse(buttons.Any(button =>
                    string.Equals(GetButtonText(button), "설정 저장", StringComparison.Ordinal)));
                Assert.IsFalse(buttons.Any(button =>
                    string.Equals(GetButtonText(button), "codex.exe 선택", StringComparison.Ordinal)));
                Assert.IsFalse(buttons.Any(button =>
                    string.Equals(GetButtonText(button), "자동으로 찾기", StringComparison.Ordinal)));

                var compatibilitySnapshot =
                    MonitorSnapshot.Waiting(GuardSettings.Default) with
                    {
                        ActionKind = CycleActionKind.Blocked,
                        StatusCode = "invalid_response",
                        IsFailure = true,
                        CompatibilityState =
                            CodexCompatibilityState.ReadUnsupported,
                    };
                var applySnapshot = typeof(MainWindowViewModel).GetMethod(
                    "ApplySnapshot",
                    System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic);
                Assert.IsNotNull(applySnapshot);
                applySnapshot.Invoke(viewModel, [compatibilitySnapshot]);
                window.UpdateLayout();

                Assert.AreEqual(Visibility.Visible, overallStatusPanel.Visibility);
                Assert.AreEqual(Visibility.Visible, compatibilityWarningIcon.Visibility);
                Assert.AreEqual(
                    "현재 Codex 응답을 지원하지 않습니다",
                    overallStatusTitle.Text);
                StringAssert.Contains(overallStatusBody.Text, "업데이트를 확인해 주세요");
                Assert.AreEqual(
                    Color.FromRgb(0xFF, 0xF4, 0xE5),
                    ((SolidColorBrush)overallStatusPanel.Background).Color);
                Assert.AreEqual(
                    Color.FromRgb(0xD9, 0x77, 0x06),
                    ((SolidColorBrush)overallStatusPanel.BorderBrush).Color);

                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                window.UpdateLayout();
                Assert.IsTrue(settingsAutomationPanel.ActualWidth > 170);
                Assert.IsTrue(settingsNumericPanel.ActualWidth > 170);
                Assert.IsTrue(
                    settingsNumericPanel.TranslatePoint(
                        new Point(),
                        settingsSplitGrid).X
                    >= settingsAutomationPanel.TranslatePoint(
                        new Point(settingsAutomationPanel.ActualWidth, 0),
                        settingsSplitGrid).X);
                Assert.IsTrue(
                    directFindButton.TranslatePoint(
                        new Point(directFindButton.ActualWidth, 0),
                        codexConnectionPathPanel).X
                    <= codexConnectionPathPanel.ActualWidth + 0.5);

                NotificationPopupWindowTestAssertions.Run();
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
