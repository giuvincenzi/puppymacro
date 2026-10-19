using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using OpenCvSharp;
using PuppyMacro.Models;
using PuppyMacro.Services.Screen;

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
internal sealed record AiSettingChange(Phrase Name, PhraseArg From, PhraseArg To)
{
    /// <summary>The setting's name in English, for the AI app.</summary>
    public string Label => Name.English;

    /// <summary>"Theme: Dark → Light", shown and kept in the language of the moment.</summary>
    public Phrase Line => Phrase.Of("Ai_SettingChange", Name, From, To);

    /// <summary>The new value in English, for the AI app.</summary>
    public string ToEnglish => To.TryRender(System.Globalization.CultureInfo.InvariantCulture) ?? "";
}

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

    /// <summary>
    /// What the user has open in PuppyMacro, an editor or a dialog (an AI window would open on top of it), said in English for
    /// the AI app ("the macro editor of \"Salvage\""), or null when nothing is open.
    /// </summary>
    string? UserHasOpen { get; }

    /// <summary>Runs <paramref name="action"/> on the UI thread.</summary>
    Task<T> Ui<T>(Func<Task<T>> action);

    /// <summary>
    /// Adds <paramref name="item"/> (a loop, macro or remap) or replaces the one with its Id, as the editor's Save does (a
    /// running one stops first), recorded as <paramref name="app"/>'s.
    /// </summary>
    void Save(object item, string app);

    /// <summary>Adds the new Library collections of a file being imported, as <paramref name="app"/>'s; why not (in English,
    /// for the AI app), or null.</summary>
    string? AddCollections(IReadOnlyList<LibraryCollection> collections, string app);

    /// <summary>
    /// Saves a Library component as <paramref name="app"/>'s: a new one into <paramref name="created"/> (a new collection)
    /// or the collection its Id is in, with the macros and components whose calls a new signature changed
    /// (<c>SignatureUpdate</c>), all together; why not (in English, for the AI app), or null.
    /// </summary>
    string? SaveComponent(LibraryComponent component, LibraryCollection? created, string app,
        IReadOnlyList<MacroDefinition>? macros = null, IReadOnlyList<LibraryComponent>? callers = null);

    /// <summary>
    /// Deletes a Library component as <paramref name="app"/>'s, as the Library's Delete does (refused while a macro or
    /// another component uses it); why not (in English, for the AI app), or null.
    /// </summary>
    string? DeleteComponent(Guid id, string app);

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

    /// <summary>
    /// The user picks on a still picture of the screen, as the macros' conditions see it: a pixel and its color, an image to
    /// look for (added to the images of the conditions), or a search area.
    /// </summary>
    Task<(AiAnswer Answer, AiScreenPick? Pick)> PickOnScreen(string app, string message, AiScreenPickKind kind);

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

    /// <summary>Checks a condition on the screen now, as the condition window's Now on the screen (off the UI thread).</summary>
    Task<ConditionReport> CheckCondition(MacroCondition condition);

    /// <summary>A picture of every monitor now, as the macros' conditions see the screen (desktop bounds, physical pixels).</summary>
    Task<List<(Rect Bounds, Mat Picture)>> TakeScreen();

    /// <summary>
    /// PuppyMacro's recorder, started at once after a countdown (the ready window was the prompt): the actions recorded
    /// until the user presses Stop, and the screen at each of the user's key presses and clicks. Cancelled during the
    /// countdown: nothing.
    /// </summary>
    Task<(AiAnswer Answer, AiRecording? Recording)> Record(string app, bool mouseMovement);

    /// <summary>
    /// Runs a saved macro as the user's Play would, with its log in <paramref name="folder"/> (always), until it ends, Stop
    /// all, or <paramref name="maxSeconds"/>. Null when the macro can't run (empty, or another item running).
    /// </summary>
    Task<AiTestRun?> TestMacro(Guid id, string app, double maxSeconds, string folder);

    /// <summary>What the AI app asked on the user's screen is over: a window says the user can go back to <paramref name="app"/>.</summary>
    void Finished(string app);
}

/// <summary>A recording for an AI app: the actions, and the screen at each of the user's inputs (the caller disposes them).</summary>
internal sealed record AiRecording(List<MacroAction> Actions, List<AiRecordedInput> Inputs);

/// <summary>
/// One of the user's inputs while recording: what it was ("Click Left", "Key down F22"), whether it is a click, where
/// (physical pixels; a key: the cursor), when (ms from the start), and each monitor as a picture at that moment.
/// </summary>
internal sealed record AiRecordedInput(string What, bool Click, int X, int Y, double TimeMs, List<(Rect Bounds, Mat Picture)> Screen);

/// <summary>A test run's end: why it ended and its log's folder.</summary>
internal sealed record AiTestRun(string EndReason, string LogFolder);

/// <summary>What an AI app asks the user to do: a short title, why (description) and the steps, shown numbered.</summary>
public sealed record AiAsk(string Title, string Description, IReadOnlyList<string> Steps);

/// <summary>What <see cref="IAiHost.PickOnScreen"/> asks for.</summary>
internal enum AiScreenPickKind { Pixel, Image, Area }

/// <summary>
/// A pick on the screen (physical pixels): a pixel (X, Y, its Color, size 1 × 1), an image (where it was taken, its size and
/// ImageId in the store) or an area.
/// </summary>
internal sealed record AiScreenPick(int X, int Y, int Width, int Height, string? Color = null, string? ImageId = null);
