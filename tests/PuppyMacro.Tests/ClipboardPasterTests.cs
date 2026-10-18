using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// ClipboardPaster, the one the engine shares between every loop row and macro that pastes. It uses the PC's real
/// clipboard (on an STA thread of its own, as the UI's): what was there before the test is put back at the end. No key
/// is sent.
/// </summary>
public sealed class ClipboardPasterTests : IDisposable
{
    private readonly Thread _ui;
    private readonly Dispatcher _dispatcher;
    private readonly ClipboardPaster _paster;
    private readonly DataObject? _users;

    public ClipboardPasterTests()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        _ui = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        { IsBackground = true, Name = "Clipboard test UI" };
        _ui.SetApartmentState(ApartmentState.STA);
        _ui.Start();
        ready.Wait();
        _dispatcher = dispatcher!;
        _paster = new ClipboardPaster(_dispatcher);
        // The user's clipboard, put back at the end; the test starts from a text of its own.
        _users = _paster.Replace("the user's text");
    }

    public void Dispose()
    {
        _paster.Restore(_users);
        _dispatcher.InvokeShutdown();
        _ui.Join(TimeSpan.FromSeconds(5));
    }

    private string? ClipboardText() => _dispatcher.Invoke(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);

    [Fact]
    public void One_paste_puts_its_text_then_gives_the_clipboard_back()
    {
        var previous = _paster.Replace("pasted");
        Assert.Equal("pasted", ClipboardText());
        _paster.Restore(previous);
        Assert.Equal("the user's text", ClipboardText());
    }

    /// <summary>
    /// Two pastes that overlap (two Paste text rows of one loop, a loop and a macro): each takes the clipboard as it finds
    /// it as "previous" (MacroRunner.Paste, ActionRunner.PasteText). The second takes the first one's text, and as each
    /// gives back what it took, in the order they started, the first one's text stays on the clipboard: the user's own
    /// clipboard is lost.
    /// </summary>
    [Fact]
    public void Two_pastes_that_overlap_give_the_users_clipboard_back()
    {
        var first = _paster.Replace("text of the first row");
        var second = _paster.Replace("text of the second row");
        _paster.Restore(first);
        _paster.Restore(second);

        Assert.Equal("the user's text", ClipboardText());
    }
}
