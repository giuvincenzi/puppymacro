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

    [Fact]
    public void A_phrase_with_fewer_values_than_its_text_is_not_shown()
    {
        Assert.Null(Record.Exception(() => MissingValue().TryRender(CultureInfo.CurrentUICulture)));
        Assert.Null(MissingValue().TryRender(CultureInfo.CurrentUICulture));
    }

    /// <summary>
    /// ai-activity.json with such an entry: the AI page (MainWindow.RefreshAiPage builds an AiActivityRow for each recent
    /// entry, which reads ShownRequest) and the AI activity window must show the English text, not throw on the UI thread
    /// at every refresh.
    /// </summary>
    [Fact]
    public void An_AI_activity_entry_from_another_version_shows_its_English_text()
    {
        string path = _folder.File("ai-activity.json");
        File.WriteAllText(path, """
            [
              {
                "Time": "2026-10-01T10:00:00",
                "App": "Claude (claude-ai)",
                "Kind": "Connection",
                "Request": "Called get_item while AI access was off",
                "RequestPhrase": { "Key": "Ai_CalledWhileOff" },
                "Result": "Refused"
              }
            ]
            """);
        var log = new AiActivityLog(path);
        log.Load();
        AiActivity entry = Assert.Single(log.Entries());

        string? shown = null;
        Assert.Null(Record.Exception(() => shown = entry.ShownRequest));
        Assert.Equal("Called get_item while AI access was off", shown);
    }

    /// <summary>A History version with such a summary or change: the History view shows the English texts kept with it.</summary>
    [Fact]
    public void A_history_version_from_another_version_shows_its_English_texts()
    {
        var version = new HistoryVersion
        {
            Summary = "Called get_item while AI access was off",
            SummaryPhrase = MissingValue(),
            Changes = new List<string> { "Name: A → B" },
            ChangePhrases = new List<Phrase> { MissingValue() },
        };

        string? summary = null;
        List<string>? changes = null;
        Assert.Null(Record.Exception(() => summary = version.ShownSummary));
        Assert.Null(Record.Exception(() => changes = version.ShownChanges));
        Assert.Equal("Called get_item while AI access was off", summary);
        Assert.Equal(new[] { "Name: A → B" }, changes);
    }

    /// <summary>
    /// ai-activity.json written by a newer version (PuppyMacro downgraded, or a backup from a newer PC imported: i18n.md
    /// says such a phrase shows its English text). Load keeps the newest entries that fit by serializing each one, and a
    /// Phrase serializes its English property, which throws for a key this version does not have. Load runs in
    /// MainWindow's constructor (InitializeAi) and catches only JsonException and I/O errors, so PuppyMacro would fail at
    /// every start until the file is deleted by hand.
    /// </summary>
    [Fact]
    public void An_AI_activity_file_from_a_newer_version_loads_and_keeps_working()
    {
        string path = _folder.File("ai-activity.json");
        File.WriteAllText(path, """
            [
              {
                "Time": "2026-10-01T10:00:00",
                "App": "Claude (claude-ai)",
                "Kind": "Read",
                "Request": "Read your timelines",
                "RequestPhrase": { "Key": "Ai_ReadTimelinesFromVersion99" },
                "Result": "None"
              }
            ]
            """);
        var log = new AiActivityLog(path);

        Assert.Null(Record.Exception(log.Load));
        Assert.Null(Record.Exception(() => log.Add("Claude (claude-ai)", AiActivityKind.Read, Phrase.Of("Ai_ReadSettings"))));
        Assert.Equal(new[] { "Read your settings", "Read your timelines" }, log.Entries().Select(e => e.ShownRequest));
    }

    /// <summary>
    /// A History file with a version saved by a newer version (a phrase key this version does not have). The next save of
    /// the item records a version: ItemHistory.Write serializes every version (to keep the newest that fit), the old
    /// phrase's English property throws, and only I/O errors are caught: the save throws on the UI thread and this item's
    /// History never grows again.
    /// </summary>
    [Fact]
    public void A_history_file_from_a_newer_version_still_takes_new_versions()
    {
        string folder = _folder.File("history");
        Directory.CreateDirectory(folder);
        Guid id = Guid.NewGuid();
        string a = $"{{\"Id\":\"{id:D}\",\"Name\":\"A\"}}";
        var old = new
        {
            Time = new DateTime(2026, 10, 1, 10, 0, 0),
            By = "You",
            Summary = "Moved to another timeline",
            Changes = Array.Empty<string>(),
            SummaryPhrase = new { Key = "History_MovedFromVersion99" },
            Json = a,
        };
        File.WriteAllText(Path.Combine(folder, id.ToString("D") + ".json"), System.Text.Json.JsonSerializer.Serialize(new[] { old }));
        var history = new ItemHistory(folder);
        history.Seed(id, a);

        Assert.Null(Record.Exception(() => history.Record(id, a.Replace("\"A\"", "\"B\""), ItemHistory.You)));
        Assert.Equal(new[] { "Name: \"A\" → \"B\"", "Moved to another timeline" }, history.Versions(id).Select(v => v.ShownSummary));
    }

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
