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
/// no saved macro uses it and nothing holds it (<see cref="MacroLibrary.ReleaseUnusedImages"/>): an editor still open,
/// the editor's copied actions, an AI app that took it. The History keeps a copy of the images of its versions, so its
/// Restore (and the AI activity's, which goes through the History) brings them back.
/// </summary>
internal sealed partial class ImageStore
{
    public ImageStore(string folder) => Folder = folder;

    public string Folder { get; }

    public static string IdOf(byte[] png) => Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();

    public string PathOf(string id) => Path.Combine(Folder, id + ".png");

    private readonly Dictionary<object, HashSet<string>> _held = new();

    /// <summary>
    /// Keeps <paramref name="ids"/> from being deleted for <paramref name="holder"/> (an editor, the copied actions, an AI
    /// app's take) until <see cref="Unhold"/>; holding again replaces what it held.
    /// </summary>
    public void Hold(object holder, IEnumerable<string> ids)
    {
        lock (_held)
            _held[holder] = ids.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Adds one image to what <paramref name="holder"/> holds.</summary>
    public void HoldAlso(object holder, string id)
    {
        lock (_held)
        {
            if (!_held.TryGetValue(holder, out var ids))
                _held[holder] = ids = new HashSet<string>(StringComparer.Ordinal);
            ids.Add(id);
        }
    }

    public void Unhold(object holder)
    {
        lock (_held)
            _held.Remove(holder);
    }

    /// <summary>The images something holds now.</summary>
    public HashSet<string> Held()
    {
        lock (_held)
            return _held.Values.SelectMany(h => h).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The ids of every image kept in the folder.</summary>
    public List<string> All()
    {
        try
        {
            return Directory.Exists(Folder)
                ? Directory.EnumerateFiles(Folder, "*.png").Select(Path.GetFileNameWithoutExtension).OfType<string>().Where(MacroJson.IsImageId).ToList()
                : new List<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }

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

    /// <summary>The ids of the images a macro uses: its conditions' and those its calls give to components.</summary>
    public static HashSet<string> IdsIn(MacroDefinition macro) => IdsIn(macro.Actions);

    /// <summary>The ids of the images some actions use, also those their calls give as arguments.</summary>
    public static HashSet<string> IdsIn(IEnumerable<MacroAction> actions)
    {
        var all = MacroDefinition.Walk(actions).ToList();
        var conditions = all.SelectMany(a => a.If?.Conditions ?? new List<MacroCondition>()).ToList();
        var ids = conditions
            .SelectMany(c => c.Images ?? new List<ConditionImage>())
            .Select(i => i.Id)
            .Where(MacroJson.IsImageId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var arguments in all.Select(a => a.Arguments).Concat(conditions.Select(c => c.Arguments)))
            foreach (var value in arguments?.Values ?? Enumerable.Empty<System.Text.Json.Nodes.JsonNode?>())
                if (value != null)
                    ids.UnionWith(IdsIn(value.ToJsonString()));
        return ids;
    }

    /// <summary>The ids of the images a component uses: its actions', and its parameters' examples and defaults.</summary>
    public static HashSet<string> IdsIn(LibraryComponent component)
    {
        var ids = IdsIn(component.Actions);
        foreach (var parameter in component.Parameters)
            foreach (var value in new[] { parameter.Example, parameter.Default })
                if (value != null)
                    ids.UnionWith(IdsIn(value.ToJsonString()));
        return ids;
    }

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
