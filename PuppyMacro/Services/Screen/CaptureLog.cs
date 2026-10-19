using System;
using System.Globalization;
using System.IO;

namespace PuppyMacro.Services.Screen;

/// <summary>
/// The screen capture's errors, one line each, in <c>logs\screen-capture.log</c> of the data folder (debugging only, not user
/// data): which monitor, what failed and Windows' error code. Up to <see cref="MaxBytes"/>: then the older half goes.
/// </summary>
internal static class CaptureLog
{
    public const long MaxBytes = 256 * 1024;

    private static readonly object Sync = new();

    public static string File => Path.Combine(AppPaths.LogsFolder, "screen-capture.log");

    public static void Write(string line)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(AppPaths.LogsFolder);
                var file = new FileInfo(File);
                if (file.Exists && file.Length > MaxBytes)
                {
                    string all = System.IO.File.ReadAllText(File);
                    int cut = all.IndexOf('\n', all.Length / 2);
                    System.IO.File.WriteAllText(File, cut >= 0 ? all[(cut + 1)..] : "");
                }
                System.IO.File.AppendAllText(File,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + line + Environment.NewLine);
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
