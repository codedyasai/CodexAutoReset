using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CodexAutoReset.Desktop;

internal static class NativeHeaderBackdrop
{
    internal static bool Apply(Window window, bool dark)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)) { return false; }
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) { return false; }
        var enabled = !SystemParameters.HighContrast
            && DwmIsCompositionEnabled(out var composition) >= 0 && composition;
        var darkMode = dark ? 1 : 0;
        DwmSetWindowAttribute(handle, 20, ref darkMode, sizeof(int));
        var backdrop = enabled ? 2 : 1;
        var result = DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int));
        var margins = new Margins { Left = enabled && result >= 0 ? -1 : 0 };
        var extended = DwmExtendFrameIntoClientArea(handle, ref margins);
        if (enabled && result >= 0 && extended >= 0)
        {
            if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target)
            {
                target.BackgroundColor = Colors.Transparent;
                return true;
            }
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
}
