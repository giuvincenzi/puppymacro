using System;
using System.Linq;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Views;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>The macro editor's rows: what each one says about its action.</summary>
public class MacroRowsTests
{
    private static MacroActionRowViewModel IfRow(params MacroCondition[] conditions) => new(new MacroAction
    {
        Type = MacroActionType.If,
        If = new MacroIf { Conditions = conditions.ToList(), Wait = ConditionWait.UpTo, WaitSeconds = 2.5 },
    });

    [Fact]
    public void An_if_row_says_what_each_kind_of_condition_looks_for()
    {
        Assert.Equal("the pixel at 10, 20 is #3BD16F · waits up to 2.5 s",
            IfRow(new MacroCondition { Kind = ConditionKind.Pixel, X = 10, Y = 20, Color = "#3BD16F" }).Detail);
        Assert.Equal("Accept is on the screen · waits up to 2.5 s",
            IfRow(new MacroCondition { Kind = ConditionKind.Image, Name = "Accept" }).Detail);
        Assert.Equal("“Game over” is on the screen · waits up to 2.5 s",
            IfRow(new MacroCondition { Kind = ConditionKind.Text, Name = "Text 1", Text = "Game over" }).Detail);
        Assert.Equal("2 conditions, all true · waits up to 2.5 s",
            IfRow(new MacroCondition { Kind = ConditionKind.Pixel, Color = "#000000" }, new MacroCondition { Kind = ConditionKind.Image, Name = "A" }).Detail);
    }

    [Fact]
    public void An_if_row_with_a_component_condition_names_it_not_an_empty_text()
    {
        // An If whose one condition calls a Library component ("Rare item" finds a rare item on the screen).
        var row = IfRow(new MacroCondition { Kind = ConditionKind.Component, Name = "Rare item", ComponentId = Guid.NewGuid() });

        Assert.DoesNotContain("“”", row.Detail);
        Assert.Contains("Rare item", row.Detail);
    }

    [Fact]
    public void A_paste_text_row_never_cuts_a_character_in_half()
    {
        // 31 letters and an emoji (two UTF-16 chars): the row keeps the first 32 chars.
        var row = new MacroActionRowViewModel(new MacroAction { Type = MacroActionType.PasteText, Text = new string('a', 31) + "😀 and more" });

        Assert.True(WellFormed(row.Detail), $"A half character in \"{row.Detail}\"");
    }

    /// <summary>No lone half of a surrogate pair (the screen shows a box, a screen reader nothing).</summary>
    internal static bool WellFormed(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                i++;
            else if (char.IsSurrogate(text[i]))
                return false;
        }
        return true;
    }

    [Fact]
    public void Rows_format_numbers_in_the_language_of_the_moment()
    {
        var row = IfRow(new MacroCondition { Kind = ConditionKind.Image, Name = "Accept" });

        string detail = Loc.In(System.Globalization.CultureInfo.GetCultureInfo("it"), () =>
        {
            var old = System.Globalization.CultureInfo.CurrentCulture;
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("it-IT");
            try
            {
                return row.Detail;
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = old;
            }
        });

        Assert.Contains("2,5", detail);
    }
}
