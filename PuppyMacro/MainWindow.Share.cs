using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using PuppyMacro.Models;
using PuppyMacro.Resources;
using PuppyMacro.Services;
using PuppyMacro.Services.Ai;
using PuppyMacro.Views;
using ContentDialog = iNKORE.UI.WPF.Modern.Controls.ContentDialog;
using ContentDialogButton = iNKORE.UI.WPF.Modern.Controls.ContentDialogButton;
using ContentDialogResult = iNKORE.UI.WPF.Modern.Controls.ContentDialogResult;

namespace PuppyMacro;

/// <summary>
/// Sharing loops, macros and remaps as .puppyitems files (<see cref="ItemFile"/>): Import and Export in the title bar, Export…
/// in each card's menu, a file opened from File Explorer, and the same windows for AI apps. Not the backup (Settings › Backup).
/// </summary>
public partial class MainWindow
{
    private static string ItemsFilter => string.Format(CultureInfo.CurrentCulture, Strings.Share_FileFilter, ItemFile.Extension);

    /// <summary>Files opened from File Explorer, waiting until PuppyMacro has no editor or dialog open.</summary>
    private readonly Queue<string> _filesToOpen = new();
    private DispatcherTimer? _filesToOpenTimer;

    private async void OnImportItemsClick(object sender, RoutedEventArgs e) => await ImportItems(null, null, null);

    private async void OnExportItemsClick(object sender, RoutedEventArgs e) => await ExportItems(Array.Empty<Guid>(), null);

    /// <summary>The card menu's Export…: the file dialog at once, for this item alone.</summary>
    private void ExportOne(object item) => SaveItemsFile(new List<object> { item }, out _);

    /// <summary>
    /// Import: the items of a file, checked, then added (new Ids, disabled), recorded as <paramref name="app"/>'s or the user's.
    /// </summary>
    /// <param name="path">The file; null with no <paramref name="text"/>: the user picks one first.</param>
    /// <param name="text">The file's text, given by an AI app instead of a file.</param>
    /// <param name="app">The AI app that opened Import, or null for the user.</param>
    /// <returns>Done with the items added, or why nothing was (cancelled, timed out for an AI app).</returns>
    private async Task<(AiAnswer Answer, List<object> Items)> ImportItems(string? path, string? text, string? app)
    {
        if (path == null && text == null)
        {
            if (app != null)
            {
                ShowFromTray();
                Activate();
            }
            path = PickItemsFile();
            if (path == null)
                return (AiAnswer.Cancelled, new List<object>());
        }

        var view = new ImportItemsView();
        var dialog = new ContentDialog
        {
            Title = Strings.Share_Import,
            Content = view,
            CloseButtonText = Strings.Main_Cancel,
            DefaultButton = ContentDialogButton.Primary,
        };
        void Load(string? file, string? given)
        {
            string name = file != null ? Path.GetFileName(file) : string.Format(CultureInfo.CurrentCulture, Strings.ImportItems_FromApp, app);
            string? content = given;
            string? error = null;
            if (content == null)
            {
                try
                {
                    content = File.ReadAllText(file!);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    error = string.Format(CultureInfo.CurrentCulture, Strings.ImportItems_ReadFailed, ex.Message);
                }
            }
            view.Show(name, only => error != null
                ? new ItemFileContent { Error = error }
                : ItemFile.Read(content!, _settings, _macros, ItemFile.AppVersion, only));
        }
        void RefreshButton()
        {
            if (view.Error != null)
            {
                // A file of a newer PuppyMacro: its button opens Updates.
                bool newer = view.NeedsUpdate && _updates.IsInstalled;
                dialog.PrimaryButtonText = newer ? Strings.ImportItems_CheckForUpdates : "";
                dialog.IsPrimaryButtonEnabled = newer;
                return;
            }
            int count = view.ChosenCount;
            dialog.PrimaryButtonText = Plurals.Format("ImportItems_Button", count);
            dialog.IsPrimaryButtonEnabled = count > 0;
        }
        view.ChoiceChanged += RefreshButton;
        view.OtherFileRequested += () =>
        {
            if (PickItemsFile() is string other)
                Load(other, null);
        };
        Load(path, text);

        (ContentDialogResult result, bool timedOut) = await ShowItemsDialog(dialog, app != null);
        if (timedOut)
            return (AiAnswer.TimedOut, new List<object>());
        if (result != ContentDialogResult.Primary)
            return (AiAnswer.Cancelled, new List<object>());
        if (view.Error != null)
        {
            Rail.Select("Settings");
            _ = CheckForUpdatesAsync();
            return (AiAnswer.Cancelled, new List<object>());
        }

        List<object> items = view.ChosenItems();
        SavingAs(app ?? ItemHistory.You, () =>
        {
            foreach (object item in items)
                SaveItem(item);
            return true;
        });
        if (items.Count > 0)
            Rail.Select(items[0] switch
            {
                LoopDefinition => "Loops",
                MacroDefinition => "Macros",
                _ => "Remap",
            });
        return (AiAnswer.Done, items);
    }

