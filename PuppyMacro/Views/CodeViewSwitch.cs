using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PuppyMacro.Services;

namespace PuppyMacro.Views;

/// <summary>Reads a Code view's text: the item, or null with the problems.</summary>
internal delegate T? CodeReader<T>(string text, out List<CodeProblem> problems) where T : class;

/// <summary>
/// The Form view / Code view of an editor window (macro, action, loop, remap). The Code view shows the
/// item's JSON as it is saved, in the same window and at the same size; it goes back to the Form view,
/// and Save works, only when the code has no problems. Meanwhile Cancel is Discard changes: back to the
/// Form view as it was.
/// </summary>
internal sealed class CodeViewSwitch<T> where T : class
{
    private readonly ViewSwitchBar _bar;
    private readonly CodeView _code;
    private readonly FrameworkElement _form;
    private readonly ContentControl _cancel;
    private readonly TextBlock _errorText;
    private readonly UIElement _save;
    private readonly Func<T> _build;
    private readonly Func<T, string> _serialize;
    private readonly CodeReader<T> _reader;
    private readonly Action<T> _load;
    private readonly Action _validate;
    private readonly object _cancelContent;

    /// <param name="build">The item as the Form view's fields describe it.</param>
    /// <param name="load">Shows an item read from the code in the Form view's fields.</param>
    /// <param name="validate">The window's Validate, which calls <see cref="ValidateCode"/> first.</param>
    public CodeViewSwitch(ViewSwitchBar bar, CodeView code, FrameworkElement form, ContentControl cancel,
        TextBlock errorText, UIElement save, string schema, Func<T> build, Func<T, string> serialize, CodeReader<T> reader,
        Action<T> load, Action validate)
    {
        _bar = bar;
        _code = code;
        _form = form;
        _cancel = cancel;
        _errorText = errorText;
        _save = save;
        _build = build;
        _serialize = serialize;
        _reader = reader;
        _load = load;
        _validate = validate;
        _cancelContent = cancel.Content;

        _code.Schema = schema;
        _code.Check = text =>
        {
            _reader(text, out var problems);
            return problems;
        };
        _code.Changed += _validate;
        _bar.ViewRequested += OnViewRequested;
        _bar.FormatRequested += _code.Format;
    }

    public bool IsCode { get; private set; }

    /// <summary>Called before the Code view opens (e.g. to stop a key capture).</summary>
    public event Action? Opening;

    /// <summary>The code's item, or null while it has problems or has not been checked yet.</summary>
    public T? Read() => _code.IsUpToDate && _code.Problems.Count == 0 ? _reader(_code.Text, out _) : null;

    /// <summary>Discard changes: back to the Form view as it was before the Code view.</summary>
    public void Discard() => SetView(code: false);

    /// <summary>
    /// The window's Validate in the Code view: Save and Form view need code without problems.
    /// Returns false in the Form view, where the window checks its fields.
    /// </summary>
    public bool ValidateCode()
    {
        if (!IsCode)
        {
            _bar.FormEnabled = true;
            return false;
        }
        int problems = _code.Problems.Count;
        bool valid = _code.IsUpToDate && problems == 0;
        _errorText.Text = problems switch
        {
            0 => "",
            1 => "Fix the problem, or discard the changes.",
            _ => $"Fix the {problems} problems, or discard the changes.",
        };
        _errorText.Visibility = problems > 0 ? Visibility.Visible : Visibility.Collapsed;
        _save.IsEnabled = valid;
        _bar.FormEnabled = valid;
        return true;
    }

    private async void OnViewRequested(bool code)
    {
        if (code && !IsCode)
            await ShowCode();
        else if (!code && IsCode)
            ShowForm();
    }

    private async Task ShowCode()
    {
        Opening?.Invoke();
        string text = _serialize(_build());
        SetView(code: true);
        if (await _code.ShowAsync(text) is string error)
        {
            SetView(code: false);
            _errorText.Text = error;
            _errorText.Visibility = Visibility.Visible;
            return;
        }
        _code.FocusEditor();
    }

    private void ShowForm()
    {
        if (Read() is not T item)
        {
            // Problems: stay in the Code view (Form view is off meanwhile, see ValidateCode).
            _bar.Show(code: true);
            _validate();
            return;
        }
        _load(item);
        SetView(code: false);
    }

    private void SetView(bool code)
    {
        IsCode = code;
        _bar.Show(code);
        _form.Visibility = code ? Visibility.Collapsed : Visibility.Visible;
        _code.Visibility = code ? Visibility.Visible : Visibility.Collapsed;
        _cancel.Content = code ? "Discard changes" : _cancelContent;
        _cancel.ToolTip = code ? "Drop the changes made in the code and go back to Form view" : null;
        _validate();
    }
}
