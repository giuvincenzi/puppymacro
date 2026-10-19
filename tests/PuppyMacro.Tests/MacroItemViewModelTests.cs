using System.Linq;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Views;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>A macro's card in the Macros list: what its summary line says about the macro.</summary>
public class MacroItemViewModelTests
{
    private static MacroAction Key() => new() { Type = MacroActionType.PressKey, Vk = 0x41 };

    /// <summary>
    /// A macro of one If (Then: three Press key) and one Press key: the editor's total says "5 actions" (it counts the
    /// actions inside Then and Else, MacroEditorWindow.UpdateTotals), and so must the macro's card in the Macros list.
    /// </summary>
    [Fact]
    public void The_card_counts_the_actions_inside_Then_and_Else_as_the_editor_does()
    {
        var macro = new MacroDefinition
        {
            Name = "Loot",
            Actions =
            {
                new MacroAction
                {
                    Type = MacroActionType.If,
                    If = new MacroIf
                    {
                        Conditions = { new MacroCondition { Kind = ConditionKind.Pixel, Color = "#000000" } },
                        Then = { Key(), Key(), Key() },
                    },
                },
                Key(),
            },
        };
        int editorCount = MacroDefinition.Walk(macro.Actions).Count();

        string summary = new MacroItemViewModel(macro).Summary;

        Assert.StartsWith(Plurals.Format("MacroEditor_ActionCount", editorCount), summary);
    }
}
