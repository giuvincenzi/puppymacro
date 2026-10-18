using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using PuppyMacro.Resources;
using PuppyMacro.Services;
using PuppyMacro.Services.Components;
using PuppyMacro.Views;

namespace PuppyMacro;

/// <summary>
/// The guided update of a component's calls (<see cref="SignatureUpdate"/>): a value for each parameter every call now needs
/// (given to the calls that don't have one of its kind), the parameters taken out of every call, and who changes. On Update
/// and save, <see cref="Values"/> holds the values; the editor saves the component and the calls together.
/// </summary>
public partial class SignatureUpdateWindow
{
    /// <summary>A parameter that needs a value: its name and type, what it is, the value for every call.</summary>
    public sealed record ValueCard(string Name, string Header, string Description, ValueEditorViewModel Value);

    public sealed record RemovedCard(string Header, string Description);

    public sealed record SiteCard(string Name, string Kind, string Calls);

    private readonly List<ValueCard> _values;

    internal SignatureUpdateWindow(LoopEngine engine, SignatureUpdate update, ComponentSignatures signatures, ImageStore? store)
    {
        InitializeComponent();
        WindowFit.Apply(this);
        KeyCaptureField.SetEngine(this, engine);
        Closed += (_, _) => engine.CancelCapture();
        IntroText.Text = Plurals.Format("Signature_Intro", update.Calls, update.ComponentName);
        _values = update.NeedValue.Select(p => new ValueCard(p.Name, p.Name,
            string.Format(CultureInfo.CurrentCulture, Strings.Signature_NeedsValueDescription, ComponentChecks.TypeName(p.Type, p.Unit)),
            new ValueEditorViewModel(p.Type, p.Unit, p.Default ?? p.Example, store, signatures.RuleOf(update.ComponentId, p.Name)))).ToList();
        foreach (var card in _values)
            card.Value.PropertyChanged += (_, _) => Validate();
        ValuesList.ItemsSource = _values;
        RemovedList.ItemsSource = update.Removed
            .Select(n => new RemovedCard(string.Format(CultureInfo.CurrentCulture, Strings.Signature_Removed, n), Strings.Signature_RemovedDescription))
            .Concat(update.Renamed.Select(r => new RemovedCard(string.Format(CultureInfo.CurrentCulture, Strings.Signature_Renamed, r.Key, r.Value),
                Strings.Signature_RenamedDescription)))
            .ToList();
        SitesList.ItemsSource = update.Sites.Select(s => new SiteCard(s.OwnerName,
            s.IsMacro ? Strings.Signature_Macro : Strings.Signature_Component, Plurals.Format("Signature_Calls", s.Calls))).ToList();
        Validate();
    }

    /// <summary>The value each parameter gives the calls that need one.</summary>
    internal Dictionary<string, JsonNode> Values { get; } = new();

    private string? Problem() => _values
        .Select(v => v.Value.Problem is string p ? string.Format(CultureInfo.CurrentCulture, Strings.Param_ArgumentProblem, v.Name, p) : null)
        .FirstOrDefault(p => p != null);

    private void Validate()
    {
        string? problem = Problem();
        ErrorText.Text = problem ?? "";
        ErrorText.Visibility = problem == null ? Visibility.Collapsed : Visibility.Visible;
        UpdateButton.IsEnabled = problem == null;
    }

    private void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        UpdateButton.Focus();
        if (Problem() != null)
        {
            Validate();
            return;
        }
        foreach (var card in _values)
            if (card.Value.ToJson() is { } value)
                Values[card.Name] = value;
        DialogResult = true;
    }
}
