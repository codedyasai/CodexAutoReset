using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace CodexAutoReset.Desktop;

public sealed class NotificationPopupController : INotificationPopupPresenter
{
    private readonly Dispatcher dispatcher;
    private readonly Action? openApp;
    private NotificationPopupWindow? window;
    private Func<Task<bool>>? currentConfirmation;
    private string? currentNotificationId;
    private nint targetMonitor;
    private int disposeState;

    public NotificationPopupController(Action? openApp = null)
    {
        var applicationDispatcher =
            System.Windows.Application.Current?.Dispatcher;
        dispatcher = applicationDispatcher is not null
            && !applicationDispatcher.HasShutdownStarted
            && !applicationDispatcher.HasShutdownFinished
                ? applicationDispatcher
                : Dispatcher.CurrentDispatcher;
        this.openApp = openApp;
    }

    public bool IsVisible
    {
        get
        {
            if (Volatile.Read(ref disposeState) != 0
                || dispatcher.HasShutdownStarted)
            {
                return false;
            }

            return dispatcher.CheckAccess()
                ? window?.IsVisible == true
                : dispatcher.Invoke(() => window?.IsVisible == true);
        }
    }

    public void Show(
        NotificationPopupRequest request,
        Func<Task<bool>> confirmAsync)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(confirmAsync);
        ThrowIfDisposed();

        if (dispatcher.CheckAccess())
        {
            ShowCore(request, confirmAsync);
            return;
        }

        _ = dispatcher.BeginInvoke(
            () => ShowCore(request, confirmAsync),
            DispatcherPriority.Normal);
    }

    public void CloseAfterSuppression()
    {
        if (Volatile.Read(ref disposeState) != 0
            || dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            CloseWindowCore();
            return;
        }

        _ = dispatcher.BeginInvoke(
            CloseWindowCore,
            DispatcherPriority.Normal);
    }

    public void BringToFrontWithoutActivation()
    {
        if (Volatile.Read(ref disposeState) != 0
            || dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            BringToFrontCore();
            return;
        }

        _ = dispatcher.BeginInvoke(
            BringToFrontCore,
            DispatcherPriority.Normal);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposeState, 1) != 0
            || dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            CloseWindowCore();
            return;
        }

        dispatcher.Invoke(CloseWindowCore);
    }

    private void ShowCore(
        NotificationPopupRequest request,
        Func<Task<bool>> confirmAsync)
    {
        if (Volatile.Read(ref disposeState) != 0)
        {
            return;
        }

        currentConfirmation = confirmAsync;
        currentNotificationId = request.NotificationId;
        if (window is not null)
        {
            window.UpdateNotification(
                request,
                openAppAvailable: openApp is not null);
            window.UpdateLayout();
            targetMonitor = NotificationPopupPlacement.Position(
                window,
                targetMonitor,
                bringToFront: false);
            return;
        }

        window = new NotificationPopupWindow();
        window.ConfirmationRequested += OnConfirmationRequested;
        window.OpenAppRequested += OnOpenAppRequested;
        window.UpdateNotification(
            request,
            openAppAvailable: openApp is not null);
        window.Opacity = 0;
        window.Show();
        window.UpdateLayout();

        targetMonitor = NotificationPopupPlacement.GetActiveMonitor();
        targetMonitor = NotificationPopupPlacement.Position(
            window,
            targetMonitor,
            bringToFront: true);
        window.UpdateLayout();
        targetMonitor = NotificationPopupPlacement.Position(
            window,
            targetMonitor,
            bringToFront: false);
        window.Opacity = 1;
    }

    private async void OnConfirmationRequested(
        object? sender,
        EventArgs eventArgs)
    {
        var confirmation = currentConfirmation;
        var confirmingWindow = window;
        var notificationId = currentNotificationId;
        if (confirmation is null || confirmingWindow is null)
        {
            return;
        }

        confirmingWindow.SetConfirmationInProgress();
        var persisted = false;
        try
        {
            persisted = await confirmation();
        }
        catch (Exception)
        {
            persisted = false;
        }

        if (!ReferenceEquals(window, confirmingWindow)
            || !string.Equals(
                currentNotificationId,
                notificationId,
                StringComparison.Ordinal))
        {
            return;
        }

        if (persisted)
        {
            CloseWindowCore();
            return;
        }

        confirmingWindow.SetConfirmationFailed();
    }

    private void OnOpenAppRequested(object? sender, EventArgs eventArgs)
    {
        openApp?.Invoke();
        _ = dispatcher.BeginInvoke(
            BringToFrontCore,
            DispatcherPriority.ContextIdle);
    }

    private void BringToFrontCore()
    {
        if (window is null || !window.IsVisible)
        {
            return;
        }

        targetMonitor = NotificationPopupPlacement.Position(
            window,
            targetMonitor,
            bringToFront: true);
    }

    private void CloseWindowCore()
    {
        var closing = window;
        window = null;
        currentConfirmation = null;
        currentNotificationId = null;
        targetMonitor = 0;
        if (closing is null)
        {
            return;
        }

        closing.ConfirmationRequested -= OnConfirmationRequested;
        closing.OpenAppRequested -= OnOpenAppRequested;
        closing.CloseFromController();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref disposeState) != 0)
        {
            throw new ObjectDisposedException(
                nameof(NotificationPopupController));
        }
    }
}

