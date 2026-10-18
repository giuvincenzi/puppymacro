using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using OpenCvSharp;
using PuppyMacro.Models;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace PuppyMacro.Services.Screen;

/// <summary>
/// Text on the screen with Windows' own text recognition (Windows.Media.Ocr, on the processor, nothing to install): the
/// languages are those whose Windows language pack is installed.
/// </summary>
internal static class TextReader
{
    private static readonly ConcurrentDictionary<string, OcrEngine?> Engines = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The languages Windows can read (their tags: "en-US", "it-IT"…).</summary>
    public static IReadOnlyList<string> Languages => OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();

    /// <summary>
    /// The engine of a language; Windows' languages when it is null or Windows has not that language (any more). Null when
    /// Windows can read none of them.
    /// </summary>
    private static OcrEngine? Engine(string? language)
    {
        return Engines.GetOrAdd(language ?? "", tag =>
        {
            if (tag.Length > 0)
            {
                try
                {
                    var lang = new Language(tag);
                    if (OcrEngine.IsLanguageSupported(lang))
                        return OcrEngine.TryCreateFromLanguage(lang);
                }
                catch (ArgumentException)
                {
                }
            }
            return OcrEngine.TryCreateFromUserProfileLanguages();
        });
    }

    /// <summary>Whether Windows can read a language on this PC (null: Windows' languages).</summary>
    public static bool CanRead(string? language)
    {
        if (language == null)
            return Engine(null) != null;
        try
        {
            return OcrEngine.IsLanguageSupported(new Language(language));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>A language's name in its own language ("Italiano"), for the editor's list.</summary>
    public static string NativeName(string tag)
    {
        try
        {
            return new Language(tag).NativeName;
        }
        catch (ArgumentException)
        {
            return tag;
        }
    }

    /// <summary>The language Windows reads when a condition has none.</summary>
    public static string? WindowsLanguage => Engine(null)?.RecognizerLanguage.LanguageTag;

    /// <summary>A word read on the picture: its text and its rectangle on the picture.</summary>
    internal readonly record struct Word(string Text, Rect Bounds);

    /// <summary>The lines read on a BGR picture, each as its words, with their places on the picture.</summary>
    public static List<List<Word>> Read(Mat bgr, string? language)
    {
        var lines = new List<List<Word>>();
        var engine = Engine(language);
        if (engine == null || bgr.Width < 1 || bgr.Height < 1)
            return lines;

        // Windows reads at most MaxImageDimension pixels on a side: a larger picture is shrunk, and the places grown back.
        double scale = Math.Min(1.0, OcrEngine.MaxImageDimension / (double)Math.Max(bgr.Width, bgr.Height));
        using Mat sized = scale < 1 ? bgr.Resize(new Size((int)(bgr.Width * scale), (int)(bgr.Height * scale)), 0, 0, InterpolationFlags.Area) : bgr.Clone();
        using var bgra = new Mat();
        Cv2.CvtColor(sized, bgra, ColorConversionCodes.BGR2BGRA);
        var bytes = new byte[bgra.Total() * 4];
        Marshal.Copy(bgra.Data, bytes, 0, bytes.Length);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(bytes.AsBuffer(), BitmapPixelFormat.Bgra8, bgra.Width, bgra.Height);
        OcrResult result = engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();

        // When Windows finds the text tilted it reads a straightened picture, and its places are turned by that angle
        // around the picture's center: they are turned back.
        double angle = (result.TextAngle ?? 0) * Math.PI / 180, cx = sized.Width / 2.0, cy = sized.Height / 2.0;
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        foreach (var line in result.Lines)
        {
            var words = new List<Word>();
            foreach (var word in line.Words)
            {
                var q = word.BoundingRect;
                double mx = q.X + q.Width / 2 - cx, my = q.Y + q.Height / 2 - cy;
                double x = (mx * cos - my * sin + cx) / scale, y = (mx * sin + my * cos + cy) / scale;
                double w = q.Width / scale, h = q.Height / scale;
                words.Add(new Word(word.Text, new Rect((int)Math.Round(x - w / 2), (int)Math.Round(y - h / 2), (int)Math.Round(w), (int)Math.Round(h))));
            }
            if (words.Count > 0)
                lines.Add(words);
        }
        return lines;
    }

    /// <summary>
    /// Where <paramref name="text"/> is on the picture: the rectangle of the words that hold it, only those (a word of a
    /// longer line is clicked on that word). Spaces do not count (Windows writes none between Chinese or Japanese words).
    /// Exact: the text is whole words; otherwise it can be a part of a word. Several places: the highest, then the
    /// leftmost. Null when it is not there.
    /// </summary>
    public static Rect? Find(List<List<Word>> lines, string text, TextMatch match, bool matchCase)
    {
        // The text is taken word by word as the words read are ("Modification (l)" is "Modification" and "l"): punctuation
        // at a word's ends does not count, a word of punctuation only stays.
        string wanted = string.Concat(text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Bare));
        if (wanted.Length == 0)
            return null;
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        Rect? best = null;
        foreach (var words in lines)
        {
            // The line without spaces, and which word each character comes from.
            var joined = new StringBuilder();
            var owner = new List<int>();
            for (int i = 0; i < words.Count; i++)
            {
                // Punctuation at a word's ends does not count ("Primes." is the word "Primes"), as in the tests.
                string w = Bare(words[i].Text);
                joined.Append(w);
                owner.AddRange(Enumerable.Repeat(i, w.Length));
            }
            string line = joined.ToString();
            for (int at = line.IndexOf(wanted, comparison); at >= 0; at = at + 1 < line.Length ? line.IndexOf(wanted, at + 1, comparison) : -1)
            {
                int first = owner[at], last = owner[at + wanted.Length - 1];
                bool whole = (at == 0 || owner[at - 1] != first) && (at + wanted.Length == line.Length || owner[at + wanted.Length] != last);
                if (match == TextMatch.Exact && !whole)
                    continue;
                Rect r = words[first].Bounds;
                for (int i = first + 1; i <= last; i++)
                    r = r.Union(words[i].Bounds);
                if (best is not { } b || r.Y < b.Y || (r.Y == b.Y && r.X < b.X))
                    best = r;
                break;
            }
        }
        return best;
    }

    /// <summary>
    /// A word without spaces and without the punctuation at its ends; a word of punctuation only, as it is. In Unicode's
    /// composed form (NFC), so "é" typed as e and an accent is the "é" Windows reads.
    /// </summary>
    private static string Bare(string word)
    {
        string spaced = WithoutSpaces(word.Normalize(NormalizationForm.FormC));
        int start = 0, end = spaced.Length;
        while (start < end && IsEndPunctuation(spaced[start]))
            start++;
        while (end > start && IsEndPunctuation(spaced[end - 1]))
            end--;
        return start == end ? spaced : spaced[start..end];
    }

    /// <summary>
    /// Punctuation that does not count at a word's ends: every quote and bracket of every script (Unicode's opening,
    /// closing, initial and final punctuation: “ ” ‘ ’ « » „ 「 」 ( )), and the marks that end or split a sentence in the
    /// app's languages. Not hyphens, % or other signs that are part of what is written ("-5", "50%").
    /// </summary>
    private static bool IsEndPunctuation(char c) => char.GetUnicodeCategory(c) switch
    {
        UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation
            or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation => true,
        _ => SentencePunctuation.Contains(c),
    };

    private const string SentencePunctuation = ".,:;!?\"'…·¿¡、。，：；！？．・؟،؛";

    private static string WithoutSpaces(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c)));
}
