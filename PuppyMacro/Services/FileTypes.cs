using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;
using PuppyMacro.Native;
using PuppyMacro.Resources;

namespace PuppyMacro.Services;

/// <summary>
/// PuppyMacro's files in File Explorer, in the current user's classes (no admin rights): .puppyitems (loops, macros and
/// remaps to share) with its icon, opened by PuppyMacro on a double click (Import); .puppymacro (backup) with its icon, a
/// double click only shows a window saying it opens from Settings › Backup (<see cref="BackupFileWindow"/>), as it replaces
/// the data. Release builds set them at every start (the exe's path follows Velopack's
/// current folder) and remove them on uninstall; the development build never touches them.
/// </summary>
internal static class FileTypes
{
    public const string ItemsProgId = "PuppyMacro.Items";
    public const string BackupProgId = "PuppyMacro.Backup";

    /// <summary>The icons, copied next to the exe (Assets\FileIcons).</summary>
    public static string IconsFolder => Path.Combine(AppContext.BaseDirectory, "Assets", "FileIcons");

    /// <summary>The keys under Software\Classes and their default values.</summary>
    public static List<(string Key, string Value)> Entries(string exe, string iconsFolder) => new()
    {
        (ItemFile.Extension, ItemsProgId),
        (ItemsProgId, Strings.FileTypes_Items),
        (ItemsProgId + @"\DefaultIcon", Path.Combine(iconsFolder, "PuppyMacroItems.ico")),
        (ItemsProgId + @"\shell\open\command", $"\"{exe}\" \"%1\""),
        (BackupService.Extension, BackupProgId),
        (BackupProgId, Strings.FileTypes_Backup),
        (BackupProgId + @"\DefaultIcon", Path.Combine(iconsFolder, "PuppyMacroBackup.ico")),
        (BackupProgId + @"\shell\open\command", $"\"{exe}\" \"%1\""),
    };

    /// <summary>Sets them for this exe, and tells File Explorer when something changed.</summary>
    public static void Register()
    {
        try
        {
            using RegistryKey classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
            if (Apply(classes, AppPaths.ExecutablePath, IconsFolder))
                NativeMethods.SHChangeNotify(NativeMethods.SHCNE_ASSOCCHANGED, NativeMethods.SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            System.Diagnostics.Debug.WriteLine($"File types failed: {ex.Message}");
        }
    }

    /// <summary>On uninstall: PuppyMacro's keys, and the extensions while they still point at them.</summary>
    public static void Unregister()
    {
        try
        {
            using RegistryKey classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
            Remove(classes);
            NativeMethods.SHChangeNotify(NativeMethods.SHCNE_ASSOCCHANGED, NativeMethods.SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            System.Diagnostics.Debug.WriteLine($"File types failed: {ex.Message}");
        }
    }

    /// <summary>Writes every entry under <paramref name="classes"/>; true when one was missing or different.</summary>
    internal static bool Apply(RegistryKey classes, string exe, string iconsFolder)
    {
        bool changed = false;
        foreach ((string key, string value) in Entries(exe, iconsFolder))
        {
            using RegistryKey entry = classes.CreateSubKey(key);
            if (entry.GetValue(null) as string == value)
                continue;
            entry.SetValue(null, value);
            changed = true;
        }
        return changed;
    }

    internal static void Remove(RegistryKey classes)
    {
        foreach ((string extension, string progId) in new[] { (ItemFile.Extension, ItemsProgId), (BackupService.Extension, BackupProgId) })
        {
            classes.DeleteSubKeyTree(progId, throwOnMissingSubKey: false);
            using RegistryKey? key = classes.OpenSubKey(extension, writable: true);
            if (key == null || key.GetValue(null) as string != progId)
                continue; // another app took it since
            key.DeleteValue("", throwOnMissingValue: false);
            bool empty = key.SubKeyCount == 0 && key.ValueCount == 0;
            key.Close();
            if (empty)
                classes.DeleteSubKey(extension, throwOnMissingSubKey: false);
        }
    }
}
