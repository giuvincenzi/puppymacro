using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PuppyMacro.Models;

namespace PuppyMacro.Services.Ai;

/// <summary>How a window opened for an AI request ended.</summary>
public enum AiAnswer
{
    /// <summary>Saved, applied, picked or pressed.</summary>
    Done,
    Cancelled,

    /// <summary>Nobody answered within <see cref="AiTools.AnswerTimeout"/>: the window closed by itself.</summary>
    TimedOut,
}

/// <summary>An editor opened with an AI proposal: how it ended and, when saved, what was saved.</summary>
internal sealed record AiEdited<T>(AiAnswer Answer, T? Saved) where T : class;

/// <summary>A setting an AI app asks to change, as the confirmation shows it ("Stop all hotkey", "Alt + Shift + S", "F12").</summary>
internal sealed record AiSettingChange(string Label, string From, string To);

/// <summary>
/// What the AI tools need from the app: its data and the windows the user answers in. <c>MainWindow</c> implements it
/// on the UI thread (every member is called there, through <see cref="Ui{T}"/>); the unit tests use a fake one.
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
    /// Opens the loop editor with <paramref name="proposal"/>: a new loop when <paramref name="isNew"/>, otherwise a change
    /// of the loop with the same Id. Saving adds or changes the loop as the editor's Save does.
    /// </summary>
    Task<AiEdited<LoopDefinition>> EditLoop(LoopDefinition proposal, bool isNew, string app);

    Task<AiEdited<MacroDefinition>> EditMacro(MacroDefinition proposal, bool isNew, string app);

    Task<AiEdited<RemapDefinition>> EditRemap(RemapDefinition proposal, bool isNew, string app);

    /// <summary>Asks the user to confirm; true when they chose <paramref name="primary"/>.</summary>
    Task<AiAnswer> Confirm(string app, string title, IReadOnlyList<string> lines, string primary);

    /// <summary>Deletes a loop, macro or remap (stopping it first), as its card's Delete does after the confirmation.</summary>
    void Delete(Guid id);

    /// <summary>Enables or disables a loop, macro or remap, as its card's switch does.</summary>
    void SetEnabled(Guid id, bool enabled);

    /// <summary>Applies settings already checked by the tools, and saves.</summary>
    void ApplySettings(AiSettingsValues values);

    Task<(AiAnswer Answer, int X, int Y)> PickPoint(string app, string reason);

    Task<(AiAnswer Answer, string? Exe)> PickApp(string app, string reason);

    Task<(AiAnswer Answer, HotkeyBinding? Keys)> CaptureKeys(string app, string reason);

    void StopAll();

    IReadOnlyList<string> RunningApps();
}
