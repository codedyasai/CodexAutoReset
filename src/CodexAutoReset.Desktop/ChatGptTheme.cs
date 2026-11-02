using System.Text.RegularExpressions;

namespace CodexAutoReset.Desktop;

public sealed record ChatGptTheme(bool Dark, string Accent, string Background, string Foreground)
{
    // Only the desktop appearance subset is consumed. Unsupported syntax fails closed.
    public static ChatGptTheme? Parse(string text, bool systemDark)
    {
        if (text.Length > 1_048_576) { return null; }
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var section = "";
        string? multiline = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (multiline is not null)
            {
                if (line.Contains(multiline, StringComparison.Ordinal)) { multiline = null; }
                continue;
            }
            if (line.Length == 0 || line.StartsWith('#')) { continue; }
            var triple = line.IndexOf("\"\"\"", StringComparison.Ordinal);
            var literalTriple = line.IndexOf("'''", StringComparison.Ordinal);
            if (triple >= 0 || literalTriple >= 0)
            {
                var delimiter = triple >= 0 ? "\"\"\"" : "'''";
                var start = triple >= 0 ? triple : literalTriple;
                if (line.IndexOf(delimiter, start + 3, StringComparison.Ordinal) < 0) { multiline = delimiter; }
                // Appearance values are always single-line strings.
                if (section == "desktop" || section.StartsWith("desktop.appearance", StringComparison.Ordinal)) { return null; }
                continue;
            }
            if (line.StartsWith('['))
            {
                var header = Regex.Match(line, @"^\[([A-Za-z0-9_.]+)\]\s*(?:#.*)?$");
                if (!header.Success && Regex.IsMatch(line, "^\\[\\s*[\"']?desktop\\b")) { return null; }
                section = header.Success ? header.Groups[1].Value : "";
                continue;
            }
            var equal = line.IndexOf('=');
            if (equal < 0)
            {
                if (section == "desktop" || section.StartsWith("desktop.appearance", StringComparison.Ordinal)) { return null; }
                continue;
            }
            var key = line[..equal].Trim();
            var relevant = section == "desktop" && key == "appearanceTheme"
                || (section == "desktop.appearanceLightChromeTheme" || section == "desktop.appearanceDarkChromeTheme")
                && key is "accent" or "surface" or "ink";
            if (!relevant) { continue; }
            var value = Regex.Match(line[(equal + 1)..].Trim(), "^(?:\"([^\"\\\\]*)\"|'([^']*)')\\s*(?:#.*)?$");
            if (!value.Success || !values.TryAdd(section + "." + key,
                    value.Groups[1].Success ? value.Groups[1].Value : value.Groups[2].Value)) { return null; }
        }
        if (multiline is not null || !values.TryGetValue("desktop.appearanceTheme", out var mode)
            || mode is not ("light" or "dark" or "system")) { return null; }
        var dark = mode == "dark" || mode == "system" && systemDark;
        var prefix = dark ? "desktop.appearanceDarkChromeTheme." : "desktop.appearanceLightChromeTheme.";
        var accent = values.GetValueOrDefault(prefix + "accent", "#339cff");
        var background = values.GetValueOrDefault(prefix + "surface", dark ? "#181818" : "#ffffff");
        var foreground = values.GetValueOrDefault(prefix + "ink", dark ? "#ffffff" : "#1a1c1f");
        if (new[] { accent, background, foreground }.Any(color => !Regex.IsMatch(color, "^#[0-9a-fA-F]{6}$"))) { return null; }
        return new ChatGptTheme(dark, accent.ToLowerInvariant(), background.ToLowerInvariant(), foreground.ToLowerInvariant());
    }
}
