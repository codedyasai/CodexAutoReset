using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CodexAutoReset.Core;
using CodexAutoReset.Desktop;
using CodexAutoReset.Runtime;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ellipse = System.Windows.Shapes.Ellipse;

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
                var usageCard =
                    (Border)window.FindName("UsageOverviewCard");
                var weeklyUsageSection =
                    (StackPanel)window.FindName("WeeklyUsageSection");
                var fiveHourUsageSection =
                    (Border)window.FindName("FiveHourUsageCard");
                var usageSummaryGrid =
                    (Grid)window.FindName("UsageSummaryGrid");
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
                var alwaysOnTopToggle =
                    (ToggleButton)window.FindName("AlwaysOnTopToggle");
                var codexConnectionPathBox =
                    (Border)window.FindName("CodexConnectionPathBox");
                var codexConnectionPathPanel =
                    (Grid)window.FindName("CodexConnectionPathPanel");
                var settingsSplitGrid = (Grid)window.FindName("SettingsSplitGrid");
                var settingsTopOptionsGrid =
                    (Grid)window.FindName("SettingsTopOptionsGrid");
                var settingsAutomationPanel =
                    (Grid)window.FindName("SettingsAutomationPanel");
                var settingsNotificationPanel =
                    (Grid)window.FindName("SettingsNotificationPanel");
                var settingsNumericPanel =
                    (Grid)window.FindName("SettingsNumericPanel");
                var weeklyAutomationRow =
                    (Grid)window.FindName("WeeklyAutomationRow");
                var fiveHourAutomationRow =
                    (Grid)window.FindName("FiveHourAutomationRow");
                var startWithWindows =
                    (CheckBox)window.FindName("StartWithWindowsCheckBox");
                var weeklyAutomationToggle =
                    (CheckBox)window.FindName("WeeklyAutomationToggle");
                var fiveHourAutomationToggle =
                    (CheckBox)window.FindName("FiveHourAutomationToggle");
                var usageResetNotificationToggle =
                    (CheckBox)window.FindName("UsageResetNotificationToggle");
                var weeklyThresholdInput =
                    (TextBox)window.FindName("WeeklyThresholdInput");
                var fiveHourThresholdInput =
                    (TextBox)window.FindName("FiveHourThresholdInput");
                var refreshButton = (Button)window.FindName("RefreshButton");
                var buttons = FindVisualChildren<Button>(window).ToArray();
                var progressBars =
                    FindVisualChildren<ProgressBar>(window).ToArray();
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
                var creditStatusValue = FindVisualChildren<TextBlock>(window)
                    .Single(textBlock => string.Equals(
                        BindingOperations.GetBinding(
                            textBlock,
                            TextBlock.TextProperty)?.Path?.Path,
                        "CreditStatus",
                        StringComparison.Ordinal));
                var lastCheckedStatusValue = FindVisualChildren<TextBlock>(window)
                    .Single(textBlock => string.Equals(
                        BindingOperations.GetBinding(
                            textBlock,
                            TextBlock.TextProperty)?.Path?.Path,
                        "LastCheckedStatus",
                        StringComparison.Ordinal));

                Assert.AreEqual(0, Grid.GetRow(connectionCard));
                Assert.AreEqual(1, Grid.GetRow(usageCard));
                Assert.AreEqual(2, Grid.GetRow(settingsCard));
                Assert.IsTrue(usageCard.IsAncestorOf(weeklyUsageSection));
                Assert.IsTrue(usageCard.IsAncestorOf(fiveHourUsageSection));
                Assert.IsTrue(usageCard.IsAncestorOf(refreshButton));
                Assert.IsTrue(usageCard.IsAncestorOf(usageSummaryGrid));
                Assert.IsTrue(
                    weeklyUsageSection.TranslatePoint(
                        new Point(0, weeklyUsageSection.ActualHeight),
                        window).Y
                    <= fiveHourUsageSection.TranslatePoint(
                        new Point(),
                        window).Y);
                Assert.IsTrue(
                    fiveHourUsageSection.TranslatePoint(
                        new Point(0, fiveHourUsageSection.ActualHeight),
                        window).Y
                    <= usageSummaryGrid.TranslatePoint(
                        new Point(),
                        window).Y);
                Assert.AreEqual(3, appHeaderGrid.ColumnDefinitions.Count);
                Assert.AreEqual(2, Grid.GetColumn(alwaysOnTopToggle));
                Assert.IsFalse(alwaysOnTopToggle.IsChecked);
                Assert.IsFalse(window.Topmost);
                Assert.AreEqual(
                    "창을 항상 위에 고정",
                    alwaysOnTopToggle.ToolTip);
                Assert.AreEqual(
                    "창을 항상 위에 고정",
                    AutomationProperties.GetName(alwaysOnTopToggle));
                Assert.AreEqual(
                    "켜면 이 창이 다른 창 뒤로 넘어가지 않습니다.",
                    AutomationProperties.GetHelpText(alwaysOnTopToggle));
                Assert.AreEqual(
                    "\uE718",
                    ((TextBlock)alwaysOnTopToggle.Template.FindName(
                        "PinGlyph",
                        alwaysOnTopToggle)).Text);
                var pinnedStateTrigger = alwaysOnTopToggle.Style.Triggers
                    .OfType<Trigger>()
                    .Single(trigger =>
                        trigger.Property == ToggleButton.IsCheckedProperty
                        && Equals(trigger.Value, true));
                Assert.IsTrue(pinnedStateTrigger.Setters
                    .OfType<Setter>()
                    .Any(setter =>
                        setter.Property == FrameworkElement.ToolTipProperty
                        && Equals(setter.Value, "창 고정 해제")));
                Assert.IsTrue(pinnedStateTrigger.Setters
                    .OfType<Setter>()
                    .Any(setter =>
                        setter.Property == AutomationProperties.NameProperty
                        && Equals(setter.Value, "창 고정 해제")));
                Assert.IsTrue(pinnedStateTrigger.Setters
                    .OfType<Setter>()
                    .Any(setter =>
                        setter.Property
                            == AutomationProperties.ItemStatusProperty
                        && Equals(setter.Value, "고정됨")));
                Assert.IsTrue(
                    alwaysOnTopToggle.TranslatePoint(
                        new Point(alwaysOnTopToggle.ActualWidth, 0),
                        appHeaderGrid).X
                    <= appHeaderGrid.ActualWidth + 0.5);
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
                Assert.IsTrue(boundTextPaths.Contains(
                    "FiveHourRemainingText",
                    StringComparer.Ordinal));
                Assert.IsTrue(boundTextPaths.Contains(
                    "FiveHourResetStatus",
                    StringComparer.Ordinal));
                Assert.IsTrue(FindVisualChildren<TextBlock>(window).Any(textBlock =>
                    string.Equals(
                        textBlock.Text,
                        "최근 사용량 확인",
                        StringComparison.Ordinal)));
                Assert.IsFalse(FindVisualChildren<TextBlock>(window).Any(textBlock =>
                    string.Equals(
                        textBlock.Text,
                        "마지막 정상 확인",
                        StringComparison.Ordinal)));
                Assert.AreEqual(16, creditStatusValue.FontSize);
                Assert.AreEqual(
                    creditStatusValue.FontSize,
                    lastCheckedStatusValue.FontSize);
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
                Assert.AreEqual(3, settingsSplitGrid.RowDefinitions.Count);
                Assert.IsTrue(settingsSplitGrid.IsAncestorOf(
                    settingsTopOptionsGrid));
                Assert.IsTrue(settingsSplitGrid.IsAncestorOf(
                    settingsNumericPanel));
                Assert.IsNull(window.FindName("AutomationUseWarningPanel"));
                Assert.AreEqual(3, settingsTopOptionsGrid.ColumnDefinitions.Count);
                Assert.AreEqual(0, Grid.GetColumn(settingsAutomationPanel));
                Assert.AreEqual(2, Grid.GetColumn(settingsNotificationPanel));
                Assert.IsTrue(settingsTopOptionsGrid.IsAncestorOf(
                    settingsAutomationPanel));
                Assert.IsTrue(settingsTopOptionsGrid.IsAncestorOf(
                    settingsNotificationPanel));
                Assert.IsTrue(settingsAutomationPanel.IsAncestorOf(
                    startWithWindows));
                Assert.IsTrue(settingsNotificationPanel.IsAncestorOf(
                    usageResetNotificationToggle));
                Assert.IsTrue(
                    settingsAutomationPanel.TranslatePoint(
                        new Point(),
                        settingsTopOptionsGrid).X
                    < settingsNotificationPanel.TranslatePoint(
                        new Point(),
                        settingsTopOptionsGrid).X);
                Assert.AreEqual(
                    startWithWindows.TranslatePoint(
                        new Point(),
                        settingsTopOptionsGrid).Y,
                    usageResetNotificationToggle.TranslatePoint(
                        new Point(),
                        settingsTopOptionsGrid).Y,
                    0.5);
                Assert.IsTrue(settingsNumericPanel.IsAncestorOf(
                    weeklyAutomationRow));
                Assert.IsTrue(settingsNumericPanel.IsAncestorOf(
                    fiveHourAutomationRow));
                Assert.IsTrue(weeklyAutomationRow.IsAncestorOf(
                    weeklyAutomationToggle));
                Assert.IsTrue(fiveHourAutomationRow.IsAncestorOf(
                    fiveHourAutomationToggle));
                Assert.IsTrue(settingsNumericPanel.IsAncestorOf(
                    weeklyThresholdInput));
                Assert.IsTrue(settingsNumericPanel.IsAncestorOf(
                    fiveHourThresholdInput));
                Assert.AreEqual(string.Empty, weeklyThresholdInput.Text);
                Assert.IsFalse(weeklyAutomationToggle.IsChecked);
                Assert.AreEqual(string.Empty, fiveHourThresholdInput.Text);
                Assert.IsFalse(fiveHourAutomationToggle.IsChecked);
                Assert.IsTrue(
                    weeklyAutomationRow.TranslatePoint(
                        new Point(0, weeklyAutomationRow.ActualHeight),
                        settingsNumericPanel).Y
                    <= fiveHourAutomationRow.TranslatePoint(
                        new Point(),
                        settingsNumericPanel).Y);
                Assert.AreEqual(2, Grid.GetColumn(weeklyThresholdInput));
                Assert.AreEqual(5, Grid.GetColumn(weeklyAutomationToggle));
                Assert.AreEqual(2, Grid.GetColumn(fiveHourThresholdInput));
                Assert.AreEqual(5, Grid.GetColumn(fiveHourAutomationToggle));
                Assert.IsTrue(
                    weeklyThresholdInput.TranslatePoint(
                        new Point(weeklyThresholdInput.ActualWidth, 0),
                        weeklyAutomationRow).X
                    < weeklyAutomationToggle.TranslatePoint(
                        new Point(),
                        weeklyAutomationRow).X);
                Assert.IsTrue(
                    fiveHourThresholdInput.TranslatePoint(
                        new Point(fiveHourThresholdInput.ActualWidth, 0),
                        fiveHourAutomationRow).X
                    < fiveHourAutomationToggle.TranslatePoint(
                        new Point(),
                        fiveHourAutomationRow).X);
                Assert.IsNull(window.FindName("PollIntervalInput"));
                Assert.IsFalse(boundTextPaths.Contains(
                    "PollIntervalText",
                    StringComparer.Ordinal));
                Assert.AreEqual(
                    2,
                    FindVisualChildren<Label>(window).Count(label =>
                        string.Equals(
                            label.Content as string,
                            "잔여량 임계값 (0~99)",
                            StringComparison.Ordinal)));
                Assert.AreEqual(
                    "공란 또는 0에서 99 사이 숫자를 입력합니다.",
                    AutomationProperties.GetHelpText(weeklyThresholdInput));
                Assert.AreEqual(
                    "공란 또는 0에서 99 사이 숫자를 입력합니다.",
                    AutomationProperties.GetHelpText(fiveHourThresholdInput));
                Assert.AreEqual("새로고침", refreshButton.ToolTip);
                Assert.AreEqual(
                    "사용량 새로고침",
                    AutomationProperties.GetName(refreshButton));
                Assert.AreEqual(
                    "주간 및 5시간 사용량을 다시 확인합니다.",
                    AutomationProperties.GetHelpText(refreshButton));
                Assert.AreEqual(
                    "\uE72C",
                    ((TextBlock)refreshButton.Content).Text);
                Assert.IsTrue(progressBars.Any(progressBar =>
                    string.Equals(
                        AutomationProperties.GetName(progressBar),
                        "주간 한도 잔여량 백분율",
                        StringComparison.Ordinal)));
                Assert.IsTrue(progressBars.Any(progressBar =>
                    string.Equals(
                        AutomationProperties.GetName(progressBar),
                        "5시간 한도 잔여량 백분율",
                        StringComparison.Ordinal)));
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
                Assert.IsTrue(FindVisualChildren<TextBlock>(window).Any(textBlock =>
                    string.Equals(
                        textBlock.Text,
                        "초기화권 자동 사용",
                        StringComparison.Ordinal)));
                Assert.IsTrue(FindVisualChildren<TextBlock>(window).Any(textBlock =>
                    string.Equals(
                        textBlock.Text,
                        "5시간 한도 잔여량",
                        StringComparison.Ordinal)));
                Assert.IsFalse(FindVisualChildren<TextBlock>(window).Any(textBlock =>
                    string.Equals(
                        textBlock.Text,
                        "확인 주기",
                        StringComparison.Ordinal)));
                Assert.IsFalse(FindVisualChildren<TextBlock>(window).Any(textBlock =>
                    textBlock.Text?.StartsWith(
                        "켜면 주간 잔여량이",
                        StringComparison.Ordinal) == true));
                Assert.IsFalse(FindVisualChildren<TextBlock>(window).Any(textBlock =>
                    textBlock.Text?.Contains("1~100%", StringComparison.Ordinal) == true));
                Assert.AreEqual(
                    "선택하면 연결 경로만 바로 저장됩니다. 초기화권 자동 사용 설정은 바뀌지 않으며, 사용량은 다음 1분 확인에서 반영됩니다.",
                    AutomationProperties.GetHelpText(directFindButton));
                AssertNumericInputPointerFocusBehavior(
                    weeklyThresholdInput,
                    fiveHourThresholdInput,
                    usageCard);
                AssertAutomationToggleLayoutStability(
                    window,
                    viewModel,
                    settingsCard,
                    weeklyAutomationToggle,
                    fiveHourAutomationToggle);
                AssertToggleThumbAnimationDefinition(
                    window,
                    weeklyAutomationToggle);

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
                Assert.IsTrue(settingsAutomationPanel.ActualWidth > 180);
                Assert.IsTrue(settingsNotificationPanel.ActualWidth > 180);
                Assert.IsTrue(settingsNumericPanel.ActualWidth > 440);
                Assert.IsTrue(
                    settingsNotificationPanel.TranslatePoint(
                        new Point(),
                        settingsTopOptionsGrid).X
                    >= settingsAutomationPanel.TranslatePoint(
                        new Point(settingsAutomationPanel.ActualWidth, 0),
                        settingsTopOptionsGrid).X);
                Assert.IsTrue(
                    weeklyAutomationToggle.TranslatePoint(
                        new Point(weeklyAutomationToggle.ActualWidth, 0),
                        weeklyAutomationRow).X
                    <= weeklyAutomationRow.ActualWidth + 0.5);
                Assert.IsTrue(
                    fiveHourAutomationToggle.TranslatePoint(
                        new Point(fiveHourAutomationToggle.ActualWidth, 0),
                        fiveHourAutomationRow).X
                    <= fiveHourAutomationRow.ActualWidth + 0.5);
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

    private static void AssertNumericInputPointerFocusBehavior(
        TextBox weeklyThresholdInput,
        TextBox fiveHourThresholdInput,
        UIElement nonTextTarget)
    {
        Assert.IsTrue(
            weeklyThresholdInput.Focus(),
            "The weekly threshold input should accept keyboard focus.");
        Assert.AreSame(weeklyThresholdInput, Keyboard.FocusedElement);
        Assert.IsTrue(weeklyThresholdInput.IsKeyboardFocusWithin);

        Assert.IsFalse(RaisePreviewMouseDown(nonTextTarget));

        Assert.IsFalse(
            weeklyThresholdInput.IsKeyboardFocusWithin,
            "Clicking outside every text box should hide its caret and clear focus.");
        Assert.AreNotSame(weeklyThresholdInput, Keyboard.FocusedElement);

        Assert.IsTrue(weeklyThresholdInput.Focus());
        Assert.AreSame(weeklyThresholdInput, Keyboard.FocusedElement);

        Assert.IsFalse(
            RaisePreviewMouseDown(fiveHourThresholdInput),
            "A text-box click must remain available to normal WPF input processing.");

        Assert.AreSame(
            weeklyThresholdInput,
            Keyboard.FocusedElement,
            "The window preview handler must leave focus intact during a text-box click.");
        Assert.IsTrue(
            fiveHourThresholdInput.Focus(),
            "The clicked text box should receive focus during normal input processing.");
        Assert.AreSame(fiveHourThresholdInput, Keyboard.FocusedElement);
        Assert.IsTrue(fiveHourThresholdInput.IsKeyboardFocusWithin);
        Assert.IsFalse(weeklyThresholdInput.IsKeyboardFocusWithin);
    }

    private static bool RaisePreviewMouseDown(UIElement target)
    {
        var eventArgs = new MouseButtonEventArgs(
            Mouse.PrimaryDevice,
            Environment.TickCount,
            MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent,
        };
        target.RaiseEvent(eventArgs);
        return eventArgs.Handled;
    }

    private static void AssertAutomationToggleLayoutStability(
        MainWindow window,
        MainWindowViewModel viewModel,
        Border settingsCard,
        CheckBox weeklyAutomationToggle,
        CheckBox fiveHourAutomationToggle)
    {
        SetSaveStatus(viewModel, string.Empty);
        weeklyAutomationToggle.IsChecked = false;
        fiveHourAutomationToggle.IsChecked = false;
        window.UpdateLayout();
        var emptyStatusDisabledHeight = settingsCard.ActualHeight;

        weeklyAutomationToggle.IsChecked = true;
        fiveHourAutomationToggle.IsChecked = true;
        window.UpdateLayout();
        var emptyStatusEnabledHeight = settingsCard.ActualHeight;

        Assert.AreEqual(
            emptyStatusDisabledHeight,
            emptyStatusEnabledHeight,
            0.5,
            "Automation toggles must not add or remove warning space.");

        SetSaveStatus(viewModel, "설정을 저장했습니다.");
        weeklyAutomationToggle.IsChecked = false;
        fiveHourAutomationToggle.IsChecked = false;
        window.UpdateLayout();
        var successStatusDisabledHeight = settingsCard.ActualHeight;

        weeklyAutomationToggle.IsChecked = true;
        fiveHourAutomationToggle.IsChecked = true;
        window.UpdateLayout();
        var successStatusEnabledHeight = settingsCard.ActualHeight;

        Assert.IsTrue(
            successStatusDisabledHeight >= emptyStatusDisabledHeight,
            "A visible one-line save status may occupy its own fixed status area.");
        Assert.AreEqual(
            successStatusDisabledHeight,
            successStatusEnabledHeight,
            0.5,
            "Automation toggles must not change card height with SaveStatus visible.");
    }

    private static void AssertToggleThumbAnimationDefinition(
        MainWindow window,
        CheckBox toggle)
    {
        toggle.IsChecked = false;
        window.UpdateLayout();

        var thumb = toggle.Template.FindName("Thumb", toggle) as Ellipse;
        Assert.IsNotNull(thumb, "The toggle template must expose its thumb.");
        var translation = thumb.RenderTransform as TranslateTransform;
        Assert.IsNotNull(
            translation,
            "The toggle thumb must move with a TranslateTransform.");
        Assert.AreEqual(0, translation.X, 0.01);

        var checkedTrigger = toggle.Template.Triggers
            .OfType<Trigger>()
            .Single(trigger =>
                trigger.Property == CheckBox.IsCheckedProperty
                && Equals(trigger.Value, true));
        var enablingAnimation = FindThumbTranslationAnimation(
            checkedTrigger.EnterActions);
        var disablingAnimation = FindThumbTranslationAnimation(
            checkedTrigger.ExitActions);

        AssertThumbTranslationProperty(enablingAnimation);
        AssertThumbTranslationProperty(disablingAnimation);
        Assert.AreEqual(
            22d,
            enablingAnimation.To.GetValueOrDefault(),
            0.01);
        Assert.AreEqual(
            0d,
            disablingAnimation.To.GetValueOrDefault(),
            0.01);
        Assert.IsTrue(
            enablingAnimation.Duration.HasTimeSpan
            && enablingAnimation.Duration.TimeSpan > TimeSpan.Zero);
        Assert.IsTrue(
            disablingAnimation.Duration.HasTimeSpan
            && disablingAnimation.Duration.TimeSpan > TimeSpan.Zero);
    }

    private static void SetSaveStatus(
        MainWindowViewModel viewModel,
        string status)
    {
        var property = typeof(MainWindowViewModel).GetProperty(
            nameof(MainWindowViewModel.SaveStatus));
        var setter = property?.GetSetMethod(nonPublic: true);
        Assert.IsNotNull(setter);
        setter.Invoke(viewModel, [status]);
    }

    private static DoubleAnimation FindThumbTranslationAnimation(
        TriggerActionCollection actions)
    {
        return actions
            .OfType<BeginStoryboard>()
            .SelectMany(action => action.Storyboard.Children)
            .OfType<DoubleAnimation>()
            .Single(animation =>
                string.Equals(
                    Storyboard.GetTargetName(animation),
                    "Thumb",
                    StringComparison.Ordinal));
    }

    private static void AssertThumbTranslationProperty(
        DoubleAnimation animation)
    {
        var targetProperty = Storyboard.GetTargetProperty(animation);
        Assert.IsNotNull(targetProperty);
        var pathParameters = targetProperty.PathParameters
            .Cast<object>()
            .ToArray();
        Assert.IsTrue(
            targetProperty.Path.Contains(
                "TranslateTransform.X",
                StringComparison.Ordinal)
            || pathParameters.Contains(TranslateTransform.XProperty),
            "The thumb animation must target TranslateTransform.X.");
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
