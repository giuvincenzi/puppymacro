using System;
using System.IO;
using System.Linq;
using FlaUI.Core.Input;
using PuppyMacro.Models;
using Xunit;
using static PuppyMacro.E2E.Playback;

namespace PuppyMacro.E2E;

/// <summary>Remaps on the target window. make e2e CATEGORY=Remaps</summary>
[Trait("Category", "Remaps")]
public class RemapTests
{
    private static RemapDefinition Remap(int source, HotkeyBinding target, string? appExe = null, bool enabled = true) =>
        new() { SourceVk = source, Target = target, AppExe = appExe, Enabled = enabled, Note = $"E2E {source:X2}" };

    [Fact]
    public void A_key_or_button_becomes_another_key_a_combination_or_a_button_for_as_long_as_it_is_held()
    {
        using var app = new AppSession(seed => seed.Settings.Remaps.AddRange(new[]
        {
            Remap(Vk.F20, Key(Vk.F21)),
            Remap(Vk.F22, new HotkeyBinding { Vk = Vk.F16, Ctrl = true }),
            Remap(Vk.F23, Key(Vk.MButton)),
            Remap(Vk.XButton2, Key(Vk.F17)),
        }));
        using var target = new TargetWindow();
        var point = TargetWindow.PointAt(0.5, 0.5);
        Mouse.MoveTo(point);

        // Key to key, held for half a second.
        target.Press(Vk.F20);
        target.WaitFor(t => t.Downs(Vk.F21).Count == 1, 5, "F20 did not become F21");
        TargetWindow.Quiet(0.5);
        Assert.Empty(target.Ups(Vk.F21));
        target.Release(Vk.F20);
        target.WaitFor(t => t.Ups(Vk.F21).Count == 1, 5, "releasing F20 did not release F21");
        Assert.True(target.Ups(Vk.F21)[0].Ms - target.Downs(Vk.F21)[0].Ms >= 450, "F21 was not held as long as F20");

        // Key to a combination: Ctrl down first, up last.
        target.Tap(Vk.F22);
        target.WaitFor(t => t.Ups(Vk.Control).Count == 1, 5, "F22 did not become Ctrl+F16");
        Assert.True(target.Downs(Vk.Control)[0].Ms <= target.Downs(Vk.F16)[0].Ms
            && target.Ups(Vk.F16)[0].Ms <= target.Ups(Vk.Control)[0].Ms, $"F16 is not inside Ctrl. Received: {target.Describe()}");

        // Key to a mouse button, at the cursor.
        target.Tap(Vk.F23);
        target.WaitFor(t => t.Ups(Vk.MButton).Count == 1, 5, "F23 did not become a middle click");
        Near(point, target.Downs(Vk.MButton)[0].Position, "remapped middle click");

        // Mouse button to key.
        target.Click(point, MouseButton.XButton2);
        target.WaitFor(t => t.Ups(Vk.F17).Count == 1, 5, "the forward button did not become F17");

        // The sources never reached the window.
        Assert.Empty(target.Downs(Vk.F20));
        Assert.Empty(target.Downs(Vk.F22));
        Assert.Empty(target.Downs(Vk.F23));
        Assert.Empty(target.Downs(Vk.XButton2));
    }

    [Fact]
    public void An_app_remap_wins_over_an_all_apps_one_other_apps_and_disabled_remaps_change_nothing_and_hotkeys_come_first()
    {
        string thisApp = Path.GetFileName(Environment.ProcessPath)!; // the target window's process
        using var app = new AppSession(seed =>
        {
            seed.Settings.Remaps.AddRange(new[]
            {
                Remap(Vk.F20, Key(Vk.F16)),
                Remap(Vk.F20, Key(Vk.F21), appExe: thisApp),
                Remap(Vk.F22, Key(Vk.F21), appExe: "notepad.exe"),
                Remap(Vk.F23, Key(Vk.F21), enabled: false),
                Remap(Vk.F13, Key(Vk.F21)),
            });
            seed.Settings.Loops.Add(Loop("E2E hotkey first", Vk.F13, Every(Vk.F15, 300)));
        });
        using var target = new TargetWindow();

        target.Tap(Vk.F20);
        target.WaitFor(t => t.Ups(Vk.F21).Count == 1, 5, $"the remap for {thisApp} did not win");
        Assert.Empty(target.Downs(Vk.F16));

        target.Tap(Vk.F22);
        target.WaitFor(t => t.Ups(Vk.F22).Count == 1, 5, "F22 (remapped only in notepad.exe) did not reach the window");

        target.Tap(Vk.F23);
        target.WaitFor(t => t.Ups(Vk.F23).Count == 1, 5, "F23 (disabled remap) did not reach the window");

        // F13 is a loop's hotkey and a remap's source: the loop starts, the remap does nothing.
        target.Tap(Vk.F13);
        target.WaitFor(t => t.Downs(Vk.F15).Count >= 2, 5, "the loop did not start");
        target.Tap(Vk.F13);
        AssertStopped(target, Vk.F15, "the loop");

        Assert.Single(target.Downs(Vk.F21)); // only the F20 remap
        Assert.Empty(target.Downs(Vk.F13));
        Assert.Empty(target.Downs(Vk.F20));
    }
}
