using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using PCasa.App.Services;

// Audit the SAME German keys used by the WPF markup extension and C#.
// One missing translation cannot silently fall back to German in English UI.
internal static class LocalizationFlow
{
    internal static void Run()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null &&
               !Directory.Exists(Path.Combine(root.FullName, "src", "PCasa.App")))
            root = root.Parent;
        if (root is null)
            throw new Exception("Cannot locate PCasa source for localization audit.");

        var app = Path.Combine(root.FullName, "src", "PCasa.App");
        var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(app, "Resources", "english.json")))
            ?? throw new Exception("Empty English catalog.");

        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var literalTips = new List<string>();

        foreach (var file in Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(source,
                @"\{loc:Tr\s+Key='([^']+)'"))
            {
                var key = WebUtility.HtmlDecode(match.Groups[1].Value);
                if (!translations.ContainsKey(key))
                    missing.Add(key);
            }

            // Tooltips must be bound through the central localization system,
            // never static German XAML strings.
            foreach (Match match in Regex.Matches(source, "ToolTip=\"([^\"]+)\""))
            {
                var literal = match.Groups[1].Value;
                if (!literal.StartsWith("{", StringComparison.Ordinal))
                    literalTips.Add(Path.GetFileName(file) + ": " + literal);
            }
        }

        foreach (var file in Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(source,
                "UiLanguage\\.(?:T|Format)\\(\\s*\"((?:[^\"\\\\]|\\\\.)*)\""))
            {
                // Literal C# escape sequences decode to the actual dictionary
                // keys (not the two-character text backslash + n).
                var sourceKey = match.Groups[1].Value;
                var key = sourceKey.Replace("\\n", "\n", StringComparison.Ordinal)
                    .Replace("\\r", "\r", StringComparison.Ordinal)
                    .Replace("\\t", "\t", StringComparison.Ordinal)
                    .Replace("\\\"", "\"", StringComparison.Ordinal);
                if (!translations.ContainsKey(key))
                    missing.Add(key);
            }
            foreach (Match match in Regex.Matches(source, "ToolTip\\s*=\\s*\"([^\"]+)\""))
                literalTips.Add(Path.GetFileName(file) + ": " + match.Groups[1].Value);
        }

        if (missing.Count > 0 || literalTips.Count > 0)
            throw new Exception("English localization incomplete.\nMissing keys:\n" +
                string.Join("\n", missing) + "\nStatic tooltips:\n" +
                string.Join("\n", literalTips));

        // Verify actual translation, not merely catalog key presence.
        var old = UiLanguage.Current.Code;
        try
        {
            UiLanguage.Current.Select("en");
            if (UiLanguage.T("Zurücksetzen") != "Reset" ||
                UiLanguage.T("Keine GPX-Dateien.") != "No GPX files." ||
                UiLanguage.T("Großer Bereich ±45°") != "Extended range ±45°")
                throw new Exception("Central language switch did not translate editor/GPS text.");
        }
        finally
        {
            UiLanguage.Current.Select(old);
        }
        Console.WriteLine("PASS: All static C#/XAML localization keys and tooltips have English translations.");
    }
}
