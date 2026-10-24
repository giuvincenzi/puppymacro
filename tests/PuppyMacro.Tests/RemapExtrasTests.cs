using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Threading;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// Pass extra modifiers (<see cref="RemapExtras"/>, <see cref="RemapDefinition.PassExtraModifiers"/>): the source also works
/// with other Ctrl, Alt, Shift or Win held, and those reach the app with the target (Alt + A → Left: Shift + Alt + A gives
/// Shift + Left). On for new remaps, off for those saved before it. The engine's keys go to a list, never to the PC (the
/// unit tests run while the user works). ModifierTracker is static: same collection as its own tests.
/// </summary>
[Collection(nameof(ModifierTracker))]
public sealed class RemapExtrasTests : IDisposable
{
    private const int F24 = 0x87, F23 = 0x86, F22 = 0x85, F21 = 0x84, KeyA = 0x41, Left = 0x25, Home = 0x24;
    private const int LCtrl = 0xA2, LAlt = 0xA4, RAlt = 0xA5, LShift = 0xA0, Mask = InputSender.MaskKeyVk;

    private readonly List<KeyStep> _sent = new();
    private readonly LoopEngine _engine;

    public RemapExtrasTests()
    {
        ModifierTracker.Reset();
        _engine = new LoopEngine(new EngineSnapshot(), Dispatcher.CurrentDispatcher, steps => _sent.AddRange(steps));
    }

    public void Dispose()
    {
        _engine.Dispose();
        ModifierTracker.Reset();
    }

    private static KeyStep Down(int vk) => new(vk, true);

    private static KeyStep Up(int vk) => new(vk, false);

    private static RemapDefinition Remap(HotkeyBinding source, HotkeyBinding target, bool passExtras = true) =>
        new() { Source = source, Target = target, PassExtraModifiers = passExtras };

    private static HotkeyBinding AltF23 => new() { Vk = F23, Alt = true };

    private void Publish(params RemapDefinition[] remaps) => _engine.Publish(new EngineSnapshot { Remaps = remaps });

    private bool Press(int vk) => _engine.OnKey(vk, isDown: true, physicalModifierEvent: true);

    private bool Release(int vk) => _engine.OnKey(vk, isDown: false, physicalModifierEvent: true);

    private KeyStep[] Sent()
    {
        var steps = _sent.ToArray();
        _sent.Clear();
        return steps;
    }

    // ---- When it applies, and the data ----

    [Fact]
    public void It_applies_when_on_and_the_source_is_a_key_or_a_button()
    {
        Assert.True(RemapExtras.Applies(Remap(AltF23, HotkeyBinding.FromKey(F24))));
        Assert.True(RemapExtras.Applies(Remap(HotkeyBinding.FromKey(F23), HotkeyBinding.FromKey(F24))));
        Assert.True(RemapExtras.Applies(Remap(HotkeyBinding.FromKey(KeyNames.VK_XBUTTON1), HotkeyBinding.FromKey(F24))));
        Assert.False(RemapExtras.Applies(Remap(AltF23, HotkeyBinding.FromKey(F24), passExtras: false)));
        Assert.False(RemapExtras.Applies(Remap(HotkeyBinding.FromKey(RAlt), HotkeyBinding.FromKey(F24)))); // Right Alt alone
    }

    [Fact]
    public void A_new_remap_has_it_on()
    {
        Assert.True(new RemapDefinition().PassExtraModifiers);
    }

    [Fact]
    public void Remaps_saved_before_it_have_it_off_and_those_saved_after_keep_theirs()
    {
        using var folder = new TempFolder();
        string path = folder.File("settings.json");
        File.WriteAllText(path, """
            { "SchemaVersion": 8, "Remaps": [ { "Source": { "Vk": 65, "Alt": true }, "Target": { "Vk": 37 } } ] }
            """);
        Assert.False(Assert.Single(new SettingsStore(path).Load(out _).Remaps).PassExtraModifiers);

        File.WriteAllText(path, """
            { "SchemaVersion": 9, "Remaps": [ { "Source": { "Vk": 65, "Alt": true }, "Target": { "Vk": 37 } } ] }
            """);
        Assert.True(Assert.Single(new SettingsStore(path).Load(out _).Remaps).PassExtraModifiers);
    }

