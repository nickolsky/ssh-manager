using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace SshManager.Core.Localization;

/// <summary>
/// UI strings in Russian and English (see <see cref="Strings"/>). The language can be switched at runtime;
/// WPF bindings refresh through <see cref="LanguageChanged"/>.
/// </summary>
public static class L
{
    public const string Russian = "ru";
    public const string English = "en";

    private static string _language = Resolve(null);

    public static event EventHandler? LanguageChanged;

    /// <summary>Current language; assigning null picks the Windows UI language.</summary>
    [AllowNull]
    public static string Language
    {
        get => _language;
        set
        {
            var lang = Resolve(value);
            ApplyCulture(lang);
            if (lang == _language) return;
            _language = lang;
            LanguageChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    public static CultureInfo Culture => CultureInfo.GetCultureInfo(_language == English ? "en-US" : "ru-RU");

    /// <summary>"ru"/"en" as configured, otherwise from the Windows UI language.</summary>
    public static string Resolve(string? configured) => configured switch
    {
        Russian or English => configured,
        _ => CultureInfo.InstalledUICulture.TwoLetterISOLanguageName is "ru" or "uk" or "be" or "kk" ? Russian : English,
    };

    public static string Get(string key) =>
        Strings.Table.TryGetValue(key, out var t) ? (_language == English ? t.En : t.Ru) : key;

    public static string F(string key, params object?[] args) => string.Format(Culture, Get(key), args);

    private static void ApplyCulture(string lang)
    {
        var culture = CultureInfo.GetCultureInfo(lang == English ? "en-US" : "ru-RU");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
