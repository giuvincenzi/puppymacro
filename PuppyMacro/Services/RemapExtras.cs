using System.Collections.Generic;
using System.Linq;
using PuppyMacro.Models;

namespace PuppyMacro.Services;

/// <summary>
/// Pass extra modifiers (<see cref="RemapDefinition.PassExtraModifiers"/>): the source pressed with other Ctrl, Alt, Shift
/// or Win held too (Shift + Alt + A for Alt + A → Left). Only the source's own modifiers are released for the app; the
/// others stay down, as the user holds them, so the app gets them with the target (Shift + Left). Nothing is pressed for
/// the user. Only decides the keys: <see cref="LoopEngine"/> sends them, in order, on its injector.
/// </summary>
internal static class RemapExtras
{
    /// <summary>The remap also works with extra modifiers: the option is on and its source is a key or a button.</summary>
    public static bool Applies(RemapDefinition remap) => remap.PassExtraModifiers && remap.CanPassExtraModifiers;

    /// <summary>
    /// The keys of <paramref name="held"/> (the modifier keys physically held) that are the source's own modifiers: those
    /// released for the app. The others are the extra ones.
    /// </summary>
    public static List<int> SourceKeys(HotkeyBinding source, IReadOnlyList<int> held) =>
        held.Where(vk => IsOf(source, vk)).ToList();

    /// <summary>
    /// The target pressed: the source's own modifiers released (behind the mask key when one is Alt or Win, which released
    /// alone would open a menu), then the target's modifiers the user does not already hold, then its key.
    /// <paramref name="pressedModifiers"/>: the target's modifiers pressed, to release with its key.
    /// </summary>
    public static List<KeyStep> Press(HotkeyBinding source, HotkeyBinding target, IReadOnlyList<int> held,
        out List<int> pressedModifiers)
    {
        var sourceKeys = SourceKeys(source, held);
        var extras = held.Except(sourceKeys).ToList();
        pressedModifiers = InputSender.ModifierKeys(target).Where(vk => !extras.Any(e => SameModifier(e, vk))).ToList();

        var steps = new List<KeyStep>();
        if (sourceKeys.Any(IsAltOrWin))
        {
            steps.Add(new KeyStep(InputSender.MaskKeyVk, true));
            steps.Add(new KeyStep(InputSender.MaskKeyVk, false));
        }
        steps.AddRange(sourceKeys.Select(vk => new KeyStep(vk, false)));
        steps.AddRange(pressedModifiers.Select(vk => new KeyStep(vk, true)));
        steps.Add(new KeyStep(target.Vk, true));
        return steps;
    }

    /// <summary>The target released: its key, then the modifiers <see cref="Press"/> pressed, the last pressed first.</summary>
    public static List<KeyStep> Release(int targetVk, IReadOnlyList<int> pressedModifiers)
    {
        var steps = new List<KeyStep> { new(targetVk, false) };
        for (int i = pressedModifiers.Count - 1; i >= 0; i--)
            steps.Add(new KeyStep(pressedModifiers[i], false));
        return steps;
    }

    /// <summary><paramref name="vk"/> (a left or right modifier key) is one of <paramref name="binding"/>'s modifiers.</summary>
    public static bool IsOf(HotkeyBinding binding, int vk) => vk switch
    {
        KeyNames.VK_LCONTROL or KeyNames.VK_RCONTROL => binding.Ctrl,
        KeyNames.VK_LMENU or KeyNames.VK_RMENU => binding.Alt,
        KeyNames.VK_LSHIFT or KeyNames.VK_RSHIFT => binding.Shift,
        KeyNames.VK_LWIN or KeyNames.VK_RWIN => binding.Win,
        _ => false,
    };

    /// <summary>Two modifier keys are the same modifier, on either side (left and right Shift).</summary>
    public static bool SameModifier(int a, int b) => Kind(a) == Kind(b);

    private static int Kind(int vk) => vk switch
    {
        KeyNames.VK_LCONTROL or KeyNames.VK_RCONTROL => 1,
        KeyNames.VK_LMENU or KeyNames.VK_RMENU => 2,
        KeyNames.VK_LSHIFT or KeyNames.VK_RSHIFT => 3,
        KeyNames.VK_LWIN or KeyNames.VK_RWIN => 4,
        _ => 0,
    };

    private static bool IsAltOrWin(int vk) =>
        vk is KeyNames.VK_LMENU or KeyNames.VK_RMENU or KeyNames.VK_LWIN or KeyNames.VK_RWIN;
}
