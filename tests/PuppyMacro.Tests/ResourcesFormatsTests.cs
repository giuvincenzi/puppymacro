using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PuppyMacro.Resources;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// The texts as the app formats them, in every language: a text with placeholders is a composite format string
/// (string.Format, Plurals.Format, Phrase), and a broken one throws only at run time, in that language.
/// </summary>
public class ResourcesFormatsTests
{
    private static string ResourcesFolder =>
        Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", "..", "PuppyMacro", "Resources");

    private static Dictionary<string, string> English() =>
        XDocument.Load(Path.Combine(ResourcesFolder, "Strings.resx")).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "");

    private static readonly string[] Forms = { Plurals.Zero, Plurals.One, Plurals.Two, Plurals.Few, Plurals.Many, Plurals.Other };

    public static TheoryData<string> Languages() => new(AppLanguages.All.Select(l => l.Code));

    [Theory]
    [MemberData(nameof(Languages))]
    public void Every_text_with_placeholders_formats_in_every_language(string language)
    {
        CultureInfo culture = CultureInfo.GetCultureInfo(language);
        // Numbers for {0:X2} and the like, texts for the others: an int formats with any specifier the texts use.
        object[] args = Enumerable.Range(0, 10).Select(i => (object)(i + 10)).ToArray();
        var broken = new List<string>();
        foreach (var (key, english) in English())
        {
            if (!Regex.IsMatch(english, @"\{\d"))
                continue; // no placeholder: never given to string.Format (some show JSON with braces)
            string text = Strings.ResourceManager.GetString(key, culture)!;
            try
            {
                string.Format(culture, text, args);
            }
            catch (FormatException e)
            {
                broken.Add($"{key}: {e.Message} in \"{text}\"");
            }
        }
        Assert.Empty(broken);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Every_count_has_a_text_in_every_language(string language)
    {
        var plurals = English().Keys
            .Select(k => Forms.FirstOrDefault(f => k.EndsWith("_" + f, StringComparison.Ordinal)) is string f ? k[..^(f.Length + 1)] : null)
            .OfType<string>().Distinct().ToList();
        var counts = Enumerable.Range(0, 250).Select(i => (long)i)
            .Concat(new long[] { 1000, 1001, 1_000_000, 2_000_000, 1_000_001, -1, -2, -11, -21 });
        var missing = new List<string>();
        Loc.In(CultureInfo.GetCultureInfo(language), () =>
        {
            foreach (string key in plurals)
                foreach (long count in counts)
                {
                    try
                    {
                        string text = Plurals.Format(key, count, "a", "b", "c", "d");
                        if (text.Length == 0)
                            missing.Add($"{key} {count}: empty");
                    }
                    catch (Exception e) when (e is FormatException or InvalidOperationException)
                    {
                        missing.Add($"{key} {count}: {e.Message}");
                    }
                }
            return 0;
        });
        Assert.Empty(missing);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Every_plural_form_a_language_uses_is_one_it_has(string language)
    {
        IReadOnlyList<string> required = Plurals.RequiredForms(language);
        for (long n = 0; n <= 2_000; n++)
        {
            string form = Plurals.Category(language, n);
            // fr, es, it, pt use "many" for whole millions only; their texts fall back to "other".
            Assert.True(required.Contains(form) || (form == Plurals.Many && required.Contains(Plurals.Other)),
                $"{language} {n}: {form}");
        }
    }

    private static string ThisFile([CallerFilePath] string path = "") => path;
}
