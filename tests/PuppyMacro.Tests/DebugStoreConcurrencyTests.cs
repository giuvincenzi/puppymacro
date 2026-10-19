using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// The debug archive used from several threads at once, as the app does: the hourly cleaner (a System.Threading.Timer in
/// App.OnStartup, where an exception that escapes ends the process), Settings › Debug data's Delete (a pool thread), the
/// debug writer thread, and the UI thread that looks for a macro's last run.
/// </summary>
[Collection(nameof(DebugStore))]
public class DebugStoreConcurrencyTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public DebugStoreConcurrencyTests() => DebugStore.Root = _folder.File("debug");

    public void Dispose() => _folder.Dispose();

    private static void OldOperations(int count)
    {
        for (int i = 0; i < count; i++)
        {
            string folder = Path.Combine(DebugStore.Root, "user", "macro", $"20260101-000000-{i:000}-macro");
            Directory.CreateDirectory(folder);
            string log = Path.Combine(folder, RunLog.LogFile);
            File.WriteAllText(log, "old");
            File.SetLastWriteTimeUtc(log, DateTime.UtcNow.AddDays(-8));
        }
    }

    /// <summary>
    /// The hourly cleaner and the user's Delete debug data run at the same moment (two threads, as App's Timer and
    /// MainWindow.OnDeleteDebugDataClick's Task.Run do): each lists the operations, then the other deletes a folder it
    /// listed. A folder that is gone must be skipped; an exception out of DeleteOlderThan on the Timer's thread ends
    /// PuppyMacro.
    /// </summary>
    [Fact]
    public void Two_cleaners_at_once_never_throw()
    {
        var errors = new ConcurrentQueue<Exception>();
        for (int round = 0; round < 20 && errors.IsEmpty; round++)
        {
            OldOperations(150);
            using var start = new Barrier(2);
            var threads = new List<Thread>();
            foreach (TimeSpan age in new[] { DebugStore.Life, TimeSpan.Zero })
            {
                var thread = new Thread(() =>
                {
                    start.SignalAndWait();
                    try
                    {
                        DebugStore.DeleteOlderThan(age, DateTime.UtcNow);
                    }
                    catch (Exception ex)
                    {
                        errors.Enqueue(ex);
                    }
                }) { IsBackground = true };
                threads.Add(thread);
                thread.Start();
            }
            foreach (Thread thread in threads)
                Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        }

        Assert.True(errors.IsEmpty, errors.TryPeek(out Exception? first) ? first.ToString() : "");
    }

    /// <summary>
    /// A folder of the archive that can't be listed (its permissions changed, or it is being deleted) is left for the next
    /// time, like a folder that can't be deleted: the cleaner must not throw (on the Timer's thread that ends PuppyMacro).
    /// </summary>
    [Fact]
    public void A_folder_that_cannot_be_listed_never_stops_the_cleaner()
    {
        OldOperations(1);
        string locked = Path.Combine(DebugStore.Root, "user", "locked");
        Directory.CreateDirectory(locked);
        var info = new DirectoryInfo(locked);
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var deny = new System.Security.AccessControl.FileSystemAccessRule(user,
            System.Security.AccessControl.FileSystemRights.ListDirectory, System.Security.AccessControl.AccessControlType.Deny);
        var security = info.GetAccessControl();
        security.AddAccessRule(deny);
        info.SetAccessControl(security);
        try
        {
            Exception? error = Record.Exception(() => DebugStore.DeleteOlderThan(DebugStore.Life, DateTime.UtcNow));
            Assert.Null(error);
        }
        finally
        {
            security = info.GetAccessControl();
            security.RemoveAccessRule(deny);
            info.SetAccessControl(security);
        }
    }

    /// <summary>The writer thread survives a write that throws, and the ones after it still run in order.</summary>
    [Fact]
    public void A_write_that_throws_never_stops_the_writer()
    {
        var done = new List<int>();
        DebugStore.Write(() => done.Add(1));
        DebugStore.Write(() => throw new IOException("disk full"));
        DebugStore.Write(() => throw new InvalidOperationException("a bug"));
        DebugStore.Write(() => done.Add(2));
        Assert.True(DebugStore.WaitWritten(TimeSpan.FromSeconds(10)));
        Assert.Equal(new[] { 1, 2 }, done);
    }

    /// <summary>
    /// Resource URIs an AI app could build to leave the archive: encoded "..", a backslash, an NTFS stream, a drive, a
    /// UNC path. None names a file.
    /// </summary>
    [Theory]
    [InlineData("puppymacro://debug/user/macro/run/%2e%2e/%2e%2e/%2e%2e/secret.txt")]
    [InlineData("puppymacro://debug/user/macro/run/..%2F..%2F..%2Fsecret.txt")]
    [InlineData("puppymacro://debug/user%5Cmacro%5Crun%5Clog.txt")]
    [InlineData("puppymacro://debug/user/macro/run/log.txt::$DATA")]
    [InlineData("puppymacro://debug/C:/secret.txt")]
    [InlineData("puppymacro://debug//server/share/secret.txt")]
    [InlineData("puppymacro://debug/user/macro/run/LOG~1.TXT")]
    [InlineData("puppymacro://debug/user/macro/run/log.txt.")]
    public void Encoded_or_disguised_paths_never_leave_the_archive(string uri)
    {
        string folder = Path.Combine(DebugStore.Root, "user", "macro", "run");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "log.txt"), "log");
        File.WriteAllText(_folder.File("secret.txt"), "secret");

        Assert.Null(DebugStore.PathOf(uri));
    }
}
