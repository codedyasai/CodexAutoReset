using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CodexAutoReset.AppServer;

namespace CodexAutoReset.Desktop;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel viewModel;
    private readonly Func<bool, Task>? usageResetNotificationSettingChanged;
    private readonly DispatcherTimer weeklyThresholdSaveTimer;
    private readonly DispatcherTimer fiveHourThresholdSaveTimer;
    private bool weeklyThresholdSaveInProgress;
    private bool fiveHourThresholdSaveInProgress;
    private bool allowClose;

    public MainWindow(
        MainWindowViewModel viewModel,
        Func<bool, Task>? usageResetNotificationSettingChanged = null)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        this.usageResetNotificationSettingChanged =
            usageResetNotificationSettingChanged;
        DataContext = viewModel;

        weeklyThresholdSaveTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(650),
            DispatcherPriority.Background,
            OnWeeklyThresholdSaveTimerTick,
            Dispatcher);
        weeklyThresholdSaveTimer.Stop();
        fiveHourThresholdSaveTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(650),
            DispatcherPriority.Background,
            OnFiveHourThresholdSaveTimerTick,
            Dispatcher);
        fiveHourThresholdSaveTimer.Stop();
    }

    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    public void CloseForExit()
    {
        weeklyThresholdSaveTimer.Stop();
        fiveHourThresholdSaveTimer.Stop();
        allowClose = true;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs eventArgs)
    {
        if (allowClose)
        {
            return;
        }

        eventArgs.Cancel = true;
        Hide();
    }

    private void OnWindowPreviewMouseDown(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (Keyboard.FocusedElement
                is not System.Windows.Controls.TextBox focusedTextBox
            || eventArgs.OriginalSource is not DependencyObject clickedElement
            || FindVisualAncestor<System.Windows.Controls.TextBox>(
                clickedElement) is not null)
        {
            return;
        }

        var focusScope = FocusManager.GetFocusScope(focusedTextBox);
        FocusManager.SetFocusedElement(focusScope, null);
        Keyboard.ClearFocus();
    }

    private static TElement? FindVisualAncestor<TElement>(
        DependencyObject element)
        where TElement : DependencyObject
    {
        DependencyObject? current = element;
        while (current is not null)
        {
            if (current is TElement match)
            {
                return match;
            }

            current = current is System.Windows.Media.Visual
                or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private async void OnWeeklyAutomationToggleClick(
        object sender,
        RoutedEventArgs eventArgs) =>
        await HandleAutomationToggleAsync(
            (System.Windows.Controls.CheckBox)sender,
            isFiveHour: false);

    private async void OnFiveHourAutomationToggleClick(
        object sender,
        RoutedEventArgs eventArgs) =>
        await HandleAutomationToggleAsync(
            (System.Windows.Controls.CheckBox)sender,
            isFiveHour: true);

    private async Task HandleAutomationToggleAsync(
        System.Windows.Controls.CheckBox toggle,
        bool isFiveHour)
    {
        var enabled = toggle.IsChecked == true;

        if (isFiveHour)
        {
            await viewModel.SetFiveHourAutomationEnabledAsync(
                enabled,
                automationEnableConfirmed: enabled);
        }
        else
        {
            await viewModel.SetAutomationEnabledAsync(
                enabled,
                automationEnableConfirmed: enabled);
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs eventArgs) =>
        await viewModel.RefreshNowAsync();

    private void OnWeeklyThresholdTextChanged(
        object sender,
        TextChangedEventArgs eventArgs)
    {
        if (!IsLoaded)
        {
            return;
        }

        weeklyThresholdSaveTimer.Stop();
        weeklyThresholdSaveTimer.Start();
    }

    private void OnFiveHourThresholdTextChanged(
        object sender,
        TextChangedEventArgs eventArgs)
    {
        if (!IsLoaded)
        {
            return;
        }

        fiveHourThresholdSaveTimer.Stop();
        fiveHourThresholdSaveTimer.Start();
    }

    private async void OnNumericInputLostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs eventArgs)
    {
        if (ReferenceEquals(sender, WeeklyThresholdInput))
        {
            weeklyThresholdSaveTimer.Stop();
            await SaveThresholdFromUiAsync(isFiveHour: false);
            return;
        }

        fiveHourThresholdSaveTimer.Stop();
        await SaveThresholdFromUiAsync(isFiveHour: true);
    }

    private async void OnNumericInputKeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Enter)
        {
            return;
        }

        eventArgs.Handled = true;
        if (ReferenceEquals(sender, WeeklyThresholdInput))
        {
            weeklyThresholdSaveTimer.Stop();
            await SaveThresholdFromUiAsync(isFiveHour: false);
            return;
        }

        fiveHourThresholdSaveTimer.Stop();
        await SaveThresholdFromUiAsync(isFiveHour: true);
    }

    private async void OnWeeklyThresholdSaveTimerTick(
        object? sender,
        EventArgs eventArgs)
    {
        weeklyThresholdSaveTimer.Stop();
        await SaveThresholdFromUiAsync(isFiveHour: false);
    }

    private async void OnFiveHourThresholdSaveTimerTick(
        object? sender,
        EventArgs eventArgs)
    {
        fiveHourThresholdSaveTimer.Stop();
        await SaveThresholdFromUiAsync(isFiveHour: true);
    }

    private async void OnStartWithWindowsClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        var checkBox = (System.Windows.Controls.CheckBox)sender;
        await viewModel.SetStartWithWindowsAsync(checkBox.IsChecked == true);
    }

    private async void OnUsageResetNotificationClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        var checkBox = (System.Windows.Controls.CheckBox)sender;
        var enabled = checkBox.IsChecked == true;
        var saved = await viewModel.SetNotifyOnUsageResetAsync(enabled);
        if (saved && usageResetNotificationSettingChanged is not null)
        {
            await usageResetNotificationSettingChanged(enabled);
        }
    }

    private async Task SaveThresholdFromUiAsync(bool isFiveHour)
    {
        if (isFiveHour
            ? fiveHourThresholdSaveInProgress
            : weeklyThresholdSaveInProgress)
        {
            return;
        }

        if (isFiveHour)
        {
            fiveHourThresholdSaveInProgress = true;
        }
        else
        {
            weeklyThresholdSaveInProgress = true;
        }

        try
        {
            var immediateResetRiskConfirmed = false;
            var requiresConfirmation = isFiveHour
                ? viewModel.RequiresFiveHourThresholdChangeConfirmation()
                : viewModel.RequiresThresholdChangeConfirmation();
            if (requiresConfirmation)
            {
                var limitLabel = isFiveHour
                    ? "5시간 한도"
                    : "주간 한도";
                var result = System.Windows.MessageBox.Show(
                    this,
                    $"이 임계값을 적용하면 현재 {limitLabel} 잔여량이 조건에 들어가 "
                        + "초기화권 1개가 바로 사용될 수 있습니다.\n\n"
                        + "Codex 서버가 현재 초기화 가능한 한도들을 결정합니다. "
                        + "임계값을 적용하시겠습니까?",
                    "잔여량 임계값 변경",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);
                if (result != MessageBoxResult.Yes)
                {
                    if (isFiveHour)
                    {
                        viewModel.CancelFiveHourThresholdChange();
                    }
                    else
                    {
                        viewModel.CancelThresholdChange();
                    }

                    return;
                }

                immediateResetRiskConfirmed = true;
            }

            if (isFiveHour)
            {
                await viewModel.SaveFiveHourThresholdAsync(
                    immediateResetRiskConfirmed);
            }
            else
            {
                await viewModel.SaveThresholdAsync(
                    immediateResetRiskConfirmed);
            }
        }
        finally
        {
            if (isFiveHour)
            {
                fiveHourThresholdSaveInProgress = false;
            }
            else
            {
                weeklyThresholdSaveInProgress = false;
            }
        }
    }

    private async void OnSelectCodexExecutableClick(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Codex 실행 파일 선택",
            Filter = "Codex 실행 파일 (codex.exe)|codex.exe",
            FileName = "codex.exe",
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
            ValidateNames = true,
        };

        var suggestedPath = CodexExecutableLocator.TryGetFilePickerExecutablePath(
            viewModel.ConfiguredCodexExecutablePath);
        if (suggestedPath is not null)
        {
            var suggestedDirectory = Path.GetDirectoryName(suggestedPath);
            if (suggestedDirectory is not null)
            {
                dialog.FileName = suggestedPath;
                dialog.InitialDirectory = suggestedDirectory;
            }
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (!viewModel.TrySetCodexExecutablePath(dialog.FileName, out var errorMessage))
        {
            System.Windows.MessageBox.Show(
                this,
                errorMessage,
                "Codex 실행 파일 선택",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        await viewModel.SaveCodexExecutablePathAsync();
    }

    private async void OnUseAutomaticCodexExecutableClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        viewModel.UseAutomaticCodexExecutablePath();
        await viewModel.SaveCodexExecutablePathAsync();
    }
}
