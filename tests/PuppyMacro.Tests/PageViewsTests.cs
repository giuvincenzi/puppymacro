using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Data;
using PuppyMacro.Views;
using Xunit;

namespace PuppyMacro.Tests;

public class PageViewsTests
{
    private sealed record Item(string Name, string? App)
    {
        public string GroupName => App ?? "All apps";
    }

    private static PageViews Views(ObservableCollection<Item> items, string search = "") =>
        new(items, item => ((Item)item).Name.Contains(search), item => ((Item)item).App);

    [Fact]
    public void Groups_show_only_when_the_items_work_in_more_than_one_place()
    {
        Assert.False(PageViews.NeedsGroups(new string?[] { null, null }));
        Assert.False(PageViews.NeedsGroups(new[] { "game.exe", "GAME.exe" }));
        Assert.False(PageViews.NeedsGroups(new string?[0]));
        Assert.True(PageViews.NeedsGroups(new[] { null, "game.exe" }));
        Assert.True(PageViews.NeedsGroups(new[] { "game.exe", "notepad.exe" }));
    }

    [Fact]
    public void List_and_grid_have_all_apps_first_then_each_app_by_name_in_the_users_order_while_the_table_keeps_it()
    {
        var items = new ObservableCollection<Item>
        {
            new("Farm", "game.exe"),
            new("Chat", "notepad.exe"),
            new("Click", null),
            new("Inventory", "Game.exe"),
            new("Spam", null),
        };
        PageViews views = Views(items);

        Assert.True(views.IsGrouped);
        Assert.Equal(new[] { "Click", "Spam", "Farm", "Inventory", "Chat" }, views.Grouped.Cast<Item>().Select(i => i.Name));
        // Apps compared without case, as everywhere: Game.exe and game.exe are one group.
        Assert.Equal(new[] { "All apps", "game.exe", "notepad.exe" },
            views.Grouped.Groups.Cast<CollectionViewGroup>().Select(g => (string)g.Name));
        Assert.Equal(new[] { "Farm", "Chat", "Click", "Inventory", "Spam" }, views.Table.Cast<Item>().Select(i => i.Name));
        Assert.Equal(5, views.Count);

        // Moving an item (drag, Move up) keeps its place inside its group.
        items.Move(3, 0);
        views.Refresh();
        Assert.Equal(new[] { "Click", "Spam", "Inventory", "Farm", "Chat" }, views.Grouped.Cast<Item>().Select(i => i.Name));
    }

    [Fact]
    public void Groups_come_and_go_with_the_apps_of_the_items_and_the_search_filters_both_views()
    {
        var items = new ObservableCollection<Item> { new("Farm", null), new("Spam", null) };
        PageViews views = Views(items);
        Assert.False(views.IsGrouped);
        Assert.Equal(new[] { "Farm", "Spam" }, views.Grouped.Cast<Item>().Select(i => i.Name));

        items.Add(new Item("Chat", "notepad.exe"));
        views.Refresh();
        Assert.True(views.IsGrouped);

        items.RemoveAt(2);
        views.Refresh();
        Assert.False(views.IsGrouped);
        Assert.Null(views.Grouped.CustomSort);

        PageViews searched = Views(new ObservableCollection<Item> { new("Farm", null), new("Spam", "game.exe") }, search: "Sp");
        Assert.Equal(1, searched.Count);
        Assert.Equal("Spam", Assert.Single(searched.Grouped.Cast<Item>()).Name);
        Assert.Equal("Spam", Assert.Single(searched.Table.Cast<Item>()).Name);
    }
}