    [Fact]
    public void A_remap_from_an_older_puppyitems_file_comes_in_off_and_one_from_a_newer_file_as_written()
    {
        using var folder = new TempFolder();
        var macros = new MacroLibrary(folder.Path);
        string ItemsFile(string item) => $$"""{ "Format": 1, "App": "1.25.0", "Items": [ { "Kind": "Remap", "Item": {{item}} } ] }""";

        var old = ItemFile.Read(ItemsFile("""{ "Source": { "Vk": 65, "Alt": true }, "Target": { "Vk": 37 } }"""),
            new AppSettings(), macros, new Version(1, 26, 0));
        var written = ItemFile.Read(ItemsFile("""{ "Source": { "Vk": 65, "Alt": true }, "Target": { "Vk": 37 }, "PassExtraModifiers": true }"""),
            new AppSettings(), macros, new Version(1, 26, 0));

        Assert.False(Assert.IsType<RemapDefinition>(Assert.Single(old.Entries).Item).PassExtraModifiers);
        Assert.True(Assert.IsType<RemapDefinition>(Assert.Single(written.Entries).Item).PassExtraModifiers);
    }

    [Fact]
    public void Off_is_kept_by_the_Code_view_and_by_a_copy()
    {
        var remap = Remap(AltF23, HotkeyBinding.FromKey(F24), passExtras: false);

        RemapDefinition? read = RemapJson.Parse(RemapJson.Serialize(remap), remap.Id, (_, _) => null, out var problems);

        Assert.Empty(problems);
        Assert.False(read!.PassExtraModifiers);
        Assert.False(remap.Clone().PassExtraModifiers);
        var copy = new RemapDefinition();
        copy.CopyFrom(remap);
        Assert.False(copy.PassExtraModifiers);
    }

    // ---- What matches ----

    [Fact]
    public void The_source_matches_with_more_modifiers_never_with_fewer_nor_exactly()
    {
        var altA = new HotkeyBinding { Vk = KeyA, Alt = true, AltSide = ModifierSide.Left };

        Assert.True(altA.MatchesWithExtras(new HotkeyBinding { Vk = KeyA, Alt = true, AltSide = ModifierSide.Left, Shift = true }));
        Assert.True(altA.MatchesWithExtras(new HotkeyBinding { Vk = KeyA, Alt = true, AltSide = ModifierSide.Left, Ctrl = true, Shift = true }));
        Assert.False(altA.MatchesWithExtras(new HotkeyBinding { Vk = KeyA, Alt = true, AltSide = ModifierSide.Left })); // exact
        Assert.False(altA.MatchesWithExtras(new HotkeyBinding { Vk = KeyA, Shift = true }));                            // no Alt
        Assert.False(altA.MatchesWithExtras(HotkeyBinding.FromKey(KeyA)));
        Assert.False(altA.MatchesWithExtras(new HotkeyBinding { Vk = KeyA, Alt = true, AltSide = ModifierSide.Right, Shift = true })); // other side
        Assert.False(altA.MatchesWithExtras(new HotkeyBinding { Vk = 0x42, Alt = true, AltSide = ModifierSide.Left, Shift = true }));  // other key
    }

    // ---- The keys RemapExtras decides ----

    [Fact]
    public void Only_the_source_modifiers_are_released_and_the_extra_ones_stay_for_the_app()
    {
        var steps = RemapExtras.Press(new HotkeyBinding { Vk = KeyA, Alt = true }, HotkeyBinding.FromKey(Left),
            new[] { LAlt, LShift }, out var pressed);

        Assert.Equal(new[] { Down(Mask), Up(Mask), Up(LAlt), Down(Left) }, steps); // Shift stays down: Shift + Left
        Assert.Empty(pressed);
        Assert.Equal(new[] { Up(Left) }, RemapExtras.Release(Left, pressed));
    }

