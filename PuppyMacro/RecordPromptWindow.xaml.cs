using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using PuppyMacro.Resources;
using PuppyMacro.Views;

namespace PuppyMacro;

/// <summary>"Press F8 to start recording". Closed with DialogResult true when recording starts.</summary>
public partial class RecordPromptWindow
{
    public RecordPromptWindow(string hotkey, bool recordMouseMovement)
    {
        InitializeComponent();
        WindowFit.Apply(this);
        HotkeyText.Text = hotkey;
        _recordMouseMovement = recordMouseMovement;
        MouseMovementBox.IsChecked = recordMouseMovement;
        FillSentence();
        DescriptionText.Text = string.Format(CultureInfo.CurrentCulture, Strings.Recording_PromptDescription, hotkey);
    }

    /// <summary>"Press {0} or select {1}": the key cap ({0}) and the button's name ({1}) where the language puts them.</summary>
    private void FillSentence()
    {
        SentencePanel.Children.Clear();
        foreach (string part in Regex.Split(Strings.Recording_PressOrSelect, @"(\{[01]\})"))
        {
            if (part == "{0}")
                SentencePanel.Children.Add(HotkeyCap);
            else if (part == "{1}")
                SentencePanel.Children.Add(StartName);
            else if (part.Length > 0)
                SentencePanel.Children.Add(new TextBlock { Text = part, VerticalAlignment = VerticalAlignment.Center });
        }
    }

    /// <summary>True when closed with the Start recording button (countdown), false with the hotkey.</summary>
    public bool StartNow { get; private set; }

    private void OnStartNowClick(object sender, RoutedEventArgs e)
    {
        StartNow = true;
        DialogResult = true;
    }

    private volatile bool _recordMouseMovement;

    /// <summary>Current checkbox value. Safe to read from any thread (the input thread reads it).</summary>
    public bool RecordMouseMovement => _recordMouseMovement;

    private void OnMouseMovementChanged(object sender, RoutedEventArgs e) =>
        _recordMouseMovement = MouseMovementBox.IsChecked == true;

    /// <summary>Called when the record hotkey was pressed.</summary>
    public void Started()
    {
        if (IsVisible)
            DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