    /// <summary>Export: the user chooses items, then where to save them.</summary>
    /// <param name="chosen">The items checked at first.</param>
    /// <param name="app">The AI app that opened Export, or null for the user.</param>
    private async Task<(AiAnswer Answer, List<object> Items, string? File)> ExportItems(ICollection<Guid> chosen, string? app)
    {
        var all = _items.Select(i => ((object)i.Definition, i.Definition.Id))
            .Concat(_macroItems.Select(m => ((object)m.Definition, m.Definition.Id)))
            .Concat(_remapItems.Select(r => ((object)r.Definition, r.Definition.Id)));
        var view = new ExportItemsView(all, chosen);
        var dialog = new ContentDialog
        {
            Title = Strings.Share_Export,
            Content = view,
            CloseButtonText = Strings.Main_Cancel,
            DefaultButton = ContentDialogButton.Primary,
        };
        void RefreshButton()
        {
            int count = view.ChosenCount;
            dialog.PrimaryButtonText = Plurals.Format("ExportItems_Button", count);
            dialog.IsPrimaryButtonEnabled = count > 0;
        }
        view.ChoiceChanged += RefreshButton;
        RefreshButton();

        (ContentDialogResult result, bool timedOut) = await ShowItemsDialog(dialog, app != null);
        if (timedOut)
            return (AiAnswer.TimedOut, new List<object>(), null);
        if (result != ContentDialogResult.Primary)
            return (AiAnswer.Cancelled, new List<object>(), null);
        List<object> items = view.ChosenItems;
        return SaveItemsFile(items, out string? file)
            ? (AiAnswer.Done, items, file)
            : (AiAnswer.Cancelled, new List<object>(), null);
    }

    /// <summary>
    /// Shows Import or Export. For an AI app, this window comes to the front first and the dialog closes by itself when nobody
    /// used PuppyMacro for <see cref="AiTools.AnswerTimeout"/>, as the other windows of AI requests do.
    /// </summary>
    private async Task<(ContentDialogResult Result, bool TimedOut)> ShowItemsDialog(ContentDialog dialog, bool forAi)
    {
        DispatcherTimer? timer = null;
        bool timedOut = false;
        void OnInput(object sender, PreProcessInputEventArgs e)
        {
            if (e.StagingItem.Input is MouseEventArgs or KeyEventArgs && timer!.IsEnabled)
            {
                timer.Stop();
                timer.Start();
            }
        }
        if (forAi)
        {
            ShowFromTray();
            Topmost = true; // the AI app is in front, and Windows may not let PuppyMacro take the focus
            Activate();
            timer = new DispatcherTimer { Interval = AiTools.AnswerTimeout };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                timedOut = true;
                dialog.Hide();
            };
            timer.Start();
            InputManager.Current.PreProcessInput += OnInput;
        }

        _userDialogs++;
        try
        {
            return (await dialog.ShowAsync(this), timedOut);
        }
        finally
        {
            _userDialogs--;
            if (timer != null)
            {
                timer.Stop();
                InputManager.Current.PreProcessInput -= OnInput;
                Topmost = false;
            }
        }
    }

    private string? PickItemsFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Strings.Share_ImportFileTitle,
            Filter = ItemsFilter,
        };
        return ShowUserDialog(() => dialog.ShowDialog(this)) == true ? dialog.FileName : null;
    }

    /// <summary>The file dialog, then the file. False when the user cancelled or it could not be written (said in a dialog).</summary>
    private bool SaveItemsFile(List<object> items, out string? file)
    {
        file = null;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Strings.Share_ExportFileTitle,
            FileName = ItemFile.FileName(items),
            Filter = ItemsFilter,
            DefaultExt = ItemFile.Extension,
        };
        if (ShowUserDialog(() => dialog.ShowDialog(this)) != true)
            return false;
        try
        {
            File.WriteAllText(dialog.FileName, ItemFile.Write(items, ItemFile.AppVersion));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = ShowMessage(Strings.ExportItems_Failed, ex.Message);
            return false;
        }
        file = dialog.FileName;
        return true;
    }

    private async Task ShowMessage(string title, string text)
    {
        var message = new ContentDialog { Title = title, Content = text, CloseButtonText = Strings.Share_Ok, DefaultButton = ContentDialogButton.Close };
        _userDialogs++;
        try
        {
            await message.ShowAsync(this);
        }
        finally
        {
            _userDialogs--;
        }
    }

    // ================= Files opened from File Explorer =================

    /// <summary>
    /// A .puppyitems file opened from File Explorer (this process started with it, or another one passed it on): Import shows
    /// it once PuppyMacro has no editor or dialog open.
    /// </summary>
    internal void OpenItemsFile(string path)
    {
        _filesToOpen.Enqueue(path);
        if (!IsLoaded)
        {
            void OnLoaded(object sender, RoutedEventArgs e)
            {
                Loaded -= OnLoaded;
                OpenWaitingFiles();
            }
            Loaded += OnLoaded;
            return;
        }
        OpenWaitingFiles();
    }

    private async void OpenWaitingFiles()
    {
        if (_engine.HotkeysSuspended || _userDialogs > 0)
        {
            // An editor or a dialog is open: try again in a moment.
            _filesToOpenTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) =>
            {
                _filesToOpenTimer!.Stop();
                OpenWaitingFiles();
            }, Dispatcher);
            _filesToOpenTimer.Start();
            return;
        }
        if (!_filesToOpen.TryDequeue(out string? path))
            return;
        ShowFromTray();
        await ImportItems(path, null, null);
        if (_filesToOpen.Count > 0)
            OpenWaitingFiles();
    }

    // ================= AI apps =================

    Task<(AiAnswer Answer, List<object> Items)> IAiHost.ShowImport(string app, string? text) => ImportItems(null, text, app);

    Task<(AiAnswer Answer, List<object> Items, string? File)> IAiHost.ShowExport(string app, IReadOnlyCollection<Guid> ids) =>
        ExportItems(ids.ToList(), app);
}
