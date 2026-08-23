using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace ClipToGif.Localization;

public static class Loc
{
    public const string Chinese = "zh-CN";
    public const string English = "en";

    public static string Current { get; private set; } = Chinese;

    public static event Action? LanguageChanged;

    public static IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new(Chinese, "中文"),
        new(English, "English")
    ];

    public static void Initialize()
    {
        SetLanguage(LoadSaved() ?? DetectSystem(), persist: false);
    }

    public static void SetLanguage(string code, bool persist = true)
    {
        code = Normalize(code);
        Current = code;

        var app = Application.Current;
        if (app is not null)
        {
            var dict = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/Localization/Strings.{code}.xaml", UriKind.Absolute)
            };

            var merged = app.Resources.MergedDictionaries;
            for (var i = merged.Count - 1; i >= 0; i--)
            {
                var source = merged[i].Source?.OriginalString ?? string.Empty;
                if (source.Contains("Localization/Strings.", StringComparison.OrdinalIgnoreCase))
                    merged.RemoveAt(i);
            }

            merged.Insert(0, dict);
        }

        if (persist)
            Save(code);

        LanguageChanged?.Invoke();
    }

    public static string Get(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;

    public static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    private static string Normalize(string? code) =>
        string.Equals(code, English, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(code, "en-US", StringComparison.OrdinalIgnoreCase)
            ? English
            : Chinese;

    private static string DetectSystem() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("en", StringComparison.OrdinalIgnoreCase)
            ? English
            : Chinese;

    private static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClipToGif",
            "settings.json");

    private static string? LoadSaved()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return null;
            var data = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
            return string.IsNullOrWhiteSpace(data?.Language) ? null : data.Language;
        }
        catch
        {
            return null;
        }
    }

    private static void Save(string code)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new AppSettings { Language = code }));
        }
        catch
        {
            // ignore
        }
    }

    private sealed class AppSettings
    {
        public string Language { get; set; } = Chinese;
    }
}

public sealed record LanguageOption(string Code, string Name);
