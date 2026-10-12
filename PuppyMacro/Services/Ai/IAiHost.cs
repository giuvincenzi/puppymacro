using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PuppyMacro.Models;

namespace PuppyMacro.Services.Ai;

/// <summary>How a window opened for an AI request ended.</summary>
public enum AiAnswer
{
    /// <summary>Confirmed, picked or pressed.</summary>
    Done,
    Cancelled,

    /// <summary>Nobody used PuppyMacro for <see cref="AiTools.AnswerTimeout"/>: the window closed by itself.</summary>
    TimedOut,
}

/// <summary>A setting an AI app asks to change, as the confirmation shows it ("Stop all hotkey", "Alt + Shift + S", "F12").</summary>
internal sealed record AiSettingChange(string Label, string From, string To);

/// <summary>
/// What the AI tools need from the app: its data, the changes it makes as the user's own Save would (recorded in the
/// History by the AI app's name) and the windows the user answers in. <c>MainWindow</c> implements it on the UI thread
/// (every member is called there, through <see cref="Ui{T}"/>); the unit tests use a fake one.
/// </summary>
internal interface IAiHost
{
    AppSettings Settings { get; }

    MacroLibrary Macros { get; }

    bool IsRunning(Guid id);

    bool AnyRunning { get; }

    bool OverlayModeActive { get; }

    /// <summary>The user has an editor or a dialog of PuppyMacro open: an AI window would open on top of it.</summary>
    bool UserIsEditing { get; }

    /// <summary>Runs <paramref name="action"/> on the UI thread.</summary>
    Task<T> Ui<T>(Func<Task<T>> action);

    /// <summary>
    /// Adds <paramref name="item"/> (a loop, macro or remap) or replaces the one with its Id, as the editor's Save does (a
    /// running one stops first), recorded as <paramref name="app"/>'s.
    /// </summary>
    void Save(object item, string app);

    /// <summary>Deletes a loop, macro or remap (stopping it first), as its card's Delete does.</summary>
    void Delete(Guid id, string app);

    /// <summary>Enables or disables a loop, macro or remap, as its card's switch does.</summary>
    void SetEnabled(Guid id, bool enabled, string app);

    /// <summary>Applies settings already checked by the tools, and saves.</summary>
    void ApplySettings(AiSettingsValues values);

    /// <summary>Asks the user to confirm a change (Ask before … on the AI page).</summary>
    Task<AiAnswer> Confirm(string app, string title, IReadOnlyList<string> lines, string primary);

    /// <summary>
    /// The window before an interaction: the AI's title, description and numbered steps, centered on the screen and always
    /// on top; Enter or Start when the user is ready, Esc cancels.
    /// </summary>
    Task<AiAnswer> Ready(string app, AiAsk ask);

    /// <summary>The user clicks one point per label on the screen, in order (physical pixels), the current label next to the cursor.</summary>
    Task<(AiAnswer Answer, List<(int X, int Y)> Points)> PickPoints(string app, string message, IReadOnlyList<string> labels);

    Task<(AiAnswer Answer, string? Exe)> PickApp(string app, string message);

    /// <summary>The user presses a key or combination in a window that shows the AI's title, description and steps.</summary>
    Task<(AiAnswer Answer, HotkeyBinding? Keys)> CaptureKeys(string app, AiAsk ask);

    /// <summary>
    /// Turns on the floating button of a Toggle loop or macro (and Show in overlay), then opens the placement screen; Enter
    /// saves its position, recorded as <paramref name="app"/>'s. Cancelled leaves the item as it was.
    /// </summary>
    Task<AiAnswer> PlaceFloatingButton(Guid id, string app);

    /// <summary>Opens the item's editor with the AI's <paramref name="title"/> and <paramref name="description"/> on top: Done when the user saved.</summary>
    Task<AiAnswer> OpenItem(Guid id, string app, string title, string description);

    /// <summary>
    /// Opens Import (in front of the other apps) with <paramref name="text"/>, a .puppyitems file's text, or for the user to
    /// pick a file when null; Done with the items the user added (new Ids, disabled), recorded as <paramref name="app"/>'s.
    /// </summary>
    Task<(AiAnswer Answer, List<object> Items)> ShowImport(string app, string? text);

    /// <summary>Opens Export (in front of the other apps) with <paramref name="ids"/> checked; Done with what the user saved and where.</summary>
    Task<(AiAnswer Answer, List<object> Items, string? File)> ShowExport(string app, IReadOnlyCollection<Guid> ids);

    void StopAll();

    IReadOnlyList<string> RunningApps();
}

/// <summary>What an AI app asks the user to do: a short title, why (description) and the steps, shown numbered.</summary>
public sealed record AiAsk(string Title, string Description, IReadOnlyList<string> Steps);
