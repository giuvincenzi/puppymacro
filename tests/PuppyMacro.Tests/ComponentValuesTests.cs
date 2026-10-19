using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using PuppyMacro.Models;
using PuppyMacro.Resources;
using PuppyMacro.Services;
using PuppyMacro.Services.Components;
using Xunit;
using static PuppyMacro.Tests.Kit;

namespace PuppyMacro.Tests;

/// <summary>
/// The values of parameters and arguments (ComponentValues): each type read from its JSON, every way a value can be wrong,
/// the rules a value keeps (limits joined from the fields, whole numbers, an empty text, a mouse button, a key with
/// modifiers), references ({"From"}, {"Found"}) told from values, values written back and said in words; and the table of
/// slots (ComponentSlots): which field of which action or condition can take a value, when it applies, and each one written
/// and read back.
/// </summary>
public class ComponentValuesTests
{
    private static object? Read(JsonNode? node, ParameterType type, ValueRule? rule, out string? problem, Func<string, bool>? hasImage = null) =>
        ComponentValues.Read(node, type, rule ?? ValueRule.Any, out problem, hasImage);

    // ================= Values =================

    [Fact]
    public void A_point_is_two_whole_coordinates_on_the_screen()
    {
        Assert.Equal(new PointValue(3912, 1056), Read(Point(3912, 1056), ParameterType.Point, null, out var problem));
        Assert.Null(problem);
        Assert.Equal(new PointValue(-100, 0), Read(Point(-100, 0), ParameterType.Point, null, out _));

        foreach (var bad in new JsonNode?[]
                 {
                     null, Number(3), Text("3912,1056"), new JsonObject { ["X"] = 1 }, new JsonObject { ["X"] = 1, ["Y"] = 2, ["Z"] = 3 },
                     new JsonObject { ["X"] = 1.5, ["Y"] = 2 }, new JsonObject { ["X"] = "1", ["Y"] = 2 }, new JsonArray(1, 2),
                 })
        {
            Assert.Null(Read(bad, ParameterType.Point, null, out problem));
            Assert.Equal(Strings.Code_ValuePoint, problem);
        }
        Assert.Null(Read(Point(100_001, 0), ParameterType.Point, null, out problem));
        Assert.Contains("100", problem); // between -100000 and 100000
    }

    [Fact]
    public void A_number_keeps_its_rule_s_limits_and_whole_numbers()
    {
        Assert.Equal(250.5, Read(Number(250.5), ParameterType.Number, null, out var problem));
        Assert.Null(problem);

        var hold = ValueRule.Number(MacroJson.HoldMs);
        Assert.Null(Read(Number(0), ParameterType.Number, hold, out problem));
        Assert.NotNull(problem);
        Assert.Null(Read(Number(60_001), ParameterType.Number, hold, out _));
        Assert.Equal(60_000d, Read(Number(60_000), ParameterType.Number, hold, out _));

        var count = ValueRule.Number(MacroJson.Repeat, whole: true);
        Assert.Null(Read(Number(2.5), ParameterType.Number, count, out problem));
        Assert.Equal(Strings.Code_ValueWhole, problem);
        Assert.Equal(3d, Read(Number(3), ParameterType.Number, count, out _));

        foreach (var bad in new JsonNode?[] { null, Text("3"), Point(1, 2), JsonValue.Create(true) })
        {
            Assert.Null(Read(bad, ParameterType.Number, null, out problem));
            Assert.Equal(Strings.Code_ValueNumber, problem);
        }
    }

    [Fact]
    public void Limits_that_leave_no_value_say_so()
    {
        var none = ValueRule.Number((10, 20)).And(ValueRule.Number((30, 40)));
        Assert.Null(Read(Number(15), ParameterType.Number, none, out var problem));
        Assert.Equal(Strings.Code_ValueNoRoom, problem);
    }

    [Fact]
    public void Rules_join_to_the_tighter_one()
    {
        var joined = ValueRule.Number((0, 100), whole: false).And(ValueRule.Number((50, 200), whole: true)).And(new ValueRule { NotEmpty = true })
            .And(new ValueRule { Keyboard = true }).And(new ValueRule { KeyAlone = true });
        Assert.Equal((50d, 100d), joined.Range);
        Assert.True(joined.Whole);
        Assert.True(joined.NotEmpty);
        Assert.True(joined.Keyboard);
        Assert.True(joined.KeyAlone);
        Assert.Equal(ValueRule.Any, ValueRule.Any.And(ValueRule.Any));
    }

