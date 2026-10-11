using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PuppyMacro.Native;
using PuppyMacro.Services.Ai;

namespace PuppyMacro.Views;

/// <summary>One request in Recent activity and in the AI activity window.</summary>
internal sealed class AiActivityRow
{
    public AiActivityRow(AiActivity entry)
    {
        Entry = entry;
        TimeText = entry.Time.ToString("HH:mm");
        DayText = entry.Time.Date == DateTime.Today ? "Today"
            : entry.Time.Date == DateTime.Today.AddDays(-1) ? "Yesterday"
            : entry.Time.ToString("D");
        ResultText = ResultName(entry.Result) is { Length: > 0 } result ? result : entry.Detail;
        KindText = KindName(entry.Kind);
        DetailText = string.Join("\n", new[]
        {
            entry.Detail.Length > 0 && entry.Detail != ResultText ? entry.Detail : null,
            entry.Data,
        }.Where(s => !string.IsNullOrEmpty(s)));
    }

    public AiActivity Entry { get; }
    public string TimeText { get; }
    public string DayText { get; }
    public string App => Entry.App;
    public string Request => Entry.Request;
    public string KindText { get; }
    public string ResultText { get; }

    /// <summary>The detail and the JSON proposed or saved, shown under a selected row.</summary>
    public string DetailText { get; }

    public static string KindName(AiActivityKind kind) => kind switch
    {
        AiActivityKind.Read => "Read",
        AiActivityKind.Proposal => "Proposal",
        AiActivityKind.Interaction => "Interaction",
        AiActivityKind.Confirmation => "Confirmation",
        _ => "Connection",
    };

    public static string ResultName(AiActivityResult result) => result switch
    {
        AiActivityResult.Saved => "Saved",
        AiActivityResult.SavedWithChanges => "Saved with changes",
        AiActivityResult.Applied => "Done",
        AiActivityResult.Cancelled => "Cancelled",
        AiActivityResult.TimedOut => "No answer",
        AiActivityResult.Refused => "Refused",
        AiActivityResult.Error => "Error",
        _ => "",
    };
}

/// <summary>An AI app's tile on the AI page: its icon and state, and its button.</summary>
internal sealed class AiAppTile
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public ImageSource? Icon { get; init; }
    public required string Status { get; init; }
    public bool Connected { get; init; }
    public bool Waiting { get; init; }

    /// <summary>"Connect", "Remove", "Copy configuration", or null for none.</summary>
    public string? ActionText { get; init; }
    public bool ActionIsAccent { get; init; }
    public bool ActionEnabled { get; init; } = true;
    public string? LinkText { get; init; }
    public Uri? LinkUri { get; init; }

    public Visibility IconVisibility => Icon != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GenericIconVisibility => Icon == null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ConnectedVisibility => Connected ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WaitingVisibility => Waiting ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ActionVisibility => ActionText != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LinkVisibility => LinkText != null ? Visibility.Visible : Visibility.Collapsed;
    public Style? ActionStyle => ActionIsAccent ? Application.Current.TryFindResource("AccentButtonStyle") as Style : null;
    public string ActionAutomationName => $"{ActionText} {Name}";

    /// <summary>The icon Windows shows for an exe (its own icon), or null when the file is not there.</summary>
    public static ImageSource? ExeIcon(string? exe)
    {
        if (exe == null || !File.Exists(exe))
            return null;
        var info = new NativeMethods.SHFILEINFO();
        if (NativeMethods.SHGetFileInfo(exe, 0, ref info, (uint)Marshal.SizeOf(info), NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON) == IntPtr.Zero
            || info.hIcon == IntPtr.Zero)
            return null;
        try
        {
            BitmapSource icon = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            icon.Freeze();
            return icon;
        }
        finally
        {
            NativeMethods.DestroyIcon(info.hIcon);
        }
    }
}
