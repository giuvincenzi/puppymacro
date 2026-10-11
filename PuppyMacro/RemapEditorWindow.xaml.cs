using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PuppyMacro.Models;
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
    private bool _enabled;
    private HotkeyBinding? _source;
    private HotkeyBinding? _target;
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

        string title = existing == null ? "Add remap" : "Edit remap";
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

    /// <summary>Shows <paramref name="source"/> in the Form view (at start, and when leaving the Code view).</summary>
    private void LoadRemap(RemapDefinition source)
    {
        _enabled = source.Enabled;
        _source = source.Source?.Clone();
        _target = source.Target?.Clone();
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
        Note = (NoteBox.Text ?? "").Trim(),
        Enabled = _enabled,
    };

    /// <summary>Why <paramref name="source"/> cannot be remapped for <paramref name="app"/> (null: all apps): a hotkey or another remap.</summary>
    private string? SourceClash(HotkeyBinding source, string? app)
    {
        string? conflict = HotkeyConflicts.FindForRemap(source, app, _settings, _macros);
        if (conflict != null)
            return conflict + " Hotkeys take priority over remaps.";

        var same = _settings.Remaps.FirstOrDefault(r => r.Id != _editingId && source.Overlaps(r.Source)
            && string.Equals(r.AppExe, app, StringComparison.OrdinalIgnoreCase));
        return same != null ? $"{KeyNames.Format(source)} is already remapped {(app == null ? "for all apps" : "for " + app)}." : null;
    }

    private void UpdateLabels()
    {
        SourceField.Value = _source?.Clone();
        TargetField.Value = _target?.Clone();
    }

    private void OnSourceFieldChanged(object? sender, EventArgs e)
    {
        _source = SourceField.Value?.Clone();
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
        Validate();
    }

    private void OnAppScopeChanged(object? sender, EventArgs e) => Validate();

    private string? GetValidationError()
    {
        if (_source is not { IsSet: true } source)
            return "Choose the key, mouse button or combination to remap.";
        if (_target is not { IsSet: true })
            return "Choose what to send instead.";
        if (_target.SameAs(source))
            return "What is sent must be different from what is pressed.";

        if (AppScopeCard.ValidationError is string appError)
            return appError;
        string? app = AppScopeCard.AppExe;
        if (KeyNames.IsPrimaryMouse(source.Vk) && !source.HasModifiers && app == null)
            return "Left and right click can be remapped only for a specific app, so they keep working everywhere else.";

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
