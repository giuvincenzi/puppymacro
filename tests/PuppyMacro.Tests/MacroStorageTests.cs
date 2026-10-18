using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// Saving, reading and editing macros: the macro files (<see cref="MacroLibrary"/>), the Code view's checks
/// (<see cref="MacroJson"/>), the editor's tree (<see cref="MacroEditList"/>), the images and the History of macros.
/// </summary>
public class MacroStorageTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private string Macros => Directory.CreateDirectory(_folder.File("macros")).FullName;
    private string ImagesFolder => _folder.File("images");

    private const string ImageA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static MacroAction Press(int vk = 0x41, Guid? group = null) =>
        new() { Type = MacroActionType.PressKey, Vk = vk, GroupId = group };

    private static MacroCondition Pixel() =>
        new() { Kind = ConditionKind.Pixel, X = 5, Y = 6, Color = "#3BD16F", Tolerance = 5 };

    private static MacroCondition Image(string name, string imageId = ImageA) => new()
    {
        Kind = ConditionKind.Image,
        Name = name,
        Images = new() { new ConditionImage { Id = imageId, X = 100, Y = 200 } },
        Similarity = 90,
    };

    private static MacroAction If(MacroCondition condition, params MacroAction[] then) => new()
    {
        Type = MacroActionType.If,
        If = new MacroIf { Conditions = { condition }, Then = then.ToList() },
    };

    private static MacroDefinition Macro(params MacroAction[] actions)
    {
        var macro = new MacroDefinition { Name = "Accept the match" };
        macro.Actions.AddRange(actions);
        return macro;
    }

    private static List<CodeProblem> Problems(string text, Guid id)
    {
        MacroJson.Parse(text, id, (_, _) => null, out var problems);
        return problems;
    }

    // ================= The Code view's checks on empty entries =================

    /// <summary>
    /// A null in a list of the Code view's text: the strict reading (RespectNullableAnnotations) does not refuse null
    /// elements of a list, so the checks get them. They must be a problem at their line, never an exception: the Code
    /// view checks the text as it is typed, and a .puppyitems file from someone else is read with the same checks.
    /// </summary>
    [Theory]
    [InlineData("Actions")]
    [InlineData("Groups")]
    [InlineData("Actions[0].If.Conditions")]
    [InlineData("Actions[0].If.Then")]
    [InlineData("Actions[0].If.Conditions[0].Images")]
    [InlineData("Actions[1].Path")]
    public void A_null_entry_in_a_list_is_a_problem_not_a_crash(string list)
    {
        var path = new MacroAction { Type = MacroActionType.MovePath, Path = { new PathPoint { X = 1, Y = 2, T = 0 } } };
        var macro = Macro(If(Image("Button"), Press()), path);
        JsonObject root = JsonNode.Parse(MacroJson.Serialize(macro))!.AsObject();
        JsonNode node = root;
        foreach (string part in list.Split('.'))
        {
            int bracket = part.IndexOf('[');
            node = bracket < 0 ? node[part]! : node[part[..bracket]]![int.Parse(part[(bracket + 1)..^1], CultureInfo.InvariantCulture)]!;
        }
        node.AsArray().Add(null);
        string text = root.ToJsonString(CodeJson.Options);

        var exception = Record.Exception(() => Problems(text, macro.Id));

        Assert.Null(exception);
        Assert.NotEmpty(Problems(text, macro.Id));
    }

    [Fact]
    public void A_shared_file_with_a_null_action_is_shown_with_why_instead_of_failing_the_import()
    {
        JsonObject file = JsonNode.Parse(ItemFile.Write(new object[] { Macro(Press()) }, new Version(1, 17, 0)))!.AsObject();
        file["Items"]![0]!["Item"]!["Actions"]!.AsArray().Add(null);
        var macros = new MacroLibrary(Macros, ImagesFolder);

        ItemFileContent? content = null;
        var exception = Record.Exception(() => content = ItemFile.Read(file.ToJsonString(), new AppSettings(), macros, new Version(1, 17, 0)));

        Assert.Null(exception);
        Assert.NotNull(content!.Entries.Single().Problem);
    }

    // ================= Groups: one list each =================

    /// <summary>
    /// A group lives in one list (data.md). The Code view refuses a group whose run in the macro's list is followed by
    /// its actions inside a Then of the run, but accepts the same when the If with the Then comes last in the run; then
    /// the save (<see cref="MacroDefinition.NormalizeGroups()"/>) silently takes the inner action out of the group.
    /// </summary>
    [Fact]
    public void A_group_with_actions_in_two_lists_is_refused_whatever_their_order()
    {
        var group = new MacroGroup { Name = "G" };
        var first = Macro(If(Pixel(), Press(0x43, group.Id)), Press(0x42, group.Id));
        first.Actions[0].GroupId = group.Id;
        first.Groups.Add(group);
        var last = Macro(Press(0x42, group.Id), If(Pixel(), Press(0x43, group.Id)));
        last.Actions[1].GroupId = group.Id;
        last.Groups.Add(group);

        Assert.NotEmpty(Problems(MacroJson.Serialize(first), first.Id)); // refused: the If's Then ends the group
        Assert.NotEmpty(Problems(MacroJson.Serialize(last), last.Id));
    }

    // ================= Recorded macros pass the Code view's checks =================

    /// <summary>
    /// What Record makes must be a macro the Code view, the AI tools and a .puppyitems import accept: a touchpad tap
    /// (button down and up in the same millisecond) or a key held more than a minute are recorded as they happened.
    /// </summary>
    [Theory]
    [InlineData(RawEventKind.KeyDown, RawEventKind.KeyUp, 0x41, 0)]
    [InlineData(RawEventKind.KeyDown, RawEventKind.KeyUp, 0x41, 61_000)]
    [InlineData(RawEventKind.ButtonDown, RawEventKind.ButtonUp, 0x01, 0)]
    [InlineData(RawEventKind.ButtonDown, RawEventKind.ButtonUp, 0x01, 61_000)]
    public void A_recorded_press_reads_back_in_the_code_view(RawEventKind down, RawEventKind up, int vk, double heldMs)
    {
        var actions = MacroBuilder.Build(new[] { new RawEvent(down, vk, 10, 10, 0, 1000), new RawEvent(up, vk, 10, 10, 0, 1000 + heldMs) });
        var macro = Macro(actions.ToArray());

        Assert.Empty(Problems(MacroJson.Serialize(macro), macro.Id));
    }

    // ================= Macro files =================

    /// <summary>
    /// A macro file without an Id (written by hand, by a script, an older tool): each start gives it a new Id, so every
    /// save writes one more file next to it, and the macro is there twice at the next start.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("\"Id\": \"00000000-0000-0000-0000-000000000000\",")]
    public void A_file_without_an_id_is_still_one_macro_after_a_save_and_a_restart(string id)
    {
        File.WriteAllText(Path.Combine(Macros, "hand made.json"),
            "{ " + id + " \"Name\": \"Hand made\", \"Actions\": [ { \"Type\": \"PressKey\", \"Vk\": 65 } ] }");
        var library = new MacroLibrary(Macros, ImagesFolder);
        library.Load(new List<Guid>(), out _);
        MacroDefinition macro = library.Macros.Single();
        macro.Enabled = false; // its switch
        Assert.True(library.TrySave(macro, out _));

        var restarted = new MacroLibrary(Macros, ImagesFolder);
        restarted.Load(new List<Guid> { macro.Id }, out _);

        Assert.Single(restarted.Macros);
    }

    /// <summary>
    /// A copy of a macro file in the folder (File Explorer's "- Copy", a sync conflict copy): two macros with one Id, which
    /// every save, delete and hotkey finds by Id.
    /// </summary>
    [Fact]
    public void Two_files_with_one_id_never_load_as_two_macros_with_one_id()
    {
        var library = new MacroLibrary(Macros, ImagesFolder);
        var macro = Macro(Press());
        Assert.True(library.TrySave(macro, out _));
        File.Copy(Path.Combine(Macros, $"{macro.Id}.json"), Path.Combine(Macros, $"{macro.Id} - Copy.json"));

        var restarted = new MacroLibrary(Macros, ImagesFolder);
        restarted.Load(new List<Guid> { macro.Id }, out _);

        var ids = restarted.Macros.Select(m => m.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    /// <summary>
    /// The images of a macro file that could not be read at start (a newer version's file, a file another program had
    /// open) are not deleted by the next save: the macro and its images come back when the file can be read.
    /// </summary>
    [Fact]
    public void The_images_of_a_macro_file_that_could_not_be_read_are_kept()
    {
        var library = new MacroLibrary(Macros, ImagesFolder);
        string id = library.Images.Add(new byte[] { 1, 2, 3, 4, 5 });
        var macro = Macro(If(Image("Button", id), Press()));
        string text = MacroJson.Serialize(macro).Replace("\"PressKey\"", "\"PressKeyOfANewerVersion\"");
        File.WriteAllText(Path.Combine(Macros, $"{macro.Id}.json"), text);

        var restarted = new MacroLibrary(Macros, ImagesFolder);
        restarted.Load(new List<Guid>(), out string? warning);
        Assert.NotNull(warning); // skipped
        Assert.True(restarted.TrySave(Macro(Press()), out _));
        restarted.ReleaseUnusedImages();

        Assert.True(restarted.Images.Has(id));
    }

    /// <summary>A file edited by hand with an empty group loads (the empty entry left out) instead of failing the start.</summary>
    [Fact]
    public void A_file_with_an_empty_group_loads()
    {
        string text = "{ \"Actions\": [ { \"Type\": \"PressKey\", \"Vk\": 65 } ], \"Groups\": [ null ], \"Name\": \"Hand made\" }";
        File.WriteAllText(Path.Combine(Macros, $"{Guid.NewGuid()}.json"), text);
        var library = new MacroLibrary(Macros, ImagesFolder);

        var exception = Record.Exception(() =>
        {
            library.Load(new List<Guid>(), out _);
            Assert.Single(library.Macros);
        });

        Assert.Null(exception);
    }

    // ================= History =================

    /// <summary>
    /// The History says what changed in a call's arguments, also of a parameter named like a key ("Target", "Source",
    /// "…Hotkey"): a point is not a key.
    /// </summary>
    [Fact]
    public void A_changed_argument_named_Target_is_in_the_history()
    {
        Guid component = Guid.NewGuid();
        MacroDefinition Calling(int x) => new()
        {
            Id = Guid.Parse("8f0b8f1e-58c5-4d0a-9d4e-0d6e0f5b2c11"),
            Name = "Calls",
            Actions =
            {
                new MacroAction
                {
                    Type = MacroActionType.Component,
                    ComponentId = component,
                    Arguments = new() { ["Target"] = new JsonObject { ["X"] = x, ["Y"] = 20 } },
                },
            },
        };

        List<string> changes = ItemHistory.Changes(MacroJson.Serialize(Calling(10)), MacroJson.Serialize(Calling(500)));

        Assert.NotEmpty(changes);
    }

    // ================= Sharing =================

    /// <summary>The file name Export offers for one macro is a name Windows can save: at most 255 characters.</summary>
    [Fact]
    public void The_file_name_of_a_macro_with_a_very_long_name_fits_in_windows()
    {
        var macro = Macro(Press());
        macro.Name = new string('a', 300);

        Assert.True(ItemFile.FileName(new object[] { macro }).Length <= 255);
    }
}