    [Theory]
    [InlineData(NumberUnit.Pixels, -100_000, 100_000, true)]
    [InlineData(NumberUnit.Ms, 0, 86_400_000, false)]
    [InlineData(NumberUnit.Seconds, 0, 86_400, false)]
    [InlineData(NumberUnit.Count, 0, 100_000, true)]
    [InlineData(NumberUnit.Percent, 0, 100, true)]
    public void Each_unit_starts_from_its_own_limits(NumberUnit unit, double min, double max, bool whole)
    {
        var rule = ComponentValues.BaseRule(ParameterType.Number, unit);
        Assert.Equal((min, max), rule.Range);
        Assert.Equal(whole, rule.Whole);
        Assert.Equal(ValueRule.Any, ComponentValues.BaseRule(ParameterType.Text, null));
    }

    [Fact]
    public void A_text_is_a_string_and_can_be_required_not_empty()
    {
        Assert.Equal("Primes", Read(Text("Primes"), ParameterType.Text, null, out _));
        Assert.Equal("", Read(Text(""), ParameterType.Text, null, out _));
        Assert.Null(Read(Text(""), ParameterType.Text, new ValueRule { NotEmpty = true }, out var problem));
        Assert.Equal(Strings.Code_ValueTextEmpty, problem);
        Assert.Null(Read(Number(3), ParameterType.Text, null, out problem));
        Assert.Equal(Strings.Code_ValueText, problem);
    }

    [Fact]
    public void A_key_is_written_as_a_hotkey_and_can_be_required_alone_and_of_the_keyboard()
    {
        var key = (HotkeyBinding)Read(Key(0x53, ctrl: true), ParameterType.Key, null, out var problem)!;
        Assert.Null(problem);
        Assert.Equal(0x53, key.Vk);
        Assert.True(key.Ctrl);

        Assert.Null(Read(Key(0x53, ctrl: true), ParameterType.Key, new ValueRule { KeyAlone = true }, out problem));
        Assert.Equal(Strings.Code_ValueKeyAlone, problem);
        Assert.NotNull(Read(Key(0x53), ParameterType.Key, new ValueRule { KeyAlone = true }, out _));

        Assert.Null(Read(Key(0x01), ParameterType.Key, new ValueRule { Keyboard = true }, out problem));
        Assert.Equal(Strings.Code_ValueKeyboard, problem);

        foreach (var bad in new JsonNode?[] { null, Number(65), Text("A"), new JsonObject { ["Vk"] = 0 }, new JsonObject { ["Vk"] = 300 }, new JsonObject { ["Key"] = 65 } })
        {
            Assert.Null(Read(bad, ParameterType.Key, null, out problem));
            Assert.Equal(Strings.Code_ValueKey, problem);
        }
    }

    [Fact]
    public void A_color_is_a_hash_and_six_hexadecimal_digits()
    {
        Assert.Equal("#463405", Read(Text("#463405"), ParameterType.Color, null, out _));
        foreach (var bad in new JsonNode?[] { Text("463405"), Text("#46340"), Text("#46340G"), Number(1), null })
        {
            Assert.Null(Read(bad, ParameterType.Color, null, out var problem));
            Assert.Equal(Strings.Code_ConditionColor, problem);
        }
    }

    [Fact]
    public void Images_are_at_least_one_with_their_place_and_PuppyMacro_must_have_them()
    {
        var images = (List<ConditionImage>)Read(Images(ImageA, ImageB), ParameterType.Image, null, out var problem)!;
        Assert.Null(problem);
        Assert.Equal(new[] { ImageA, ImageB }, images.Select(i => i.Id));
        Assert.Equal(10, images[1].X);

        Assert.Null(Read(new JsonArray(), ParameterType.Image, null, out problem));
        Assert.Equal(Strings.Code_ValueImage, problem);
        Assert.Null(Read(Images("not-an-id"), ParameterType.Image, null, out problem));
        Assert.Equal(Strings.Code_TakeImage, problem);
        Assert.Null(Read(Images(ImageA), ParameterType.Image, null, out problem, hasImage: id => id != ImageA));
        Assert.Equal(Strings.Code_ImageMissing, problem);
        Assert.Null(Read(new JsonArray(new JsonObject { ["Id"] = ImageA }), ParameterType.Image, null, out problem));
        Assert.Equal(Strings.Code_ValueImage, problem);
        Assert.Null(Read(Text(ImageA), ParameterType.Image, null, out problem));
        Assert.Equal(Strings.Code_ValueImage, problem);
    }

