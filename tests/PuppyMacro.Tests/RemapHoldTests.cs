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
/// Keep modifiers held (<see cref="RemapHold"/>, <see cref="RemapDefinition.KeepModifiersHeld"/>): the target's modifiers
/// stay held while the source's are, as Ctrl + Tab → Alt + Tab needs to move on in the switcher. The engine's keys of a
/// hold go to a list, never to the PC (the unit tests run while the user works), so these tests send nothing: a remap the
/// hold leaves to the usual path (a target without modifiers) is LoopEngineInputTests'. ModifierTracker is static: same
/// collection as its own tests.
/// </summary>
[Collection(nameof(ModifierTracker))]
public sealed class RemapHoldTests : IDisposable
{
    private const int F24 = 0x87, F23 = 0x86, F22 = 0x85, Tab = 0x09, C = 0x43;
    private const int LCtrl = 0xA2, LAlt = 0xA4, LShift = 0xA0, LWin = 0x5B, Mask = InputSender.MaskKeyVk;

    private readonly List<KeyStep> _sent = new();
    private readonly LoopEngine _engine;
    private bool _disposed;

    public RemapHoldTests()
    {
        ModifierTracker.Reset();
        _engine = new LoopEngine(new EngineSnapshot(), Dispatcher.CurrentDispatcher, steps => _sent.AddRange(steps));
    }

    public void Dispose()
    {
        if (!_disposed)
            _engine.Dispose();
        ModifierTracker.Reset();
    }

    private static KeyStep Down(int vk) => new(vk, true);

    private static KeyStep Up(int vk) => new(vk, false);

    private static RemapDefinition Remap(HotkeyBinding source, HotkeyBinding target, bool keep = true) =>
        new() { Source = source, Target = target, KeepModifiersHeld = keep };

    private static HotkeyBinding CtrlTab => new() { Vk = Tab, Ctrl = true };

    private static HotkeyBinding AltTab => new() { Vk = Tab, Alt = true };

    private void Publish(RemapDefinition remap) => _engine.Publish(new EngineSnapshot { Remaps = new[] { remap } });

    private bool Press(int vk) => _engine.OnKey(vk, isDown: true, physicalModifierEvent: true);

    private bool Release(int vk) => _engine.OnKey(vk, isDown: false, physicalModifierEvent: true);

    /// <summary>The steps sent since the last call.</summary>
    private KeyStep[] Sent()
    {
        var steps = _sent.ToArray();
        _sent.Clear();
        return steps;
    }

    // ---- When it applies ----

    [Fact]
    public void It_applies_only_when_on_and_both_keys_have_modifiers()
    {
        Assert.True(RemapHold.Applies(Remap(CtrlTab, AltTab)));
        Assert.False(RemapHold.Applies(Remap(CtrlTab, AltTab, keep: false)));
        Assert.False(RemapHold.Applies(Remap(HotkeyBinding.FromKey(F23), AltTab)));          // F23 → Alt + Tab
        Assert.False(RemapHold.Applies(Remap(CtrlTab, HotkeyBinding.FromKey(0x0D))));        // Ctrl + Tab → Enter
        Assert.False(RemapHold.Applies(Remap(HotkeyBinding.FromKey(0xA5), HotkeyBinding.FromKey(LCtrl)))); // Right Alt → Ctrl
    }

    [Fact]
    public void A_remap_from_a_file_without_the_field_keeps_modifiers_held()
    {
        using var folder = new TempFolder();
        string path = folder.File("settings.json");
        File.WriteAllText(path, """
            { "SchemaVersion": 8, "Remaps": [ { "Source": { "Vk": 9, "Ctrl": true }, "Target": { "Vk": 9, "Alt": true } } ] }
            """);

        AppSettings settings = new SettingsStore(path).Load(out _);

        Assert.True(Assert.Single(settings.Remaps).KeepModifiersHeld);
    }

    [Fact]
    public void Off_is_kept_by_the_Code_view_and_by_a_copy()
    {
        var remap = Remap(CtrlTab, AltTab, keep: false);

        RemapDefinition? read = RemapJson.Parse(RemapJson.Serialize(remap), remap.Id, (_, _) => null, out var problems);

        Assert.Empty(problems);
        Assert.False(read!.KeepModifiersHeld);
        Assert.False(remap.Clone().KeepModifiersHeld);
        var copy = new RemapDefinition();
        copy.CopyFrom(remap);
        Assert.False(copy.KeepModifiersHeld);
    }

