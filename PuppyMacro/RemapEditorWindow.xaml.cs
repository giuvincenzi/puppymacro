using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PuppyMacro.Models;
using PuppyMacro.Resources;
using PuppyMacro.Services;
using PuppyMacro.Views;

namespace PuppyMacro;

/// <summary>Adds or edits a remap. On Save, <see cref="Result"/> holds the edited copy.</summary>
public partial class RemapEditorWindow
{
    private readonly LoopEngine _engine;
    private readonly AppSettings _settings;
    private readonly MacroLibrary _macros;
    private readonly Guid _editingId;
    private readonly Guid _itemId;
    private readonly CodeViewSwitch<RemapDefinition> _code;
    private HotkeyBinding? _source;
    private HotkeyBinding? _target;

    // Keep modifiers held as saved: the switch shows it only while it applies (both keys with modifiers), else off.
    private bool _keepModifiers = true;
    private bool _ready;

    internal RemapEditorWindow(LoopEngine engine, AppSettings settings, MacroLibrary macros, RemapDefinition? existing)
    {
        InitializeComponent();
        WindowFit.Apply(this);
        _engine = engine;
        _settings = settings;
        _macros = macros;

        var source = existing?.Clone() ?? new RemapDefinition();
        _editingId = existing?.Id ?? Guid.Empty;
        _itemId = existing?.Id ?? Guid.NewGuid();
        source.Id = _itemId;

        // "Edit remap - Caps Lock to Esc": its note, or its keys when it has none.
        string title = existing == null ? Strings.RemapEditor_AddTitle
            : string.IsNullOrWhiteSpace(existing.Note)
                ? string.Format(CultureInfo.CurrentCulture, Strings.RemapEditor_EditKeysTitle,
                    KeyNames.Format(existing.Source), KeyNames.Format(existing.Target))
                : string.Format(CultureInfo.CurrentCulture, Strings.RemapEditor_EditNamedTitle, existing.Note);
        Title = title;

        LoadRemap(source);

        _code = new CodeViewSwitch<RemapDefinition>(ViewBar, CodeView, FormBody, CancelButton, ErrorText, SaveButton,
            CodeSchema.ForRemap(_itemId), FormRemap, RemapJson.Serialize,
            (string text, out List<CodeProblem> problems) => RemapJson.Parse(text, _itemId, SourceClash, out problems),
            LoadRemap, Validate);
        _code.Opening += () =>
        {
            _engine.CancelCapture();
            UpdateLabels();
        };
        _ready = true;
        Validate();
        KeyCaptureField.SetEngine(this, _engine); // the key fields wait for keys through it
        Closed += (_, _) => _engine.CancelCapture();
    }

    public RemapDefinition? Result { get; private set; }

    /// <summary>Offers the History view of a remap saved before.</summary>
    internal void EnableHistory(ItemHistory history)
    {
        if (_editingId == Guid.Empty)
            return;
        _code.EnableHistory(HistoryPanel, () => HistoryPanel.Load(history.Versions(_itemId)));
        HistoryPanel.RestoreRequested += version =>
        {
            if (HistoryRestore.Read<RemapDefinition>(version, _itemId) is not { } remap)
                return;
            LoadRemap(remap);
            _code.ShowFormView();
            HistoryRestore.Notice(NoticeBar, version);
        };
    }

    /// <summary>Shows <paramref name="source"/> in the Form view (at start, and when leaving the Code view).</summary>
    private void LoadRemap(RemapDefinition source)
    {
        EnabledSwitch.IsOn = source.Enabled;
        _source = source.Source?.Clone();
        _target = source.Target?.Clone();
        _keepModifiers = source.KeepModifiersHeld;
        AppScopeCard.Load(source.AppExe);
        NoteBox.Text = source.Note;
        UpdateLabels();
        Validate();
    }

