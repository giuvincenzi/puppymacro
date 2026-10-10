using System;

namespace PuppyMacro.Views;

/// <summary>What the lists (drag and drop) and the overlay panel need from a loop or a macro.</summary>
public interface IListItem
{
    Guid Id { get; }
    string Name { get; }
    string HotkeyText { get; }
    bool HasHotkey { get; }
    string Status { get; }
    bool IsRunning { get; set; }
    bool IsItemEnabled { get; }
    bool IsHoldMode { get; }
    Models.FloatingButton FloatingButton { get; }

    /// <summary>The app it works in (.exe name); null for all apps.</summary>
    string? AppExe { get; }

    /// <summary>It works only in one app: the cards show <see cref="AppExe"/>.</summary>
    bool HasApp { get; }
    bool DropBefore { get; set; }
    bool DropAfter { get; set; }
    bool IsDragging { get; set; }
}
