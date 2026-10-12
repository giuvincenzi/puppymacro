using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PuppyMacro.Services;

/// <summary>
/// A file opened from File Explorer while PuppyMacro already runs: the new process hands its path to the running one on a
/// named pipe (current user only, one per build) and exits. Without an answer (another user's or an elevated PuppyMacro) it
/// only shows the running one's window.
/// </summary>
internal static class OpenFileChannel
{
    public static string Name { get; } =
        $"PuppyMacro.{(App.IsDevBuild ? "Dev." : "")}Open.{WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName}";

    /// <summary>In the running PuppyMacro: calls <paramref name="opened"/> (on a pool thread) with each path it receives.</summary>
    public static void Listen(Action<string> opened)
    {
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(Name, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync().ConfigureAwait(false);
                    using var reader = new StreamReader(pipe, Encoding.UTF8);
                    string? path = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(path))
                        opened(path.Trim());
                }
                catch (IOException)
                {
                    // A client that left early, or the name is taken (another PuppyMacro of this user): try again shortly.
                    await Task.Delay(1000).ConfigureAwait(false);
                }
            }
        });
    }

    /// <summary>In the new process: hands <paramref name="path"/> to the running PuppyMacro. False when it did not answer.</summary>
    public static bool Send(string path)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(2000);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false));
            writer.WriteLine(path);
            writer.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
