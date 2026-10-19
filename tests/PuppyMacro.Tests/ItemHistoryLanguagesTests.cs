using System.Globalization;
using System.Linq;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>What the History says changed (<see cref="ItemHistory.Diff"/>), kept as phrases, in every language.</summary>
public class ItemHistoryLanguagesTests
{
    /// <summary>
    /// Between two generated macros (every action type, condition kind, hotkey and field), and two loops and two remaps:
    /// every phrase renders in every language, its English text is the one kept for older versions, and it survives being
    /// written to the History file and read back.
    /// </summary>
    [Fact]
    public void Every_change_between_generated_items_reads_in_every_language()
    {
        var generator = new CodeJsonGenerator(7);
        for (int i = 0; i < 40; i++)
        {
            var pairs = new[]
            {
                (MacroJson.Serialize(generator.Macro()), MacroJson.Serialize(generator.Macro())),
                (LoopJson.Serialize(generator.Loop()), LoopJson.Serialize(generator.Loop())),
                (RemapJson.Serialize(generator.Remap()), RemapJson.Serialize(generator.Remap())),
            };
            foreach (var (before, after) in pairs)
            {
                var changes = ItemHistory.Diff(before, after);
                Assert.NotEmpty(changes);
                foreach (Phrase change in changes)
                {
                    var stored = System.Text.Json.JsonSerializer.Deserialize<Phrase>(System.Text.Json.JsonSerializer.Serialize(change))!;
                    Assert.Equal(change.English, stored.English);
                    foreach (var language in AppLanguages.All)
                    {
                        string? text = Loc.In(CultureInfo.GetCultureInfo(language.Code), () => stored.TryRender(CultureInfo.CurrentUICulture));
                        Assert.False(string.IsNullOrWhiteSpace(text), $"{language.Code}: {change.English}");
                    }
                }
            }
        }
    }
}
