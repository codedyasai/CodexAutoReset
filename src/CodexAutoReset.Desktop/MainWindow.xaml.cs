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
    private readonly DispatcherTimer thresholdSaveTimer;
    private readonly DispatcherTimer pollIntervalSaveTimer;
    private bool thresholdSaveInProgress;
    private bool allowClose;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = viewModel;

        thresholdSaveTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(650),
            DispatcherPriority.Background,
            OnThresholdSaveTimerTick,
            Dispatcher);
        thresholdSaveTimer.Stop();
        pollIntervalSaveTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(650),
            DispatcherPriority.Background,
            OnPollIntervalSaveTimerTick,
            Dispatcher);
        pollIntervalSaveTimer.Stop();
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
        thresholdSaveTimer.Stop();
        pollIntervalSaveTimer.Stop();
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

    private async void OnAutomationToggleClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        var toggle = (System.Windows.Controls.CheckBox)sender;
        var enabled = toggle.IsChecked == true;
        var automationEnableConfirmed = false;
        if (enabled && viewModel.RequiresAutomationEnableConfirmation)
        {
            var result = System.Windows.MessageBox.Show(
                this,
                "초기화권 자동 사용을 켜면 현재 주간 잔여량이 임계값 이하인 경우 "
                    + "설정을 저장한 직후 초기화권 1개가 사용될 수 있습니다.\n\n"
                    + "초기화권 자동 사용을 켜시겠습니까?",
                "초기화권 자동 사용 켜기",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (result != MessageBoxResult.Yes)
            {
                viewModel.CancelAutomationEnable();
                toggle.IsChecked = viewModel.AutomationEnabled;
                return;
            }

            automationEnableConfirmed = true;
        }

        await viewModel.SetAutomationEnabledAsync(
            enabled,
            automationEnableConfirmed);
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs eventArgs) =>
        await viewModel.RefreshNowAsync();

    private void OnThresholdTextChanged(
        object sender,
        TextChangedEventArgs eventArgs)
    {
        if (!IsLoaded)
        {
            return;
        }

        thresholdSaveTimer.Stop();
        thresholdSaveTimer.Start();
    }

    private void OnPollIntervalTextChanged(
        object sender,
        TextChangedEventArgs eventArgs)
    {
        if (!IsLoaded)
        {
            return;
        }

        pollIntervalSaveTimer.Stop();
        pollIntervalSaveTimer.Start();
    }

    private async void OnNumericInputLostKeyboardFocus(
        object sender,
        KeyboardFocusChangedEventArgs eventArgs)
    {
        if (ReferenceEquals(sender, ThresholdInput))
        {
            thresholdSaveTimer.Stop();
            await SaveThresholdFromUiAsync();
            return;
        }

        pollIntervalSaveTimer.Stop();
        await viewModel.SavePollIntervalAsync();
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
        if (ReferenceEquals(sender, ThresholdInput))
        {
            thresholdSaveTimer.Stop();
            await SaveThresholdFromUiAsync();
            return;
        }

        pollIntervalSaveTimer.Stop();
        await viewModel.SavePollIntervalAsync();
    }

    private async void OnThresholdSaveTimerTick(
        object? sender,
        EventArgs eventArgs)
    {
        thresholdSaveTimer.Stop();
        await SaveThresholdFromUiAsync();
    }

    private async void OnPollIntervalSaveTimerTick(
        object? sender,
        EventArgs eventArgs)
    {
        pollIntervalSaveTimer.Stop();
        await viewModel.SavePollIntervalAsync();
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
        await viewModel.SetNotifyOnUsageResetAsync(checkBox.IsChecked == true);
    }

    private async Task SaveThresholdFromUiAsync()
    {
        if (thresholdSaveInProgress)
        {
            return;
        }

        thresholdSaveInProgress = true;
        try
        {
            var immediateResetRiskConfirmed = false;
            if (viewModel.RequiresThresholdChangeConfirmation())
            {
                var result = System.Windows.MessageBox.Show(
                    this,
                    "이 임계값을 적용하면 현재 주간 잔여량이 조건에 들어가 "
                        + "초기화권 1개가 바로 사용될 수 있습니다.\n\n"
                        + "임계값을 적용하시겠습니까?",
                    "잔여량 임계값 변경",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);
                if (result != MessageBoxResult.Yes)
                {
                    viewModel.CancelThresholdChange();
                    return;
                }

                immediateResetRiskConfirmed = true;
            }

            await viewModel.SaveThresholdAsync(immediateResetRiskConfirmed);
        }
        finally
        {
            thresholdSaveInProgress = false;
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
