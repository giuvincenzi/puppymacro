using System;
using System.Collections.Generic;
using System.Linq;
using PuppyMacro.Models;

namespace PuppyMacro.Services;

/// <summary>
/// A remap that keeps the target's modifiers held while the user holds the source's
/// (<see cref="RemapDefinition.KeepModifiersHeld"/>): Ctrl + Tab → Alt + Tab, then Tab again with Ctrl still held moves on
/// in the Alt + Tab switcher, and releasing Ctrl releases Alt. Releasing any one of the source's modifiers releases all the
/// target's. Only decides the keys to send: <see cref="LoopEngine"/> sends them, in order, and calls it with its lock held.
/// </summary>
internal sealed class RemapHold
{
    private HotkeyBinding? _source;
    private int _targetVk;
    private List<int> _sourceModifiers = new();
    private List<int> _targetModifiers = new();

    /// <summary>A remap holds its target's modifiers now.</summary>
    public bool Active => _source != null;

    /// <summary>The remap's target key (while <see cref="Active"/>).</summary>
    public int TargetVk => _targetVk;

    /// <summary>The remap keeps its target's modifiers held: the option is on and both keys have modifiers.</summary>
    public static bool Applies(RemapDefinition remap) => remap.KeepModifiersHeld && remap.CanKeepModifiersHeld;

    /// <summary>
    /// Starts holding <paramref name="remap"/>'s target, its source just pressed with <paramref name="held"/> (the modifier
    /// keys physically held: the source's). Those are released first, so the app gets only the target (behind the mask
    /// key: Alt or Win released alone would open a menu); they are not pressed again while the hold lasts.
    /// </summary>
    public List<KeyStep> Start(RemapDefinition remap, IReadOnlyList<int> held)
    {
        _source = remap.Source!.Clone();
        _targetVk = remap.Target!.Vk;
        _sourceModifiers = held.ToList();
        _targetModifiers = InputSender.ModifierKeys(remap.Target);

        var steps = new List<KeyStep>();
        if (held.Any(IsAltOrWin))
        {
            steps.Add(new KeyStep(InputSender.MaskKeyVk, true));
            steps.Add(new KeyStep(InputSender.MaskKeyVk, false));
        }
        steps.AddRange(held.Select(vk => new KeyStep(vk, false)));
        steps.AddRange(_targetModifiers.Select(vk => new KeyStep(vk, true)));
        steps.Add(new KeyStep(_targetVk, true));
        return steps;
    }

    /// <summary>The source pressed again (<paramref name="pressed"/>: the key with the modifiers held), exactly as it started.</summary>
    public bool IsSourceAgain(HotkeyBinding pressed) => _source?.Matches(pressed) == true;

    /// <summary>The target's key again, its modifiers still held (Tab again in the switcher).</summary>
    public List<KeyStep> Again() => new() { new KeyStep(_targetVk, true) };

    /// <summary>
    /// A press that leaves the hold on: the source's key with a modifier added (Shift + Tab goes back in the switcher) and
    /// mouse buttons (a click in the switcher). Any other key ends it (<see cref="End"/> with the modifiers still held).
    /// </summary>
    public bool Continues(int vk) => _source != null && (vk == _source.Vk || KeyNames.IsMouse(vk));

    /// <summary>The release of <paramref name="modifierVk"/> ends the hold: it is one of the source's modifiers.</summary>
    public bool EndsWith(int modifierVk) => _source != null && _sourceModifiers.Contains(modifierVk);

    /// <summary>
    /// Ends the hold: the target's modifiers released, the last pressed first. With <paramref name="stillHeld"/> (another
    /// key pressed while the source's modifiers are held), the source's modifiers still held are pressed again, so that key
    /// gets them (Ctrl + C, not Alt + C).
    /// </summary>
    public List<KeyStep> End(IReadOnlyList<int>? stillHeld = null)
    {
        var steps = new List<KeyStep>();
        if (_source == null)
            return steps;
        for (int i = _targetModifiers.Count - 1; i >= 0; i--)
            steps.Add(new KeyStep(_targetModifiers[i], false));
        if (stillHeld != null)
            steps.AddRange(_sourceModifiers.Where(stillHeld.Contains).Select(vk => new KeyStep(vk, true)));

        _source = null;
        _targetVk = 0;
        _sourceModifiers = new List<int>();
        _targetModifiers = new List<int>();
        return steps;
    }

    private static bool IsAltOrWin(int vk) =>
        vk is KeyNames.VK_LMENU or KeyNames.VK_RMENU or KeyNames.VK_LWIN or KeyNames.VK_RWIN;
}
