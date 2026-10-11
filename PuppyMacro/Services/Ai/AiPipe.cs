using System;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PuppyMacro.Services.Ai;

/// <summary>
/// The named pipe between the running PuppyMacro (server) and <c>PuppyMacro.exe --mcp</c> (the bridge an AI app
/// starts). One per Windows user and per build: the development build has its own, like its own data folder.
/// Only the current user can open it.
/// </summary>
internal static class AiPipe
{
    /// <summary>"PuppyMacro.Mcp.S-1-5-21-…" (development build: "PuppyMacro.Dev.Mcp.S-1-5-21-…").</summary>
    public static string Name { get; } =
        $"PuppyMacro.{(App.IsDevBuild ? "Dev." : "")}Mcp.{WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName}";

    /// <summary>
    /// A new server end, waiting for one bridge. Every bridge gets its own instance. The <paramref name="first"/> one
    /// fails (IOException) when the name is taken: by another program, which would otherwise get the AI apps' messages.
    /// </summary>
    public static NamedPipeServerStream CreateServer(bool first)
    {
        PipeSecurity security = Security(withMediumLabel: IsElevated());
        try
        {
            return Create(security, first);
        }
        catch (UnauthorizedAccessException) when (IsElevated())
        {
            // The integrity label could not be set: only an elevated bridge can connect then.
            return Create(Security(withMediumLabel: false), first);
        }
    }

    private static NamedPipeServerStream Create(PipeSecurity security, bool first) =>
        NamedPipeServerStreamAcl.Create(Name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None),
            0, 0, security);

    /// <summary>
    /// Full access for the current user only. Run as administrator, PuppyMacro labels the pipe with the medium
    /// integrity level, so the bridge of an AI app that is not elevated can still open it.
    /// </summary>
    private static PipeSecurity Security(bool withMediumLabel)
    {
        SecurityIdentifier user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current Windows user has no SID.");
        var security = new PipeSecurity();
        if (withMediumLabel)
            security.SetSecurityDescriptorSddlForm($"D:P(A;;GA;;;{user.Value})S:(ML;;NW;;;ME)");
        else
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private static bool IsElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
