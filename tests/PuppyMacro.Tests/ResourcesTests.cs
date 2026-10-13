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

/// <summary>The app's texts (i18n.md): every language has every text, with the same placeholders and its plural forms.</summary>
public sealed class ResourcesTests
{
    private static readonly string[] Forms = { Plurals.Zero, Plurals.One, Plurals.Two, Plurals.Few, Plurals.Many, Plurals.Other };

    /// <summary>
    /// Labels that are only a global word (i18n.md: the user decides which words are global): the same in every
    /// language, so no translation has them and every language shows the text of Strings.resx.
    /// </summary>
    public static readonly string[] Global = { "Rail_Ai", "AiPage_Title" };

    /// <summary>Global words (i18n.md): written exactly as they are inside every translated sentence that has them.</summary>
    public static readonly string[] GlobalWords =
        { "AI", "PuppyMacro", "MCP", "JSON", "Claude", "Codex", "Copilot", "GitHub", "Windows" };

    private static string ResourcesFolder =>
        Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", "..", "PuppyMacro", "Resources");

    private static Dictionary<string, string> Read(string file) =>
        XDocument.Load(file).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "");

    /// <summary>"Loops_Count" for "Loops_Count_one"; null for a text with no plural forms.</summary>
    private static string? PluralBase(string key) =>
        Forms.FirstOrDefault(f => key.EndsWith("_" + f, StringComparison.Ordinal)) is string form ? key[..^(form.Length + 1)] : null;

    /// <summary>The word on its own (not inside a longer Latin word: "AI" in "AI-Apps", not in "MAIL").</summary>
    private static bool HasWord(string text, string word) =>
        Regex.IsMatch(text, $"(?<![A-Za-z]){Regex.Escape(word)}(?![a-z])");

    private static HashSet<string> Placeholders(string text) =>
        Regex.Matches(text, @"\{(\d+)(?:[,:][^}]*)?\}").Select(m => m.Groups[1].Value).ToHashSet();

    public static TheoryData<string> Translations() =>
        new(AppLanguages.All.Select(l => l.Code).Where(c => c != AppLanguages.English));

    [Theory]
    [MemberData(nameof(Translations))]
    public void Every_language_has_every_text_with_the_same_placeholders_and_its_plural_forms(string language)
    {
        Dictionary<string, string> english = Read(Path.Combine(ResourcesFolder, "Strings.resx"));
        string file = Path.Combine(ResourcesFolder, $"Strings.{language}.resx");
        Assert.True(File.Exists(file), $"Strings.{language}.resx is missing.");
        Dictionary<string, string> texts = Read(file);
        var problems = new List<string>();

        foreach (string key in Global.Where(texts.ContainsKey))
            problems.Add($"{key}: a global text, the same in every language: remove it from Strings.{language}.resx");
        foreach (var (key, value) in english.Where(e => PluralBase(e.Key) == null && !Global.Contains(e.Key)))
        {
            if (!texts.TryGetValue(key, out string? translated) || translated.Length == 0)
                problems.Add($"{key}: missing");
            else if (!Placeholders(value).SetEquals(Placeholders(translated)))
                problems.Add($"{key}: placeholders {string.Join(",", Placeholders(translated))}, English {string.Join(",", Placeholders(value))}");
        }

        IReadOnlyList<string> required = Plurals.RequiredForms(language);
        foreach (string plural in english.Keys.Select(PluralBase).OfType<string>().Distinct())
        {
            HashSet<string> englishPlaceholders = Placeholders(english[$"{plural}_{Plurals.Other}"]);
            foreach (string form in Forms)
            {
                bool has = texts.TryGetValue($"{plural}_{form}", out string? translated);
                if (required.Contains(form) && !has)
                    problems.Add($"{plural}_{form}: missing");
                else if (!required.Contains(form) && has)
                    problems.Add($"{plural}_{form}: {language} has no such form");
                // A form may leave the count out (Arabic says "one loop" without the number), never add a placeholder.
                else if (has && !(Placeholders(translated!).IsSubsetOf(englishPlaceholders)
                                  && englishPlaceholders.Except(new[] { "0" }).All(Placeholders(translated!).Contains)))
                    problems.Add($"{plural}_{form}: placeholders {string.Join(",", Placeholders(translated!))}");
            }
        }

        foreach (string key in texts.Keys.Where(k => PluralBase(k) == null && !english.ContainsKey(k)))
            problems.Add($"{key}: not in Strings.resx");
        // A global word in the English text is in the translation too, as it is (never "IA", "KI", "ИИ"…).
        foreach (var (key, translated) in texts)
        {
            string englishKey = english.ContainsKey(key) ? key : $"{PluralBase(key)}_{Plurals.Other}";
            if (!english.TryGetValue(englishKey, out string? source))
                continue;
            foreach (string word in GlobalWords.Where(w => HasWord(source, w) && !HasWord(translated, w)))
                problems.Add($"{key}: the global word \"{word}\" is missing");
        }
        // The editors find a sound by the name they show (SoundService.IdOf).
        foreach (var same in texts.Where(t => t.Key.StartsWith("Sound_", StringComparison.Ordinal)).GroupBy(t => t.Value).Where(g => g.Count() > 1))
            problems.Add($"{string.Join(", ", same.Select(s => s.Key))}: the same name");
        Assert.Empty(problems);
    }

    [Fact]
    public void Every_text_named_in_the_code_exists()
    {
        string appFolder = Path.Combine(ResourcesFolder, "..");
        Dictionary<string, string> english = Read(Path.Combine(ResourcesFolder, "Strings.resx"));
        var missing = new List<string>();
        foreach (string file in Directory.EnumerateFiles(appFolder, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            string code = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(code, @"Phrase\.Of\(""([A-Za-z_]+)"""))
                if (!english.ContainsKey(m.Groups[1].Value))
                    missing.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value}");
            foreach (Match m in Regex.Matches(code, @"(?:Phrase\.Plural|Plurals\.Format)\(""([A-Za-z_]+)"""))
                if (!english.ContainsKey(m.Groups[1].Value + "_" + Plurals.Other))
                    missing.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value}_other");
        }

        // Texts named from a value: an item's kind, a verb.
        foreach (string kind in new[] { "loop", "macro", "remap" })
        {
            foreach (string key in new[] { "Ai_Read", "Ai_New", "Ai_Changed", "Ai_Deleted", "Ai_Enabled", "Ai_Disabled", "Ai_Opened",
                         "Ai_Item", "Ai_NewProposal", "Ai_ChangeProposal", "Ai_ConfirmNew", "Ai_ConfirmChanges",
                         "Ai_ConfirmDelete", "Ai_ConfirmEnable", "Ai_ConfirmDisable", "Ai_Imported", "ItemFile_Missing" })
                if (!english.ContainsKey($"{key}_{kind}"))
                    missing.Add($"{key}_{kind}");
        }
        foreach (string kind in new[] { "loop", "macro" })
        {
            foreach (string key in new[] { "Ai_PlacedButton", "ItemFile_NameTaken", "ItemFile_HotkeyUsedInFile" })
                if (!english.ContainsKey($"{key}_{kind}"))
                    missing.Add($"{key}_{kind}");
        }
        foreach (string key in new[] { "Ai_Enable", "Ai_Disable" }.Concat(Enum.GetNames<Models.AppTheme>().Select(t => $"Theme_{t}")))
            if (!english.ContainsKey(key))
                missing.Add(key);
        Assert.Empty(missing);
    }

    /// <summary>The XAML attributes and elements that show a text to the user.</summary>
    private static readonly HashSet<string> TextAttributes = new()
    {
        "Text", "Content", "Header", "Description", "ToolTip", "Title", "Label", "PlaceholderText", "OnContent", "OffContent",
        "PrimaryButtonText", "SecondaryButtonText", "CloseButtonText", "Message",
        "AutomationProperties.Name", // the name a screen reader says (x:Name is an identifier: not here)
    };

    [Fact]
    public void Every_text_of_the_xaml_comes_from_the_resources()
    {
        // A text written in the XAML stays the same in every language (i18n.md). Allowed as it is: a markup extension
        // ({x:Static r:Strings.…}, {Binding …}), a global word, a single letter or digit, or F1 to F24 (as on every keyboard).
        string appFolder = Path.Combine(ResourcesFolder, "..");
        var written = new List<string>();
        foreach (string file in Directory.EnumerateFiles(appFolder, "*.xaml", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            foreach (XElement element in XDocument.Load(file).Descendants())
            {
                // "ui:ControlHelper.PlaceholderText" counts as PlaceholderText; "AutomationProperties.Name" as itself.
                var texts = element.Attributes()
                    .Where(a => TextAttributes.Contains(a.Name.LocalName) || TextAttributes.Contains(a.Name.LocalName.Split('.').Last()))
                    .Select(a => (a.Name.LocalName, a.Value)).ToList();
                if (element.Name.LocalName is "TextBlock" or "Run" && !element.HasElements && element.Value.Trim().Length > 0)
                    texts.Add(("(text)", element.Value.Trim()));
                foreach (var (attribute, value) in texts.Distinct())
                    if (!Allowed(value))
                        written.Add($"{Path.GetFileName(file)}: {element.Name.LocalName} {attribute}=\"{value}\": use Strings");
            }
        }
        Assert.Empty(written);
    }

    /// <summary>A text that may be written in the XAML: no letters, a markup extension, a global word, a single letter or
    /// digit, or a function key.</summary>
    private static bool Allowed(string value) =>
        !value.Any(char.IsLetter) || value.StartsWith('{') || GlobalWords.Contains(value)
        || Regex.IsMatch(value, "^([A-Z0-9]|F([1-9]|1[0-9]|2[0-4]))$");

    [Fact]
    public void Global_words_are_the_same_in_every_language()
    {
        foreach (string code in AppLanguages.All.Select(l => l.Code))
        {
            CultureInfo language = CultureInfo.GetCultureInfo(code);
            foreach (string key in Global)
                Assert.Equal(Strings.ResourceManager.GetString(key, CultureInfo.InvariantCulture),
                    Strings.ResourceManager.GetString(key, language));
        }
        Assert.Equal("AI", Strings.Rail_Ai);
    }

    [Fact]
    public void A_phrase_is_shown_in_the_language_of_the_moment()
    {
        Phrase phrase = Phrase.Plural("History_Changes", 3);

        Assert.Equal("3 changes", phrase.English);
        Assert.Equal("3 changes", phrase.Render(CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal(Strings.ResourceManager.GetString("History_Changes_other", CultureInfo.GetCultureInfo("it"))!.Replace("{0}", "3"),
            phrase.Render(CultureInfo.GetCultureInfo("it")));
    }

    private static string ThisFile([CallerFilePath] string path = "") => path;
}
