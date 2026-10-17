using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace PuppyMacro.Services;

/// <summary>
/// What a running macro does, step by step, in <c>logs\macro-trace.log</c> of the data folder: development builds only (the
/// calls vanish from Release builds), to find out why a macro does not do what it should.
/// </summary>
internal static class MacroTrace
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Sync = new();

    [Conditional("DEBUG")]
    public static void Write(string line)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(AppPaths.LogsFolder);
                string file = Path.Combine(AppPaths.LogsFolder, "macro-trace.log");
                if (new FileInfo(file).Exists && new FileInfo(file).Length > MaxBytes)
                    File.Delete(file);
                File.AppendAllText(file, DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + line + Environment.NewLine);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
