using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using PuppyMacro.Models;
using Xunit;

namespace PuppyMacro.E2E;

/// <summary>
/// Builders and checks shared by the playback tests (loops, macros, remaps, recording and overlay
/// mode really sending input to a <see cref="TargetWindow"/>). Every loop and macro has its sound on.
/// </summary>
internal static class Playback
{
    public static HotkeyBinding Key(int vk) => HotkeyBinding.FromKey(vk);

    public static MacroDefinition Macro(string name, int? hotkey, params MacroAction[] actions) => new()
    {
        Name = name,
        Hotkey = hotkey is int vk ? Key(vk) : null,
        SoundEnabled = true,
        Actions = actions.ToList(),
    };

    public static MacroAction Press(int vk, double delayMs = 0, double holdMs = 30) =>
        new() { Type = MacroActionType.PressKey, Vk = vk, DelayMs = delayMs, HoldMs = holdMs };

    public static MacroAction Click(int button, Point at, double delayMs = 300) =>
        new() { Type = MacroActionType.Click, Vk = button, X = at.X, Y = at.Y, DelayMs = delayMs };

    public static LoopDefinition Loop(string name, int? hotkey, params LoopAction[] rows) => new()
    {
        Name = name,
        Hotkey = hotkey is int vk ? Key(vk) : null,
        SoundEnabled = true,
        Actions = rows.ToList(),
    };

    public static LoopAction Every(int vk, double value, IntervalUnit unit = IntervalUnit.Milliseconds) =>
        new() { Type = ActionType.Key, KeyVk = vk, IntervalValue = value, IntervalUnit = unit };

    /// <summary>
    /// A time measured on the target window, with wide margins (CI machines are slow and busy): from
    /// 60% of the expected time minus 30 ms to 160% plus 250 ms.
    /// </summary>
    public static void About(double actualMs, double expectedMs, string what) =>
        Assert.True(actualMs >= expectedMs * 0.6 - 30 && actualMs <= expectedMs * 1.6 + 250,
            $"{what}: {actualMs:0} ms, expected about {expectedMs:0} ms");

    /// <summary>The times between consecutive events.</summary>
    public static List<double> Gaps(IReadOnlyList<TargetEvent> events) =>
        events.Zip(events.Skip(1), (a, b) => b.Ms - a.Ms).ToList();

    public static void Near(Point expected, Point actual, string what) =>
        Assert.True(System.Math.Abs(expected.X - actual.X) <= 2 && System.Math.Abs(expected.Y - actual.Y) <= 2,
            $"{what}: at {actual}, expected {expected}");

    /// <summary>
    /// Waits until <paramref name="vk"/> stops arriving (a stopped loop or macro may still finish the
    /// press it was sending), then checks that nothing more comes for a second and that every press was
    /// released.
    /// </summary>
    public static void AssertStopped(TargetWindow target, int vk, string what)
    {
        TargetWindow.Quiet(0.5);
        int count = target.Downs(vk).Count;
        TargetWindow.Quiet(1);
        Assert.True(target.Downs(vk).Count == count, $"{what} still runs. Received: {target.Describe()}");
        Assert.True(target.Ups(vk).Count == count, $"{what} left a key down. Received: {target.Describe()}");
    }

    /// <summary>The Play / Stop button of a card, then the target window to the front again.</summary>
    public static void PressCard(AppSession app, TargetWindow target, string itemName, string button) =>
        InvokeInMainWindow(target, app.CardButton(itemName, button));

    /// <summary>
    /// Presses a button of the main window through UI Automation, then brings the target window to the
    /// front again (clearing its events), as a user does: press Play in PuppyMacro, go back to the app.
    /// The Invoke brings PuppyMacro to the front, so whatever a loop or macro sends in that moment goes
    /// to PuppyMacro: items started this way wait about a second before their first input.
    /// </summary>
    public static void InvokeInMainWindow(TargetWindow target, AutomationElement button)
    {
        button.Patterns.Invoke.Pattern.Invoke();
        target.Activate();
    }

    /// <summary>Waits until the card shows Play again (its loop or macro stopped).</summary>
    public static void WaitIdle(AppSession app, string itemName) =>
        Assert.True(Retry.WhileFalse(() => app.CardButton(itemName, "Play") != null, AppSession.Timeout, ignoreException: true).Success,
            $"\"{itemName}\" did not stop");
}
