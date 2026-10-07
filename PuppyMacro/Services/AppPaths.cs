using System;
using System.IO;

namespace PuppyMacro.Services;

/// <summary>Where PuppyMacro keeps its data and where its exe is.</summary>
internal static class AppPaths
{
    /// <summary>%AppData%\PuppyMacro: survives new versions extracted to other folders.</summary>
    public static string DataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PuppyMacro");

    public static string SettingsFile => Path.Combine(DataFolder, "settings.json");

    public static string MacrosFolder => Path.Combine(DataFolder, "macros");

    /// <summary>The PuppyMacro.exe to start (also when launched through dotnet.exe).</summary>
    public static string ExecutablePath
    {
        get
        {
            string exe = Path.Combine(AppContext.BaseDirectory, "PuppyMacro.exe");
            return File.Exists(exe) ? exe : Environment.ProcessPath ?? exe;
        }
    }

    /// <summary>
    /// Versions before 1.6 kept settings.json and macros\ next to the exe. Copies them to the
    /// data folder once, if the data folder has no settings yet. Returns true if it copied.
    /// </summary>
    public static bool MigrateFromExeFolder()
    {
        string oldSettings = Path.Combine(AppContext.BaseDirectory, "settings.json");
        if (File.Exists(SettingsFile) || !File.Exists(oldSettings))
            return false;

        Directory.CreateDirectory(DataFolder);
        File.Copy(oldSettings, SettingsFile);
        string oldMacros = Path.Combine(AppContext.BaseDirectory, "macros");
        if (Directory.Exists(oldMacros))
        {
            Directory.CreateDirectory(MacrosFolder);
            foreach (string file in Directory.GetFiles(oldMacros, "*.json"))
                File.Copy(file, Path.Combine(MacrosFolder, Path.GetFileName(file)), overwrite: true);
        }
        return true;
    }
}
