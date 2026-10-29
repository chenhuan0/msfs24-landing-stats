using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Markup;

namespace LandingStats.App.Settings;

internal static class LocalizationManager
{
    public const string AutomaticLanguage = "auto";
    public const string EnglishLanguage = "en";
    public const string RussianLanguage = "ru";
    public const string ChineseLanguage = "zh";

    private static readonly CultureInfo DetectedSystemCulture = CultureInfo.CurrentCulture;
    private static readonly object DictionaryGate = new object();
    private static readonly Dictionary<string, LoadedDictionary> LoadedDictionaries =
        new Dictionary<string, LoadedDictionary>(StringComparer.Ordinal);
    private static IReadOnlyDictionary<string, string> _strings =
        new Dictionary<string, string>(StringComparer.Ordinal);
    private static ResourceDictionary? _activeDictionary;

    public static string EffectiveLanguage { get; private set; } = EnglishLanguage;

    // WPF picks the fallback font for Chinese characters from the element language,
    // not the thread culture; under its en-US default they render with Japanese
    // glyphs. Other languages keep that default, which bindings also format with.
    public static XmlLanguage DisplayLanguage => XmlLanguage.GetLanguage(
        EffectiveLanguage == ChineseLanguage ? "zh-CN" : "en-US");

    public static string NormalizePreference(string? preference)
    {
        var normalized = preference?.Trim().ToLowerInvariant();
        return normalized == EnglishLanguage || normalized == RussianLanguage || normalized == ChineseLanguage
            ? normalized
            : AutomaticLanguage;
    }

    public static string ResolveLanguage(string? preference, CultureInfo systemCulture)
    {
        var normalized = NormalizePreference(preference);
        if (normalized != AutomaticLanguage)
        {
            return normalized;
        }

        if (string.Equals(systemCulture.TwoLetterISOLanguageName, RussianLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return RussianLanguage;
        }

        return IsSimplifiedChinese(systemCulture) ? ChineseLanguage : EnglishLanguage;
    }

    public static CultureInfo CultureFor(string effectiveLanguage)
    {
        return effectiveLanguage switch
        {
            RussianLanguage => CultureInfo.GetCultureInfo("ru-RU"),
            ChineseLanguage => CultureInfo.GetCultureInfo("zh-CN"),
            _ => CultureInfo.GetCultureInfo("en-GB"),
        };
    }

    // Traditional Chinese locales (zh-TW, zh-HK, zh-MO) descend from zh-Hant and
    // keep the English default; they can still choose Simplified Chinese explicitly.
    private static bool IsSimplifiedChinese(CultureInfo culture)
    {
        for (var current = culture; !string.IsNullOrEmpty(current.Name); current = current.Parent)
        {
            if (string.Equals(current.Name, "zh-Hans", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static void Apply(string? preference)
    {
        var effectiveLanguage = ResolveLanguage(preference, DetectedSystemCulture);
        var culture = CultureFor(effectiveLanguage);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;

        var loaded = LoadDictionary(effectiveLanguage);
        var dictionary = loaded.Dictionary;
        var strings = loaded.Strings;

        var application = Application.Current;
        if (application != null)
        {
            var resources = application.Resources;
            if (_activeDictionary != null)
            {
                resources.MergedDictionaries.Remove(_activeDictionary);
            }

            // WPF resolves duplicate keys from the last merged dictionary.
            resources.MergedDictionaries.Add(dictionary);
            _activeDictionary = dictionary;
        }

        Volatile.Write(ref _strings, strings);
        Models.ModelText.SetStrings(strings);
        EffectiveLanguage = effectiveLanguage;

        // Tooltips and popups inherit the language from their placement target.
        if (application != null)
        {
            foreach (Window window in application.Windows)
            {
                window.Language = DisplayLanguage;
            }
        }
    }

    public static string Text(string key)
    {
        var strings = Volatile.Read(ref _strings);
        return strings.TryGetValue(key, out var value) ? value : key;
    }

    public static string Format(string key, params object[] arguments)
    {
        return string.Format(CultureInfo.CurrentCulture, Text(key), arguments);
    }

    private static LoadedDictionary LoadDictionary(string effectiveLanguage)
    {
        lock (DictionaryGate)
        {
            if (LoadedDictionaries.TryGetValue(effectiveLanguage, out var cached))
            {
                return cached;
            }

            var assemblyName = typeof(LocalizationManager).Assembly.GetName().Name
                               ?? throw new InvalidOperationException("Localization assembly name is unavailable.");
            var dictionary = new ResourceDictionary
            {
                Source = new Uri(
                    $"pack://application:,,,/{assemblyName};component/Localization/Strings.{effectiveLanguage}.xaml",
                    UriKind.Absolute),
            };
            var strings = dictionary
                .Cast<DictionaryEntry>()
                .Where(entry => entry.Key is string && entry.Value is string)
                .ToDictionary(
                    entry => (string)entry.Key,
                    entry => (string)entry.Value,
                    StringComparer.Ordinal);
            var loaded = new LoadedDictionary(dictionary, strings);
            LoadedDictionaries.Add(effectiveLanguage, loaded);
            return loaded;
        }
    }

    private sealed class LoadedDictionary
    {
        public LoadedDictionary(ResourceDictionary dictionary, IReadOnlyDictionary<string, string> strings)
        {
            Dictionary = dictionary;
            Strings = strings;
        }

        public ResourceDictionary Dictionary { get; }
        public IReadOnlyDictionary<string, string> Strings { get; }
    }
}
