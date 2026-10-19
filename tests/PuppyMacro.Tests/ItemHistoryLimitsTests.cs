using System;
using System.IO;
using System.Linq;
using PuppyMacro.Models;
using PuppyMacro.Services;
using Xunit;
using static PuppyMacro.Tests.Kit;

namespace PuppyMacro.Tests;

/// <summary>
/// The History's size limit (<see cref="ItemHistory.MaxBytes"/> per item, data.md: "the newest versions, up to
/// ItemHistory.MaxBytes per item") when its versions carry images: each image is in the oldest version that names it.
/// </summary>
public class ItemHistoryLimitsTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private DateTime _now = new(2026, 10, 11, 14, 0, 0);

    public void Dispose() => _folder.Dispose();

    private static byte[] Png(int size, int seed) => Enumerable.Range(0, size).Select(i => (byte)(i * seed + 3)).ToArray();

    private static MacroAction LookFor(string image) => If(new MacroCondition
    {
        Kind = ConditionKind.Image,
        Name = "Button",
        Images = new() { new ConditionImage { Id = image, X = 1, Y = 2 } },
        Similarity = 90,
    }, Press());

    private static MacroAction MovePath(int seed) => new()
    {
        Type = MacroActionType.MovePath,
        Path = Enumerable.Range(0, 80).Select(i => new PathPoint { X = i * seed, Y = i, T = i * 8 }).ToList(),
    };

    /// <summary>A version's images go with it to the next oldest version kept, so Restore of any version kept finds them.</summary>
    [Fact]
    public void When_the_oldest_version_goes_its_images_stay_with_the_versions_that_name_them()
    {
        var images = new ImageStore(_folder.File("images"));
        string image = images.Add(Png(2_000, 5));
        var history = new ItemHistory(_folder.File("history"), () => _now = _now.AddMinutes(1), 30_000, images);
        var macro = Macro("Accept", LookFor(image));
        for (int i = 0; i < 40; i++)
        {
            macro.Name = $"Accept {i} " + new string('y', 500);
            history.Record(macro.Id, MacroJson.Serialize(macro), ItemHistory.You);
        }
        images.Delete(image);

        var versions = history.Versions(macro.Id);
        Assert.InRange(versions.Count, 2, 39);
        new ItemHistory(_folder.File("history"), images: images).RestoreImages(macro.Id, versions[0].Json);

        Assert.Equal(Png(2_000, 5), images.Read(image));
    }
}