    [Fact]
    public void An_area_has_its_corner_and_a_size_of_at_least_8_pixels()
    {
        Assert.Equal(new AreaValue(10, 20, 300, 200), Read(Area(10, 20, 300, 200), ParameterType.Area, null, out _));
        Assert.Null(Read(Area(10, 20, 7, 200), ParameterType.Area, null, out var problem));
        Assert.Contains("8", problem);
        Assert.Null(Read(Point(10, 20), ParameterType.Area, null, out problem));
        Assert.Equal(Strings.Code_ValueArea, problem);
    }

    [Fact]
    public void A_reference_is_told_from_a_value_and_must_be_written_right()
    {
        Assert.Null(ComponentValues.Reference(Point(1, 2)));
        Assert.Null(ComponentValues.Reference(Number(3)));

        var from = ComponentValues.Reference(From("Slot"))!;
        Assert.Equal("Slot", from.From);
        Assert.True(from.WellFormed);
        Assert.False(ComponentValues.Reference(new JsonObject { ["From"] = "Slot", ["X"] = 1 })!.WellFormed);
        Assert.False(ComponentValues.Reference(new JsonObject { ["From"] = 3 })!.WellFormed);

        Guid id = Guid.NewGuid();
        var found = ComponentValues.Reference(Found(id, 5, -3))!;
        Assert.Equal(id, found.Found);
        Assert.Equal((5, -3), (found.OffsetX, found.OffsetY));
        Assert.True(found.WellFormed);
        Assert.Equal((0, 0), (ComponentValues.Reference(Found(id))!.OffsetX, ComponentValues.Reference(Found(id))!.OffsetY));
        Assert.False(ComponentValues.Reference(new JsonObject { ["Found"] = "nope" })!.WellFormed);
        Assert.False(ComponentValues.Reference(new JsonObject { ["Found"] = id.ToString(), ["OffsetX"] = 1.5 })!.WellFormed);
        Assert.False(ComponentValues.Reference(new JsonObject { ["Found"] = id.ToString(), ["Other"] = 1 })!.WellFormed);
    }

    [Fact]
    public void A_value_read_is_written_back_the_same()
    {
        foreach (var (node, type) in new (JsonNode, ParameterType)[]
                 {
                     (Point(3, 4), ParameterType.Point), (Number(2.5), ParameterType.Number), (Text("x"), ParameterType.Text),
                     (Text("#112233"), ParameterType.Color), (Images(ImageA), ParameterType.Image), (Area(1, 2, 30, 40), ParameterType.Area),
                 })
        {
            object value = ComponentValues.Read(node, type, ValueRule.Any, out _)!;
            Assert.True(JsonNode.DeepEquals(node, ComponentValues.ToJson(value)), $"{type}: {ComponentValues.ToJson(value).ToJsonString()}");
        }
        var key = (HotkeyBinding)ComponentValues.Read(Key(0x53, ctrl: true), ParameterType.Key, ValueRule.Any, out _)!;
        Assert.True(key.SameAs((HotkeyBinding)ComponentValues.Read(ComponentValues.ToJson(key), ParameterType.Key, ValueRule.Any, out _)!));
    }

    [Fact]
    public void A_value_is_said_in_a_few_words()
    {
        Assert.Equal("3986, 1056", ComponentValues.Describe(Point(3986, 1056), ParameterType.Point, null));
        Assert.Equal("100 ms", ComponentValues.Describe(Number(100), ParameterType.Number, NumberUnit.Ms));
        Assert.Equal("2 s", ComponentValues.Describe(Number(2), ParameterType.Number, NumberUnit.Seconds));
        Assert.Equal("90%", ComponentValues.Describe(Number(90), ParameterType.Number, NumberUnit.Percent));
        Assert.Equal("Primes", ComponentValues.Describe(Text("Primes"), ParameterType.Text, null));
        Assert.Equal("2 images", ComponentValues.Describe(Images(ImageA, ImageB), ParameterType.Image, null));
        Assert.Contains("S", ComponentValues.Describe(Key(0x53, ctrl: true), ParameterType.Key, null));
        Assert.Equal("", ComponentValues.Describe(Text("x"), ParameterType.Point, null)); // not a value of the type
    }

    // ================= Slots =================

    private static string[] Names(IEnumerable<ComponentSlot> slots) => slots.Select(s => s.Name).ToArray();

