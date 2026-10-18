using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace PuppyMacro.Services;

/// <summary>
/// Puts a text on the clipboard and later restores what was there before. One is shared by every loop row and macro.
/// Clipboard access must happen on the UI (STA) thread, so every call is marshalled there.
/// </summary>
/// <remarks>
/// The pastes of loops and macros take turns (<see cref="TakeTurn"/>), so one never changes the clipboard under another.
/// Pastes open at the same time (one inside another) keep the user's clipboard as the first one found it: otherwise each
/// later one would take the text of the one before as "previous", and the user's own clipboard would be lost.
/// </remarks>
internal sealed class ClipboardPaster
{
    // Clipboard formats documented by Windows to keep an entry out of
    // clipboard history (Win+V) and cloud clipboard.
    private const string CanIncludeInHistory = "CanIncludeInClipboardHistory";
    private const string CanUploadToCloud = "CanUploadToCloudClipboard";

    private readonly Dispatcher _dispatcher;

    // One paste at a time: signaled while no paste has the turn.
    private readonly AutoResetEvent _turn = new(true);

    // UI thread only: the pastes not restored yet, in the order they started, and the clipboard before the first of them.
    private readonly List<(DataObject Paste, string Text)> _open = new();
    private DataObject? _users;

    public ClipboardPaster(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>
    /// Waits for this paste's turn: the one before it has restored the clipboard. Dispose the result to give the turn to
    /// the next. Null when <paramref name="stop"/> is signaled first (nothing is pasted).
    /// </summary>
    public IDisposable? TakeTurn(WaitHandle stop) =>
        WaitHandle.WaitAny(new[] { stop, _turn }) == 1 ? new Turn(_turn) : null;

    private sealed class Turn(AutoResetEvent turn) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                turn.Set();
        }
    }

    /// <summary>
    /// Puts <paramref name="text"/> on the clipboard. The first paste open keeps a copy of what the clipboard held (the
    /// user's). Returns this paste, to give to <see cref="Restore"/>; null when the clipboard could not be changed.
    /// </summary>
    public DataObject? Replace(string text)
    {
        try
        {
            return _dispatcher.Invoke(() =>
            {
                DataObject? users = _open.Count == 0 ? TakeSnapshot() : _users;
                var data = TextData(text);
                Clipboard.SetDataObject(data, copy: true);
                _users = users;
                _open.Add((data, text));
                return data;
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Clipboard replace failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Ends a paste that <see cref="Replace"/> returned. When its text was the one on the clipboard, the clipboard gets
    /// back the text of the newest paste still open, or the user's own once none is.
    /// </summary>
    public void Restore(DataObject? paste)
    {
        if (paste == null)
            return;
        try
        {
            _dispatcher.Invoke(() =>
            {
                int index = _open.FindIndex(p => ReferenceEquals(p.Paste, paste));
                if (index < 0)
                    return;
                _open.RemoveAt(index);
                if (index < _open.Count)
                    return; // a newer paste's text is on the clipboard, and stays there
                if (_open.Count > 0)
                {
                    Clipboard.SetDataObject(TextData(_open[^1].Text), copy: true);
                    return;
                }
                var users = _users;
                _users = null;
                if (users == null)
                {
                    Clipboard.Clear();
                    return;
                }
                MarkPrivate(users);
                Clipboard.SetDataObject(users, copy: true);
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Clipboard restore failed: {ex.Message}");
        }
    }

    /// <summary>Copies every format that can be read (text, images, files...). Best effort.</summary>
    private static DataObject? TakeSnapshot()
    {
        IDataObject? current;
        try
        {
            current = Clipboard.GetDataObject();
        }
        catch
        {
            return null;
        }
        if (current == null)
            return null;

        var copy = new DataObject();
        bool any = false;
        foreach (string format in current.GetFormats(autoConvert: false))
        {
            if (format is CanIncludeInHistory or CanUploadToCloud)
                continue;
            try
            {
                object? value = current.GetData(format, autoConvert: false);
                if (value != null)
                {
                    copy.SetData(format, value, autoConvert: false);
                    any = true;
                }
            }
            catch
            {
                // Some formats cannot be read back; skip them.
            }
        }
        return any ? copy : null;
    }

    /// <summary>A paste's text, kept out of clipboard history.</summary>
    private static DataObject TextData(string text)
    {
        var data = new DataObject();
        data.SetText(text, TextDataFormat.UnicodeText);
        MarkPrivate(data);
        return data;
    }

    private static void MarkPrivate(DataObject data)
    {
        data.SetData(CanIncludeInHistory, new MemoryStream(BitConverter.GetBytes(0)), autoConvert: false);
        data.SetData(CanUploadToCloud, new MemoryStream(BitConverter.GetBytes(0)), autoConvert: false);
    }
}