    /// <summary>The remap as the Form view's fields describe it, also when they are not valid yet.</summary>
    private RemapDefinition FormRemap() => new()
    {
        Id = _itemId,
        Source = _source?.Clone(),
        Target = _target?.Clone(),
        AppExe = AppScopeCard.AppExe,
        KeepModifiersHeld = _keepModifiers,
        Note = (NoteBox.Text ?? "").Trim(),
        Enabled = EnabledSwitch.IsOn,
    };

    /// <summary>Why <paramref name="source"/> cannot be remapped for <paramref name="app"/> (null: all apps): a hotkey or another remap.</summary>
    private string? SourceClash(HotkeyBinding source, string? app) =>
        HotkeyConflicts.RemapClash(source, app, _settings, _macros, _editingId);

    private void UpdateLabels()
    {
        SourceField.Value = _source?.Clone();
        TargetField.Value = _target?.Clone();
        UpdateKeepModifiers();
    }

    /// <summary>
    /// Keep modifiers held: on or off as saved while both keys have Ctrl, Alt, Shift or Win; otherwise off and greyed, its
    /// description saying when it applies. The saved value stays, for when both have them again.
    /// </summary>
    private void UpdateKeepModifiers()
    {
        bool applies = _source?.HasModifiers == true && _target?.HasModifiers == true;
        // The card is disabled before the switch changes, so the change is not taken as the user's (OnKeepModifiersToggled).
        KeepModifiersCard.IsEnabled = false;
        KeepModifiersSwitch.IsOn = applies && _keepModifiers;
        KeepModifiersCard.IsEnabled = applies;
        KeepModifiersCard.Description = applies ? Strings.RemapEditor_KeepModifiersDescription : Strings.RemapEditor_KeepModifiersUnavailable;
    }

    private void OnKeepModifiersToggled(object sender, RoutedEventArgs e)
    {
        if (KeepModifiersCard.IsEnabled)
            _keepModifiers = KeepModifiersSwitch.IsOn;
    }

    private void OnSourceFieldChanged(object? sender, EventArgs e)
    {
        _source = SourceField.Value?.Clone();
        UpdateKeepModifiers();
        Validate();
    }

    /// <summary>A side chosen in a key field's menu that cannot be kept.</summary>
    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void OnTargetFieldChanged(object? sender, EventArgs e)
    {
        _target = TargetField.Value?.Clone();
        UpdateKeepModifiers();
        Validate();
    }

    private void OnAppScopeChanged(object? sender, EventArgs e) => Validate();

    private string? GetValidationError()
    {
        if (_source is not { IsSet: true } source)
            return Strings.RemapEditor_NoSourceError;
        if (_target is not { IsSet: true })
            return Strings.RemapEditor_NoTargetError;
        if (_target.SameAs(source))
            return Strings.RemapEditor_SameKeysError;

        if (AppScopeCard.ValidationError is string appError)
        {
            // Turning off Specific app would not help when the keys are taken for all apps: say by what.
            return SourceClash(source, null) is string clash
                ? string.Format(CultureInfo.CurrentCulture, Strings.AppScope_NoAppKeyUsed, clash)
                : appError;
        }
        string? app = AppScopeCard.AppExe;
        if (KeyNames.IsPrimaryMouse(source.Vk) && !source.HasModifiers && app == null)
            return Strings.RemapEditor_PrimaryMouseError;

        return SourceClash(source, app);
    }

    private void Validate()
    {
        if (!_ready || _code.ValidateCode())
            return;
        string? error = GetValidationError();
        ErrorText.Text = error ?? "";
        ErrorText.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
        SaveButton.IsEnabled = error == null;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_code.IsCode)
        {
            if (_code.Read() is { } fromCode)
            {
                Result = fromCode;
                DialogResult = true;
            }
            return;
        }
        if (GetValidationError() != null)
        {
            Validate();
            return;
        }
        Result = FormRemap();
        DialogResult = true;
    }

    /// <summary>Form view: closes without saving. Code view: Discard changes (see <see cref="CodeViewSwitch{T}"/>).</summary>
    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_code.IsCode)
            _code.Discard();
        else
            DialogResult = false;
    }
}