    [Fact]
    public void A_target_modifier_is_pressed_and_released_with_its_key()
    {
        var steps = RemapExtras.Press(new HotkeyBinding { Vk = KeyA, Alt = true }, new HotkeyBinding { Vk = Left, Ctrl = true },
            new[] { LAlt, LShift }, out var pressed);

        Assert.Equal(new[] { Down(Mask), Up(Mask), Up(LAlt), Down(LCtrl), Down(Left) }, steps); // Ctrl + Shift + Left
        Assert.Equal(new[] { Up(Left), Up(LCtrl) }, RemapExtras.Release(Left, pressed));
    }

    [Fact]
    public void A_target_modifier_the_user_already_holds_is_neither_pressed_nor_released()
    {
        var steps = RemapExtras.Press(new HotkeyBinding { Vk = KeyA, Alt = true }, new HotkeyBinding { Vk = Home, Shift = true },
            new[] { LAlt, LShift }, out var pressed);

        Assert.Equal(new[] { Down(Mask), Up(Mask), Up(LAlt), Down(Home) }, steps); // the user's own Shift
        Assert.Empty(pressed);
        Assert.Equal(new[] { Up(Home) }, RemapExtras.Release(Home, pressed));
    }

    // ---- The engine ----

    [Fact]
    public void Shift_with_the_source_sends_the_target_with_Shift()
    {
        Publish(Remap(AltF23, HotkeyBinding.FromKey(F24)));

        Assert.False(Press(LAlt));
        Assert.False(Press(LShift));
        Assert.True(Press(F23));
        Assert.Equal(new[] { Down(Mask), Up(Mask), Up(LAlt), Down(F24) }, Sent());
        Assert.True(Press(F23)); // auto-repeat: the target's key again
        Assert.Equal(new[] { Down(F24) }, Sent());
        Assert.True(Release(F23));
        Assert.Equal(new[] { Up(F24) }, Sent());
        Assert.False(Release(LShift));
        Assert.False(Release(LAlt));
        Assert.Empty(Sent());
    }

    [Fact]
    public void With_it_off_the_source_with_another_modifier_passes()
    {
        Publish(Remap(AltF23, HotkeyBinding.FromKey(F24), passExtras: false));

        Press(LAlt);
        Press(LShift);
        Assert.False(Press(F23));
        Assert.False(Release(F23));
        Assert.Empty(Sent());
    }

    [Fact]
    public void An_exact_match_wins_over_one_with_extra_modifiers()
    {
        // Alt + Shift + F23 has its own remap (Ctrl + F22, held while the source's modifiers are): it starts, not Alt + F23's.
        Publish(Remap(AltF23, HotkeyBinding.FromKey(F24)),
            Remap(new HotkeyBinding { Vk = F23, Alt = true, Shift = true }, new HotkeyBinding { Vk = F22, Ctrl = true }));

        Press(LAlt);
        Press(LShift);
        Assert.True(Press(F23));
        Assert.Equal(new[] { Down(Mask), Up(Mask), Up(LAlt), Up(LShift), Down(LCtrl), Down(F22) }, Sent());
        Assert.True(Release(F23));
        Assert.Equal(new[] { Up(F22) }, Sent());
        Release(LShift);
        Assert.Equal(new[] { Up(LCtrl) }, Sent());
    }

    [Fact]
    public void The_remap_with_the_most_of_the_modifiers_held_wins()
    {
        Publish(Remap(HotkeyBinding.FromKey(F23), HotkeyBinding.FromKey(F21)), Remap(AltF23, HotkeyBinding.FromKey(F24)));

        Press(LCtrl);
        Press(LAlt);
        Assert.True(Press(F23)); // Ctrl + Alt + F23: Alt + F23's, Ctrl passes with it
        Assert.Equal(new[] { Down(Mask), Up(Mask), Up(LAlt), Down(F24) }, Sent());
        Release(F23);
        Assert.Equal(new[] { Up(F24) }, Sent());
    }

