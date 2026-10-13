using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace PuppyMacro.Services;

/// <summary>Names of loops and macros: a copy's name ("Farm (2)"), never one already used.</summary>
public static class ItemNames
{
    private static readonly Regex Numbered = new(@"^(?<name>.*) \((?<n>\d+)\)$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The name of a copy of <paramref name="name"/>: the name with the first free number, " (2)", " (3)"…, compared without
    /// case as Import does; a copy of "Farm (2)" is "Farm (3)", not "Farm (2) (2)".
    /// </summary>
    public static string Copy(string name, IEnumerable<string> taken)
    {
        var used = new HashSet<string>(taken.Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
        string trimmed = name.Trim();
        Match numbered = Numbered.Match(trimmed);
        string start = numbered.Success ? numbered.Groups["name"].Value : trimmed;
        for (int n = 2; ; n++)
        {
            string candidate = string.Format(CultureInfo.InvariantCulture, "{0} ({1})", start, n);
            if (!used.Contains(candidate))
                return candidate;
        }
    }
}