internal static class NotificationPopupPlacement
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint SwpNoZOrder = 0x0004;
    private const double MarginDip = 16;
    private static readonly nint HwndTop = 0;

    public static nint GetActiveMonitor()
    {
        var foreground = GetForegroundWindow();
        if (foreground != 0)
        {
            var foregroundMonitor = MonitorFromWindow(
                foreground,
                MonitorDefaultToNearest);
            if (foregroundMonitor != 0)
            {
                return foregroundMonitor;
            }
        }

        if (GetCursorPos(out var cursor))
        {
            return MonitorFromPoint(cursor, MonitorDefaultToNearest);
        }

        return MonitorFromWindow(0, MonitorDefaultToNearest);
    }

    public static nint Position(
        Window window,
        nint preferredMonitor,
        bool bringToFront)
    {
        ArgumentNullException.ThrowIfNull(window);
        var monitor = preferredMonitor;
        if (!TryGetMonitorInfo(monitor, out var monitorInfo))
        {
            monitor = GetActiveMonitor();
            if (!TryGetMonitorInfo(monitor, out monitorInfo))
            {
                PositionOnPrimaryWorkingArea(window);
                return 0;
            }
        }

        var dpi = GetEffectiveDpi(monitor);
        var scale = dpi / 96d;
        var margin = (int)Math.Ceiling(MarginDip * scale);
        var width = Math.Max(
            1,
            (int)Math.Ceiling(window.ActualWidth * scale));
        var height = Math.Max(
            1,
            (int)Math.Ceiling(window.ActualHeight * scale));
        var left = monitorInfo.Work.Right - margin - width;
        var top = monitorInfo.Work.Bottom - margin - height;
        var handle = new WindowInteropHelper(window).Handle;
        var flags = SwpNoActivate | SwpNoOwnerZOrder;
        if (!bringToFront)
        {
            flags |= SwpNoZOrder;
        }

        if (!SetWindowPos(
            handle,
            HwndTop,
            left,
            top,
            width,
            height,
            flags))
        {
            var work = monitorInfo.Work;
            window.Left = work.Right / scale - window.ActualWidth - MarginDip;
            window.Top = work.Bottom / scale - window.ActualHeight - MarginDip;
        }

        return monitor;
    }

    private static void PositionOnPrimaryWorkingArea(Window window)
    {
        var workingArea = SystemParameters.WorkArea;
        window.Left = Math.Max(
            workingArea.Left,
            workingArea.Right - window.ActualWidth - MarginDip);
        window.Top = Math.Max(
            workingArea.Top,
            workingArea.Bottom - window.ActualHeight - MarginDip);
    }

    private static bool TryGetMonitorInfo(
        nint monitor,
        out MonitorInfo monitorInfo)
    {
        monitorInfo = new MonitorInfo
        {
            Size = (uint)Marshal.SizeOf<MonitorInfo>(),
        };
        return monitor != 0 && GetMonitorInfo(monitor, ref monitorInfo);
    }

    private static uint GetEffectiveDpi(nint monitor)
    {
        try
        {
            return GetDpiForMonitor(
                    monitor,
                    MonitorDpiType.Effective,
                    out var dpiX,
                    out _) == 0
                && dpiX is >= 96 and <= 768
                    ? dpiX
                    : 96;
        }
        catch (Exception exception) when (exception is
            DllNotFoundException
                or EntryPointNotFoundException)
        {
            return 96;
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(
        nint window,
        uint flags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(
        NativePoint point,
        uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(
        nint monitor,
        ref MonitorInfo monitorInfo);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        nint monitor,
        MonitorDpiType dpiType,
        out uint dpiX,
        out uint dpiY);

    private enum MonitorDpiType
    {
        Effective = 0,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;

        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;

        public int Top;

        public int Right;

        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;

        public NativeRect Monitor;

        public NativeRect Work;

        public uint Flags;
    }
}