    [Fact]
    public void A_desktop_switch_releases_a_target_started_with_extra_modifiers()
    {
        Publish(Remap(AltF23, new HotkeyBinding { Vk = F24, Ctrl = true }, passExtras: true));
        Press(LAlt);
        Press(LShift);
        // Keep modifiers held applies too (both have modifiers): the hold starts, without the extra Shift.
        Assert.True(Press(F23));
        Assert.Equal(new[] { Down(Mask), Up(Mask), Up(LAlt), Down(LCtrl), Down(F24) }, Sent());

        typeof(LoopEngine).GetMethod("OnDesktopSwitch", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(_engine, null);

        Assert.Equal(new[] { Up(F24), Up(LCtrl) }, Sent());
    }

    [Fact]
    public void A_desktop_switch_releases_a_target_its_modifiers_were_pressed_for()
    {
        var remap = Remap(AltF23, new HotkeyBinding { Vk = F24, Ctrl = true });
        remap.KeepModifiersHeld = false;
        Publish(remap);
        Press(LAlt);
        Press(LShift);
        Assert.True(Press(F23));
        Assert.Equal(new[] { Down(Mask), Up(Mask), Up(LAlt), Down(LCtrl), Down(F24) }, Sent());

        typeof(LoopEngine).GetMethod("OnDesktopSwitch", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(_engine, null);

        Assert.Equal(new[] { Up(F24), Up(LCtrl) }, Sent());
    }

    // ---- With Keep modifiers held ----

    private static HotkeyBinding CtrlF23 => new() { Vk = F23, Ctrl = true };

    [Fact]
    public void Shift_during_a_hold_sends_the_target_key_and_the_hold_goes_on()
    {
        Publish(Remap(CtrlF23, new HotkeyBinding { Vk = F24, Alt = true }));
        Press(LCtrl);
        Press(F23);
        Release(F23);
        Sent();

        Assert.False(Press(LShift));
        Assert.True(Press(F23)); // Ctrl + Shift + F23: the target's key, Alt still held (the app gets Alt + Shift + F24)
        Assert.Equal(new[] { Down(F24) }, Sent());
        Assert.True(Release(F23));
        Assert.Equal(new[] { Up(F24) }, Sent());
        Assert.False(Release(LShift));
        Assert.Empty(Sent());

        Assert.False(Release(LCtrl));
        Assert.Equal(new[] { Up(LAlt) }, Sent());
    }

    [Fact]
    public void A_hold_started_with_an_extra_modifier_leaves_it_down_and_ends_with_the_source_modifier()
    {
        Publish(Remap(CtrlF23, new HotkeyBinding { Vk = F24, Alt = true }));
        Press(LCtrl);
        Press(LShift);

        Assert.True(Press(F23));
        Assert.Equal(new[] { Up(LCtrl), Down(LAlt), Down(F24) }, Sent()); // Shift stays: Alt + Shift + F24
        Release(F23);
        Sent();
        Assert.False(Release(LShift)); // not the source's: the hold goes on
        Assert.Empty(Sent());
        Assert.False(Release(LCtrl));
        Assert.Equal(new[] { Up(LAlt) }, Sent());
    }

    [Fact]
    public void A_hold_whose_target_modifier_the_user_holds_does_not_press_nor_release_it()
    {
        Publish(Remap(CtrlF23, new HotkeyBinding { Vk = F24, Shift = true }));
        Press(LCtrl);
        Press(LShift);

        Assert.True(Press(F23));
        Assert.Equal(new[] { Up(LCtrl), Down(F24) }, Sent());
        Release(F23);
        Sent();
        Assert.False(Release(LCtrl));
        Assert.Empty(Sent()); // the Shift is the user's
    }
}
