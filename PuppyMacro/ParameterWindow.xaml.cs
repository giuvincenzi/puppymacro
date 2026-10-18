using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using PuppyMacro.Models;
using PuppyMacro.Resources;
using PuppyMacro.Services;
using PuppyMacro.Services.Components;
using PuppyMacro.Views;

namespace PuppyMacro;

/// <summary>
/// Adds or edits one parameter of a component. On OK, <see cref="Result"/> holds it. The name follows the rules of a
/// component's parameters and is unique in it; the example and the default are values of the type, within its limits (the
/// limits of the fields it goes to are checked when the component is saved).
/// </summary>
public partial class ParameterWindow
{
    private static readonly Regex NamePattern = new("^[A-Za-z][A-Za-z0-9_]{0,39}$", RegexOptions.CultureInvariant);

    private readonly IReadOnlyCollection<string> _otherNames;
    private readonly ImageStore? _store;
    private ValueEditorViewModel _example = null!;
    private ValueEditorViewModel _default = null!;
    private bool _ready;

    /// <param name="parameter">The parameter to edit, or null for a new one.</param>
    /// <param name="otherNames">The names of the component's other parameters.</param>
    internal ParameterWindow(LoopEngine engine, ComponentParameter? parameter, IReadOnlyCollection<string> otherNames, ImageStore? store)
    {
        InitializeComponent();
        WindowFit.Apply(this);
        KeyCaptureField.SetEngine(this, engine); // a Key's value waits for keys through it
        Closed += (_, _) => engine.CancelCapture();
        _otherNames = otherNames;
        _store = store;
        Title = parameter == null ? Strings.Param_AddTitle : string.Format(CultureInfo.CurrentCulture, Strings.Param_EditTitle, parameter.Name);
        foreach (ParameterType type in Enum.GetValues<ParameterType>())
            TypeBox.Items.Add(new ComboBoxItem { Content = ComponentChecks.TypeName(type, null), Tag = type });
        foreach (NumberUnit unit in Enum.GetValues<NumberUnit>())
            UnitBox.Items.Add(new ComboBoxItem { Content = UnitTitle(unit), Tag = unit });

        var source = parameter ?? new ComponentParameter { Name = "", Type = ParameterType.Point };
        NameBox.Text = source.Name;
        TypeBox.SelectedIndex = (int)source.Type;
        UnitBox.SelectedIndex = (int)(source.Unit ?? NumberUnit.Ms);
        RequiredSwitch.IsOn = source.Required;
        DescriptionBox.Text = source.Description;
        LoadValues(source.Example, source.Default);
        _ready = true;
        Validate();
    }

    public ComponentParameter? Result { get; private set; }

    private ParameterType SelectedType => TypeBox.SelectedItem is ComboBoxItem { Tag: ParameterType type } ? type : ParameterType.Point;

    private NumberUnit? SelectedUnit => SelectedType == ParameterType.Number && UnitBox.SelectedItem is ComboBoxItem { Tag: NumberUnit unit } ? unit : null;

    /// <summary>"Milliseconds (ms)": a unit as the list shows it.</summary>
    private static string UnitTitle(NumberUnit unit) => unit switch
    {
        NumberUnit.Pixels => Strings.Param_UnitPixelsTitle,
        NumberUnit.Ms => Strings.Param_UnitMsTitle,
        NumberUnit.Seconds => Strings.Param_UnitSecondsTitle,
        NumberUnit.Count => Strings.Param_UnitCountTitle,
        _ => Strings.Param_UnitPercentTitle,
    };

    /// <summary>The example and default editors for the type and unit chosen, with these values (when of that type).</summary>
    private void LoadValues(System.Text.Json.Nodes.JsonNode? example, System.Text.Json.Nodes.JsonNode? @default)
    {
        var rule = ComponentValues.BaseRule(SelectedType, SelectedUnit);
        _example = new ValueEditorViewModel(SelectedType, SelectedUnit, example, _store, rule);
        _default = new ValueEditorViewModel(SelectedType, SelectedUnit, @default ?? example, _store, rule);
        _example.PropertyChanged += (_, _) => Validate();
        _default.PropertyChanged += (_, _) => Validate();
        ExampleEditor.DataContext = _example;
        DefaultEditor.DataContext = _default;
        UnitCard.Visibility = SelectedType == ParameterType.Number ? Visibility.Visible : Visibility.Collapsed;
        DefaultCard.Visibility = RequiredSwitch.IsOn ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready)
            return;
        // Another type or unit: the values start again (a point is not a number).
        LoadValues(null, null);
        Validate();
    }

    private void OnRequiredToggled(object sender, RoutedEventArgs e)
    {
        DefaultCard.Visibility = RequiredSwitch.IsOn ? Visibility.Collapsed : Visibility.Visible;
        Validate();
    }

    private void OnChanged(object sender, TextChangedEventArgs e) => Validate();

    private string? Problem()
    {
        string name = NameBox.Text.Trim();
        if (!NamePattern.IsMatch(name))
            return Strings.Code_ParameterName;
        if (_otherNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            return Strings.Code_ParameterNameTwice;
        if (_example.Problem is string example)
            return string.Format(CultureInfo.CurrentCulture, Strings.Param_ExampleProblem, example);
        if (!RequiredSwitch.IsOn && _default.Problem is string @default)
            return string.Format(CultureInfo.CurrentCulture, Strings.Param_DefaultProblem, @default);
        return null;
    }

    private void Validate()
    {
        if (!_ready)
            return;
        string? problem = Problem();
        ErrorText.Text = problem ?? "";
        ErrorText.Visibility = problem == null ? Visibility.Collapsed : Visibility.Visible;
        OkButton.IsEnabled = problem == null;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        OkButton.Focus(); // commit the number being edited
        if (Problem() != null)
        {
            Validate();
            return;
        }
        Result = new ComponentParameter
        {
            Name = NameBox.Text.Trim(),
            Type = SelectedType,
            Unit = SelectedUnit,
            Example = _example.ToJson(),
            Default = RequiredSwitch.IsOn ? null : _default.ToJson(),
            Description = DescriptionBox.Text.Trim(),
        };
        DialogResult = true;
    }
}
