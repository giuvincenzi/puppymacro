using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using PuppyMacro.Services;
using PuppyMacro.Services.Ai;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// Texts kept in files as a Phrase (the AI activity, the History, i18n.md "Texts kept in files") read back by a version
/// whose text for that key takes another number of values: a file written by an older version (before a placeholder was
/// added to the text), a newer one, or a backup from another PC. Such a phrase can't be shown in the language of the
/// moment, so the English text kept next to it is shown, as for a key this version does not have.
/// </summary>
public class PhraseFilesTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    /// <summary>"Called {0} while AI access was off" with no value: a phrase this version can't fill.</summary>
    private static Phrase MissingValue() => new() { Key = "Ai_CalledWhileOff" };

    /// <summary>A key this version does not have (a newer version's file) falls back to the English text: already true.</summary>
    [Fact]
    public void A_key_this_version_does_not_have_shows_the_English_text()
    {
        var entry = new AiActivity { Request = "Did something new", RequestPhrase = new Phrase { Key = "Ai_SomethingFromVersion99" } };
        Assert.Equal("Did something new", entry.ShownRequest);
    }

    /// <summary>More values than the text uses (a newer version's longer phrase) are left out, never an error.</summary>
    [Fact]
    public void Extra_values_are_left_out()
    {
        var phrase = Phrase.Of("Ai_CalledWhileOff", "get_item", "unused");
        Assert.Equal("Called get_item while AI access was off", phrase.TryRender(CultureInfo.InvariantCulture));
    }
}
