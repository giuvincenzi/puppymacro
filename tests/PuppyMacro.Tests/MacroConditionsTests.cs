using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>Macros with If actions: the model, the files, the Code view's checks and the images of Image conditions.</summary>
public class MacroConditionsTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private string Macros => Directory.CreateDirectory(_folder.File("macros")).FullName;
    private string ImagesFolder => _folder.File("images");

    private static byte[] Png(int seed)
    {
        var bytes = new byte[64];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static MacroAction Click(Guid? found = null) =>
        new() { Type = MacroActionType.Click, Vk = 0x01, X = 10, Y = 20, FoundId = found };

    private static MacroAction Press(int vk = 0x41) => new() { Type = MacroActionType.PressKey, Vk = vk };

    private static MacroCondition Pixel() =>
        new() { Kind = ConditionKind.Pixel, X = 5, Y = 6, Color = "#3BD16F", Tolerance = 5 };

    private static MacroCondition Image(string name, string imageId) => new()
    {
        Kind = ConditionKind.Image,
        Name = name,
        Images = new() { new ConditionImage { Id = imageId, X = 100, Y = 200 } },
        Similarity = 90,
    };

    private static MacroAction If(MacroCondition condition, IEnumerable<MacroAction>? then = null, IEnumerable<MacroAction>? orElse = null) => new()
    {
        Type = MacroActionType.If,
        If = new MacroIf
        {
            Conditions = { condition },
            Wait = ConditionWait.UpTo,
            WaitSeconds = 5,
            Then = (then ?? Enumerable.Empty<MacroAction>()).ToList(),
            Else = (orElse ?? Enumerable.Empty<MacroAction>()).ToList(),
        },
    };

    private static MacroDefinition Macro(params MacroAction[] actions)
    {
        var macro = new MacroDefinition { Name = "Accept the match" };
        macro.Actions.AddRange(actions);
        return macro;
    }

    private static List<CodeProblem> Problems(MacroDefinition macro, Func<string, bool>? hasImage = null)
    {
        MacroJson.Parse(MacroJson.Serialize(macro), macro.Id, (_, _) => null, out var problems, hasImage);
        return problems;
    }

    private const string ImageA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    // ================= Model and files =================

    [Fact]
    public void An_action_without_conditions_writes_none_of_the_new_fields()
    {
        string json = MacroJson.Serialize(Press());

        Assert.DoesNotContain("\"If\"", json);
        Assert.DoesNotContain("FoundId", json);
        Assert.DoesNotContain("FoundOffset", json);
    }

    [Fact]
    public void An_If_with_its_branches_reads_back_the_same()
    {
        var image = Image("Accept button", ImageA);
        var macro = Macro(Press(), If(image, then: new[] { Click(image.Id), If(Pixel(), then: new[] { Press(0x0D) }) }, orElse: new[] { Press(0x1B) }));

        string json = MacroJson.Serialize(macro);
        var read = JsonSerializer.Deserialize<MacroDefinition>(json, MacroJson.Options)!;

        Assert.Equal(json, MacroJson.Serialize(read));
        Assert.Equal(6, read.AllActions.Count());
        Assert.Equal(image.Id, read.Actions[1].If!.Then[0].FoundId);
    }

    [Fact]
    public void A_clone_copies_the_branches_and_the_conditions()
    {
        var original = If(Image("A", ImageA), then: new[] { Press() });
        var copy = original.Clone();

        copy.If!.Then.Add(Press());
        copy.If.Conditions[0].Images![0].X = 999;

        Assert.Single(original.If!.Then);
        Assert.Equal(100, original.If.Conditions[0].Images![0].X);
    }

    [Fact]
    public void Walk_goes_through_then_and_else_in_playback_order()
    {
        MacroAction a = Press(0x41), b = Press(0x42), c = Press(0x43), d = Press(0x44);
        var macro = Macro(a, If(Pixel(), then: new[] { b }, orElse: new[] { c }), d);

        Assert.Equal(new[] { a, macro.Actions[1], b, c, d }, macro.AllActions);
        Assert.Equal(3, MacroDefinition.Lists(macro.Actions).Count());
    }

    [Fact]
    public void The_duration_is_a_range_and_a_wait_as_long_as_it_takes_has_no_end()
    {
        var press = Press(); // 30 ms held
        var wait = If(Pixel(), then: new[] { press });
        wait.If!.WaitSeconds = 2;

        var range = Macro(wait).Duration;
        Assert.Equal(30, range.MinMs);
        Assert.Equal(2030, range.MaxMs);
        Assert.False(range.Endless);

        wait.If.Wait = ConditionWait.Forever;
        Assert.True(Macro(wait).Duration.Endless);
    }

    [Fact]
    public void A_group_lives_in_one_list()
    {
        var group = new MacroGroup { Name = "G" };
        MacroAction outside = Press(0x41), inside = Press(0x42);
        outside.GroupId = group.Id;
        inside.GroupId = group.Id;
        var macro = Macro(outside, If(Pixel(), then: new[] { inside }));
        macro.Groups.Add(group);

        macro.NormalizeGroups();

        Assert.Equal(group.Id, outside.GroupId);
        Assert.Null(inside.GroupId);
    }

    [Fact]
    public void A_group_inside_then_is_kept()
    {
        var group = new MacroGroup { Name = "G" };
        MacroAction a = Press(0x41), b = Press(0x42);
        a.GroupId = group.Id;
        b.GroupId = group.Id;
        var macro = Macro(Press(), If(Pixel(), then: new[] { a, b }));
        macro.Groups.Add(group);

        macro.NormalizeGroups();

        Assert.Equal(group.Id, b.GroupId);
        Assert.Single(macro.Groups);
        Assert.Empty(Problems(macro));
    }

    [Fact]
    public void Loading_drops_conditions_from_actions_that_are_not_ifs_and_fixes_checks_per_second()
    {
        var library = new MacroLibrary(Macros, ImagesFolder);
        var press = Press();
        press.If = new MacroIf { Conditions = { Pixel() } };
        var macro = Macro(press, Click(Guid.NewGuid()));
        macro.Actions[1].Type = MacroActionType.PressKey;
        macro.ChecksPerSecond = 7;
        Assert.True(library.TrySave(macro, out _));

        library.Load(new List<Guid>(), out _);
        var read = library.Macros.Single();

        Assert.Null(read.Actions[0].If);
        Assert.Null(read.Actions[1].FoundId);
        Assert.Equal(MacroDefinition.DefaultChecksPerSecond, read.ChecksPerSecond);
    }

    [Fact]
    public void A_file_from_before_conditions_works_every_30_checks_a_second()
    {
        string old = "{\"Id\":\"3f2504e0-4f89-11d3-9a0c-0305e82c3301\",\"Name\":\"Old\",\"Actions\":[{\"Type\":\"PressKey\",\"Vk\":65}]}";
        File.WriteAllText(Path.Combine(Macros, "3f2504e0-4f89-11d3-9a0c-0305e82c3301.json"), old);
        var library = new MacroLibrary(Macros, ImagesFolder);

        library.Load(new List<Guid>(), out string? warning);

        Assert.Null(warning);
        Assert.Equal(30, library.Macros.Single().ChecksPerSecond);
    }

    // ================= The Code view's checks =================

    [Fact]
    public void A_whole_macro_with_conditions_has_no_problems()
    {
        var image = Image("Accept button", ImageA);
        var macro = Macro(If(image, then: new[] { Click(image.Id) }, orElse: new[] { Press() }));

        Assert.Empty(Problems(macro, _ => true));
    }

    [Fact]
    public void Going_where_something_was_found_only_works_inside_its_then()
    {
        var image = Image("Accept button", ImageA);
        var inElse = Macro(If(image, orElse: new[] { Click(image.Id) }));
        var after = Macro(If(image), Click(image.Id));
        var nested = Macro(If(image, then: new[] { If(Pixel(), then: new[] { Click(image.Id) }) }));

        Assert.Contains(Problems(inElse), p => p.Message.EndsWith(Resources.Strings.Code_FoundOutside));
        Assert.Contains(Problems(after), p => p.Message.EndsWith(Resources.Strings.Code_FoundOutside));
        Assert.Empty(Problems(nested));
    }

    [Fact]
    public void A_condition_with_Not_gives_no_place_to_go_and_needs_its_name_still()
    {
        var image = Image("Accept button", ImageA);
        image.Not = true;
        var macro = Macro(If(image, then: new[] { Click(image.Id) }));

        Assert.Contains(Problems(macro), p => p.Message.EndsWith(Resources.Strings.Code_FoundNot));
        Assert.DoesNotContain(Problems(macro), p => p.Message.EndsWith(Resources.Strings.Code_FoundOutside));
        Assert.Empty(Problems(Macro(If(image, then: new[] { Press() }))));
        image.Name = "";
        Assert.Contains(Problems(Macro(If(image))), p => p.Message.EndsWith(Resources.Strings.Code_EnterConditionName));
    }

    [Fact]
    public void Not_is_saved_only_when_set_and_older_files_have_it_off()
    {
        var condition = new MacroCondition { Kind = ConditionKind.Pixel, X = 1, Y = 2, Color = "#000000" };
        Assert.DoesNotContain("\"Not\"", System.Text.Json.JsonSerializer.Serialize(condition, CodeJson.Options));
        condition.Not = true;
        string json = System.Text.Json.JsonSerializer.Serialize(condition, CodeJson.Options);
        Assert.Contains("\"Not\": true", json);
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<MacroCondition>(json, CodeJson.Options)!.Not);
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<MacroCondition>("{\"Kind\":\"Pixel\"}", CodeJson.Options)!.Not);
    }

    [Fact]
    public void Only_mouse_actions_go_where_something_was_found()
    {
        var image = Image("A", ImageA);
        var press = Press();
        press.FoundId = image.Id;

        Assert.Contains(Problems(Macro(If(image, then: new[] { press }))), p => p.Message.EndsWith(Resources.Strings.Code_FoundOnlyMouse));
    }

    [Fact]
    public void Conditions_that_find_something_need_a_name_unique_in_the_macro()
    {
        var unnamed = Image("", ImageA);
        var twice = Macro(If(Image("Button", ImageA)), If(Image("button", ImageA)));

        Assert.Contains(Problems(Macro(If(unnamed))), p => p.Message.EndsWith(Resources.Strings.Code_EnterConditionName));
        Assert.Contains(Problems(twice), p => p.Message.EndsWith(Resources.Strings.Code_ConditionNameTwice));
        Assert.Empty(Problems(Macro(If(Pixel()), If(Pixel())))); // pixels have no name
    }

    [Theory]
    [InlineData("#3BD16F", true)]
    [InlineData("3BD16F", false)]
    [InlineData("#3BD16", false)]
    [InlineData("#3BD16G", false)]
    public void A_color_is_hash_and_six_hexadecimal_digits(string color, bool ok)
    {
        var pixel = Pixel();
        pixel.Color = color;

        Assert.Equal(ok, !Problems(Macro(If(pixel))).Any(p => p.Message.EndsWith(Resources.Strings.Code_ConditionColor)));
    }

    [Fact]
    public void An_image_must_exist_and_a_text_looks_in_a_drawn_area_or_a_screen()
    {
        var text = new MacroCondition { Kind = ConditionKind.Text, Name = "Victory", Text = "Victory", Area = SearchArea.Around };
        var noImages = new MacroCondition { Kind = ConditionKind.Image, Name = "A", Similarity = 90 };

        Assert.Contains(Problems(Macro(If(Image("A", ImageA))), _ => false), p => p.Message.EndsWith(Resources.Strings.Code_ImageMissing));
        Assert.Contains(Problems(Macro(If(noImages))), p => p.Message.EndsWith(Resources.Strings.Code_TakeImage));
        Assert.Contains(Problems(Macro(If(text))), p => p.Message.EndsWith(Resources.Strings.Code_TextArea));
    }

    [Fact]
    public void Checks_per_second_are_10_30_60_or_every_frame()
    {
        var macro = Macro(Press());
        foreach (int ok in MacroDefinition.ChecksPerSecondSteps)
        {
            macro.ChecksPerSecond = ok;
            Assert.Empty(Problems(macro));
        }
        macro.ChecksPerSecond = 45;
        Assert.Contains(Problems(macro), p => p.Message.EndsWith(Resources.Strings.Code_ChecksPerSecond));
    }

    [Fact]
    public void Found_positions_offered_to_an_action_are_those_of_the_ifs_around_it()
    {
        var outer = Image("Outer", ImageA);
        var inner = Image("Inner", ImageA);
        var innerIf = If(inner, then: new[] { Click() });
        var outerIf = If(outer, then: new[] { innerIf }, orElse: new[] { Press() });
        var macro = Macro(outerIf);

        Assert.Equal(new[] { "Inner", "Outer" }, MacroJson.FoundAt(macro.Actions, innerIf.If!.Then).Values.OrderBy(n => n));
        Assert.Empty(MacroJson.FoundAt(macro.Actions, outerIf.If!.Else));
        Assert.Empty(MacroJson.FoundAt(macro.Actions, macro.Actions));
    }

    [Fact]
    public void The_schema_describes_the_new_fields()
    {
        string schema = CodeSchema.ForMacro(Guid.NewGuid());

        Assert.Contains("\"If\"", schema);
        Assert.Contains("\"FoundId\"", schema);
        Assert.Contains("\"ChecksPerSecond\"", schema);
        Assert.Contains("\"NearbySizes\"", schema);
    }

    // ================= Images =================

    [Fact]
    public void An_image_is_kept_once_by_its_hash()
    {
        var store = new ImageStore(ImagesFolder);

        string id = store.Add(Png(1));

        Assert.True(MacroJson.IsImageId(id));
        Assert.Equal(id, store.Add(Png(1)));
        Assert.NotEqual(id, store.Add(Png(2)));
        Assert.Equal(Png(1), store.Read(id));
    }

    [Fact]
    public void Restore_skips_an_image_whose_bytes_are_not_its_id()
    {
        var store = new ImageStore(ImagesFolder);
        string real = ImageStore.IdOf(Png(1));

        store.Restore(new Dictionary<string, string>
        {
            [real] = Convert.ToBase64String(Png(1)),
            [ImageA] = Convert.ToBase64String(Png(2)),
        });

        Assert.True(store.Has(real));
        Assert.False(store.Has(ImageA));
    }

    [Fact]
    public void The_images_a_json_names_are_found_without_reading_it_as_an_item()
    {
        var macro = Macro(If(Image("A", ImageA)));

        Assert.Equal(new[] { ImageA }, ImageStore.IdsIn(MacroJson.Serialize(macro)));
        Assert.Equal(new[] { ImageA }, ImageStore.IdsIn(macro));
    }

    [Fact]
    public void An_image_goes_when_no_macro_uses_it_any_more()
    {
        var library = new MacroLibrary(Macros, ImagesFolder);
        string id = library.Images.Add(Png(1));
        var first = Macro(If(Image("A", id)));
        var second = Macro(If(Image("B", id)));
        library.TrySave(first, out _);
        library.TrySave(second, out _);

        first.Actions.Clear();
        first.Actions.Add(Press());
        library.TrySave(first, out _);
        library.ReleaseUnusedImages();
        Assert.True(library.Images.Has(id)); // the second macro still uses it

        library.Delete(second);
        library.ReleaseUnusedImages();
        Assert.False(library.Images.Has(id));
    }

    [Fact]
    public void An_image_taken_and_not_saved_stays_while_held_and_goes_after()
    {
        var library = new MacroLibrary(Macros, ImagesFolder);
        string taken = library.Images.Add(Png(3));
        var editor = new object();
        library.Images.HoldAlso(editor, taken);

        // Another macro is saved meanwhile: the image an open editor took stays.
        library.TrySave(Macro(Press()), out _);
        library.ReleaseUnusedImages();
        Assert.True(library.Images.Has(taken));

        library.Images.Unhold(editor);
        library.ReleaseUnusedImages();
        Assert.False(library.Images.Has(taken));
    }

    [Fact]
    public void A_macro_whose_file_could_not_be_deleted_keeps_its_images()
    {
        var library = new MacroLibrary(Macros, ImagesFolder);
        string id = library.Images.Add(Png(4));
        var macro = Macro(If(Image("A", id)));
        library.TrySave(macro, out _);
        string file = System.IO.Path.Combine(Macros, macro.Id + ".json");

        using (System.IO.File.Open(file, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.None))
        {
            library.Delete(macro);
            library.ReleaseUnusedImages();
        }

        Assert.True(library.Images.Has(id)); // the macro comes back at the next start, with its image
    }

    [Fact]
    public void The_history_keeps_the_images_its_versions_name_for_restore()
    {
        var store = new ImageStore(ImagesFolder);
        var history = new ItemHistory(_folder.File("history"), images: store);
        string id = store.Add(Png(1));
        var macro = Macro(If(Image("A", id)));
        string withImage = MacroJson.Serialize(macro);
        history.Record(macro.Id, withImage, ItemHistory.You);
        macro.Actions.Clear();
        macro.Actions.Add(Press());
        history.Record(macro.Id, MacroJson.Serialize(macro), ItemHistory.You);
        store.Delete(id);

        history.RestoreImages(macro.Id, withImage);

        Assert.Equal(Png(1), store.Read(id));
    }

    [Fact]
    public void An_image_survives_when_the_version_that_held_it_is_dropped_for_space()
    {
        var store = new ImageStore(ImagesFolder);
        var history = new ItemHistory(_folder.File("history"), maxBytes: 6000, images: store);
        string id = store.Add(Png(1));
        var macro = Macro(If(Image("A", id)));
        for (int i = 0; i < 12; i++)
        {
            macro.Name = $"Version {i}";
            history.Record(macro.Id, MacroJson.Serialize(macro), ItemHistory.You);
        }
        store.Delete(id);
        var versions = history.Versions(macro.Id);

        Assert.True(versions.Count < 12);
        Assert.Single(versions, v => v.Images?.ContainsKey(id) == true); // one copy per file
        history.RestoreImages(macro.Id, versions[0].Json);
        Assert.True(store.Has(id));
    }

    [Fact]
    public void A_shared_file_carries_the_images_of_its_macros()
    {
        var source = new MacroLibrary(Macros, ImagesFolder);
        string id = source.Images.Add(Png(1));
        var macro = Macro(If(Image("A", id)));
        string text = ItemFile.Write(new object[] { macro }, new Version(1, 24, 0), source.Images);

        using var other = new TempFolder();
        var target = new MacroLibrary(Directory.CreateDirectory(other.File("macros")).FullName, other.File("images"));
        var content = ItemFile.Read(text, new AppSettings(), target, new Version(1, 24, 0));

        Assert.Null(content.Entries.Single().Problem);
        Assert.False(target.Images.Has(id));
        ItemFile.AddImages(content, target);
        Assert.Equal(Png(1), target.Images.Read(id));
    }

    [Fact]
    public void A_shared_file_without_its_image_says_so()
    {
        var macro = Macro(If(Image("A", ImageA)));
        string text = ItemFile.Write(new object[] { macro }, new Version(1, 24, 0));

        var content = ItemFile.Read(text, new AppSettings(), new MacroLibrary(Macros, ImagesFolder), new Version(1, 24, 0));

        Assert.NotNull(content.Entries.Single().Problem);
    }

    [Fact]
    public void A_pasted_If_whose_condition_name_the_macro_has_gets_the_name_of_a_copy()
    {
        var list = new MacroEditList(new[] { If(Image("Button", ImageA)) }, Array.Empty<MacroGroup>());
        var block = list.Copy(list.Actions, Array.Empty<MacroGroup>());

        var pasted = list.Insert(block, list.Actions.Count, null);

        Assert.Equal("Button (2)", pasted[0].If!.Conditions[0].Name);
        Assert.Equal("Button", list.Actions[0].If!.Conditions[0].Name);
    }

    [Fact]
    public void Two_conditions_with_one_Id_are_refused()
    {
        var first = Image("A", ImageA);
        var second = Image("B", ImageA);
        second.Id = first.Id;
        var macro = Macro(If(first), If(second));

        MacroJson.Parse(MacroJson.Serialize(macro), macro.Id, (_, _) => null, out var problems);

        Assert.Contains(problems, p => p.Message.EndsWith(PuppyMacro.Resources.Strings.Code_ConditionIdTwice));
    }
}
