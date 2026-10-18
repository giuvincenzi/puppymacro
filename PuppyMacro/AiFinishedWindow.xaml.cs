using System;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using PuppyMacro.Resources;
using PuppyMacro.Services.Ai;

namespace PuppyMacro;

/// <summary>
/// "Done": what an AI app asked on the user's screen is over, and the user can go back to it (by the name the app gave,
/// or "your AI app" when it gave none). On top, without taking the focus; OK or <see cref="ShownFor"/> closes it.
/// </summary>
public partial class AiFinishedWindow
{
    public static readonly TimeSpan ShownFor = TimeSpan.FromSeconds(8);

    internal AiFinishedWindow(string app)
    {
        InitializeComponent();
        BackText.Text = string.IsNullOrWhiteSpace(app) || app == AiServer.UnknownApp
            ? Strings.AiFinished_BackToAiApp
            : string.Format(CultureInfo.CurrentCulture, Strings.AiFinished_BackTo, app);
        var timer = new DispatcherTimer { Interval = ShownFor };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Close();
        };
        Loaded += (_, _) => timer.Start();
        Closed += (_, _) => timer.Stop();
    }

    private void OnOkClick(object sender, RoutedEventArgs e) => Close();
}
