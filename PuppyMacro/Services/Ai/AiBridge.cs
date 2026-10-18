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
            // PuppyMacro is closed: started in the tray, unless the user turned that off (Start PuppyMacro when an AI app
            // needs it). Then, or when it does not start, the AI app still gets an answer: why, and what the user can do.
            if (IsPuppyMacroRunning())
            {
                // Running, still starting or busy: it opens its pipe in a moment (or its AI page says why it can't).
                pipe = Connect(StartTimeout);
                if (pipe == null)
                    return AnswerWhileClosed("PuppyMacro is open but AI apps can't connect to it now. Ask the user to look at PuppyMacro's AI page.");
            }
            else
            {
                ClosedSettings settings = ReadSettings();
                if (!settings.StartWhenNeeded)
                    return AnswerWhileClosed(ClosedMessage(settings));
                StartPuppyMacro();
                pipe = Connect(StartTimeout);
                if (pipe == null)
                    return AnswerWhileClosed("PuppyMacro did not start. Ask the user to open PuppyMacro, then try again.");
            }
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

    /// <summary>PuppyMacro runs: it holds its single-instance mutex (bridges never take it), also while it starts.</summary>
    private static bool IsPuppyMacroRunning()
    {
        if (!Mutex.TryOpenExisting(App.SingleInstanceName, out Mutex? mutex))
            return false;
        mutex.Dispose();
        return true;
    }

    /// <summary>What the bridge reads of the settings while PuppyMacro is closed.</summary>
    internal sealed record ClosedSettings(bool StartWhenNeeded, bool StartWithWindows, bool CloseToTray);

    /// <summary>The settings file as it is (never written by the bridge); a missing or unreadable one: the defaults.</summary>
    private static ClosedSettings ReadSettings()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(AppPaths.SettingsFile));
                bool Flag(string name, bool fallback) =>
                    doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False
                        ? value.GetBoolean()
                        : fallback;
                return new ClosedSettings(Flag("AiStartWhenNeeded", true), Flag("StartWithWindows", false), Flag("CloseToTray", true));
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
        }
        return new ClosedSettings(true, false, true);
    }

    /// <summary>
    /// What the AI app tells the user while PuppyMacro is closed and may not be started: open it, and how to keep it
    /// available (only the settings that are off).
    /// </summary>
    internal static string ClosedMessage(ClosedSettings settings)
    {
        var ways = new System.Collections.Generic.List<string>();
        if (!settings.StartWithWindows)
            ways.Add("turning on Start with Windows in PuppyMacro's Settings");
        if (!settings.CloseToTray)
            ways.Add("turning on Close to system tray in PuppyMacro's Settings");
        ways.Add("turning on Start PuppyMacro when an AI app needs it on PuppyMacro's AI page (Allow AI access)");
        return "PuppyMacro is closed, and the user chose that AI apps don't start it. Ask the user to open PuppyMacro, then " +
            "try again. To keep it always available, suggest " + string.Join(", or ", ways) + ".";
    }

    /// <summary>
    /// A small MCP server on stdio, in this process, while PuppyMacro is closed: the same tools, each answering
    /// <paramref name="message"/>, until the AI app closes the connection.
    /// </summary>
    private static int AnswerWhileClosed(string message)
    {
        var options = AiServer.Options(AiTools.Definitions(), message,
            _ => Task.FromResult(new AiTools.OpenedResource(null, message, false)),
            (_, _, _, _) => Task.FromResult(AiToolResult.Error(message)));
        try
        {
            RunStdioServer(options).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        return 0;
    }

    private static async Task RunStdioServer(ModelContextProtocol.Server.McpServerOptions options)
    {
        await using var transport = new ModelContextProtocol.Server.StdioServerTransport(options);
        await using ModelContextProtocol.Server.McpServer server = ModelContextProtocol.Server.McpServer.Create(transport, options);
        await server.RunAsync().ConfigureAwait(false);
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
