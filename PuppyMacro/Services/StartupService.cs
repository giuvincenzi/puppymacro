using System;
using Microsoft.Win32;

namespace PuppyMacro.Services;

/// <summary>"Start with Windows": a value in the current user's Run key (no admin rights needed).</summary>
internal static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PuppyMacro";
    public const string TrayArgument = "--tray";

    /// <summary>Adds, updates (current exe path) or removes the entry.</summary>
    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
                key.SetValue(ValueName, $"\"{AppPaths.ExecutablePath}\" {TrayArgument}");
            else if (key.GetValue(ValueName) != null)
                key.DeleteValue(ValueName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            System.Diagnostics.Debug.WriteLine($"Start with Windows failed: {ex.Message}");
        }
    }
}
