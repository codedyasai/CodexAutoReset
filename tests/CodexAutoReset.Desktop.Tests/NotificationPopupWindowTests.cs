using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CodexAutoReset.Core;
using CodexAutoReset.Desktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Forms = System.Windows.Forms;

namespace CodexAutoReset.Desktop.Tests;

internal static class NotificationPopupWindowTestAssertions
{
    public static void Run()
    {
        var openAppCount = 0;
        var firstConfirmationCount = 0;
        var secondConfirmationCount = 0;
        using var controller = new NotificationPopupController(
            () => openAppCount++);
        var first = NotificationPopupRequest.FromUsageReset(
            new WeeklyUsageResetDetection(
                WeeklyUsageResetKind.Scheduled,
                new DateTimeOffset(
                    2026,
                    8,
                    5,
                    12,
                    0,
                    0,
                    TimeSpan.FromHours(9)).ToUnixTimeSeconds(),
                new DateTimeOffset(
                    2026,
                    7,
                    29,
                    12,
                    0,
                    0,
                    TimeSpan.FromHours(9))));

        controller.Show(
            first,
            () =>
            {
                firstConfirmationCount++;
                return Task.FromResult(false);
            });

        var popup = GetActiveWindow(controller);
        popup.UpdateLayout();

        Assert.IsTrue(controller.IsVisible);
        Assert.IsTrue(popup.IsVisible);
        Assert.IsFalse(popup.IsActive);
        Assert.IsFalse(popup.ShowActivated);
        Assert.IsFalse(popup.ShowInTaskbar);
        Assert.IsFalse(popup.Topmost);
        Assert.AreEqual(WindowStyle.None, popup.WindowStyle);
        Assert.AreEqual(1d, popup.Opacity, 0.001);

        var shell = (Border)popup.FindName("PopupShell");
        var card = (Border)popup.FindName("NotificationCard");
        var wordmark =
            (Image)popup.FindName("ResetWordmarkImage");
        var title =
            (TextBlock)popup.FindName("NotificationTitleText");
        var message =
            (TextBlock)popup.FindName("NotificationMessageText");
        var detail =
            (TextBlock)popup.FindName("NotificationDetailText");
        var failure =
            (TextBlock)popup.FindName("ConfirmationFailureText");
        var actionPanel =
            (StackPanel)popup.FindName("ActionPanel");
        var openButton = (Button)popup.FindName("OpenAppButton");
        var closeButton = (Button)popup.FindName("CloseButton");

        Assert.AreEqual(
            Color.FromRgb(0xF8, 0xF2, 0xE8),
            ((SolidColorBrush)shell.Background).Color);
        Assert.AreEqual(
            Colors.White,
            ((SolidColorBrush)card.Background).Color);
        Assert.AreEqual(
            Color.FromRgb(0xE3, 0xDD, 0xD5),
            ((SolidColorBrush)card.BorderBrush).Color);
        Assert.AreEqual(126d, wordmark.Width, 0.001);
        Assert.AreEqual(48d, wordmark.Height, 0.001);
        Assert.AreEqual(Stretch.Uniform, wordmark.Stretch);
        StringAssert.EndsWith(
            wordmark.Source.ToString(),
            "Assets/CodexAutoResetWordmark.png");
        Assert.AreEqual(first.Title, title.Text);
        Assert.AreEqual(first.Message, message.Text);
        Assert.AreEqual(first.Detail, detail.Text);
        Assert.AreEqual(Visibility.Visible, detail.Visibility);
        Assert.AreEqual(
            "CodexAutoReset 옵션 열기",
            AutomationProperties.GetName(openButton));
        Assert.AreEqual(
            "알림 확인 및 닫기",
            AutomationProperties.GetName(closeButton));
        Assert.AreEqual(
            "CodexAutoReset 옵션 열기",
            openButton.Content);
        Assert.AreEqual(1, actionPanel.Children.Count);
        Assert.AreSame(openButton, actionPanel.Children[0]);
        Assert.AreEqual("×", closeButton.Content);
        Assert.IsTrue(openButton.ActualHeight >= 42);
        Assert.AreEqual(32d, closeButton.ActualWidth, 0.001);
        Assert.AreEqual(32d, closeButton.ActualHeight, 0.001);
        AssertPopupUsesSixteenDipWorkingAreaMargin(popup);

        popup.Close();
        Assert.AreEqual(0, firstConfirmationCount);
        Assert.IsTrue(controller.IsVisible);

        var presentationSource = PresentationSource.FromVisual(popup);
        Assert.IsNotNull(presentationSource);
        var escapeKey = new KeyEventArgs(
            Keyboard.PrimaryDevice,
            presentationSource,
            Environment.TickCount,
            Key.Escape)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
        popup.RaiseEvent(escapeKey);
        Assert.AreEqual(0, firstConfirmationCount);
        Assert.IsTrue(controller.IsVisible);

        controller.BringToFrontWithoutActivation();
        Assert.IsFalse(popup.IsActive);

        openButton.RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual(1, openAppCount);
        Assert.IsTrue(controller.IsVisible);

        closeButton.RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual(1, firstConfirmationCount);
        Assert.IsTrue(controller.IsVisible);
        Assert.IsTrue(closeButton.IsEnabled);
        Assert.AreEqual("×", closeButton.Content);
        Assert.AreEqual(Visibility.Visible, failure.Visibility);

        var second = NotificationPopupRequest.FromUsageReset(
            new WeeklyUsageResetDetection(
                WeeklyUsageResetKind.Early,
                new DateTimeOffset(
                    2026,
                    8,
                    6,
                    12,
                    0,
                    0,
                    TimeSpan.FromHours(9)).ToUnixTimeSeconds(),
                new DateTimeOffset(
                    2026,
                    7,
                    30,
                    12,
                    0,
                    0,
                    TimeSpan.FromHours(9))));
        controller.Show(
            second,
            () =>
            {
                secondConfirmationCount++;
                return Task.FromResult(true);
            });

        var updatedPopup = GetActiveWindow(controller);
        updatedPopup.UpdateLayout();
        Assert.AreSame(popup, updatedPopup);
        Assert.AreEqual(second.Title, title.Text);
        Assert.AreEqual(second.Message, message.Text);
        Assert.AreEqual(second.Detail, detail.Text);
        Assert.AreEqual(Visibility.Visible, detail.Visibility);
        Assert.AreEqual(Visibility.Collapsed, failure.Visibility);

        closeButton.RaiseEvent(
            new RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual(1, firstConfirmationCount);
        Assert.AreEqual(1, secondConfirmationCount);
        Assert.IsFalse(controller.IsVisible);
        Assert.IsNull(TryGetActiveWindow(controller));

        var suppressedConfirmationCount = 0;
        controller.Show(
            first,
            () =>
            {
                suppressedConfirmationCount++;
                return Task.FromResult(true);
            });
        controller.CloseAfterSuppression();
        Assert.AreEqual(0, suppressedConfirmationCount);
        Assert.IsFalse(controller.IsVisible);

        var shutdownConfirmationCount = 0;
        controller.Show(
            second,
            () =>
            {
                shutdownConfirmationCount++;
                return Task.FromResult(true);
            });
        controller.Dispose();
        Assert.AreEqual(0, shutdownConfirmationCount);
        Assert.IsFalse(controller.IsVisible);
    }

    private static NotificationPopupWindow GetActiveWindow(
        NotificationPopupController controller) =>
        TryGetActiveWindow(controller)
            ?? throw new AssertFailedException(
                "Notification popup was not created.");

    private static NotificationPopupWindow? TryGetActiveWindow(
        NotificationPopupController controller)
    {
        var field = typeof(NotificationPopupController).GetField(
            "window",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field);
        return (NotificationPopupWindow?)field.GetValue(controller);
    }

    private static void AssertPopupUsesSixteenDipWorkingAreaMargin(
        NotificationPopupWindow popup)
    {
        var handle = new WindowInteropHelper(popup).Handle;
        Assert.IsTrue(GetWindowRect(handle, out var bounds));
        var screen = Forms.Screen.FromHandle(handle);
        var dpi = GetDpiForWindow(handle);
        var scale = dpi == 0 ? 1d : dpi / 96d;
        var rightMargin =
            (screen.WorkingArea.Right - bounds.Right) / scale;
        var bottomMargin =
            (screen.WorkingArea.Bottom - bounds.Bottom) / scale;

        Assert.AreEqual(16d, rightMargin, 2d);
        Assert.AreEqual(16d, bottomMargin, 2d);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(
        nint window,
        out NativeRect rectangle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;

        public int Top;

        public int Right;

        public int Bottom;
    }
}
