using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// ClipboardPaster and a loop's Paste text (ActionRunner) when the clipboard misbehaves: Windows' clipboard can be held
/// open by another app (a clipboard manager, a remote desktop session), so a read or a write fails after WPF's own
/// retries (CLIPBRD_E_CANT_OPEN). Also the order of the keys of a paste, the formats of the user's clipboard, an empty
/// clipboard, the paste kept out of clipboard history. The paster works on a clipboard of the test (on an STA thread of
/// its own, as the UI's), never the PC's, and the loop's paste gets keys of the test: no key is sent.
/// </summary>
public sealed class ClipboardPasterEdgesTests : IDisposable
{
    private const int CantOpen = unchecked((int)0x800401D0); // CLIPBRD_E_CANT_OPEN

    private readonly Thread _ui;
    private readonly Dispatcher _dispatcher;
    private readonly FakeClipboard _clipboard = new();
    private readonly ClipboardPaster _paster;

    public ClipboardPasterEdgesTests()
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        _ui = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        { IsBackground = true, Name = "Clipboard edges test UI" };
        _ui.SetApartmentState(ApartmentState.STA);
        _ui.Start();
        ready.Wait();
        _dispatcher = dispatcher!;
        _paster = new ClipboardPaster(_dispatcher, _clipboard);
        var users = new DataObject();
        users.SetText("the user's text", TextDataFormat.UnicodeText);
        _clipboard.Set(users);
    }

    public void Dispose()
    {
        _dispatcher.InvokeShutdown();
        _ui.Join(TimeSpan.FromSeconds(5));
    }

    /// <summary>A clipboard of the test whose next reads or writes can fail, as Windows' does while another app holds it open.</summary>
    private sealed class FakeClipboard : ClipboardPaster.IClipboard
    {
        private IDataObject? _data;

        public int FailingGets, FailingSets;

        public IDataObject? Get()
        {
            if (FailingGets > 0)
            {
                FailingGets--;
                throw new COMException("OpenClipboard failed", CantOpen);
            }
            return _data;
        }

        public void Set(DataObject data)
        {
            if (FailingSets > 0)
            {
                FailingSets--;
                throw new COMException("OpenClipboard failed", CantOpen);
            }
            _data = data;
        }

        public void Clear() => _data = null;

        public IDataObject? Data => _data;

        public string? Text => _data?.GetDataPresent(DataFormats.UnicodeText) == true
            ? (string)_data.GetData(DataFormats.UnicodeText)
            : null;
    }

    private string? ClipboardText() => _dispatcher.Invoke(() => _clipboard.Text);

    /// <summary>The keys of a loop's paste, recorded in order instead of sent.</summary>
    private sealed class FakePasteKeys : IPasteKeys
    {
        private readonly Func<string?> _clipboardText;
        public readonly List<string> Sent = new();
        public Action OnPaste = () => { };

        public FakePasteKeys(Func<string?> clipboardText) => _clipboardText = clipboardText;

        public bool TapEnter(double holdMs, PreciseTimer timer, WaitHandle stop)
        {
            lock (Sent)
                Sent.Add("Enter");
            return timer.Wait(holdMs, stop);
        }

        public void SendPasteShortcut()
        {
            lock (Sent)
                Sent.Add($"Ctrl+V ({_clipboardText()})");
            OnPaste();
        }
    }

    private static LoopAction LoopPaste(bool enterBefore, bool enterAfter) => new()
    {
        Type = ActionType.Text,
        Text = "pasted",
        EnterBefore = enterBefore,
        EnterAfter = enterAfter,
        IntervalValue = 10,
        IntervalUnit = IntervalUnit.Seconds,
    };

    /// <summary>
    /// Starts a loop's Paste text row with the keys of the test, lets its first paste play (it takes about 200 ms; the next
    /// one would come 10 s later), stops it and waits for its end.
    /// </summary>
    private void RunFirstPaste(LoopAction action, FakePasteKeys keys)
    {
        var runner = new ActionRunner(action, _paster, keys);
        using var exited = new ManualResetEventSlim();
        runner.Exited += _ => exited.Set();
        runner.Start();
        Thread.Sleep(1000);
        runner.Stop();
        Assert.True(exited.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "the paste did not end");
    }

    // ================= The clipboard fails =================

    /// <summary>
    /// A loop's Paste text (or a macro's: MacroRunner.Paste is the same code) while another app holds the clipboard open:
    /// ClipboardPaster.Replace cannot put the text and returns null, but the paste goes on and sends Ctrl+V, so the app in
    /// front gets the user's own clipboard (a password, a private message) instead of the text, and Enter after sends it.
    /// Expected: nothing is pasted when the text could not be put on the clipboard.
    /// </summary>
    [Fact]
    public void A_paste_whose_text_could_not_be_put_on_the_clipboard_never_pastes_the_users_clipboard()
    {
        var keys = new FakePasteKeys(ClipboardText);
        _dispatcher.Invoke(() => _clipboard.FailingSets = 100); // held open by another app for the whole paste

        RunFirstPaste(LoopPaste(enterBefore: false, enterAfter: true), keys);

        Assert.Equal("the user's text", ClipboardText());
        Assert.Empty(keys.Sent); // observed: "Ctrl+V (the user's text)", then "Enter"
    }

    /// <summary>
    /// The paste could not read the user's clipboard (another app held it open for a moment), then put its text: at the end
    /// ClipboardPaster.Restore finds no copy of the user's clipboard and clears it. The user's clipboard is lost, while the
    /// rule (input.md) is that the user's own clipboard always comes back. Expected: the user's text is on the clipboard
    /// once the paste ends (a paste that can't keep the user's clipboard should not change it at all).
    /// </summary>
    [Fact]
    public void A_paste_that_could_not_read_the_users_clipboard_never_clears_it()
    {
        _dispatcher.Invoke(() => _clipboard.FailingGets = 1);

        var paste = _paster.Replace("pasted");
        _paster.Restore(paste);

        Assert.Equal("the user's text", ClipboardText()); // observed: null, the clipboard was cleared
    }

    /// <summary>
    /// The clipboard is held open by another app just when the paste gives the user's clipboard back: the one write fails,
    /// and ClipboardPaster.Restore has already forgotten the copy of the user's clipboard, so the paste's text stays on the
    /// clipboard for good. Expected: the user's own clipboard always comes back (input.md), so a failed restore is tried
    /// again rather than given up.
    /// </summary>
    [Fact]
    public void A_restore_that_fails_once_still_gives_the_users_clipboard_back()
    {
        var paste = _paster.Replace("pasted");
        Assert.Equal("pasted", ClipboardText());
        _dispatcher.Invoke(() => _clipboard.FailingSets = 1);

        _paster.Restore(paste);

        Assert.Equal("the user's text", ClipboardText()); // observed: "pasted"
    }

    [Fact]
    public void A_paste_whose_text_could_not_be_put_leaves_the_clipboard_and_the_next_paste_as_they_were()
    {
        _dispatcher.Invoke(() => _clipboard.FailingSets = 1);
        Assert.Null(_paster.Replace("pasted"));
        Assert.Equal("the user's text", ClipboardText());

        // Nothing stays open: the next paste takes the user's clipboard as "previous" and gives it back.
        var next = _paster.Replace("second");
        Assert.Equal("second", ClipboardText());
        _paster.Restore(next);
        Assert.Equal("the user's text", ClipboardText());
    }

    // ================= Keys, formats, history =================

    /// <summary>Enter before, Ctrl+V with the paste's text on the clipboard, Enter after; then the user's text is back.</summary>
    [Fact]
    public void A_loops_paste_sends_Enter_before_Ctrl_V_with_its_text_and_Enter_after()
    {
        var keys = new FakePasteKeys(ClipboardText);

        RunFirstPaste(LoopPaste(enterBefore: true, enterAfter: true), keys);

        Assert.Equal(new[] { "Enter", "Ctrl+V (pasted)", "Enter" }, keys.Sent);
        Assert.Equal("the user's text", ClipboardText());
    }

    [Fact]
    public void A_loops_paste_stopped_while_waiting_for_its_turn_sends_nothing_and_leaves_the_clipboard()
    {
        var keys = new FakePasteKeys(ClipboardText);
        using var never = new ManualResetEvent(false);
        var turn = _paster.TakeTurn(never); // another loop row or macro is pasting
        try
        {
            RunFirstPaste(LoopPaste(enterBefore: true, enterAfter: true), keys);
        }
        finally
        {
            turn!.Dispose();
        }

        Assert.Empty(keys.Sent);
        Assert.Equal("the user's text", ClipboardText());
    }

    [Fact]
    public void The_users_clipboard_comes_back_with_every_format_it_had()
    {
        _dispatcher.Invoke(() =>
        {
            var users = new DataObject();
            users.SetText("the user's text", TextDataFormat.UnicodeText);
            users.SetData("PuppyMacro.Tests.Custom", "custom value", autoConvert: false);
            _clipboard.Set(users);
        });

        var paste = _paster.Replace("pasted");
        _paster.Restore(paste);

        _dispatcher.Invoke(() =>
        {
            Assert.Equal("the user's text", _clipboard.Text);
            Assert.Equal("custom value", _clipboard.Data!.GetData("PuppyMacro.Tests.Custom", autoConvert: false));
        });
    }

    [Fact]
    public void An_empty_clipboard_is_empty_again_after_a_paste()
    {
        _dispatcher.Invoke(() => _clipboard.Clear());

        var paste = _paster.Replace("pasted");
        Assert.Equal("pasted", ClipboardText());
        _paster.Restore(paste);

        Assert.Null(_dispatcher.Invoke(() => _clipboard.Data));
    }

    /// <summary>The paste's text is kept out of clipboard history (Win+V) and the cloud clipboard, with Windows' formats.</summary>
    [Fact]
    public void A_pastes_text_is_kept_out_of_clipboard_history_and_the_cloud()
    {
        var paste = _paster.Replace("pasted");
        try
        {
            _dispatcher.Invoke(() =>
            {
                foreach (string format in new[] { "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard" })
                {
                    var value = Assert.IsType<MemoryStream>(_clipboard.Data!.GetData(format, autoConvert: false));
                    Assert.Equal(0, BitConverter.ToInt32(value.ToArray()));
                }
            });
        }
        finally
        {
            _paster.Restore(paste);
        }
    }

    [Fact]
    public void A_restore_of_nothing_or_twice_changes_nothing()
    {
        _paster.Restore(null);
        Assert.Equal("the user's text", ClipboardText());

        var paste = _paster.Replace("pasted");
        _paster.Restore(paste);
        _dispatcher.Invoke(() =>
        {
            var other = new DataObject();
            other.SetText("copied by the user after", TextDataFormat.UnicodeText);
            _clipboard.Set(other);
        });
        _paster.Restore(paste);

        Assert.Equal("copied by the user after", ClipboardText());
    }
}
