using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using PuppyMacro.Models;
using PuppyMacro.Services.Screen;

namespace PuppyMacro.Services.Ai;

/// <summary>
/// The tools that let an AI app build a macro with the screen: look at it (look_at_screen, then take_image and pick_pixel
/// from that picture), check a condition (check_condition), record what the user does (record_macro), test a macro
/// (test_macro) and find how its last run went (get_last_run). Each one that needs the user shows the ready window first,
/// with the AI's title, description and steps. What they capture stays in the call's debug folder (<see cref="DebugCall"/>):
/// the answer says how it went and links its files (<see cref="AiToolResult.Files"/>, MCP resource links), which the AI
/// app reads only if it needs them. Pictures are the screen's own, never shrunk or re-encoded.
/// </summary>
internal sealed partial class AiTools
{
    /// <summary>The side of the image kept around each recorded click.</summary>
    public const int ClickImageSide = 96;

    public const double DefaultTestSeconds = 20;

    /// <summary>A look_at_screen picture's monitors (bounds), next to its PNGs.</summary>
    private const string ScreenFile = "screen.json";

    private const int CachedSnapshots = 3;

    private const string SeeScreenOff =
        "Let AI apps see the screen is off on PuppyMacro's AI page, so pictures of the screen and what it shows are not given " +
        "to AI apps. The user can turn it on there.";

    /// <summary>How the linked files open, in every answer and description that links them.</summary>
    private const string LinksOpen =
        " A link opens with resources/read, or, in an AI app that cannot open resource links, with read_resource, which can " +
        "also give a part or a smaller copy of a picture and some lines of a text.";

    private const string TestOff =
        "Let AI apps test macros is off on PuppyMacro's AI page. The user can turn it on there, or start the macro themselves.";

    /// <summary>The newest look_at_screen pictures, decoded (their PNGs are the source; this only spares reading them).</summary>
    private readonly List<(string Id, List<(Rect Bounds, Mat Picture)> Monitors)> _snapshots = new();

    /// <summary>The folder this call keeps its files in (outside a connection, one of its own).</summary>
    private static string CallFolder(string tool)
    {
        if (DebugCall.Current is { } call)
            return call.Folder;
        using JsonDocument none = JsonDocument.Parse("{}");
        return DebugSession.Detached.NewCall(tool, null, none.RootElement.Clone(), null).Folder;
    }

    // ================= look_at_screen =================

    private async Task<AiToolResult> LookAtScreen(JsonElement args, string app, CancellationToken cancel)
    {
        if (!_host.Settings.AiSeeScreen)
            return AiToolResult.Error(SeeScreenOff);
        AiAsk ask = Asking(args);
        string folder = CallFolder(Name("look_at_screen"));
        if (await Refusal().ConfigureAwait(false) is string busy)
            return AiToolResult.Error(busy);
        Phrase label = Phrase.Of("Ai_LookedAtScreen", ask.Title);
        return await StartForUser(app, async () =>
        {
            AiAnswer ready = await _host.Ready(app, ask);
            if (ready != AiAnswer.Done)
            {
                Log(app, AiActivityKind.Interaction, label, ready, "");
                return Answered(ready, "");
            }
            var monitors = await _host.TakeScreen();
            if (monitors.Count == 0)
                return AiToolResult.Error("PuppyMacro could not see the screen (no monitor gave a picture). Ask the user to try again.");
            string id = DebugStore.UriOf(folder)[DebugStore.UriPrefix.Length..];
            var files = new List<string>();
            var described = new List<object>();
            var where = new List<object>();
            for (int i = 0; i < monitors.Count; i++)
            {
                var (bounds, picture) = monitors[i];
                string file = Path.Combine(folder, $"monitor-{i + 1}.png");
                files.Add(file);
                described.Add(new { File = Path.GetFileName(file), bounds.X, bounds.Y, bounds.Width, bounds.Height });
                where.Add(new { File = Path.GetFileName(file), bounds.X, bounds.Y });
                Mat copy = picture.Clone();
                DebugStore.Write(() =>
                {
                    using (copy)
                        Cv2.ImWrite(file, copy);
                });
            }
            string screen = JsonSerializer.Serialize(described, DebugJson.Options);
            DebugStore.Write(() => File.WriteAllText(Path.Combine(folder, ScreenFile), screen));
            // The links point to files already written (an AI app may read them at once).
            await Task.Run(() => DebugStore.WaitWritten(TimeSpan.FromSeconds(30)));
            KeepSnapshot(id, monitors);
            Log(app, AiActivityKind.Interaction, label, AiAnswer.Done, "");
            _host.Finished(app);
            return new AiToolResult(JsonSerializer.Serialize(new
            {
                SnapshotId = id,
                Monitors = where,
                Note = Prefixed("Each monitor's picture is linked below (its size in the list of linked files), at the screen's own size, in " +
                       "physical pixels: a point at (px, py) in a monitor's picture is at X + px, Y + py on the screen. " +
                       "take_image and pick_pixel read this same picture with snapshot_id and screen coordinates, without the " +
                       "user. PuppyMacro's own windows are left out." + LinksOpen),
            }, AnswerOptions)) { Files = files };
        }, cancel).ConfigureAwait(false);
    }

