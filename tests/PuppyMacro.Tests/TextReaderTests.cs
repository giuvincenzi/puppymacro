using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;
using PuppyMacro.Models;
using PuppyMacro.Services.Screen;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// Text on the screen: where a text is among the words Windows read (only the words that hold it, spaces and the
/// punctuation around words not counting, the highest then the leftmost), and a real read of drawn text.
/// </summary>
public class TextReaderTests
{
    private static List<TextReader.Word> Line(int y, params (string Text, int X, int Width)[] words) =>
        words.Select(w => new TextReader.Word(w.Text, new Rect(w.X, y, w.Width, 20))).ToList();

    [Fact]
    public void Only_the_words_that_hold_the_text_count()
    {
        var lines = new List<List<TextReader.Word>> { Line(100, ("Stats", 10, 50), ("&", 70, 10), ("Materials", 90, 90)) };

        Assert.Equal(new Rect(90, 100, 90, 20), TextReader.Find(lines, "materials", TextMatch.Contains, false));
        Assert.Equal(new Rect(10, 100, 170, 20), TextReader.Find(lines, "Stats & Materials", TextMatch.Exact, true));
    }

    [Fact]
    public void Punctuation_around_the_words_of_the_text_looked_for_does_not_count_either()
    {
        var lines = new List<List<TextReader.Word>> { Line(0, ("Modification", 0, 120), ("(l)", 130, 30)) };

        Assert.Equal(new Rect(0, 0, 160, 20), TextReader.Find(lines, "Modification (l)", TextMatch.Exact, false));
        Assert.Equal(new Rect(0, 0, 160, 20), TextReader.Find(lines, "Modification (l)", TextMatch.Contains, false));
        Assert.Equal(new Rect(0, 0, 160, 20), TextReader.Find(lines, "Modification l", TextMatch.Exact, false));
    }

    [Fact]
    public void Exact_needs_whole_words_and_Contains_takes_a_part()
    {
        var lines = new List<List<TextReader.Word>> { Line(0, ("Primes.", 0, 70), ("Primeval", 80, 80)) };

        Assert.Equal(new Rect(0, 0, 70, 20), TextReader.Find(lines, "Primes", TextMatch.Exact, false));
        Assert.Null(TextReader.Find(lines, "Prime", TextMatch.Exact, false));
        Assert.Equal(new Rect(0, 0, 70, 20), TextReader.Find(lines, "Prime", TextMatch.Contains, false));
    }

    [Fact]
    public void Match_case_tells_capitals_apart()
    {
        var lines = new List<List<TextReader.Word>> { Line(0, ("Armor", 0, 50)) };

        Assert.Null(TextReader.Find(lines, "armor", TextMatch.Exact, true));
        Assert.NotNull(TextReader.Find(lines, "armor", TextMatch.Exact, false));
    }

    [Fact]
    public void Words_written_without_spaces_are_found()
    {
        // Windows gives Chinese and Japanese text as words with no spaces between them.
        var lines = new List<List<TextReader.Word>> { Line(0, ("开始", 0, 40), ("游戏", 40, 40)) };

        Assert.Equal(new Rect(0, 0, 80, 20), TextReader.Find(lines, "开始 游戏", TextMatch.Exact, true));
    }

    [Fact]
    public void Several_places_give_the_highest_then_the_leftmost()
    {
        var lines = new List<List<TextReader.Word>>
        {
            Line(300, ("Currency", 10, 80)),
            Line(120, ("Currency", 400, 80)),
            Line(120, ("Currency", 200, 80)),
        };

        Assert.Equal(new Rect(200, 120, 80, 20), TextReader.Find(lines, "Currency", TextMatch.Exact, false));
    }

    [Fact]
    public void Windows_reads_drawn_text_where_it_is()
    {
        // Needs a language Windows can read; every Windows has the one of its display language.
        if (TextReader.Languages.Count == 0)
            return;
        using var picture = new Mat(200, 700, MatType.CV_8UC3, Scalar.All(255));
        Cv2.PutText(picture, "Open the CURRENCY tab", new Point(40, 120), HersheyFonts.HersheySimplex, 1.4, Scalar.All(0), 3);

        var found = TextReader.Find(TextReader.Read(picture, null), "currency", TextMatch.Exact, false);

        Assert.NotNull(found);
        var center = new Point(found.Value.X + found.Value.Width / 2, found.Value.Y + found.Value.Height / 2);
        // Where OpenCV drew "CURRENCY": after "Open the ", on the baseline at y 120.
        int start = 40 + Cv2.GetTextSize("Open the ", HersheyFonts.HersheySimplex, 1.4, 3, out _).Width;
        var word = Cv2.GetTextSize("CURRENCY", HersheyFonts.HersheySimplex, 1.4, 3, out _);
        Assert.InRange(center.X, start + word.Width / 2 - 15, start + word.Width / 2 + 15);
        Assert.InRange(center.Y, 120 - word.Height / 2 - 10, 120 - word.Height / 2 + 10);
    }
}
