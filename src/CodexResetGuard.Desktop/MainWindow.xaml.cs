using System.ComponentModel;
using System.Windows;

namespace CodexResetGuard.Desktop;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel viewModel;
    private bool allowClose;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = viewModel;
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

    private async void OnSaveClick(object sender, RoutedEventArgs eventArgs)
    {
        var automationEnableConfirmed = false;
        if (viewModel.RequiresAutomationEnableConfirmation)
        {
            var result = System.Windows.MessageBox.Show(
                this,
                "자동 초기화를 켜면 현재 주간 잔여량이 임계값 이하인 경우 "
                    + "설정을 저장한 직후 초기화권 1개가 사용될 수 있습니다.\n\n"
                    + "자동 초기화를 켜시겠습니까?",
                "자동 초기화 켜기",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (result != MessageBoxResult.Yes)
            {
                viewModel.CancelAutomationEnable();
                return;
            }

            automationEnableConfirmed = true;
        }

        await viewModel.SaveAsync(automationEnableConfirmed);
    }

    private void OnRefreshClick(object sender, RoutedEventArgs eventArgs) =>
        viewModel.RequestRefresh();

    private void OnHideClick(object sender, RoutedEventArgs eventArgs) => Hide();
}
