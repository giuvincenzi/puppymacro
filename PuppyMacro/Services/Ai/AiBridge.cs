using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using PuppyMacro.Native;

namespace PuppyMacro.Services.Ai;

/// <summary>
/// <c>PuppyMacro.exe --mcp</c>: what an AI app starts as its MCP server. It has no window and no MCP logic: it copies
/// the AI app's stdin to the running PuppyMacro's pipe (<see cref="AiPipe"/>) and the pipe to stdout, and starts
/// PuppyMacro in the system tray when it is not running.
/// </summary>
internal static class AiBridge
{
    public const string Argument = "--mcp";

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Runs until the AI app or PuppyMacro closes the connection. The process exit code.</summary>
    public static int Run()
    {
        NamedPipeClientStream? pipe = Connect(TimeSpan.FromMilliseconds(500));
        if (pipe == null)
        {
            StartPuppyMacro();
            pipe = Connect(StartTimeout);
        }
        if (pipe == null)
        {
            Console.Error.WriteLine("PuppyMacro did not start: open PuppyMacro, then try again.");
            return 1;
        }

        using (pipe)
        {
            // Only PuppyMacro gets the AI app's messages: the pipe's server must be this same PuppyMacro.exe (a program of
            // another user could have made a pipe with this name before PuppyMacro started).
            uint server = NativeMethods.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint id) ? id : 0;
            string? serverExe = server == 0 ? null : AppWindows.ExePath(server);
            if (serverExe == null || !string.Equals(Path.GetFullPath(serverExe), Path.GetFullPath(AppPaths.ExecutablePath), StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("The connection is not PuppyMacro's: close the program that uses it, then open PuppyMacro.");
                return 1;
            }

            // This process was started by the AI app, usually the window in front: it may let PuppyMacro bring its
            // windows (an editor, a confirmation) to the front. Done again for every message the AI app sends.
            AllowForeground(server);

            using Stream input = Console.OpenStandardInput();
            using Stream output = Console.OpenStandardOutput();
            using var done = new CancellationTokenSource();
            Task toApp = Copy(input, pipe, done.Token, () => AllowForeground(server));
            Task toAi = Copy(pipe, output, done.Token, null);
            Task.WaitAny(toApp, toAi);
            done.Cancel();
        }
        return 0;
    }

    private static NamedPipeClientStream? Connect(TimeSpan timeout)
    {
        var pipe = new NamedPipeClientStream(".", AiPipe.Name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                pipe.Connect(500);
                return pipe;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    pipe.Dispose();
                    return null;
                }
                Thread.Sleep(250);
            }
        }
    }

    /// <summary>Starts PuppyMacro in the system tray (it opens the pipe once its window is ready).</summary>
    private static void StartPuppyMacro()
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppPaths.ExecutablePath, StartupService.TrayArgument) { UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            Console.Error.WriteLine("PuppyMacro could not be started: " + ex.Message);
        }
    }

    private static void AllowForeground(uint processId)
    {
        if (processId != 0)
            NativeMethods.AllowSetForegroundWindow(processId);
    }

    private static async Task Copy(Stream from, Stream to, CancellationToken cancel, Action? onData)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                int read = await from.ReadAsync(buffer, cancel).ConfigureAwait(false);
                if (read == 0)
                    return;
                onData?.Invoke();
                await to.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                await to.FlushAsync(cancel).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }
}
