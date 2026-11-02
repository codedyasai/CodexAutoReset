using CodexAutoReset.Desktop;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexAutoReset.Desktop.Tests;

[TestClass]
public sealed class ChatGptThemeTests
{
    private const string Config = """
        [desktop]
        appearanceTheme = "system"
        appearanceLightCodeThemeId = "codex"
        [desktop.appearanceLightChromeTheme]
        accent = "#0169cc" # custom
        surface = "#ffffff"
        ink = "#0d0d0d"
        contrast = 99
        [desktop.appearanceDarkChromeTheme]
        accent = '#aabbcc'
        surface = '#101112'
        ink = '#eeeeee'
        """;

    [TestMethod]
    public void Parse_SelectsSystemVariantAndIgnoresAdvancedSettings()
    {
        Assert.AreEqual(new ChatGptTheme(false, "#0169cc", "#ffffff", "#0d0d0d"), ChatGptTheme.Parse(Config, false));
        Assert.AreEqual(new ChatGptTheme(true, "#aabbcc", "#101112", "#eeeeee"), ChatGptTheme.Parse(Config, true));
    }

    [TestMethod]
    public void Parse_ExplicitLightOverridesWindowsDarkMode()
    {
        Assert.IsFalse(ChatGptTheme.Parse(Config.Replace("\"system\"", "\"light\""), true)!.Dark);
    }

    [TestMethod]
    public void Parse_IgnoresUnrelatedQuotedHeadersContainingDesktop()
    {
        var text = "[plugins.\"desktop-tools@example\"]\nenabled = true\n" + Config;
        Assert.AreEqual("#0169cc", ChatGptTheme.Parse(text, false)?.Accent);
    }

    [TestMethod]
    public void Parse_MissingVariantUsesAppDefaults()
    {
        Assert.AreEqual(new ChatGptTheme(true, "#339cff", "#181818", "#ffffff"),
            ChatGptTheme.Parse("[desktop]\nappearanceTheme = 'dark'", false));
    }

    [DataTestMethod]
    [DataRow("[desktop]\nappearanceTheme = 'unknown'")]
    [DataRow("[desktop]\nappearanceTheme = 'light'\n[desktop.appearanceLightChromeTheme]\naccent = 'red'")]
    [DataRow("[desktop]\nappearanceTheme = 'light'\nappearanceTheme = 'dark'")]
    [DataRow("[desktop]\nappearanceTheme = 'light'\n[desktop.appearanceLightChromeTheme]\nink = \"#123")]
    [DataRow("")]
    [DataRow("[desktop]\nappearanceTheme = 'dark'\n[desktop.appearanceDarkChromeTheme\naccent = '#0169cc'")]
    [DataRow("[desktop]\nappearanceTheme = 'dark'\n[desktop.appearanceDarkChromeTheme]\naccent '#0169cc'")]
    [DataRow("[desktop]\nappearanceTheme = 'dark'\n[desktop.\"appearanceDarkChromeTheme\"]\naccent = '#0169cc'")]
    public void Parse_InvalidOrIncompleteInputDoesNotProduceTheme(string config)
    {
        Assert.IsNull(ChatGptTheme.Parse(config, false));
    }

    [TestMethod]
    public void Parse_DoesNotReadFakeSettingsInsideMultilineText()
    {
        var text = "instructions = \"\"\"\n[desktop]\nappearanceTheme = 'dark'\n\"\"\"\n";
        Assert.IsNull(ChatGptTheme.Parse(text, false));
    }

