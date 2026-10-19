using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// ClipboardPaster, the one the engine shares between every loop row and macro that pastes, and a loop's Paste text
/// (ActionRunner). The paster works on a clipboard of the test (on an STA thread of its own, as the UI's), never the
/// PC's: a copy the user makes while the tests run stays theirs. No key is sent: the loop's paste gets keys of the test.
/// </summary>
public sealed class ClipboardPasterTests : IDisposable
{
    private readonly Thread _ui;
    private readonly Dispatcher _dispatcher;
    private readonly FakeClipboard _clipboard = new();
    private readonly ClipboardPaster _paster;

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
        _paster = new ClipboardPaster(_dispatcher, _clipboard);
        // The user's clipboard: the test starts from a text of its own.
        var users = new DataObject();
        users.SetText("the user's text", TextDataFormat.UnicodeText);
        _clipboard.Set(users);
    }

    public void Dispose()
    {
        _dispatcher.InvokeShutdown();
        _ui.Join(TimeSpan.FromSeconds(5));
    }

    /// <summary>A clipboard of the test, read and written only through the paster's UI thread.</summary>
    private sealed class FakeClipboard : ClipboardPaster.IClipboard
    {
        private IDataObject? _data;

        public IDataObject? Get() => _data;

        public void Set(DataObject data) => _data = data;

        public void Clear() => _data = null;

        public string? Text => _data?.GetDataPresent(DataFormats.UnicodeText) == true
            ? (string)_data.GetData(DataFormats.UnicodeText)
            : null;
    }

    private string? ClipboardText() => _dispatcher.Invoke(() => _clipboard.Text);

    /// <summary>The keys of a loop's paste, recorded instead of sent.</summary>
    private sealed class FakePasteKeys : IPasteKeys
    {
        public Func<double, PreciseTimer, WaitHandle, bool> OnEnter = (holdMs, timer, stop) => timer.Wait(holdMs, stop);
        public Action OnPaste = () => { };
        public int Enters, Pastes;

        public bool TapEnter(double holdMs, PreciseTimer timer, WaitHandle stop)
        {
            Enters++;
            return OnEnter(holdMs, timer, stop);
        }

        public void SendPasteShortcut()
        {
            Pastes++;
            OnPaste();
        }
    }

    /// <summary>Runs a loop's Paste text row with the keys of the test until it exits.</summary>
    private void RunLoopPaste(LoopAction action, FakePasteKeys keys, Action<ActionRunner> setUp)
    {
        var runner = new ActionRunner(action, _paster, keys);
        using var exited = new ManualResetEventSlim();
        runner.Exited += _ => exited.Set();
        setUp(runner);
        runner.Start();
        Assert.True(exited.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "the paste did not end");
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

    /// <summary>Stopped while Enter before is held: Ctrl+V is never sent and the clipboard is never changed.</summary>
    [Fact]
    public void A_loops_paste_stopped_during_Enter_before_never_pastes()
    {
        var keys = new FakePasteKeys();
        RunLoopPaste(LoopPaste(enterBefore: true, enterAfter: true), keys, runner =>
            keys.OnEnter = (holdMs, timer, stop) =>
            {
                runner.Stop();
                return timer.Wait(holdMs, stop); // as InputSender.Tap: false, the hold ended by the stop
            });

        Assert.Equal((1, 0), (keys.Enters, keys.Pastes));
        Assert.Equal("the user's text", ClipboardText());
    }

    /// <summary>Stopped once Ctrl+V is sent: the paste completes and the clipboard comes back; only Enter after is skipped.</summary>
    [Fact]
    public void A_loops_paste_stopped_after_Ctrl_V_completes_and_skips_only_Enter_after()
    {
        var keys = new FakePasteKeys();
        string? onClipboard = null;
        RunLoopPaste(LoopPaste(enterBefore: true, enterAfter: true), keys, runner =>
            keys.OnPaste = () =>
            {
                onClipboard = ClipboardText();
                runner.Stop();
            });

        Assert.Equal("pasted", onClipboard);
        Assert.Equal((1, 1), (keys.Enters, keys.Pastes)); // Enter before only
        Assert.Equal("the user's text", ClipboardText());
    }

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

    [Fact]
    public void A_paste_that_ends_before_a_newer_one_leaves_the_newer_text_until_it_ends()
    {
        var first = _paster.Replace("text of the first row");
        var second = _paster.Replace("text of the second row");
        _paster.Restore(second);
        Assert.Equal("text of the first row", ClipboardText());
        _paster.Restore(first);
        Assert.Equal("the user's text", ClipboardText());
    }

    /// <summary>The pastes of loop rows and macros take turns: one never changes the clipboard under another.</summary>
    [Fact]
    public async Task A_paste_waits_for_the_one_before_to_give_its_turn()
    {
        using var never = new ManualResetEvent(false);
        var turn = _paster.TakeTurn(never);
        Assert.NotNull(turn);

        var next = Task.Run(() => _paster.TakeTurn(never), TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(next.IsCompleted, "a paste took the turn while another had it");

        turn!.Dispose();
        turn.Dispose(); // twice gives it only once
        using var nextTurn = await next.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.NotNull(nextTurn);
    }

    [Fact]
    public async Task A_stopped_paste_never_takes_the_turn()
    {
        using var never = new ManualResetEvent(false);
        using var turn = _paster.TakeTurn(never);
        using var stop = new ManualResetEvent(false);

        var waiting = Task.Run(() => _paster.TakeTurn(stop), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        stop.Set();

        Assert.Null(await waiting.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
    }
}
