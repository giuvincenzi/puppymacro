using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;
using PuppyMacro.Models;
using PuppyMacro.Services.Screen;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// Where a text is among the words read, at its edges: the punctuation of other languages around a word, the same letters
/// written in another Unicode form, capitals outside English.
/// </summary>
public class TextReaderEdgesTests
{
    private static List<List<TextReader.Word>> Lines(params string[] words) =>
        new() { words.Select((w, i) => new TextReader.Word(w, new Rect(i * 100, 0, 90, 20))).ToList() };

    [Theory]
    [InlineData("“Start”", "Start")]   // “Start”, English typographic quotes
    [InlineData("‘Start’", "Start")]   // ‘Start’
    [InlineData("«Démarrer»", "Démarrer")] // «Démarrer», French
    [InlineData("„Starten“", "Starten")] // „Starten“, German
    [InlineData("¿Continuar?", "Continuar")] // ¿Continuar?, Spanish
    [InlineData("¡Listo!", "Listo")]          // ¡Listo!
    public void Punctuation_of_other_languages_around_a_word_does_not_count(string read, string wanted)
    {
        // The rule of TextReader.Find: punctuation at a word's ends does not count, so the word is found whole.
        Assert.Equal(new Rect(0, 0, 90, 20), TextReader.Find(Lines(read), wanted, TextMatch.Exact, false));
    }

    [Fact]
    public void The_same_letters_in_another_Unicode_form_are_the_same_text()
    {
        // "Café" read by Windows (é as one character) and typed or pasted with e and a combining accent (macOS, some web pages).
        string composed = "Café", decomposed = "Café";

        Assert.NotNull(TextReader.Find(Lines(composed), decomposed, TextMatch.Exact, true));
        Assert.NotNull(TextReader.Find(Lines(decomposed), composed, TextMatch.Exact, true));
    }

    [Theory]
    [InlineData("ÉCRAN", "écran")]
    [InlineData("ΔΕΛΤΑ", "δελτα")] // ΔΕΛΤΑ / δελτα
    [InlineData("ПУСК", "пуск")]             // ПУСК / пуск
    public void Without_Match_case_capitals_of_other_alphabets_are_the_same(string read, string wanted)
    {
        Assert.NotNull(TextReader.Find(Lines(read), wanted, TextMatch.Exact, false));
        Assert.Null(TextReader.Find(Lines(read), wanted, TextMatch.Exact, true));
    }

    [Fact]
    public void Only_spaces_or_punctuation_looked_for_find_nothing_or_the_punctuation()
    {
        Assert.Null(TextReader.Find(Lines("Start"), "   ", TextMatch.Contains, false));
        Assert.Equal(new Rect(100, 0, 90, 20), TextReader.Find(Lines("Loading", "..."), "...", TextMatch.Exact, false));
    }
}
