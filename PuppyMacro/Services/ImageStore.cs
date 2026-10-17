using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using PuppyMacro.Models;

namespace PuppyMacro.Services;

/// <summary>
/// The images of the macros' Image conditions: <c>images\{id}.png</c> in the data folder, where the id is the SHA-256
/// of the PNG file (the same image taken twice is one file). A macro names its images by id. An image is deleted when
/// no macro uses it any more (<see cref="MacroLibrary.ReleaseUnusedImages"/>); the History and the AI activity keep a
/// copy of the images of their versions, so Restore brings them back.
/// </summary>
internal sealed partial class ImageStore
{
    public ImageStore(string folder) => Folder = folder;

    public string Folder { get; }

    public static string IdOf(byte[] png) => Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();

    public string PathOf(string id) => Path.Combine(Folder, id + ".png");

    public bool Has(string id) => MacroJson.IsImageId(id) && File.Exists(PathOf(id));

    /// <summary>Keeps a PNG and returns its id; an image already kept is not written again.</summary>
    public string Add(byte[] png)
    {
        string id = IdOf(png);
        Write(id, png);
        return id;
    }

    public byte[]? Read(string id)
    {
        if (!MacroJson.IsImageId(id))
            return null;
        try
        {
            return File.Exists(PathOf(id)) ? File.ReadAllBytes(PathOf(id)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Delete(string id)
    {
        if (!MacroJson.IsImageId(id))
            return;
        try
        {
            File.Delete(PathOf(id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover file: harmless, and in the backup like the rest.
        }
    }

    /// <summary>The images as Base64 by id (only those kept here), to travel with a History version, an AI change or a file.</summary>
    public Dictionary<string, string>? Embed(IEnumerable<string> ids)
    {
        var images = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string id in ids.Distinct())
            if (Read(id) is { } png)
                images[id] = Convert.ToBase64String(png);
        return images.Count == 0 ? null : images;
    }

    /// <summary>Puts back the embedded images that are missing (an image whose bytes are not its id is skipped).</summary>
    public void Restore(IReadOnlyDictionary<string, string>? images)
    {
        if (images == null)
            return;
        foreach (var (id, base64) in images)
        {
            if (!MacroJson.IsImageId(id) || Has(id))
                continue;
            byte[] png;
            try
            {
                png = Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                continue;
            }
            if (IdOf(png) == id)
                Write(id, png);
        }
    }

    /// <summary>The ids of the images a macro uses.</summary>
    public static HashSet<string> IdsIn(MacroDefinition macro) =>
        macro.AllActions
            .SelectMany(a => a.If?.Conditions ?? new List<MacroCondition>())
            .SelectMany(c => c.Images ?? new List<ConditionImage>())
            .Select(i => i.Id)
            .Where(MacroJson.IsImageId)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>The ids of the images an item's JSON names (a History version, an AI change), without reading it as an item.</summary>
    public static HashSet<string> IdsIn(string json) =>
        ImageIdPattern().Matches(json).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    // An image id is the only "Id" of 64 hexadecimal digits: every other Id is a GUID.
    [GeneratedRegex("\"Id\"\\s*:\\s*\"([0-9a-f]{64})\"")]
    private static partial Regex ImageIdPattern();

    private void Write(string id, byte[] png)
    {
        string path = PathOf(id);
        if (File.Exists(path))
            return;
        try
        {
            Directory.CreateDirectory(Folder);
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, png);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The condition will say the image is missing.
        }
    }
}
