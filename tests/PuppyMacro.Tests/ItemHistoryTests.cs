using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

public class ItemHistoryTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private DateTime _now = new(2026, 10, 11, 14, 0, 0);
    private readonly ItemHistory _history;

    public ItemHistoryTests() => _history = new ItemHistory(_folder.File("history"), () => _now = _now.AddMinutes(1));

    public void Dispose() => _folder.Dispose();

    private static LoopDefinition Loop(Guid id, string name, double interval = 100, HotkeyBinding? hotkey = null) => new()
    {
        Id = id,
        Name = name,
        Hotkey = hotkey,
        Actions = { new LoopAction { KeyVk = 0x87, IntervalValue = interval } },
    };

    [Fact]
    public void A_new_item_starts_with_Created_and_each_save_says_who_and_what_changed()
    {
        Guid id = Guid.NewGuid();
        _history.Record(id, LoopJson.Serialize(Loop(id, "Spam")), "Claude Code");
        _history.Record(id, LoopJson.Serialize(Loop(id, "Spam", hotkey: HotkeyBinding.FromKey(0x75))), ItemHistory.You);
        _history.Record(id, LoopJson.Serialize(Loop(id, "Spam F24", 250, HotkeyBinding.FromKey(0x76))), "Claude Code");

        List<HistoryVersion> versions = _history.Versions(id);

        Assert.Equal(new[] { "3 changes", "Hotkey: none → F6", "Created" }, versions.Select(v => v.Summary));
        Assert.Equal(new[] { "Claude Code", "You", "Claude Code" }, versions.Select(v => v.By));
        Assert.Contains("Name: \"Spam\" → \"Spam F24\"", versions[0].Changes);
        Assert.Contains("Hotkey: F6 → F7", versions[0].Changes);
        Assert.Contains("Action 1 (Key): Interval value 100 → 250", versions[0].Changes);
    }

    [Fact]
    public void A_save_with_no_change_keeps_no_version()
    {
        Guid id = Guid.NewGuid();
        string json = LoopJson.Serialize(Loop(id, "Spam"));
        _history.Record(id, json, ItemHistory.You);
        _history.Record(id, json, ItemHistory.You);

        Assert.Single(_history.Versions(id));
    }

    [Fact]
    public void The_first_change_of_an_item_saved_before_the_history_keeps_its_earlier_version()
    {
        Guid id = Guid.NewGuid();
        string before = LoopJson.Serialize(Loop(id, "Old"));
        _history.Seed(id, before);
        _history.Record(id, LoopJson.Serialize(Loop(id, "New")), "Codex");

        List<HistoryVersion> versions = _history.Versions(id);

        Assert.Equal(2, versions.Count);
        Assert.Equal(before, versions[1].Json);
        Assert.Equal("Name: \"Old\" → \"New\"", versions[0].Summary);
    }

    [Fact]
    public void Only_the_last_50_versions_are_kept()
    {
        Guid id = Guid.NewGuid();
        for (int i = 0; i < ItemHistory.MaxVersions + 10; i++)
            _history.Record(id, LoopJson.Serialize(Loop(id, $"Spam {i}")), ItemHistory.You);

        List<HistoryVersion> versions = _history.Versions(id);

        Assert.Equal(ItemHistory.MaxVersions, versions.Count);
        Assert.Contains("\"Spam 59\"", versions[0].Json);
    }

    [Fact]
    public void A_deleted_item_keeps_its_last_version_and_comes_back_as_restored()
    {
        Guid id = Guid.NewGuid();
        string json = LoopJson.Serialize(Loop(id, "Spam"));
        _history.Record(id, json, ItemHistory.You);
        _history.RecordDeleted(id, "Claude Code");
        _history.Record(id, json, ItemHistory.You);

        List<HistoryVersion> versions = _history.Versions(id);

        Assert.Equal(new[] { "Restored", "Deleted", "Created" }, versions.Select(v => v.Summary));
        Assert.True(versions[1].Deleted);
        Assert.Equal(json, versions[1].Json);
    }

    [Fact]
    public void Added_and_removed_macro_actions_are_named()
    {
        var before = new MacroDefinition { Name = "M", Actions = { new MacroAction { Type = MacroActionType.PressKey, Vk = 0x41, Name = "Open" } } };
        var after = new MacroDefinition
        {
            Id = before.Id,
            Name = "M",
            Actions =
            {
                new MacroAction { Type = MacroActionType.PressKey, Vk = 0x41, Name = "Open", DelayMs = 250 },
                new MacroAction { Type = MacroActionType.Click, Vk = 1 },
            },
        };

        List<string> changes = ItemHistory.Changes(MacroJson.Serialize(before), MacroJson.Serialize(after));

        Assert.Contains("Added action 2 (Click)", changes);
        Assert.Contains("Action 1 (Open): Delay ms 0 → 250", changes);
        Assert.Contains("Removed action 2 (Click)", ItemHistory.Changes(MacroJson.Serialize(after), MacroJson.Serialize(before)));
    }

    [Fact]
    public void An_unreadable_history_file_is_an_empty_history()
    {
        Guid id = Guid.NewGuid();
        Directory.CreateDirectory(_folder.File("history"));
        File.WriteAllText(Path.Combine(_folder.File("history"), id + ".json"), "not json");

        Assert.Empty(_history.Versions(id));
    }
}
