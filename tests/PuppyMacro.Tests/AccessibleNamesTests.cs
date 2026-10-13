using System;
using System.Globalization;
using PuppyMacro.Models;
using PuppyMacro.Services;
using PuppyMacro.Services.Ai;
using PuppyMacro.Views;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>A list row's accessible name is what the row shows, never its item's class (ui.md).</summary>
public class AccessibleNamesTests
{
    [Fact]
    public void The_texts_are_joined_as_a_list_without_the_empty_ones()
    {
        Assert.Equal("10:42 AM, Claude, Saved", AccessibleNames.Join("10:42 AM", "", null, " ", "Claude", "Saved"));
    }

    [Fact]
    public void The_texts_are_joined_with_the_language_s_list_separator()
    {
        CultureInfo before = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ar");
            Assert.Equal("Spam، Disabled", AccessibleNames.Join("Spam", "Disabled"));
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }

    [Fact]
    public void An_AI_activity_row_reads_its_time_app_request_and_result_and_in_the_window_its_kind_too()
    {
        var row = new AiActivityRow(new AiActivity
        {
            Time = new DateTime(2026, 1, 2, 10, 42, 0),
            App = "Claude",
            Kind = AiActivityKind.Proposal,
            Request = "Save loop Spam",
            Result = AiActivityResult.Saved,
        });

        Assert.Equal($"{row.TimeText}, Claude, Save loop Spam, Saved", row.RecentAccessibleName);
        Assert.Equal($"{row.TimeText}, Claude, {row.KindText}, Save loop Spam, Saved", row.AccessibleName);
    }

    [Fact]
    public void A_History_row_reads_its_time_who_and_summary()
    {
        var row = new HistoryRow(new HistoryVersion { Time = new DateTime(2026, 1, 2, 10, 42, 0), By = ItemHistory.You, Summary = "Created" },
            current: true);

        Assert.Equal($"{row.TimeText}, You, Created (current)", row.AccessibleName);
    }

    [Fact]
    public void A_loop_editor_row_reads_its_title_and_summary_and_follows_the_summary()
    {
        var action = new ActionBoxViewModel(new LoopAction { Type = ActionType.Key, KeyVk = 0x41, IntervalValue = 40 });
        string? changed = null;
        action.PropertyChanged += (_, e) => changed = e.PropertyName == nameof(ActionBoxViewModel.AccessibleName) ? e.PropertyName : changed;

        Assert.Equal($"{action.Title}, {action.Summary}", action.AccessibleName);
        action.Times = 3;
        Assert.Equal(nameof(ActionBoxViewModel.AccessibleName), changed);
        Assert.Equal($"{action.Title}, {action.Summary}", action.AccessibleName);
    }

    [Fact]
    public void A_remap_row_reads_its_note_or_its_keys()
    {
        var keys = new RemapItemViewModel(new RemapDefinition { Source = HotkeyBinding.FromKey(0x4A), Target = HotkeyBinding.FromKey(0x43) });
        var named = new RemapItemViewModel(new RemapDefinition
        {
            Source = HotkeyBinding.FromKey(0x4A), Target = HotkeyBinding.FromKey(0x43), Note = "Jump",
        });

        Assert.Equal("J → C", keys.AccessibleName);
        Assert.Equal("Jump", named.AccessibleName);
    }
}
