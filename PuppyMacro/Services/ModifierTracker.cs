using System.Collections.Generic;
using System.Threading;

namespace PuppyMacro.Services;

/// <summary>
/// Physical state of Ctrl, Alt, Shift and Win (left and right). The single source of truth is
/// the input hook: only real key events change it; keys sent by PuppyMacro or other programs
/// (injected) never do. Read from any thread.
/// </summary>
internal static class ModifierTracker
{
    private static readonly int[] TrackedKeys =
    {
        KeyNames.VK_LCONTROL, KeyNames.VK_RCONTROL,
        KeyNames.VK_LMENU, KeyNames.VK_RMENU,
        KeyNames.VK_LSHIFT, KeyNames.VK_RSHIFT,
        KeyNames.VK_LWIN, KeyNames.VK_RWIN,
    };

    private static int _mask;

    public static bool Ctrl => IsDown(KeyNames.VK_LCONTROL) || IsDown(KeyNames.VK_RCONTROL);
    public static bool Alt => IsDown(KeyNames.VK_LMENU) || IsDown(KeyNames.VK_RMENU);
    public static bool Shift => IsDown(KeyNames.VK_LSHIFT) || IsDown(KeyNames.VK_RSHIFT);
    public static bool Win => IsDown(KeyNames.VK_LWIN) || IsDown(KeyNames.VK_RWIN);

    /// <summary>Ctrl, Alt, Shift or Win is held.</summary>
    public static bool Any => Volatile.Read(ref _mask) != 0;

    /// <summary>Called by the hook for every real modifier event.</summary>
    public static void Update(int vk, bool isDown)
    {
        int bit = BitOf(vk);
        if (bit == 0)
            return;

        int current, updated;
        do
        {
            current = Volatile.Read(ref _mask);
            updated = isDown ? current | bit : current & ~bit;
        }
        while (Interlocked.CompareExchange(ref _mask, updated, current) != current);
    }

    public static void Reset() => Interlocked.Exchange(ref _mask, 0);

    /// <summary>The specific (left/right) modifier keys currently held.</summary>
    public static List<int> HeldKeys()
    {
        var held = new List<int>();
        foreach (int vk in TrackedKeys)
        {
            if (IsDown(vk))
                held.Add(vk);
        }
        return held;
    }

    /// <summary>True if the user is physically holding the key (from real, non-injected events only).</summary>
    public static bool IsDown(int vk)
    {
        int bit = BitOf(vk);
        return bit != 0 && (Volatile.Read(ref _mask) & bit) != 0;
    }

    private static int BitOf(int vk)
    {
        for (int i = 0; i < TrackedKeys.Length; i++)
        {
            if (TrackedKeys[i] == vk)
                return 1 << i;
        }
        return 0;
    }
}