    private void KeepSnapshot(string id, List<(Rect Bounds, Mat Picture)> monitors)
    {
        lock (_sync)
        {
            _snapshots.Insert(0, (id, monitors));
            foreach (var old in _snapshots.Skip(CachedSnapshots).ToList())
            {
                foreach (var (_, picture) in old.Monitors)
                    picture.Dispose();
                _snapshots.Remove(old);
            }
        }
    }

    /// <summary>The part of a look_at_screen picture at a screen area (one monitor), from memory or its files, or why not.</summary>
    private (Mat? Part, string? Problem) FromSnapshot(string id, Rect area)
    {
        lock (_sync)
        {
            if (_snapshots.FirstOrDefault(s => s.Id == id) is { Monitors: { } kept })
                return Cut(kept, area);
        }
        string? screen = DebugStore.PathOf(DebugStore.UriPrefix + id + "/" + ScreenFile);
        if (screen == null)
            return (null, $"No picture \"{id}\": it is not in PuppyMacro's debug data any more. Call look_at_screen again.");
        DebugStore.WaitWritten(TimeSpan.FromSeconds(10));
        var monitors = new List<(Rect, Mat)>();
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(screen));
        foreach (JsonElement m in doc.RootElement.EnumerateArray())
        {
            var bounds = new Rect(m.GetProperty("X").GetInt32(), m.GetProperty("Y").GetInt32(), m.GetProperty("Width").GetInt32(), m.GetProperty("Height").GetInt32());
            monitors.Add((bounds, Cv2.ImRead(Path.Combine(Path.GetDirectoryName(screen)!, m.GetProperty("File").GetString()!))));
        }
        KeepSnapshot(id, monitors);
        return Cut(monitors, area);
    }

    private static (Mat? Part, string? Problem) Cut(List<(Rect Bounds, Mat Picture)> monitors, Rect area)
    {
        foreach (var (bounds, picture) in monitors)
        {
            if (picture.Empty() || !bounds.Contains(area.TopLeft) || !bounds.Contains(new Point(area.Right - 1, area.Bottom - 1)))
                continue;
            using var view = new Mat(picture, new Rect(area.X - bounds.X, area.Y - bounds.Y, area.Width, area.Height));
            return (view.Clone(), null);
        }
        return (null, "That area is not inside one monitor of the picture. Screen coordinates are physical pixels; look_at_screen gives each monitor's bounds.");
    }

    private static Rect? AreaArg(JsonElement args, string name, string unit = "screen pixels")
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out JsonElement a) || a.ValueKind != JsonValueKind.Object)
            return null;
        int Get(string field) => a.TryGetProperty(field, out JsonElement v) && v.TryGetInt32(out int n)
            ? n : throw new FormatException($"\"{name}\" needs X, Y, Width and Height (whole numbers, {unit}).");
        var rect = new Rect(Get("X"), Get("Y"), Get("Width"), Get("Height"));
        if (rect.Width < 1 || rect.Height < 1)
            throw new FormatException($"\"{name}\" needs a Width and a Height of at least 1.");
        return rect;
    }

    private static int? IntArg(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out JsonElement v) && v.TryGetInt32(out int n) ? n : null;

    private static double? NumberArg(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out JsonElement v) && v.TryGetDouble(out double n) ? n : null;

    private static bool? BoolArg(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out JsonElement v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : null;

    /// <summary>take_image and pick_pixel with snapshot_id: from a look_at_screen picture, without asking the user.</summary>
    private async Task<AiToolResult> FromSnapshotPick(JsonElement args, string app, AiScreenPickKind kind, string snapshotId)
    {
        if (kind == AiScreenPickKind.Pixel)
        {
            int x = IntArg(args, "x") ?? throw new FormatException("With snapshot_id, give \"x\" and \"y\": the pixel on the screen.");
            int y = IntArg(args, "y") ?? throw new FormatException("With snapshot_id, give \"x\" and \"y\": the pixel on the screen.");
            var (dot, problem) = FromSnapshot(snapshotId, new Rect(x, y, 1, 1));
            if (dot == null)
                return AiToolResult.Error(problem!);
            string color;
            using (dot)
                color = ScreenSnapshot.Hex(dot.At<Vec3b>(0, 0));
            _log.Add(app, AiActivityKind.Read, Phrase.Of("Ai_ReadPixel", $"{x}, {y} {color}"));
            return new AiToolResult(JsonSerializer.Serialize(new { X = x, Y = y, Color = color }));
        }
        Rect area = AreaArg(args, "area") ?? throw new FormatException("With snapshot_id, give \"area\": X, Y, Width and Height of the image on the screen.");
        if (area.Width < MacroJson.AreaSize.Min || area.Height < MacroJson.AreaSize.Min)
            return AiToolResult.Error($"An image needs at least {MacroJson.AreaSize.Min} × {MacroJson.AreaSize.Min} pixels.");
        var (cut, why) = FromSnapshot(snapshotId, area);
        if (cut == null)
            return AiToolResult.Error(why!);
        using (cut)
        {
            if (OneColor(cut))
                return AiToolResult.Error("That area is all one color: an Image condition can't find it (it matches shapes, not a flat color). A Pixel color condition fits it.");
            Cv2.ImEncode(".png", cut, out byte[] png);
            string id = await _host.Ui(() =>
            {
                string added = _host.Macros.Images.Add(png);
                _host.Macros.Images.HoldAlso(typeof(AiTools), added);
                return Task.FromResult(added);
            }).ConfigureAwait(false);
            _log.Add(app, AiActivityKind.Read, Phrase.Of("Ai_TookImage", $"{area.X}, {area.Y}, {area.Width} × {area.Height}"));
            return new AiToolResult(JsonSerializer.Serialize(new { Id = id, area.X, area.Y, area.Width, area.Height }));
        }
    }

    /// <summary>Nothing but one color (a flat area): normalized correlation finds no shape in it.</summary>
    internal static bool OneColor(Mat picture)
    {
        Cv2.MeanStdDev(picture, out _, out Scalar spread);
        return spread.Val0 < 1 && spread.Val1 < 1 && spread.Val2 < 1;
    }

    // ================= check_condition =================

    private async Task<AiToolResult> CheckCondition(JsonElement args, string app, CancellationToken cancel)
    {
        if (!_host.Settings.AiSeeScreen)
            return AiToolResult.Error(SeeScreenOff);
        AiAsk ask = Asking(args);
        if (!args.TryGetProperty("condition", out JsonElement given) || given.ValueKind != JsonValueKind.Object)
            return AiToolResult.Error("Give \"condition\": one condition as in a macro's If (get_schema(\"macro\"), MacroCondition).");
        // Checked as the editors and save_macro check it, inside an If.
        string action = $"{{\"Type\":\"If\",\"If\":{{\"Conditions\":[{given.GetRawText()}]}}}}";
        var (checkedAction, problems) = await _host.Ui(() =>
        {
            // The problems in English, as every text an AI app gets (i18n.md).
            List<CodeProblem> found = new();
            MacroAction? parsed = Loc.English(() => MacroJson.ParseAction(action, new Guid?[] { null }, new Dictionary<Guid, string>(), out found,
                hasImage: _host.Macros.Images.Has));
            return Task.FromResult((parsed, string.Join(" ", found.Select(p => p.Message))));
        }).ConfigureAwait(false);
        if (checkedAction?.If?.Conditions.FirstOrDefault() is not { } condition)
            return AiToolResult.Error("The condition has problems: " + problems);
        if (await Refusal().ConfigureAwait(false) is string busy)
            return AiToolResult.Error(busy);
        Phrase label = Phrase.Of("Ai_CheckedCondition", ask.Title);
        return await StartForUser(app, async () =>
        {
            AiAnswer ready = await _host.Ready(app, ask);
            if (ready != AiAnswer.Done)
            {
                Log(app, AiActivityKind.Interaction, label, ready, "");
                return Answered(ready, "");
            }
            ConditionReport report = await _host.CheckCondition(condition);
            bool isTrue = report.TrueFor(condition);
            Log(app, AiActivityKind.Interaction, label, AiAnswer.Done, isTrue ? "true" : "not true");
            _host.Finished(app);
            return new AiToolResult(JsonSerializer.Serialize(new
            {
                True = isTrue,
                There = report.Met,
                At = report.At is { } at ? new { at.X, at.Y } : null,
                Similarity = condition.Kind == ConditionKind.Image ? ImageMatch.Percent(report.Score) : (int?)null,
                Seen = report.Color,
                report.Read,
                AllBlack = report.Black ? true : (bool?)null,
                NoMonitorThere = report.NoMonitor ? true : (bool?)null,
                LanguageNotInstalled = report.NoLanguage ? true : (bool?)null,
                TextLanguages = report.NoLanguage ? Screen.TextReader.Languages : null,
            }, AnswerOptions));
        }, cancel).ConfigureAwait(false);
    }

    // ================= record_macro =================

    private async Task<AiToolResult> RecordMacro(JsonElement args, string app, CancellationToken cancel)
    {
        AiAsk ask = Asking(args);
        bool moves = BoolArg(args, "mouse_movement") ?? _host.Settings.RecordMouseMovement;
        string folder = CallFolder(Name("record_macro"));
        if (await Refusal().ConfigureAwait(false) is string busy)
            return AiToolResult.Error(busy);
        Phrase label = Phrase.Of("Ai_Recorded", ask.Title);
        return await StartForUser(app, async () =>
        {
            AiAnswer ready = await _host.Ready(app, ask);
            if (ready != AiAnswer.Done)
            {
                Log(app, AiActivityKind.Interaction, label, ready, "");
                return Answered(ready, "");
            }
            var (answer, recording) = await _host.Record(app, moves);
            Log(app, AiActivityKind.Interaction, label, answer, recording == null ? "" : $"{recording.Actions.Count}");
            if (answer != AiAnswer.Done || recording == null)
                return Answered(answer, "");
            var clicks = await Task.Run(() => KeepRecording(folder, recording));
            _host.Finished(app);
            return RecordingAnswer(folder, recording, clicks); // on the UI thread: the images go to the store there
        }, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Off the UI thread: the recording's log, its actions and the screen at each of the user's inputs into the call's
    /// folder (without Let AI apps see the screen, no pictures), and for each click the image around it (a PNG, none when
    /// flat).
    /// </summary>
    private List<(byte[]? Png, Rect Around)> KeepRecording(string folder, AiRecording recording)
    {
        bool see = _host.Settings.AiSeeScreen;
        var log = new RunLog(folder, null, "a recording of the user's inputs");
        var clicks = new List<(byte[]?, Rect)>();
        foreach (var input in recording.Inputs)
        {
            byte[]? png = null;
            Rect around = default;
            if (input.Click && see && input.Screen.FirstOrDefault(m => m.Bounds.Contains(input.X, input.Y)) is { Picture: { } screen } monitor)
            {
                int half = ClickImageSide / 2;
                around = new Rect(input.X - half, input.Y - half, ClickImageSide, ClickImageSide).Intersect(monitor.Bounds);
                if (around.Width >= MacroJson.AreaSize.Min && around.Height >= MacroJson.AreaSize.Min)
                {
                    using var cut = new Mat(screen, new Rect(around.X - monitor.Bounds.X, around.Y - monitor.Bounds.Y, around.Width, around.Height));
                    if (!OneColor(cut))
                        Cv2.ImEncode(".png", cut, out png);
                }
            }
            if (input.Click)
                clicks.Add((png, around));
            log.Event($"{input.What} at +{input.TimeMs / 1000:0.000} s of the recording", see ? input.Screen : null);
            if (!see)
                foreach (var (_, picture) in input.Screen)
                    picture.Dispose();
        }
        string actions = JsonSerializer.Serialize(recording.Actions, AnswerOptions);
        DebugStore.Write(() => File.WriteAllText(Path.Combine(folder, "actions.json"), actions));
        log.End("the user stopped the recording");
        RunLog.WaitWritten(TimeSpan.FromSeconds(30));
        return clicks;
    }

    /// <summary>
    /// On the UI thread: the actions, each click with the image around it (in the conditions' images, for an Image
    /// condition that waits for what was clicked), and the links to the recording's log and pictures.
    /// </summary>
    private AiToolResult RecordingAnswer(string folder, AiRecording recording, List<(byte[]? Png, Rect Around)> prepared)
    {
        var clicks = new List<object>();
        var clickInputs = recording.Inputs.Where(i => i.Click).ToList();
        for (int i = 0; i < clickInputs.Count; i++)
        {
            var (png, around) = prepared[i];
            string? imageId = null;
            if (png != null)
            {
                imageId = _host.Macros.Images.Add(png);
                _host.Macros.Images.HoldAlso(typeof(AiTools), imageId);
            }
            clicks.Add(new
            {
                clickInputs[i].X, clickInputs[i].Y, TimeMs = Math.Round(clickInputs[i].TimeMs),
                Image = imageId == null ? null : new { Id = imageId, around.X, around.Y, around.Width, around.Height },
            });
        }
        return new AiToolResult(JsonSerializer.Serialize(new
        {
            Note = Prefixed("Actions are as PuppyMacro's recorder builds them (not saved). Each click has an image around it, " +
                   "taken a moment after the click, ready for an Image condition's Images. The recording's log (each input, with " +
                   "the picture of the screen at that moment, named by its number) and its files are linked below." + LinksOpen),
            recording.Actions,
            Clicks = clicks,
        }, AnswerOptions)) { Files = FilesOf(folder) };
    }

    /// <summary>The files a call keeps, without its own request and response: log first, then the rest by name.</summary>
    private static List<string> FilesOf(string folder)
    {
        DebugStore.WaitWritten(TimeSpan.FromSeconds(30));
        if (!Directory.Exists(folder))
            return new List<string>();
        return Directory.GetFiles(folder)
            .Where(f => Path.GetFileName(f) is not ("request.json" or "response.json"))
            .OrderBy(f => Path.GetFileName(f) == RunLog.LogFile ? 0 : 1)
            .ThenBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    // ================= test_macro and get_last_run =================

    private async Task<AiToolResult> TestMacro(JsonElement args, string app, CancellationToken cancel)
    {
        if (!_host.Settings.AiTestMacros)
            return AiToolResult.Error(TestOff);
        Guid id = IdArg(args, "id");
        AiAsk ask = Asking(args);
        double seconds = Math.Clamp(NumberArg(args, "max_seconds") ?? DefaultTestSeconds, 1, 600);
        (string? name, string? problem) = await _host.Ui(() => Task.FromResult(_host.Macros.Find(id) switch
        {
            null => ((string?)null, (string?)"No macro has this Id (get_overview lists them)."),
            { Actions.Count: 0 } => (null, "The macro has no actions."),
            { } m when _host.AnyRunning => (m.Name, "A loop or macro is running: stop it first (stop_all), so only this test plays."),
            { } m => (m.Name, null),
        })).ConfigureAwait(false);
        if (problem != null)
            return AiToolResult.Error(problem);
        string folder = CallFolder(Name("test_macro"));
        if (await Refusal().ConfigureAwait(false) is string busy)
            return AiToolResult.Error(busy);
        Phrase label = Phrase.Of("Ai_TestedMacro", $"\"{name}\"");
        return await StartForUser(app, async () =>
        {
            AiAnswer ready = await _host.Ready(app, ask);
            if (ready != AiAnswer.Done)
            {
                Log(app, AiActivityKind.Interaction, label, ready, "");
                return Answered(ready, "");
            }
            AiTestRun? run = await _host.TestMacro(id, app, seconds, folder);
            if (run == null)
                return AiToolResult.Error("PuppyMacro could not start the macro (another item started meanwhile, or it has no actions).");
            Log(app, AiActivityKind.Interaction, label, AiAnswer.Done, run.EndReason);
            _host.Finished(app);
            return RunAnswer($"The test ended: {run.EndReason}.", folder);
        }, cancel).ConfigureAwait(false);
    }

    private Task<AiToolResult> GetLastRun(JsonElement args, string app)
    {
        Guid id = IdArg(args, "id");
        _log.Add(app, AiActivityKind.Read, Phrase.Of("Ai_ReadLastRun"));
        string? folder = DebugStore.LastRunOf(id);
        if (folder == null)
            return Task.FromResult(new AiToolResult(
                "This macro has no run in PuppyMacro's debug data: a run is kept for a macro with Debug on (the user's choice) " +
                "and for every test_macro, for 7 days."));
        string? ended = null;
        try
        {
            using JsonDocument run = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, RunLog.RunFile)));
            if (run.RootElement.TryGetProperty("EndReason", out JsonElement reason))
                ended = reason.GetString();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return Task.FromResult(RunAnswer(ended == null ? "Its last run had not ended when its log was written." : $"Its last run ended: {ended}.", folder));
    }

    /// <summary>
    /// How a run went in one line, with its files linked (the log, each picture named by its event): the AI app reads only
    /// what it needs. Without Let AI apps see the screen, the pictures are not linked.
    /// </summary>
    private AiToolResult RunAnswer(string first, string folder)
    {
        var files = FilesOf(folder);
        if (!_host.Settings.AiSeeScreen)
            files.RemoveAll(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
        int pictures = files.Count(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
        return new AiToolResult(Prefixed(
            $"{first} Its log ({RunLog.LogFile}: each event with its number and time; the picture of the screen just before an " +
            $"event has the same number) and {pictures} picture(s) are linked below, in {folder}." + LinksOpen +
            (_host.Settings.AiSeeScreen ? "" : " " + SeeScreenOff))) { Files = files };
    }
}
