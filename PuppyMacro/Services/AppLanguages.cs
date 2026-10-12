using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PuppyMacro.Services;

/// <summary>The languages PuppyMacro is translated into, and the cultures it runs with (i18n.md).</summary>
internal static class AppLanguages
{
    /// <summary>One language: its code (the name of its Strings.&lt;code&gt;.resx) and its name in that language.</summary>
    public sealed record Language(string Code, string NativeName);

    /// <summary>English, the texts in Strings.resx, used when Windows' language is not translated.</summary>
    public const string English = "en";

    /// <summary>Every language, in no particular order (<see cref="ForMenu"/> sorts them). A new language adds its line
    /// here and its Strings.&lt;code&gt;.resx.</summary>
    public static readonly IReadOnlyList<Language> All = new[]
    {
        new Language("en", "English"),
        new Language("it", "Italiano"),
        new Language("fr", "Français"),
        new Language("es", "Español"),
        new Language("de", "Deutsch"),
        new Language("pt-BR", "Português (Brasil)"),
        new Language("ro", "Română"),
        new Language("sq", "Shqip"),
        new Language("ru", "Русский"),
        new Language("ar", "العربية"),
        new Language("ja", "日本語"),
        new Language("ko", "한국어"),
        new Language("zh-Hans", "中文（简体）"),
    };

    /// <summary>The languages as the Language setting lists them: alphabetical by their own name.</summary>
    public static IReadOnlyList<Language> ForMenu() =>
        All.OrderBy(l => l.NativeName, StringComparer.InvariantCulture).ToList();

    /// <summary>The setting's value when it is one of <see cref="All"/> (compared without case), otherwise null
    /// (Use system language): a value from a newer version or written by hand is ignored.</summary>
    public static string? Normalize(string? setting) =>
        setting == null ? null : All.FirstOrDefault(l => string.Equals(l.Code, setting, StringComparison.OrdinalIgnoreCase))?.Code;

    /// <summary>The language of the texts: the setting, or Windows' display language when it is translated (its
    /// own name or a parent: "it-CH" gives "it", "zh-CN" gives "zh-Hans"), otherwise English.</summary>
    public static string Resolve(string? setting, CultureInfo windowsLanguage)
    {
        string? chosen = Normalize(setting);
        if (chosen != null)
            return chosen;
        for (CultureInfo culture = windowsLanguage; !Equals(culture, CultureInfo.InvariantCulture); culture = culture.Parent)
        {
            Language? match = All.FirstOrDefault(l => string.Equals(l.Code, culture.Name, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match.Code;
        }
        // Portuguese outside Brazil reads Brazilian Portuguese better than English.
        if (windowsLanguage.TwoLetterISOLanguageName == "pt")
            return "pt-BR";
        return English;
    }

    /// <summary>
    /// The culture of numbers and dates: Windows' regional format while it is in the language of the texts (an Italian in
    /// Switzerland keeps it-CH), otherwise the usual format of that language, so a date never mixes two languages
    /// (an English month name in Italian text).
    /// </summary>
    public static CultureInfo FormatCulture(string language, CultureInfo windowsFormat)
    {
        var ui = CultureInfo.GetCultureInfo(language);
        if (SameLanguage(ui, windowsFormat))
            return windowsFormat;
        CultureInfo format = CultureInfo.CreateSpecificCulture(language);
        // Arabic gives ar-SA, whose calendar is Umm al-Qura: the user chose a language, not a calendar, so dates stay
        // Gregorian as in Windows' default formats. Windows' own format (above) keeps the user's calendar.
        if (format.Calendar is not GregorianCalendar
            && format.OptionalCalendars.OfType<GregorianCalendar>().FirstOrDefault() is GregorianCalendar gregorian)
            format.DateTimeFormat.Calendar = gregorian;
        return format;
    }

    /// <summary>Two cultures of one language: the same two-letter language and, for Chinese, the same script.</summary>
    private static bool SameLanguage(CultureInfo a, CultureInfo b)
    {
        if (a.TwoLetterISOLanguageName != b.TwoLetterISOLanguageName)
            return false;
        if (a.TwoLetterISOLanguageName != "zh")
            return true;
        return Script(a) == Script(b);
    }

    /// <summary>"Hans" or "Hant" for a Chinese culture (zh-CN and zh-SG are Hans, zh-TW, zh-HK and zh-MO Hant).</summary>
    private static string Script(CultureInfo culture)
    {
        for (CultureInfo c = culture; !Equals(c, CultureInfo.InvariantCulture); c = c.Parent)
        {
            if (c.Name.EndsWith("Hant", StringComparison.OrdinalIgnoreCase))
                return "Hant";
            if (c.Name.EndsWith("Hans", StringComparison.OrdinalIgnoreCase))
                return "Hans";
        }
        return culture.Name is "zh-TW" or "zh-HK" or "zh-MO" ? "Hant" : "Hans";
    }
}