    // ---- The keys RemapHold decides ----

    [Fact]
    public void Ctrl_Tab_to_Alt_Tab_releases_Ctrl_then_holds_Alt_until_Ctrl_is_released()
    {
        var hold = new RemapHold();

        Assert.Equal(new[] { Up(LCtrl), Down(LAlt), Down(Tab) }, hold.Start(Remap(CtrlTab, AltTab), new[] { LCtrl }));
        Assert.True(hold.Active);
        Assert.Equal(new[] { Down(Tab) }, hold.Again());
        Assert.True(hold.EndsWith(LCtrl));
        Assert.Equal(new[] { Up(LAlt) }, hold.End());
        Assert.False(hold.Active);
        Assert.Empty(hold.End());
    }

    [Fact]
    public void A_source_with_Alt_is_released_behind_the_mask_key_so_no_menu_opens()
    {
        var hold = new RemapHold();
        var altQ = new HotkeyBinding { Vk = 0x51, Alt = true };
        var ctrlW = new HotkeyBinding { Vk = 0x57, Ctrl = true };

        Assert.Equal(new[] { Down(Mask), Up(Mask), Up(LAlt), Down(LCtrl), Down(0x57) }, hold.Start(Remap(altQ, ctrlW), new[] { LAlt }));
    }

    [Fact]
    public void Several_modifiers_are_released_all_together_the_last_pressed_first()
    {
        var hold = new RemapHold();
        var source = new HotkeyBinding { Vk = 0x4F, Ctrl = true, Alt = true };
        var target = new HotkeyBinding { Vk = 0x4F, Shift = true, Win = true };

        hold.Start(Remap(source, target), new[] { LCtrl, LAlt });

        Assert.True(hold.EndsWith(LCtrl));
        Assert.True(hold.EndsWith(LAlt));
        Assert.False(hold.EndsWith(LShift));
        Assert.Equal(new[] { Up(LWin), Up(LShift) }, hold.End());
    }

    [Fact]
    public void Another_key_ends_the_hold_and_gets_the_source_modifiers_still_held_back()
    {
        var hold = new RemapHold();
        hold.Start(Remap(CtrlTab, AltTab), new[] { LCtrl });

        Assert.True(hold.Continues(Tab));                 // Shift + Tab goes back in the switcher
        Assert.True(hold.Continues(KeyNames.VK_LBUTTON)); // a click in the switcher
        Assert.False(hold.Continues(C));
        // Shift was added by the user (it passed): only the source's Ctrl is pressed again.
        Assert.Equal(new[] { Up(LAlt), Down(LCtrl) }, hold.End(new[] { LCtrl, LShift }));
    }

    [Fact]
    public void A_target_modifier_on_the_right_side_is_held_on_the_right()
    {
        var hold = new RemapHold();
        var target = new HotkeyBinding { Vk = Tab, Alt = true, AltSide = ModifierSide.Right };

        Assert.Equal(new[] { Up(LCtrl), Down(0xA5), Down(Tab) }, hold.Start(Remap(CtrlTab, target), new[] { LCtrl }));
        Assert.Equal(new[] { Up(0xA5) }, hold.End());
    }

    // ---- The engine ----

    private static HotkeyBinding CtrlF23 => new() { Vk = F23, Ctrl = true };

    private static HotkeyBinding AltF24 => new() { Vk = F24, Alt = true };

    /// <summary>Ctrl held, the source pressed and released: Alt + F24 sent, Alt still held.</summary>
    private void StartHold()
    {
        Publish(Remap(CtrlF23, AltF24));
        Assert.False(Press(LCtrl));
        Assert.True(Press(F23));
        Assert.Equal(new[] { Up(LCtrl), Down(LAlt), Down(F24) }, Sent());
        Assert.True(Release(F23)); // F24 released, Alt stays
        Assert.Equal(new[] { Up(F24) }, Sent());
    }

    [Fact]
    public void The_source_again_sends_only_the_key_and_releasing_Ctrl_releases_Alt()
    {
        StartHold();

        Assert.True(Press(F23));
        Assert.Equal(new[] { Down(F24) }, Sent());
        Assert.True(Press(F23)); // auto-repeat: the target's key again
        Assert.Equal(new[] { Down(F24) }, Sent());
        Assert.True(Release(F23));
        Assert.Equal(new[] { Up(F24) }, Sent());
        Assert.True(Press(F23));
        Assert.True(Release(F23));
        Assert.Equal(new[] { Down(F24), Up(F24) }, Sent());

        Assert.False(Release(LCtrl)); // the user's release passes
        Assert.Equal(new[] { Up(LAlt) }, Sent());

        Assert.False(Press(F23)); // plain F23 now: not the source
        Assert.False(Release(F23));
        Assert.Empty(Sent());
    }

