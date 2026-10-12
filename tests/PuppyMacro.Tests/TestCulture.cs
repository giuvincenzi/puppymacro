using System.Globalization;
using System.Runtime.CompilerServices;

namespace PuppyMacro.Tests;

/// <summary>
/// The tests check English texts and en-US numbers: they run in English whatever the language of the PC (i18n.md).
/// A test of another language sets it itself (Loc.In, the culture of a Phrase).
/// </summary>
internal static class TestCulture
{
    [ModuleInitializer]
    internal static void English()
    {
        var english = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = english;
        CultureInfo.DefaultThreadCurrentUICulture = english;
        CultureInfo.CurrentCulture = english;
        CultureInfo.CurrentUICulture = english;
    }
}
