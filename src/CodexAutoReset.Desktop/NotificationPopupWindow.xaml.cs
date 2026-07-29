using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;

namespace CodexAutoReset.Desktop;

public partial class NotificationPopupWindow : Window
{
    private bool allowClose;
    private bool confirmationPending;

    public NotificationPopupWindow()
    {
        InitializeComponent();
    }

    public event EventHandler? ConfirmationRequested;

    public event EventHandler? OpenAppRequested;

    public void UpdateNotification(
        NotificationPopupRequest request,
        bool openAppAvailable)
    {
        ArgumentNullException.ThrowIfNull(request);
        confirmationPending = false;
        CloseButton.IsEnabled = true;
        ConfirmationFailureText.Visibility = Visibility.Collapsed;
        NotificationTitleText.Text = request.Title;
        NotificationMessageText.Text = request.Message;
        NotificationDetailText.Text = request.Detail ?? string.Empty;
        NotificationDetailText.Visibility = request.Detail is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        OpenAppButton.Visibility = openAppAvailable
            ? Visibility.Visible
            : Visibility.Collapsed;
        AutomationProperties.SetName(
            this,
            $"CodexAutoReset 사용량 초기화 알림: {request.Title}");
    }

    internal void SetConfirmationInProgress()
    {
        CloseButton.IsEnabled = false;
        ConfirmationFailureText.Visibility = Visibility.Collapsed;
    }

    internal void SetConfirmationFailed()
    {
        confirmationPending = false;
        CloseButton.IsEnabled = true;
        ConfirmationFailureText.Visibility = Visibility.Visible;
    }

    internal void CloseFromController()
    {
        allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs eventArgs)
    {
        if (allowClose)
        {
            base.OnClosing(eventArgs);
            return;
        }

        eventArgs.Cancel = true;
        base.OnClosing(eventArgs);
    }

    private void OnCloseClick(object sender, RoutedEventArgs eventArgs) =>
        RequestConfirmation();

    private void OnOpenAppClick(object sender, RoutedEventArgs eventArgs) =>
        OpenAppRequested?.Invoke(this, EventArgs.Empty);

    private void RequestConfirmation()
    {
        if (confirmationPending)
        {
            return;
        }

        confirmationPending = true;
        ConfirmationRequested?.Invoke(this, EventArgs.Empty);
    }
}
