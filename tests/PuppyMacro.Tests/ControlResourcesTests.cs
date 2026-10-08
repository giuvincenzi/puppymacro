using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Xunit;

namespace PuppyMacro.Tests;

// Checks on the app's XAML files (read as XML, no WPF).
public sealed class ControlResourcesTests
{
    [Fact]
    public void Every_number_box_in_a_settings_card_of_the_app_sets_its_minimum_width()
    {
        // iNKORE 0.10.2 styles a NumberBox inside a SettingsCard with a StaticResource that its theme style
        // cannot find (ui.md): without a MinWidth of its own the layout throws "UnsetValue is not a valid value
        // for property MinWidth" and the window does not open.
        string appFolder = Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", "..", "PuppyMacro");
        var missing = new List<string>();
        foreach (string file in Directory.EnumerateFiles(appFolder, "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;
            XDocument xaml = XDocument.Load(file);
            foreach (XElement box in xaml.Descendants().Where(e => e.Name.LocalName == "NumberBox"))
            {
                bool inCard = box.Ancestors().Any(a => a.Name.LocalName == "SettingsCard");
                if (inCard && box.Attribute("MinWidth") == null)
                    missing.Add($"{Path.GetFileName(file)}: {(string?)box.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) ?? "NumberBox"}");
            }
        }
        Assert.Empty(missing);
    }

    private static string ThisFile([CallerFilePath] string path = "") => path;
}
