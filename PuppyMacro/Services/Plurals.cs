using System;
using System.Collections.Generic;
using System.Globalization;
using PuppyMacro.Resources;

namespace PuppyMacro.Services;

/// <summary>
/// Texts with a count ("1 loop", "3 loops"). Each language has its own forms (CLDR plural rules,
/// https://www.unicode.org/cldr/charts/latest/supplemental/language_plural_rules.html): the resources hold one text per
/// form, "Key_one", "Key_other" in English, "Key_few" and "Key_many" in Russian, six in Arabic (i18n.md).
/// </summary>
internal static class Plurals
{
    public const string Zero = "zero", One = "one", Two = "two", Few = "few", Many = "many", Other = "other";

    /// <summary>The text of <paramref name="key"/> for <paramref name="count"/>, with the count as {0} and
    /// <paramref name="more"/> as {1}, {2}…</summary>
    public static string Format(string key, long count, params object[] more)
    {
        CultureInfo ui = CultureInfo.CurrentUICulture;
        string text = Strings.ResourceManager.GetString($"{key}_{Category(ui.Name, count)}", ui)
            ?? Strings.ResourceManager.GetString($"{key}_{Other}", ui)
            ?? throw new InvalidOperationException($"No text {key}_{Other}.");
        var args = new object[more.Length + 1];
        args[0] = count;
        more.CopyTo(args, 1);
        return string.Format(CultureInfo.CurrentCulture, text, args);
    }

    /// <summary>The plural form of a whole number in a language (its code or culture name: "ru", "pt-BR").</summary>
    public static string Category(string language, long count)
    {
        long n = Math.Abs(count);
        switch (LanguageOf(language))
        {
            case "fr":
            case "pt":
                // 0 and 1 are singular; a whole million is "many" (no text: "other" is used).
                return n <= 1 ? One : MillionOr(n, Other);
            case "it":
            case "es":
                return n == 1 ? One : MillionOr(n, Other);
            case "ro":
                if (n == 1)
                    return One;
                return n == 0 || n % 100 is >= 1 and <= 19 ? Few : Other;
            case "ru":
                if (n % 10 == 1 && n % 100 != 11)
                    return One;
                if (n % 10 is >= 2 and <= 4 && n % 100 is not (>= 12 and <= 14))
                    return Few;
                return Many;
            case "ar":
                return n switch
                {
                    0 => Zero,
                    1 => One,
                    2 => Two,
                    _ when n % 100 is >= 3 and <= 10 => Few,
                    _ when n % 100 is >= 11 and <= 99 => Many,
                    _ => Other,
                };
            case "ja":
            case "ko":
            case "zh":
                return Other;
            default:
                // English, German, Albanian.
                return n == 1 ? One : Other;
        }
    }

    /// <summary>The forms a language's resources must have (the unit test checks every Strings.&lt;code&gt;.resx).</summary>
    public static IReadOnlyList<string> RequiredForms(string language) => LanguageOf(language) switch
    {
        "ro" => new[] { One, Few, Other },
        "ru" => new[] { One, Few, Many },
        "ar" => new[] { Zero, One, Two, Few, Many, Other },
        "ja" or "ko" or "zh" => new[] { Other },
        _ => new[] { One, Other },
    };

    private static string MillionOr(long n, string otherwise) => n != 0 && n % 1_000_000 == 0 ? Many : otherwise;

    private static string LanguageOf(string language)
    {
        int dash = language.IndexOf('-');
        return (dash < 0 ? language : language[..dash]).ToLowerInvariant();
    }
}
