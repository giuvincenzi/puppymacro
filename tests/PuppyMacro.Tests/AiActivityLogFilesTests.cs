using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PuppyMacro.Services;
using PuppyMacro.Services.Ai;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>ai-activity.json: the AI apps' newest requests up to a size, with the data Restore needs (<see cref="AiActivityLog"/>).</summary>
public class AiActivityLogFilesTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private string FilePath => _folder.File("ai-activity.json");

    private static AiActivity Add(AiActivityLog log, string request, string before) =>
        log.Add("Claude Desktop", AiActivityKind.Proposal, Phrase.Literal(request), AiActivityResult.Saved,
            itemId: Guid.NewGuid(), itemKind: "macro", before: before);

    [Theory]
    [InlineData("""[ { "Time": "2026-10-11T14:00:00", "Kind": "AKindOfANewerVersion" } ]""")]
    public void A_file_with_a_kind_of_a_newer_version_is_an_empty_history(string text)
    {
        File.WriteAllText(FilePath, text);
        var log = new AiActivityLog(FilePath);

        log.Load();

        Assert.Empty(log.Entries());
    }

    [Fact]
    public void A_null_entry_of_the_file_is_left_out()
    {
        File.WriteAllText(FilePath, """[ null, { "Time": "2026-10-11T14:00:00", "App": "Claude", "Request": "New loop" } ]""");
        var log = new AiActivityLog(FilePath);

        log.Load();

        Assert.Equal("New loop", Assert.Single(log.Entries()).Request);
    }

    [Fact]
    public void Restore_data_and_texts_come_back_unchanged_after_a_restart()
    {
        var log = new AiActivityLog(FilePath, () => new DateTime(2026, 10, 11, 14, 0, 0, DateTimeKind.Local));
        string before = """{ "Name": "Macro è 循环 \"quoted\"", "Actions": [] }""";
        AiActivity entry = Add(log, "Changed macro", before);
        log.MarkRestored(entry);

        var restarted = new AiActivityLog(FilePath);
        restarted.Load();

        AiActivity read = Assert.Single(restarted.Entries());
        Assert.Equal(before, read.Before);
        Assert.Equal(entry.ItemId, read.ItemId);
        Assert.Equal("macro", read.ItemKind);
        Assert.True(read.Restored);
        Assert.Equal(entry.Time, read.Time);
        Assert.Equal("Changed macro", read.ShownRequest);
    }

    /// <summary>
    /// The clock goes back (daylight saving time ends, the user corrects it): the request just added has an earlier time
    /// than the ones before it. Prune keeps "the newest" by time, not by order: with the file full, the request just made
    /// (with the JSON its Restore needs) is the one dropped, and Entries shows it below older ones. The History keeps the
    /// order of the saves for this reason (ItemHistoryTests.The_order_is_the_order_of_the_saves_also_when_the_clock_goes_back);
    /// data.md: "the oldest entries go first and the newest one always stays".
    /// </summary>
    [Fact]
    public void The_request_just_made_stays_also_when_the_clock_went_back()
    {
        var clock = new Queue<DateTime>(new[] { new DateTime(2026, 10, 25, 2, 50, 0), new DateTime(2026, 10, 25, 2, 10, 0) });
        var log = new AiActivityLog(FilePath, () => clock.Dequeue(), maxBytes: 2000); // one request at a time fits
        Add(log, "First change", new string('x', 1200));

        AiActivity second = Add(log, "Second change", new string('y', 1200));

        Assert.Equal("Second change", log.Entries().First().Request);
        var restarted = new AiActivityLog(FilePath, maxBytes: 2000);
        restarted.Load();
        Assert.Contains(restarted.Entries(), e => e.Request == second.Request && e.Before == second.Before);
    }
}
