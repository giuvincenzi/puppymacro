using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace PuppyMacro.Services;

/// <summary>
/// Puts a text on the clipboard and later restores what was there before.
/// Clipboard access must happen on the UI (STA) thread, so every call is marshalled there.
/// </summary>
internal sealed class ClipboardPaster
{
    // Clipboard formats documented by Windows to keep an entry out of
    // clipboard history (Win+V) and cloud clipboard.
    private const string CanIncludeInHistory = "CanIncludeInClipboardHistory";
    private const string CanUploadToCloud = "CanUploadToCloudClipboard";

    private readonly Dispatcher _dispatcher;

    public ClipboardPaster(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>Replaces the clipboard with <paramref name="text"/>. Returns a snapshot of the previous content (null if empty).</summary>
    public DataObject? Replace(string text)
    {
        try
        {
            return _dispatcher.Invoke(() =>
            {
                DataObject? snapshot = TakeSnapshot();
                var data = new DataObject();
                data.SetText(text, TextDataFormat.UnicodeText);
                MarkPrivate(data);
                Clipboard.SetDataObject(data, copy: true);
                return snapshot;
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Clipboard replace failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Puts back the snapshot returned by <see cref="Replace"/>.</summary>
    public void Restore(DataObject? snapshot)
    {
        try
        {
            _dispatcher.Invoke(() =>
            {
                if (snapshot == null)
                {
                    Clipboard.Clear();
                    return;
                }
                MarkPrivate(snapshot);
                Clipboard.SetDataObject(snapshot, copy: true);
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

    private static void MarkPrivate(DataObject data)
    {
        data.SetData(CanIncludeInHistory, new MemoryStream(BitConverter.GetBytes(0)), autoConvert: false);
        data.SetData(CanUploadToCloud, new MemoryStream(BitConverter.GetBytes(0)), autoConvert: false);
    }
}
