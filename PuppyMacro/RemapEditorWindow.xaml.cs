using System;
using System.Diagnostics;
using System.IO;
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
    private readonly bool _wasEnabled;
    private int _sourceVk;
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
        _wasEnabled = source.Enabled;
        _sourceVk = source.SourceVk;
        _target = source.Target?.Clone();

        string title = existing == null ? "Add remap" : "Edit remap";
        Title = title;
        EditorTitleBar.Title = title;

        AllAppsRadio.IsChecked = source.AppExe == null;
        OneAppRadio.IsChecked = source.AppExe != null;
        AppBox.Text = source.AppExe ?? "";
        AppPanel.Visibility = source.AppExe != null ? Visibility.Visible : Visibility.Collapsed;
        NoteBox.Text = source.Note;
        AppBox.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler((_, _) => Validate()));

        UpdateLabels();
        _ready = true;
        Validate();
        Closed += (_, _) => _engine.CancelCapture();
    }

    public RemapDefinition? Result { get; private set; }

    private void UpdateLabels()
    {
        SourceCaptureText.Visibility = Visibility.Collapsed;
        TargetCaptureText.Visibility = Visibility.Collapsed;
        SourceCaps.Visibility = Visibility.Visible;
        TargetCaps.Visibility = Visibility.Visible;
        SourceCaps.ItemsSource = new[] { _sourceVk == 0 ? "Not set" : KeyNames.Get(_sourceVk) };
        TargetCaps.ItemsSource = _target is { IsSet: true } ? KeyNames.Parts(_target) : new System.Collections.Generic.List<string> { "Not set" };
    }

    private void OnChangeSourceClick(object sender, RoutedEventArgs e)
    {
        _engine.CancelCapture();
        SourceCaps.Visibility = Visibility.Collapsed;
        SourceCaptureText.Visibility = Visibility.Visible;
        _engine.BeginCapture(
            binding =>
            {
                _sourceVk = binding.Vk; // the source is a single key or button
                UpdateLabels();
                Validate();
            },
            UpdateLabels,
            allowPrimaryMouse: true);
    }

    private void OnChangeTargetClick(object sender, RoutedEventArgs e)
    {
        _engine.CancelCapture();
        TargetCaps.Visibility = Visibility.Collapsed;
        TargetCaptureText.Visibility = Visibility.Visible;
        _engine.BeginCapture(
            binding =>
            {
                _target = binding;
                UpdateLabels();
                Validate();
            },
            UpdateLabels,
            allowPrimaryMouse: true);
    }

    private void OnScopeChanged(object sender, RoutedEventArgs e)
    {
        if (AppPanel == null)
            return;
        AppPanel.Visibility = OneAppRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        Validate();
    }

    /// <summary>Lists the .exe names of the apps that have a window open.</summary>
    private void OnAppDropDownOpened(object? sender, EventArgs e)
    {
        string current = AppBox.Text;
        var names = Process.GetProcesses()
            .Where(p => { try { return p.MainWindowHandle != IntPtr.Zero; } catch { return false; } })
            .Select(p => p.ProcessName + ".exe")
            .Where(n => !n.Equals("PuppyMacro.exe", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        AppBox.ItemsSource = names;
        AppBox.Text = current;
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the app",
            Filter = "Programs (*.exe)|*.exe",
        };
        if (dialog.ShowDialog(this) == true)
            AppBox.Text = Path.GetFileName(dialog.FileName);
    }

    private string? SelectedApp
    {
        get
        {
            if (OneAppRadio.IsChecked != true)
                return null;
            string name = (AppBox.Text ?? "").Trim();
            if (name.Length == 0)
                return "";
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
        }
    }

    private string? GetValidationError()
    {
        if (_sourceVk == 0)
            return "Choose the key or mouse button to remap.";
        if (_target is not { IsSet: true })
            return "Choose what to send instead.";
        if (!_target.HasModifiers && _target.Vk == _sourceVk)
            return "The key to send must be different from the key pressed.";

        string? app = SelectedApp;
        if (app == "")
            return "Choose the app, or select All apps.";
        if (KeyNames.IsPrimaryMouse(_sourceVk) && app == null)
            return "Left and right click can be remapped only for a specific app, so they keep working everywhere else.";

        string? conflict = HotkeyConflicts.Find(HotkeyBinding.FromKey(_sourceVk), _settings, _macros);
        if (conflict != null)
            return conflict + " Hotkeys take priority over remaps.";

        var same = _settings.Remaps.FirstOrDefault(r => r.Id != _editingId && r.SourceVk == _sourceVk
            && string.Equals(r.AppExe, app, StringComparison.OrdinalIgnoreCase));
        if (same != null)
            return $"{KeyNames.Get(_sourceVk)} is already remapped {(app == null ? "for all apps" : "for " + app)}.";
        return null;
    }

    private void Validate()
    {
        if (!_ready)
            return;
        string? error = GetValidationError();
        ErrorText.Text = error ?? "";
        ErrorText.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
        SaveButton.IsEnabled = error == null;
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (GetValidationError() != null)
        {
            Validate();
            return;
        }
        Result = new RemapDefinition
        {
            Id = _editingId == Guid.Empty ? Guid.NewGuid() : _editingId,
            SourceVk = _sourceVk,
            Target = _target!.Clone(),
            AppExe = SelectedApp,
            Note = (NoteBox.Text ?? "").Trim(),
            Enabled = _wasEnabled,
        };
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