    [TestMethod]
    public async Task Refresh_ReadsOnlyOnRequestAndRetainsLastGoodTheme()
    {
        var directory = Directory.CreateTempSubdirectory("AutoReset-theme-test-");
        try
        {
            var path = Path.Combine(directory.FullName, "config.toml");
            await File.WriteAllTextAsync(path, Config);
            var service = new ChatGptThemeService(path, () => false);
            Assert.IsTrue(await service.RefreshAsync());
            Assert.AreEqual("#ffffff", service.Current!.Background);
            await File.WriteAllTextAsync(path, Config.Replace("#ffffff", "#123456"));
            Assert.AreEqual("#ffffff", service.Current.Background);
            Assert.IsTrue(await service.RefreshAsync());
            Assert.AreEqual("#123456", service.Current.Background);
            await File.WriteAllTextAsync(path, "[desktop]\nappearanceTheme = 'broken'");
            Assert.IsFalse(await service.RefreshAsync());
            Assert.AreEqual("#123456", service.Current.Background);
            await File.WriteAllTextAsync(path, "[desktop]\nappearanceTheme = 'dark'\n[desktop.appearanceDarkChromeTheme\naccent = '#0169cc'");
            Assert.IsFalse(await service.RefreshAsync());
            Assert.AreEqual("#123456", service.Current.Background);
            File.Delete(path);
            Assert.IsFalse(await service.RefreshAsync());
            Assert.AreEqual("#123456", service.Current.Background);
        }
        finally { directory.Delete(true); }
    }

    [TestMethod]
    public void Apply_UpdatesMainAndPopupResourcesAndExistingDynamicBindings()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var directory = Directory.CreateTempSubdirectory("AutoReset-theme-test-");
            try
            {
                var path = Path.Combine(directory.FullName, "config.toml");
                File.WriteAllText(path, Config);
                var dark = false;
                var service = new ChatGptThemeService(path, () => dark);
                Assert.IsTrue(service.RefreshAsync().GetAwaiter().GetResult());
                var border = new System.Windows.Controls.Border();
                border.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "AppBackgroundBrush");
                service.ApplyTo(border.Resources);
                Assert.AreEqual(Colors.White, ((SolidColorBrush)border.Background).Color);
                Assert.AreEqual(Color.FromRgb(248, 242, 232), ((SolidColorBrush)border.Resources["AppBarBrush"]).Color);
                Assert.AreEqual(Color.FromRgb(1, 105, 204), ((SolidColorBrush)border.Resources["AccentBrush"]).Color);
                dark = true;
                Assert.IsTrue(service.RefreshAsync().GetAwaiter().GetResult());
                service.ApplyTo(border.Resources);
                Assert.AreEqual(Color.FromRgb(16, 17, 18), ((SolidColorBrush)border.Background).Color);
                Assert.AreEqual(Color.FromRgb(32, 32, 32), ((SolidColorBrush)border.Resources["AppBarBrush"]).Color);
                Assert.AreEqual(Color.FromRgb(16, 17, 18), ((SolidColorBrush)border.Resources["PopupBackgroundBrush"]).Color);
                Assert.AreEqual(Color.FromRgb(238, 238, 238), ((SolidColorBrush)border.Resources["PopupTextBrush"]).Color);
                var nativeWindow = new System.Windows.Window { ShowInTaskbar = false, Left = -32000, Top = -32000 };
                try
                {
                    var apply = typeof(ChatGptThemeService).Assembly.GetType("CodexAutoReset.Desktop.NativeHeaderBackdrop")!
                        .GetMethod("Apply", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
                    Assert.AreEqual(false, apply.Invoke(null, [nativeWindow, false]));
                    var handle = new System.Windows.Interop.WindowInteropHelper(nativeWindow).EnsureHandle();
                    foreach (var darkMode in new[] { false, true })
                    {
                        var applied = (bool)apply.Invoke(null, [nativeWindow, darkMode])!;
                        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) && !System.Windows.SystemParameters.HighContrast)
                        {
                            Assert.IsTrue(applied, "Native backdrop must initialize on this supported Windows desktop.");
                            Assert.AreEqual(Colors.Transparent, System.Windows.Interop.HwndSource.FromHwnd(handle)!.CompositionTarget.BackgroundColor);
                        }
                    }
                }
                finally { nativeWindow.Close(); }
            }
            catch (Exception exception) { failure = exception; }
            finally { directory.Delete(true); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); }
    }
}
