using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// The debug archive's cleaner (<see cref="DebugStore.DeleteOlderThan"/>): it runs on a thread pool timer at start and every
/// hour (App.OnStartup) and from Settings › Debug data › Delete; "a folder that can't be deleted stays for the next time".
/// An exception that leaves it on the timer's thread ends the process.
/// </summary>
[Collection(nameof(DebugStore))]
public class DebugStoreCleanerTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public DebugStoreCleanerTests() => DebugStore.Root = _folder.File("debug");

    public void Dispose() => _folder.Dispose();

    private static string Operation(string name, int ageDays = 8)
    {
        string folder = Path.Combine(DebugStore.Root, "user", "macro", name);
        Directory.CreateDirectory(folder);
        string log = Path.Combine(folder, "log.txt");
        File.WriteAllText(log, name);
        File.SetLastWriteTimeUtc(log, DateTime.UtcNow.AddDays(-ageDays));
        return folder;
    }

    /// <summary>
    /// One folder of the archive can't be listed (its permissions deny listing to the user: a folder copied from another
    /// account, a security tool). DeleteOlderThan lists every folder (Directory.EnumerateDirectories with
    /// AllDirectories, then Directory.EnumerateFiles of each) with no catch around it: UnauthorizedAccessException leaves it,
    /// and on the hourly timer that ends PuppyMacro, at every start. Expected: that folder is left as it is, the other old
    /// operations go, no exception.
    /// </summary>
    [Fact]
    public void A_folder_the_cleaner_can_t_list_is_left_and_the_others_still_go()
    {
        string old = Operation("old");
        string locked = Operation("locked");
        var me = WindowsIdentity.GetCurrent().User!;
        var info = new DirectoryInfo(locked);
        var deny = new FileSystemAccessRule(me, FileSystemRights.ListDirectory, AccessControlType.Deny);
        DirectorySecurity security = info.GetAccessControl();
        security.AddAccessRule(deny);
        info.SetAccessControl(security);
        try
        {
            var thrown = Record.Exception(() => DebugStore.DeleteOlderThan(DebugStore.Life, DateTime.UtcNow));

            Assert.Null(thrown);
            Assert.False(Directory.Exists(old));
        }
        finally
        {
            security = info.GetAccessControl();
            security.RemoveAccessRule(deny);
            info.SetAccessControl(security);
        }
    }

    /// <summary>
    /// The hourly cleaner and Settings › Delete debug data run at the same time (two thread pool threads): one deletes an
    /// operation's folder while the other lists it, and Directory.EnumerateFiles of a folder just deleted throws
    /// DirectoryNotFoundException, which nothing catches. Expected: both end without an exception and the archive is empty.
    /// Two cleaners run on many folders, many times, so the moment comes up on every run.
    /// </summary>
    [Fact]
    public void Two_cleaners_at_the_same_time_never_fail()
    {
        Exception? failure = null;
        for (int round = 0; round < 30 && failure == null; round++)
        {
            for (int i = 0; i < 150; i++)
                Operation($"r{round}-{i:000}");
            using var start = new ManualResetEventSlim();
            Exception? first = null, second = null;
            var a = new Thread(() => { start.Wait(); first = Record.Exception(() => DebugStore.DeleteOlderThan(TimeSpan.Zero, DateTime.UtcNow)); });
            var b = new Thread(() => { start.Wait(); second = Record.Exception(() => DebugStore.DeleteOlderThan(TimeSpan.Zero, DateTime.UtcNow)); });
            a.Start();
            b.Start();
            start.Set();
            a.Join();
            b.Join();
            failure = first ?? second;
        }

        Assert.Null(failure);
    }
}
