using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PuppyMacro.Native;
using PuppyMacro.Resources;
using PuppyMacro.Services.Ai;

namespace PuppyMacro.Views;

/// <summary>One request in Recent activity and in the AI activity window.</summary>
internal sealed class AiActivityRow
{
    public AiActivityRow(AiActivity entry)
    {
        Entry = entry;
        TimeText = entry.Time.ToString("t", CultureInfo.CurrentCulture);
        DayText = HistoryRow.DayName(entry.Time);
        Request = entry.ShownRequest;
        ResultText = entry.Restored ? Strings.AiResult_Restored
            : ResultName(entry.Result) is { Length: > 0 } result ? result : entry.Detail;
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

    /// <summary>What the AI app asked, in the user's language (or as an older version saved it).</summary>
    public string Request { get; }
    public string KindText { get; }
    public string ResultText { get; }

    /// <summary>The detail and the JSON saved, shown under a selected row.</summary>
    public string DetailText { get; }

    /// <summary>A change or deletion an AI app made, not restored yet: Restore puts back what was there before.</summary>
    public bool CanRestore => Entry.Before != null && !Entry.Restored
        && Entry.Result is AiActivityResult.Saved or AiActivityResult.SavedWithChanges or AiActivityResult.Applied;

    public Visibility RestoreVisibility => CanRestore ? Visibility.Visible : Visibility.Collapsed;

    public string RestoreName => string.Format(CultureInfo.CurrentCulture, Strings.AiActivity_RestoreName, Request);

    /// <summary>The row's accessible name in Recent activity: what the row shows.</summary>
    public string RecentAccessibleName => AccessibleNames.Join(TimeText, App, Request, ResultText);

    /// <summary>The row's accessible name in the AI activity window: what the row shows.</summary>
    public string AccessibleName => AccessibleNames.Join(TimeText, App, KindText, Request, ResultText);

    public static string KindName(AiActivityKind kind) => kind switch
    {
        AiActivityKind.Read => Strings.AiKind_Read,
        AiActivityKind.Proposal => Strings.AiKind_Save,
        AiActivityKind.Interaction => Strings.AiKind_Interaction,
        AiActivityKind.Confirmation => Strings.AiKind_Change,
        _ => Strings.AiKind_Connection,
    };

    public static string ResultName(AiActivityResult result) => result switch
    {
        AiActivityResult.Saved => Strings.AiResult_Saved,
        AiActivityResult.SavedWithChanges => Strings.AiResult_SavedWithChanges,
        AiActivityResult.Applied => Strings.AiResult_Done,
        AiActivityResult.Cancelled => Strings.AiResult_Cancelled,
        AiActivityResult.TimedOut => Strings.AiResult_NoAnswer,
        AiActivityResult.Refused => Strings.AiResult_Refused,
        AiActivityResult.Error => Strings.AiResult_Error,
        _ => "",
    };
}

/// <summary>An AI app on the AI page: its icon and state, and its button.</summary>
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

    /// <summary>What is shown: the page is rebuilt only when this changes.</summary>
    public string Shown => $"{Id}:{Status}:{Connected}:{Waiting}:{ActionText}:{ActionEnabled}:{LinkText}:{Icon != null}";

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

/// <summary>A connected AI app in the status bar.</summary>
internal sealed class AiStatusItem
{
    public AiStatusItem(string name) => Name = name;

    public string Name { get; }

    /// <summary>The row's accessible name: the app's name.</summary>
    public string AccessibleName => Name;

    /// <summary>The pulsing halo, only while Windows' animations are on.</summary>
    public Visibility HaloVisibility => SystemParameters.ClientAreaAnimation ? Visibility.Visible : Visibility.Collapsed;
}
