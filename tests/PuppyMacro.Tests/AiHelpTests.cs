using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using PuppyMacro.Services.Ai;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>get_help's guide (site/guide, embedded in the app): every section, as readable text.</summary>
public class AiHelpTests
{
    private static string GuideFolder => Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", "..", "site", "guide");

    [Fact]
    public void Every_section_of_the_guide_has_its_file_and_every_file_its_section()
    {
        var files = Directory.EnumerateFiles(GuideFolder, "*.html").Select(Path.GetFileNameWithoutExtension)
            .Where(f => f != "_page").OrderBy(f => f).ToList();

        Assert.Equal(files, AiHelp.Sections.Select(s => s.Id).OrderBy(f => f));
        Assert.All(AiHelp.Sections, s => Assert.False(string.IsNullOrWhiteSpace(s.Title)));
        Assert.All(AiHelp.Sections, s => Assert.StartsWith($"# {s.Title}\n", AiHelp.Text(s.Id)));
    }

    [Fact]
    public void Every_section_reads_as_text_without_html()
    {
        foreach (var section in AiHelp.Sections)
        {
            string text = AiHelp.Text(section.Id)!;
            Assert.DoesNotContain("<", text);
            Assert.DoesNotContain("&amp;", text);
            Assert.DoesNotContain("&nbsp;", text);
        }
        Assert.Equal(AiHelp.Text("remap"), AiHelp.Text(" REMAP "));
    }

    [Fact]
    public void The_overlay_section_reads_as_sentences_not_as_the_drawing_of_the_panel()
    {
        // overlay-mode.html draws the overlay panel and the floating buttons with divs and spans (role="img"): the AI
        // reads their labels glued together.
        string text = AiHelp.Text("overlay-mode")!;

        Assert.DoesNotContain("Auto attackF6Running", text);
        Assert.DoesNotContain("PuppyMacro✥⇥↗", text);
    }

    private static string ThisFile([CallerFilePath] string path = "") => path;
}
