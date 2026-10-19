using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;
using static PuppyMacro.Tests.Kit;

namespace PuppyMacro.Tests;

/// <summary>
/// Which images <see cref="MacroLibrary.ReleaseUnusedImages"/> keeps: those a macro needs, also when its save failed, those
/// only a call or a component's parameter names, and those the History brings back for a deleted macro.
/// </summary>
public class MacroLibraryImagesTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private string MacrosFolder => _folder.File("macros");

    private MacroLibrary NewLibrary() => new(MacrosFolder, _folder.File("images"));

    private static byte[] Png(int seed) => Enumerable.Range(0, 64).Select(i => (byte)(i * seed + 1)).ToArray();

    private static MacroAction LookFor(string image) => If(new MacroCondition
    {
        Kind = ConditionKind.Image,
        Name = "Button",
        Images = new() { new ConditionImage { Id = image, X = 1, Y = 2 } },
        Similarity = 90,
    }, Press());

    /// <summary>
    /// The user takes an image in the macro editor and saves; the editor closes (its images are not held any more) and the
    /// macro file can't be written (read only, held by another program): MainWindow.SaveMacro shows the error, then calls
    /// ReleaseUnusedImages, which counts the images of the file on disk (the old version) and deletes the new image. The macro
    /// in memory, on its card, still names it: when it is saved again later, its condition says the image is missing.
    /// </summary>
    [Fact]
    public void An_image_of_a_macro_whose_save_failed_is_kept_for_the_next_save()
    {
        var macros = NewLibrary();
        var macro = Macro("Accept", Press());
        Assert.True(macros.TrySave(macro, out _));
        macros.Macros.Add(macro);

        object editor = new();
        string image = macros.Images.Add(Png(3));
        macros.Images.HoldAlso(editor, image); // taken in the editor
        macro.Actions.Insert(0, LookFor(image));
        macros.Images.Unhold(editor); // the editor closed with Save
        string file = Path.Combine(MacrosFolder, $"{macro.Id}.json");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        try
        {
            Assert.False(macros.TrySave(macro, out _)); // the error is shown
            macros.ReleaseUnusedImages();
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Assert.True(macros.TrySave(macro, out _)); // saved again, the file free
        Assert.True(macros.Images.Has(image), "the saved macro names an image that was deleted");
    }

    /// <summary>An image given to a component only as a call's argument, or only as a parameter's example or default, stays.</summary>
    [Fact]
    public void Images_named_only_by_a_call_or_by_a_component_s_parameter_stay()
    {
        var macros = NewLibrary();
        string argument = macros.Images.Add(Png(5));
        string example = macros.Images.Add(Png(7));
        string fallback = macros.Images.Add(Png(11));
        string unused = macros.Images.Add(Png(13));
        var look = Comp("Look", new[] { Param("Picture", ParameterType.Image, Images(example), Images(fallback)) }, Press());
        macros.Library.Collections.Add(new LibraryCollection { Name = "Game", Components = { look } });
        Assert.True(macros.Library.TrySave(out _));
        Assert.True(macros.TrySave(Macro("Calls", Call(look, ("Picture", Images(argument)))), out _));

        var restarted = NewLibrary();
        restarted.Load(new List<Guid>(), out _);
        restarted.Library.Load(out _);
        restarted.ReleaseUnusedImages();

        Assert.True(restarted.Images.Has(argument));
        Assert.True(restarted.Images.Has(example));
        Assert.True(restarted.Images.Has(fallback));
        Assert.False(restarted.Images.Has(unused));
    }

    /// <summary>
    /// A deleted macro's images go at the next release; the History kept a copy of them, so its Restore brings them back and
    /// the restored macro's condition finds them.
    /// </summary>
    [Fact]
    public void A_deleted_macro_restored_from_the_History_gets_its_images_back()
    {
        var macros = NewLibrary();
        var history = new ItemHistory(_folder.File("history"), images: macros.Images);
        string image = macros.Images.Add(Png(17));
        var macro = Macro("Accept", LookFor(image));
        Assert.True(macros.TrySave(macro, out _));
        macros.Macros.Add(macro);
        history.Record(macro.Id, MacroJson.Serialize(macro), ItemHistory.You);

        macros.Delete(macro);
        history.RecordDeleted(macro.Id, ItemHistory.You);
        macros.ReleaseUnusedImages();
        Assert.False(macros.Images.Has(image));

        HistoryVersion deleted = history.Versions(macro.Id)[0];
        Assert.True(deleted.Deleted);
        new ItemHistory(_folder.File("history"), images: macros.Images).RestoreImages(macro.Id, deleted.Json);

        Assert.Equal(Png(17), macros.Images.Read(image));
    }
}
