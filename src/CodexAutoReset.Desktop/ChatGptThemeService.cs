using System.Windows;
using System.IO;
using System.Windows.Media;
using Microsoft.Win32;
using Color = System.Windows.Media.Color;
using System.Windows.Media.Imaging;

namespace CodexAutoReset.Desktop;

public sealed class ChatGptThemeService(string path, Func<bool> systemDark)
{
    public ChatGptTheme? Current { get; private set; }
    public static ChatGptThemeService CreateDefault()
    {
        var home = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        }
        return new ChatGptThemeService(Path.Combine(home, "config.toml"), () =>
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1) is int value && value == 0);
    }

    public async Task<bool> RefreshAsync()
    {
        try
        {
            var theme = await Task.Run(() =>
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > 1_048_576) { return null; }
                using var reader = new StreamReader(stream);
                var buffer = new char[1_048_577];
                var length = reader.ReadBlock(buffer, 0, buffer.Length);
                if (length > 1_048_576) { return null; }
                return ChatGptTheme.Parse(new string(buffer, 0, length), systemDark());
            }).ConfigureAwait(false);
            if (theme is null) { return false; }
            Current = theme;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or System.Security.SecurityException)
        {
            // Do not log configuration contents or disturb usage polling.
            return false;
        }
    }

    public void ApplyTo(ResourceDictionary resources)
    {
        if (Current is not { } theme) { return; }
        var background = ParseColor(theme.Background);
        var foreground = ParseColor(theme.Foreground);
        var accent = ParseColor(theme.Accent);
        var surface = Mix(background, foreground, .04);
        var border = Mix(background, foreground, .15);
        var secondary = Mix(background, foreground, .7);
        var colors = new Dictionary<string, Color>
        {
            ["AppBackgroundBrush"] = background,
            ["AppBarBrush"] = theme.Dark ? Color.FromRgb(32, 32, 32) : Color.FromRgb(248, 242, 232),
            ["CardBrush"] = background,
            ["SurfaceBrush"] = surface,
            ["BorderBrush"] = border,
            ["PrimaryTextBrush"] = foreground,
            ["SecondaryTextBrush"] = secondary,
            ["MutedTextBrush"] = Mix(background, foreground, .55),
            ["AccentBrush"] = accent,
            ["OnAccentBrush"] = Bright(accent) ? Colors.Black : Colors.White,
            ["InverseTextBrush"] = background,
            ["ToggleOffBrush"] = Mix(background, foreground, .25),
            ["PopupBackgroundBrush"] = background,
            ["PopupCardBrush"] = background,
            ["PopupSurfaceBrush"] = surface,
            ["PopupBorderBrush"] = border,
            ["PopupTextBrush"] = foreground,
            ["PopupSecondaryTextBrush"] = secondary,
        };
        foreach (var (key, color) in colors)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            resources[key] = brush;
        }
        resources["WordmarkImage"] = CreateWordmark(foreground, background);
    }

    private static BitmapSource CreateWordmark(Color foreground, Color background)
    {
        var original = new BitmapImage(new Uri("pack://application:,,,/CodexAutoReset;component/Assets/CodexAutoResetWordmark.png"));
        var bitmap = new FormatConvertedBitmap(original, PixelFormats.Bgra32, null, 0);
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            // Preserve the original antialiasing, alpha and RESET sticker lettering.
            var lightness = (pixels[offset] + pixels[offset + 1] + pixels[offset + 2]) / 3d;
            var color = Mix(foreground, background, Math.Clamp((lightness - 45) / 200, 0, 1));
            pixels[offset] = color.B;
            pixels[offset + 1] = color.G;
            pixels[offset + 2] = color.R;
        }
        var result = BitmapSource.Create(bitmap.PixelWidth, bitmap.PixelHeight, bitmap.DpiX, bitmap.DpiY,
            PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    private static Color ParseColor(string text) => (Color)System.Windows.Media.ColorConverter.ConvertFromString(text);
    private static Color Mix(Color from, Color to, double ratio) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * ratio),
        (byte)Math.Round(from.G + (to.G - from.G) * ratio),
        (byte)Math.Round(from.B + (to.B - from.B) * ratio));
    private static bool Bright(Color color) => .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B) > .179;
    private static double Channel(byte value) => value / 255d <= .04045 ? value / 255d / 12.92 : Math.Pow((value / 255d + .055) / 1.055, 2.4);
}
