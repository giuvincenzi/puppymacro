namespace PuppyMacro.Services;

/// <summary>Texts shortened for a row, a card or a log: never in the middle of a character.</summary>
internal static class TextCut
{
    /// <summary>
    /// The first <paramref name="most"/> characters and "…", or the text as it is. A character written in two halves (an
    /// emoji, UTF-16's surrogate pair) is kept whole or left out, never cut into a half that shows as a box.
    /// </summary>
    public static string Shorten(string text, int most)
    {
        if (text.Length <= most)
            return text;
        int end = most;
        if (end > 0 && char.IsHighSurrogate(text[end - 1]))
            end--;
        return text[..end] + "…";
    }
}