    [Fact]
    public void Each_action_has_its_slots_and_a_linked_row_has_none()
    {
        Assert.Equal(new[] { "DelayMs", "Key", "HoldMs", "Repeat", "RepeatPauseMs" }, Names(ComponentSlots.Of(Press())));
        Assert.Equal(new[] { "DelayMs", "Point", "Cell", "HoldMs", "Repeat", "RepeatPauseMs" }, Names(ComponentSlots.Of(Click())));
        foreach (var type in new[] { MacroActionType.MouseDown, MacroActionType.MouseUp, MacroActionType.MoveTo })
            Assert.Equal(new[] { "DelayMs", "Point", "Cell" }, Names(ComponentSlots.Of(new MacroAction { Type = type })));
        // The cell applies only while the action has one (its point from a Grid), and never with a found place.
        Assert.False(ComponentSlots.Find(Click(), "Cell")!.Applies);
        Assert.True(ComponentSlots.Find(new MacroAction { Type = MacroActionType.Click, Cell = 2 }, "Cell")!.Applies);
        Assert.False(ComponentSlots.Find(new MacroAction { Type = MacroActionType.Click, Cell = 2, FoundId = Guid.NewGuid() }, "Cell")!.Applies);
        foreach (var type in new[] { MacroActionType.KeyDown, MacroActionType.KeyUp })
            Assert.Equal(new[] { "DelayMs", "Key" }, Names(ComponentSlots.Of(new MacroAction { Type = type })));
        Assert.Equal(new[] { "DelayMs", "ScrollSteps" }, Names(ComponentSlots.Of(new MacroAction { Type = MacroActionType.Scroll })));
        Assert.Equal(new[] { "DelayMs", "Text" }, Names(ComponentSlots.Of(new MacroAction { Type = MacroActionType.PasteText })));
        Assert.Equal(new[] { "DelayMs" }, Names(ComponentSlots.Of(new MacroAction { Type = MacroActionType.MovePath })));
        Assert.Equal(new[] { "DelayMs", "WaitSeconds" }, Names(ComponentSlots.Of(If(Pixel()))));
        Assert.Empty(ComponentSlots.Of(new MacroAction { Type = MacroActionType.Component }));
    }

    [Fact]
    public void Each_condition_has_its_slots_and_a_Component_condition_has_none()
    {
        Assert.Equal(new[] { "Point", "Cell", "Color", "Tolerance" }, Names(ComponentSlots.Of(Pixel())));
        Assert.False(ComponentSlots.Find(Pixel(), "Cell")!.Applies);
        Assert.True(ComponentSlots.Find(new MacroCondition { Kind = ConditionKind.Pixel, Cell = 1 }, "Cell")!.Applies);
        Assert.Equal(new[] { "Images", "Similarity", "Area" }, Names(ComponentSlots.Of(ImageFinder())));
        Assert.Equal(new[] { "Text", "Area" }, Names(ComponentSlots.Of(new MacroCondition { Kind = ConditionKind.Text })));
        Assert.Empty(ComponentSlots.Of(new MacroCondition { Kind = ConditionKind.Component }));
    }

    [Fact]
    public void A_slot_applies_only_when_the_field_is_used()
    {
        Assert.True(ComponentSlots.Find(Click(), "Point")!.Applies);
        Assert.False(ComponentSlots.Find(Click(found: Guid.NewGuid()), "Point")!.Applies);
        var once = If(Pixel());
        Assert.False(ComponentSlots.Find(once, "WaitSeconds")!.Applies);
        once.If!.Wait = ConditionWait.UpTo;
        Assert.True(ComponentSlots.Find(once, "WaitSeconds")!.Applies);
        once.If!.Wait = ConditionWait.Forever;
        Assert.False(ComponentSlots.Find(once, "WaitSeconds")!.Applies);
        var around = ImageFinder();
        Assert.False(ComponentSlots.Find(around, "Area")!.Applies);
        around.Area = SearchArea.Drawn;
        Assert.True(ComponentSlots.Find(around, "Area")!.Applies);
        around.Area = SearchArea.Screen;
        Assert.True(ComponentSlots.Find(around, "Area")!.Applies);
    }

