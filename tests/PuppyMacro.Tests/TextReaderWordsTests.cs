using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;
using PuppyMacro.Models;
using PuppyMacro.Services.Screen;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// Where a text is among the words read, with Contains: "a part of a word" (TextReader.Find), never the end of one word
/// glued to the start of the next; and the cases that work today kept.
/// </summary>
public class TextReaderWordsTests
{
    private static List<List<TextReader.Word>> Line(params string[] words) =>
        new() { words.Select((w, i) => new TextReader.Word(w, new Rect(i * 100, 0, 90, 20))).ToList() };

    [Theory]
    [InlineData("OK", "Go", "Kart")]         // "Go Kart" read: the OK button is not there
    [InlineData("Start", "Last", "Artist")]  // "Last Artist"
    [InlineData("Save", "Visa", "Venue")]    // "Visa Venue"
    public void Contains_does_not_find_a_text_made_of_the_end_of_one_word_and_the_start_of_the_next(string wanted, string first, string second)
    {
        // The words are glued without spaces so that Chinese and Japanese (no spaces between words) are found, and a short
        // text looked for with Contains (the default Match) is then found across two Latin words: a macro clicks there.
        Assert.Null(TextReader.Find(Line(first, second), wanted, TextMatch.Contains, false));
    }

    [Theory]
    [InlineData("OK", "Look", 0, 90)]       // inside one word
    [InlineData("Go Kart", "Go", 0, 190)]   // the words themselves, with or without the space
    [InlineData("GoKart", "Go", 0, 190)]
    [InlineData("art", "Go", 100, 90)]
    public void Contains_finds_a_part_of_a_word_and_whole_words(string wanted, string first, int x, int width)
    {
        var found = TextReader.Find(Line(first, "Kart"), wanted, TextMatch.Contains, false);

        Assert.Equal(new Rect(x, 0, width, 20), found);
    }

    [Fact]
    public void Chinese_words_read_without_spaces_are_found_across_words()
    {
        Assert.Equal(new Rect(0, 0, 190, 20), TextReader.Find(Line("开始", "游戏"), "开始游戏", TextMatch.Contains, true));
    }
}
