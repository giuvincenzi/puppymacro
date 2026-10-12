using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using PuppyMacro.Models;
using PuppyMacro.Resources;

namespace PuppyMacro.Services;

/// <summary>
/// A text kept in a file (History, AI activity) as its resource key and arguments, so it is shown in the language of the
/// moment, not the one it was saved in (i18n.md). <see cref="Count"/> makes it a plural text (<see cref="Plurals"/>):
/// its count is {0}, the arguments follow.
/// </summary>
public sealed class Phrase
{
    public string Key { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Count { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PhraseArg>? Args { get; set; }

    public static Phrase Of(string key, params PhraseArg[] args) =>
        new() { Key = key, Args = args.Length == 0 ? null : args.ToList() };

    public static Phrase Plural(string key, long count, params PhraseArg[] args) =>
        new() { Key = key, Count = count, Args = args.Length == 0 ? null : args.ToList() };

    /// <summary>The key of a list: its arguments joined by the language's separator ("A, B, C"; Arabic "A، B").</summary>
    public const string ListKey = "List";

    public static Phrase List(IEnumerable<PhraseArg> items) => new() { Key = ListKey, Args = items.ToList() };

    /// <summary>A text shown as it is, in every language (a list of one).</summary>
    public static Phrase Literal(string text) => List(new PhraseArg[] { text });

    /// <summary>In the language of the texts of this thread (CurrentUICulture).</summary>
    public override string ToString() => Render(CultureInfo.CurrentUICulture);

    /// <summary>The text in a language; English for <see cref="CultureInfo.InvariantCulture"/>. A key this version does not
    /// have (a file from a newer one) gives null: show the English text kept next to it instead.</summary>
    public string? TryRender(CultureInfo language)
    {
        if (Key == ListKey)
        {
            List<string?> items = (Args ?? new List<PhraseArg>()).Select(a => a.TryRender(language)).ToList();
            string? separator = Strings.ResourceManager.GetString(nameof(Strings.ListSeparator), language);
            return items.Any(i => i == null) || separator == null ? null : string.Join(separator, items);
        }
        string? template = Count is long count
            ? Strings.ResourceManager.GetString($"{Key}_{Plurals.Category(language.Name, count)}", language)
              ?? Strings.ResourceManager.GetString($"{Key}_{Plurals.Other}", language)
            : Strings.ResourceManager.GetString(Key, language);
        if (template == null)
            return null;
        var values = new List<object>();
        if (Count is long n)
            values.Add(n);
        foreach (PhraseArg arg in Args ?? new List<PhraseArg>())
        {
            string? value = arg.TryRender(language);
            if (value == null)
                return null;
            values.Add(value);
        }
        return string.Format(language.Equals(CultureInfo.InvariantCulture) ? CultureInfo.InvariantCulture : CultureInfo.CurrentCulture,
            template, values.ToArray());
    }

    public string Render(CultureInfo language) =>
        TryRender(language) ?? throw new InvalidOperationException($"No text {Key}.");

    /// <summary>English, for what is kept for older versions and for AI apps.</summary>
    public string English => Render(CultureInfo.InvariantCulture);
}

/// <summary>An argument of a <see cref="Phrase"/>: a text as it is (a name, a value), another phrase, or keys shown
/// with their names in the language of the moment.</summary>
public sealed class PhraseArg
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Phrase? Phrase { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HotkeyBinding? Keys { get; set; }

    public static implicit operator PhraseArg(string text) => new() { Text = text };

    public static implicit operator PhraseArg(Phrase phrase) => new() { Phrase = phrase };

    public static PhraseArg Of(HotkeyBinding keys) => new() { Keys = keys };

    public string? TryRender(CultureInfo language)
    {
        if (Phrase != null)
            return Phrase.TryRender(language);
        if (Keys != null)
            return Loc.In(language, () => KeyNames.Format(Keys));
        return Text ?? "";
    }
}

/// <summary>
/// The language of texts on a thread. Everything runs in the user's language (CurrentUICulture, set at start for every
/// thread), also an AI app's tool calls and the UI work they do; only the texts PuppyMacro gives an AI app that come from
/// code shared with the UI (the Code views' checks, hotkey conflicts, key names, what changed) are made in English with
/// <see cref="English{T}"/> (i18n.md).
/// </summary>
internal static class Loc
{
    /// <summary>Made in English, with invariant numbers: a text for an AI app.</summary>
    public static T English<T>(Func<T> make) => In(CultureInfo.InvariantCulture, make);

    /// <summary>Made in a language (InvariantCulture: English with invariant numbers; another one keeps this thread's
    /// format of numbers).</summary>
    public static T In<T>(CultureInfo language, Func<T> make)
    {
        CultureInfo oldTexts = CultureInfo.CurrentUICulture, oldFormat = CultureInfo.CurrentCulture;
        CultureInfo.CurrentUICulture = language;
        if (language.Equals(CultureInfo.InvariantCulture))
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            return make();
        }
        finally
        {
            CultureInfo.CurrentUICulture = oldTexts;
            CultureInfo.CurrentCulture = oldFormat;
        }
    }

    /// <summary>A resource text by its key ("Ai_Deleted_loop"), in this thread's language: for keys made from a value.</summary>
    public static string Text(string key) =>
        Strings.ResourceManager.GetString(key, CultureInfo.CurrentUICulture) ?? throw new InvalidOperationException($"No text {key}.");
}