    [Fact]
    public void Each_slot_s_type_unit_and_rule_are_its_field_s()
    {
        var hold = ComponentSlots.Find(Press(), "HoldMs")!;
        Assert.Equal((ParameterType.Number, NumberUnit.Ms), (hold.Type, hold.Unit));
        Assert.Equal(MacroJson.HoldMs, hold.Rule.Range);
        var repeat = ComponentSlots.Find(Press(), "Repeat")!;
        Assert.Equal(NumberUnit.Count, repeat.Unit);
        Assert.True(repeat.Rule.Whole);
        Assert.True(ComponentSlots.Find(Press(), "Key")!.Rule.Keyboard);
        Assert.False(ComponentSlots.Find(Press(), "Key")!.Rule.KeyAlone);
        Assert.True(ComponentSlots.Find(new MacroAction { Type = MacroActionType.KeyDown }, "Key")!.Rule.KeyAlone);
        Assert.True(ComponentSlots.Find(new MacroAction { Type = MacroActionType.PasteText }, "Text")!.Rule.NotEmpty);
        Assert.Equal((NumberUnit.Seconds, MacroJson.WaitSeconds), (ComponentSlots.Find(If(Pixel()), "WaitSeconds")!.Unit, ComponentSlots.Find(If(Pixel()), "WaitSeconds")!.Rule.Range));
        Assert.Equal((NumberUnit.Percent, MacroJson.Similarity), (ComponentSlots.Find(ImageFinder(), "Similarity")!.Unit, ComponentSlots.Find(ImageFinder(), "Similarity")!.Rule.Range));
        Assert.Equal(MacroJson.Tolerance, ComponentSlots.Find(Pixel(), "Tolerance")!.Rule.Range);
        Assert.Equal((0, MacroJson.MaxDelayMs), ComponentSlots.Find(Press(), "DelayMs")!.Rule.Range);
        Assert.Null(ComponentSlots.Find(Press(), "Name"));
        Assert.Null(ComponentSlots.Find(Press(), "GroupId"));
        Assert.Null(ComponentSlots.Find(Click(), "FoundId"));
    }

    [Fact]
    public void Every_slot_of_an_action_is_written_and_read_back()
    {
        var press = Press();
        foreach (var (slot, value) in new (string, object)[]
                 {
                     ("DelayMs", 120d), ("HoldMs", 45d), ("Repeat", 3d), ("RepeatPauseMs", 70d),
                     ("Key", new HotkeyBinding { Vk = 0x53, Ctrl = true }),
                 })
        {
            ComponentSlots.Write(press, slot, value);
            var back = ComponentSlots.Value(press, slot);
            if (value is HotkeyBinding key)
                Assert.True(key.SameAs((HotkeyBinding)back!));
            else
                Assert.Equal(value, back);
        }
        Assert.Equal((120d, 45d, 3, 70d, 0x53, true), (press.DelayMs, press.HoldMs, press.Repeat, press.RepeatPauseMs, press.Vk, press.Ctrl));

        var click = Click();
        ComponentSlots.Write(click, "Point", new PointValue(3986, 1056));
        Assert.Equal((3986, 1056), (click.X, click.Y));
        Assert.Equal(new PointValue(3986, 1056), ComponentSlots.Value(click, "Point"));

        var scroll = new MacroAction { Type = MacroActionType.Scroll };
        ComponentSlots.Write(scroll, "ScrollSteps", 7d);
        Assert.Equal(7, scroll.ScrollSteps);
        var paste = new MacroAction { Type = MacroActionType.PasteText };
        ComponentSlots.Write(paste, "Text", "hello");
        Assert.Equal("hello", paste.Text);
        var wait = If(Pixel());
        wait.If!.Wait = ConditionWait.UpTo;
        ComponentSlots.Write(wait, "WaitSeconds", 4.5);
        Assert.Equal(4.5, wait.If.WaitSeconds);
        Assert.Equal(4.5, ComponentSlots.Value(wait, "WaitSeconds"));
    }

    [Fact]
    public void Every_slot_of_a_condition_is_written_and_read_back()
    {
        var pixel = Pixel();
        ComponentSlots.Write(pixel, "Point", new PointValue(7, 8));
        ComponentSlots.Write(pixel, "Color", "#112233");
        ComponentSlots.Write(pixel, "Tolerance", 12d);
        Assert.Equal((7, 8, "#112233", 12), (pixel.X, pixel.Y, pixel.Color, pixel.Tolerance));
        Assert.Equal(new PointValue(7, 8), ComponentSlots.Value(pixel, "Point"));

        var image = ImageFinder();
        var images = new List<ConditionImage> { new() { Id = ImageB, X = 1, Y = 2 } };
        ComponentSlots.Write(image, "Images", images);
        ComponentSlots.Write(image, "Similarity", 80d);
        ComponentSlots.Write(image, "Area", new AreaValue(1, 2, 30, 40));
        Assert.Equal(ImageB, image.Images!.Single().Id);
        Assert.NotSame(images[0], image.Images![0]); // a copy
        Assert.Equal((80, 1, 2, 30, 40), (image.Similarity, image.AreaX, image.AreaY, image.AreaWidth, image.AreaHeight));
        Assert.Equal(new AreaValue(1, 2, 30, 40), ComponentSlots.Value(image, "Area"));

        var text = new MacroCondition { Kind = ConditionKind.Text };
        ComponentSlots.Write(text, "Text", "Remove Affix");
        Assert.Equal("Remove Affix", text.Text);
    }
}