    [Fact]
    public void Releasing_Ctrl_while_the_source_key_is_still_down_releases_Alt_then_the_key_with_its_release()
    {
        Publish(Remap(CtrlF23, AltF24));
        Press(LCtrl);
        Press(F23);
        Sent();

        Assert.False(Release(LCtrl));
        Assert.Equal(new[] { Up(LAlt) }, Sent());
        Assert.True(Release(F23));
        Assert.Equal(new[] { Up(F24) }, Sent());
    }

    [Fact]
    public void Shift_added_to_the_source_passes_with_the_target_modifiers_still_held()
    {
        StartHold();

        Assert.False(Press(LShift));
        Assert.False(Press(F23)); // Ctrl + Shift + F23: not the source, it passes (the app gets Alt + Shift + F23)
        Assert.False(Release(F23));
        Assert.False(Release(LShift));
        Assert.Empty(Sent());

        Assert.False(Release(LCtrl));
        Assert.Equal(new[] { Up(LAlt) }, Sent());
    }

    [Fact]
    public void Another_key_gets_Ctrl_back_and_the_Ctrl_pressed_again_is_released_after_it()
    {
        StartHold();

        Assert.True(Press(F22)); // blocked and sent again after Alt released and Ctrl pressed again
        Assert.Equal(new[] { Up(LAlt), Down(LCtrl), Down(F22) }, Sent());
        Assert.False(Release(F22));
        Assert.False(Press(F22)); // the hold is over: keys pass as usual
        Assert.False(Release(F22));
        Assert.Empty(Sent());

        Assert.True(Release(LCtrl)); // blocked and sent after the press, in order
        Assert.Equal(new[] { Up(LCtrl) }, Sent());
        Assert.False(ModifierTracker.Any);

        Assert.False(Press(LCtrl)); // the next press is the user's again
        Assert.False(Release(LCtrl));
        Assert.Empty(Sent());
    }

    [Fact]
    public void Ctrl_Tab_again_after_another_key_starts_a_new_hold()
    {
        StartHold();
        Press(F22);
        Release(F22);
        Sent();

        Assert.True(Press(F23));
        Assert.Equal(new[] { Up(LCtrl), Down(LAlt), Down(F24) }, Sent());
        Assert.True(Release(F23));
        Assert.True(Release(LCtrl));
        Assert.Equal(new[] { Up(F24), Up(LAlt), Up(LCtrl) }, Sent());
    }

    [Theory]
    [InlineData(LCtrl, LAlt)]
    [InlineData(LAlt, LCtrl)]
    public void With_two_source_modifiers_the_first_released_releases_every_target_modifier(int first, int second)
    {
        Publish(Remap(new HotkeyBinding { Vk = F23, Ctrl = true, Alt = true }, new HotkeyBinding { Vk = F24, Shift = true, Win = true }));
        Press(LCtrl);
        Press(LAlt);
        Assert.True(Press(F23));
        Assert.Equal(new[] { Down(Mask), Up(Mask), Up(LCtrl), Up(LAlt), Down(LShift), Down(LWin), Down(F24) }, Sent());
        Assert.True(Release(F23));
        Assert.Equal(new[] { Up(F24) }, Sent());

        Assert.False(Release(first));
        Assert.Equal(new[] { Up(LWin), Up(LShift) }, Sent());
        Assert.False(Release(second));
        Assert.Empty(Sent());
    }

    [Fact]
    public void A_desktop_switch_ends_the_hold_and_releases_what_it_pressed_again()
    {
        StartHold();
        Press(F22);
        Release(F22);
        Sent();
        Press(F23); // a new hold, Ctrl pressed again before it
        Sent();

        typeof(LoopEngine).GetMethod("OnDesktopSwitch", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(_engine, null);

        Assert.Equal(new[] { Up(F24), Up(LAlt), Up(LCtrl) }, Sent());
        Assert.False(ModifierTracker.Any);
    }

    [Fact]
    public void Closing_PuppyMacro_releases_the_target_modifiers_held()
    {
        StartHold();

        _engine.Dispose();
        _disposed = true;

        Assert.Equal(new[] { Up(LAlt) }, Sent());
    }
}
