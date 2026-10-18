using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using OpenCvSharp;

namespace PuppyMacro.Services.Ai;

/// <summary>
/// The files other tools link (MCP resource links to the debug archive), for resources/read and for read_resource, the
/// same reading as a tool for AI apps that cannot open resource links. Both go through <see cref="OpenResource"/>, so they
/// can never differ on what can be read; read_resource only adds a part or a smaller copy of a picture and some lines of a
/// text. The files themselves never change.
/// </summary>
internal sealed partial class AiTools
{
    private const string AccessOff = "AI access is off in PuppyMacro. Ask the user to turn on Allow AI access on PuppyMacro's AI page.";

    /// <summary>A debug file an AI app may read, or why not (<paramref name="Missing"/>: there is no such file).</summary>
    internal sealed record OpenedResource(DebugFile? File, string? Problem, bool Missing);

    /// <summary>
    /// The one way to a debug file for an AI app (resources/read and read_resource): the URI resolved inside the debug
    /// archive (<see cref="DebugStore.Open"/>), Allow AI access, and Let AI apps see the screen for a picture.
    /// </summary>
    public OpenedResource OpenResource(string uri)
    {
        if (!_host.Settings.AllowAiAccess)
            return new OpenedResource(null, AccessOff, false);
        DebugStore.WaitWritten(TimeSpan.FromSeconds(10));
        if (DebugStore.Open(uri) is not { } file)
            return new OpenedResource(null, $"No file \"{uri}\" in PuppyMacro's debug data. Only files another tool links can be read, " +
                $"for {DebugStore.Life.TotalDays} days.", true);
        if (file.IsImage && !_host.Settings.AiSeeScreen)
            return new OpenedResource(null, SeeScreenOff, false);
        return new OpenedResource(file, null, false);
    }

    // ================= read_resource =================

    private Task<AiToolResult> ReadResource(JsonElement args)
    {
        string uri = Str(args, "uri") ?? throw new FormatException("\"uri\" is missing: the uri of a link another tool gave.");
        OpenedResource opened = OpenResource(uri);
        if (opened.File is not { } file)
            return Task.FromResult(AiToolResult.Error(opened.Problem!));
        return Task.FromResult(file.IsImage ? ReadPicture(file, args) : ReadText(file, args));
    }

    private static bool Has(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out _);

    /// <summary>A picture as it is, or a part of it (area) at a smaller size (scale), with how its points map back.</summary>
    private static AiToolResult ReadPicture(DebugFile file, JsonElement args)
    {
        if (Has(args, "offset") || Has(args, "limit"))
            return AiToolResult.Error("offset and limit read lines of a text. For a picture, area chooses a part and scale a smaller size.");
        Rect? area = AreaArg(args, "area", "the picture's own pixels, X and Y from 0 at its top left");
        double scale = NumberArg(args, "scale") ?? 1;
        if (!(scale > 0 && scale <= 1))
            return AiToolResult.Error("\"scale\" must be more than 0 and at most 1: 1 is the picture's own size, 0.25 a quarter of its width and height.");
        string name = Path.GetFileName(file.Path);
        if (area == null && scale == 1)
        {
            string size = DebugStore.PngSize(file.Path) is var (w, h) ? Invariant($" ({w} × {h} px)") : "";
            byte[] data = File.ReadAllBytes(file.Path);
            return new AiToolResult($"{name}{size}, as it is: {DebugStore.SizeText(data.Length)}.") { Images = new[] { new AiImage(data, file.MimeType) } };
        }
        using Mat picture = Cv2.ImRead(file.Path, ImreadModes.Unchanged);
        if (picture.Empty())
            return AiToolResult.Error($"PuppyMacro could not read the picture {name}.");
        Rect part = area ?? new Rect(0, 0, picture.Width, picture.Height);
        if (!Inside(part, new Rect(0, 0, picture.Width, picture.Height)))
            return AiToolResult.Error(Invariant($"\"area\" must be inside the picture, which is {picture.Width} × {picture.Height} px (X and Y from 0)."));
        int width = Math.Max(1, (int)Math.Round(part.Width * scale));
        int height = Math.Max(1, (int)Math.Round(part.Height * scale));
        using Mat cut = new(picture, part);
        using Mat sized = new();
        if (width == part.Width && height == part.Height)
            cut.CopyTo(sized);
        else
            Cv2.Resize(cut, sized, new Size(width, height), 0, 0, InterpolationFlags.Area);
        Cv2.ImEncode(".png", sized, out byte[] png);
        return new AiToolResult(
            Invariant($"{name} is {picture.Width} × {picture.Height} px. This is its area X {part.X}, Y {part.Y}, {part.Width} × {part.Height} px, ") +
            Invariant($"at scale {scale}: {width} × {height} px, {DebugStore.SizeText(png.Length)} as PNG. ") +
            Invariant($"A point (px, py) of it is at ({part.X} + px / {scale}, {part.Y} + py / {scale}) in {name}."))
        {
            Images = new[] { new AiImage(png, "image/png") },
        };
    }

    /// <summary>A text as it is, or some of its lines (offset, limit), with where they are in it.</summary>
    private static AiToolResult ReadText(DebugFile file, JsonElement args)
    {
        if (Has(args, "area") || Has(args, "scale"))
            return AiToolResult.Error("area and scale are for pictures. For a text, offset and limit choose its lines.");
        int? offset = IntArg(args, "offset");
        int? limit = IntArg(args, "limit");
        if (offset == null && limit == null)
            return new AiToolResult(File.ReadAllText(file.Path));
        if (offset is < 1 || limit is < 1)
            return AiToolResult.Error("\"offset\" (the first line, from 1) and \"limit\" (how many lines) must be at least 1.");
        string name = Path.GetFileName(file.Path);
        string[] lines = File.ReadAllLines(file.Path);
        int first = offset ?? 1;
        if (first > lines.Length)
            return AiToolResult.Error(Invariant($"\"offset\" is past the end: {name} has {lines.Length} lines."));
        // In 64 bits: first - 1 + limit can pass the largest int (limit int.MaxValue: to the end).
        int last = (int)Math.Min(lines.Length, first - 1L + (limit ?? lines.Length));
        return new AiToolResult(Invariant($"Lines {first}–{last} of {lines.Length} of {name}:\n") + string.Join("\n", lines[(first - 1)..last]));
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
