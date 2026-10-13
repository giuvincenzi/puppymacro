using System.Linq;
using PuppyMacro.Resources;

namespace PuppyMacro.Views;

/// <summary>
/// The accessible name of a list's row (ui.md): the texts the row shows, joined as a list ("10:42, Claude, Read loop
/// Spam, Done"). Without it UI Automation names the row after its item's class.
/// </summary>
internal static class AccessibleNames
{
    public static string Join(params string?[] texts) =>
        string.Join(Strings.ListSeparator, texts.Where(text => !string.IsNullOrWhiteSpace(text)));
}
