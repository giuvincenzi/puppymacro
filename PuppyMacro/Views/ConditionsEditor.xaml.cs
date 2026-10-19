using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PuppyMacro.Models;
using PuppyMacro.Resources;
using PuppyMacro.Services;
using PuppyMacro.Services.Screen;

namespace PuppyMacro.Views;

/// <summary>
/// An If's conditions (DataContext: <see cref="IfViewModel"/>): adds and removes them, asks the screen for a pixel, an
/// image or an area, and checks each condition on the screen a moment after it changes, saying what PuppyMacro sees.
/// </summary>
public partial class ConditionsEditor : UserControl
{
    private readonly DispatcherTimer _checkLater = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly System.Collections.Generic.HashSet<ConditionViewModel> _toCheck = new();
    private bool _checking;

    public ConditionsEditor()
    {
        InitializeComponent();
        _checkLater.Tick += (_, _) =>
        {
            _checkLater.Stop();
            var conditions = _toCheck.ToList();
            _toCheck.Clear();
            _ = CheckAsync(conditions);
        };
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is IfViewModel old)
                old.ConditionChanged -= CheckSoon;
            if (e.NewValue is IfViewModel model)
            {
                model.ConditionChanged += CheckSoon;
                // A Component condition needs the Library.
                AddComponentItem.Visibility = model.Context.Macros != null ? Visibility.Visible : Visibility.Collapsed;
            }
        };
        Unloaded += (_, _) => _checkLater.Stop();
    }

    private IfViewModel? Model => DataContext as IfViewModel;

    /// <summary>Where images are searched (Settings › Screen recognition).</summary>
    internal RecognitionDevice Device { get; set; }

    /// <summary>Raised when the user's pick or a removal changes the conditions (the window validates again).</summary>
    public event Action? Changed;

    // ================= Adding and removing =================

    private void OnAddPixelClick(object sender, RoutedEventArgs e) => Add(ConditionKind.Pixel);

    private void OnAddImageClick(object sender, RoutedEventArgs e) => Add(ConditionKind.Image);

    private void OnAddTextClick(object sender, RoutedEventArgs e) => Add(ConditionKind.Text);

    /// <summary>Component: the Library's components that say what they find, to pick from; each chosen one is a condition.</summary>
    private void OnAddComponentClick(object sender, RoutedEventArgs e)
    {
        if (Model is not { Context.Macros: { } macros } model)
            return;
        foreach (var component in PickComponents(macros))
        {
            var condition = Add(ConditionKind.Component, component.Name);
            condition?.ChooseComponent(component);
        }
    }

    /// <summary>Choose: another component for a Component condition.</summary>
    private void OnChooseComponentClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ConditionViewModel condition || Model is not { Context.Macros: { } macros })
            return;
        if (PickComponents(macros).FirstOrDefault() is { } component)
        {
            condition.ChooseComponent(component);
            Changed?.Invoke();
        }
    }

    /// <summary>The Library to pick from, only the components that say what they find.</summary>
    private System.Collections.Generic.List<LibraryComponent> PickComponents(MacroLibrary macros)
    {
        var picker = new LibraryWindow(macros, LibraryWindow.Mode.Pick, onlyConditions: true) { Owner = Window.GetWindow(this) };
        if (picker.ShowDialog() != true)
            return new System.Collections.Generic.List<LibraryComponent>();
        return picker.Picked.Select(id => macros.Library.Find(id)?.Component).OfType<LibraryComponent>().ToList();
    }

    private ConditionViewModel? Add(ConditionKind kind, string? name = null)
    {
        if (Model is not { } model)
            return null;
        foreach (var other in model.Conditions)
            other.IsExpanded = false;
        var condition = new ConditionViewModel(ConditionViewModel.New(kind), model.Store, model.Context);
        if (kind != ConditionKind.Pixel)
            condition.Name = NewName(model, kind, name);
        model.Add(condition);
        Changed?.Invoke();
        return condition;
    }

    /// <summary>"Image 1", "Text 2" (a Component condition: its component's name, "Rare item 2"): a name no other condition here has.</summary>
    private static string NewName(IfViewModel model, ConditionKind kind, string? baseName = null)
    {
        bool Taken(string name) =>
            model.Conditions.Any(c => string.Equals(c.Name.Trim(), name, StringComparison.CurrentCultureIgnoreCase))
            || model.OtherNames.Contains(name, StringComparer.CurrentCultureIgnoreCase);
        if (baseName != null && !Taken(baseName))
            return baseName;
        string kindName = baseName ?? ConditionViewModel.KindShortName(kind);
        for (int n = baseName == null ? 1 : 2; ; n++)
        {
            string name = string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Condition_NewName, kindName, n);
            if (!model.Conditions.Any(c => string.Equals(c.Name.Trim(), name, StringComparison.CurrentCultureIgnoreCase))
                && !model.OtherNames.Contains(name, StringComparer.CurrentCultureIgnoreCase))
                return name;
        }
    }

    private void OnRemoveConditionClick(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model || (sender as FrameworkElement)?.Tag is not ConditionViewModel condition)
            return;
        // An action in Then goes where it was found: removing it would leave that action without a place.
        if (model.UsedByThen.Contains(condition.Id))
        {
            MessageBox.Show(Window.GetWindow(this), string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Condition_UsedByThen, condition.Name.Trim()),
                Strings.Condition_Remove, MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK, App.MessageBoxOptions);
            return;
        }
        model.Conditions.Remove(condition);
        Changed?.Invoke();
    }

    private void OnNotChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox { SelectedIndex: 1, Tag: ConditionViewModel condition } || Model is not { } model)
            return;
        // An action in Then goes where it was found: true when it is not there, it would find no place for that action.
        if (!model.UsedByThen.Contains(condition.Id))
            return;
        Dispatcher.BeginInvoke(() => condition.Not = false);
        MessageBox.Show(Window.GetWindow(this), string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Condition_UsedByThenNot, condition.Name.Trim()),
            Strings.Condition_TrueWhen, MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK, App.MessageBoxOptions);
    }

    private void OnRemoveImageClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ConditionImageViewModel image || Model is not { } model)
            return;
        foreach (var condition in model.Conditions)
            if (condition.Images.Remove(image))
                CheckSoon(condition);
        Changed?.Invoke();
    }

    // ================= On the screen =================

    private void OnPickPixelClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ConditionViewModel condition || Window.GetWindow(this) is not { } window)
            return;
        var pick = ScreenPickWindow.Ask(window, ScreenPickMode.Pixel, out string? error);
        if (error != null)
            ShowError(error, Strings.ScreenPick_PixelTitle);
        if (pick?.PickedPixel is not { } pixel)
            return;
        condition.X = pixel.X;
        condition.Y = pixel.Y;
        condition.Color = pixel.Color;
    }

    private void OnTakeImageClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ConditionViewModel condition || Window.GetWindow(this) is not { } window
            || Model?.Store is not { } store)
            return;
        var pick = ScreenPickWindow.Ask(window, ScreenPickMode.Image, out string? error);
        if (error != null)
            ShowError(error, Strings.ScreenPick_ImageTitle);
        if (pick?.PickedArea is not { Png: { } png } taken)
            return;
        // The image goes in the store at once (the Code view checks it is there), held for the macro editor until it closes:
        // then one no saved macro uses is deleted at the next save or start.
        string id = store.Add(png);
        store.HoldAlso(Window.GetWindow(this) is { } own ? own.Owner ?? own : this, id);
        condition.Images.Add(new ConditionImageViewModel(new ConditionImage { Id = id, X = taken.Area.X, Y = taken.Area.Y }, store));
        CheckSoon(condition);
        Changed?.Invoke();
    }

    private void OnDrawAreaClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ConditionViewModel condition || Window.GetWindow(this) is not { } window)
            return;
        var pick = ScreenPickWindow.Ask(window, ScreenPickMode.Area, out string? error);
        if (error != null)
            ShowError(error, Strings.ScreenPick_AreaTitle);
        if (pick?.PickedArea is { } area)
        {
            condition.SetArea(area.Area);
            CheckSoon(condition);
        }
    }

    private void ShowError(string message, string title) =>
        MessageBox.Show(Window.GetWindow(this), message, title, MessageBoxButton.OK, MessageBoxImage.Warning,
            MessageBoxResult.OK, App.MessageBoxOptions);

    // ================= Sliders (ValueSlider has no binding) =================

    private void OnSliderLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ValueSlider { DataContext: ConditionViewModel condition } slider)
            return;
        slider.Format = v => string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Slider_Percent, (int)Math.Round(v));
        slider.Value = (string)slider.Tag == nameof(ConditionViewModel.Tolerance) ? condition.Tolerance : condition.Similarity;
    }

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (sender is not ValueSlider { DataContext: ConditionViewModel condition, IsLoaded: true } slider)
            return;
        if ((string)slider.Tag == nameof(ConditionViewModel.Tolerance))
            condition.Tolerance = e.NewValue;
        else
            condition.Similarity = e.NewValue;
    }

    // ================= Checking on the screen =================

    private void CheckSoon(ConditionViewModel condition)
    {
        _toCheck.Add(condition);
        _checkLater.Stop();
        _checkLater.Start();
    }

    /// <summary>Check again: this condition, on the screen as it is now.</summary>
    private void OnCheckAgainClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is ConditionViewModel condition)
            _ = CheckAsync(new System.Collections.Generic.List<ConditionViewModel> { condition });
    }

    /// <summary>Checks every condition now (Test).</summary>
    public Task CheckAllAsync() => Model is { } model ? CheckAsync(model.Conditions.ToList()) : Task.CompletedTask;

    /// <summary>
    /// Checks conditions on the screen, one after the other, off the UI thread (text recognition can take a moment). The
    /// conditions that cannot be checked yet (no pixel, image or text) show nothing.
    /// </summary>
    private async Task CheckAsync(System.Collections.Generic.List<ConditionViewModel> conditions)
    {
        if (_checking || Model is not { } model)
        {
            foreach (var condition in conditions)
                _toCheck.Add(condition);
            return;
        }
        _checking = true;
        try
        {
            var device = Device;
            foreach (var condition in conditions.Where(model.Conditions.Contains))
            {
                if (!condition.CanCheck)
                {
                    condition.ClearReport();
                    continue;
                }
                var toCheck = condition.ToModel();
                bool? effectiveNot = null;
                // A Component condition is checked as the condition its component returns, with the call's arguments (inside a
                // component being edited, its parameters' Examples), as the engine checks it; a condition with bound fields (a
                // pixel at a cell of a Grid, at a Point parameter) with those Examples in them.
                if (toCheck.Kind == ConditionKind.Component || toCheck.Bind is { Count: > 0 })
                {
                    bool component = toCheck.Kind == ConditionKind.Component;
                    var programs = Services.Components.ComponentPrograms.Prepare(model.Context.Macros?.Library.AllComponents ?? Enumerable.Empty<LibraryComponent>());
                    var examples = Services.Components.FrameArguments.Examples(model.Context.Parameters ?? Array.Empty<ComponentParameter>());
                    if (programs.Effective(toCheck, examples, new System.Collections.Generic.Dictionary<Guid, OpenCvSharp.Point>()) is not { } effective)
                    {
                        condition.ClearReport();
                        continue;
                    }
                    toCheck = effective;
                    if (component)
                        effectiveNot = effective.Not;
                }
                try
                {
                    var report = await Task.Run(() =>
                    {
                        using var checker = new ConditionChecker(ScreenCapture.Shared, model.Store, device, 0);
                        return checker.Inspect(toCheck);
                    });
                    condition.ShowReport(report, effectiveNot);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // One check that fails (the screen went away meanwhile) leaves the others to be checked.
                    CaptureLog.Write($"a condition's check failed: {ex.Message}");
                    condition.ClearReport();
                }
            }
        }
        finally
        {
            _checking = false;
        }
        if (_toCheck.Count > 0)
            _checkLater.Start();
    }
}
