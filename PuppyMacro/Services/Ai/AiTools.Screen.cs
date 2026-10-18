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
/// from that picture), check a condition (check_condition), record what the user does (record_macro,
/// get_recording_frame), test a macro (test_macro) and read how its last run went (get_last_run). Each one that needs the
/// user shows the ready window first, with the AI's title, description and steps. Pictures go to the AI app only with Let
/// AI apps see the screen on (the AI page), and as JPEG no larger than <see cref="MaxImageSide"/>.
/// </summary>
internal sealed partial class AiTools
{
    /// <summary>The longest side of a picture sent to an AI app: larger ones cost far more and are shrunk by the AI app anyway.</summary>
    public const int MaxImageSide = 1568;

    /// <summary>How long a look_at_screen picture stays for take_image and pick_pixel.</summary>
    public static readonly TimeSpan SnapshotLife = TimeSpan.FromMinutes(10);

    private const int MaxSnapshots = 3;

    /// <summary>The side of the image kept around each recorded click.</summary>
    public const int ClickImageSide = 96;

    /// <summary>The most pictures a test's or get_last_run's answer carries (the newest).</summary>
    private const int MaxRunPictures = 6;

    public const double DefaultTestSeconds = 20;

    private const string SeeScreenOff =
        "Let AI apps see the screen is off on PuppyMacro's AI page, so pictures of the screen and what it shows are not given " +
        "to AI apps. The user can turn it on there.";

    private const string TestOff =
        "Let AI apps test macros is off on PuppyMacro's AI page. The user can turn it on there, or start the macro themselves.";

    private readonly Dictionary<string, (DateTime At, List<(Rect Bounds, Mat Picture)> Monitors)> _snapshots = new();
    private (string Id, List<(int X, int Y, Rect Monitor, byte[]? Jpeg)> Clicks)? _recording;

    // ================= Pictures =================

    /// <summary>A picture for the AI app: shrunk to <see cref="MaxImageSide"/> at most, as JPEG, and how much it was shrunk.</summary>
    internal static (byte[] Jpeg, double Scale, int Width, int Height) ForAi(Mat picture)
    {
        double scale = Math.Min(1, (double)MaxImageSide / Math.Max(picture.Width, picture.Height));
        using Mat small = scale < 1
            ? picture.Resize(new Size(Math.Max(1, (int)Math.Round(picture.Width * scale)), Math.Max(1, (int)Math.Round(picture.Height * scale))), 0, 0, InterpolationFlags.Area)
            : picture.Clone();
        Cv2.ImEncode(".jpg", small, out byte[] jpeg, new ImageEncodingParam(ImwriteFlags.JpegQuality, 85));
        return (jpeg, scale, small.Width, small.Height);
    }

    /// <summary>Keeps a look_at_screen picture for take_image and pick_pixel (the newest few, for <see cref="SnapshotLife"/>).</summary>
    private string KeepSnapshot(List<(Rect Bounds, Mat Picture)> monitors)
    {
        string id = Guid.NewGuid().ToString("N")[..8];
        lock (_sync)
        {
            foreach (var old in _snapshots.Where(s => DateTime.UtcNow - s.Value.At > SnapshotLife)
                         .Concat(_snapshots.OrderByDescending(s => s.Value.At).Skip(MaxSnapshots - 1)).Select(s => s.Key).Distinct().ToList())
            {
                foreach (var (_, picture) in _snapshots[old].Monitors)
                    picture.Dispose();
                _snapshots.Remove(old);
            }
            _snapshots[id] = (DateTime.UtcNow, monitors);
        }
        return id;
    }

    /// <summary>Under no lock: the part of a kept picture at a screen area (one monitor), or why not.</summary>
    private (Mat? Part, string? Problem) FromSnapshot(string id, Rect area)
    {
        lock (_sync)
        {
            if (!_snapshots.TryGetValue(id, out var snapshot) || DateTime.UtcNow - snapshot.At > SnapshotLife)
                return (null, $"No picture \"{id}\": pictures from look_at_screen last {SnapshotLife.TotalMinutes:0} minutes. Call look_at_screen again.");
            foreach (var (bounds, picture) in snapshot.Monitors)
            {
                if (!bounds.Contains(area.TopLeft) || !bounds.Contains(new Point(area.Right - 1, area.Bottom - 1)))
                    continue;
                using var view = new Mat(picture, new Rect(area.X - bounds.X, area.Y - bounds.Y, area.Width, area.Height));
                return (view.Clone(), null);
            }
        }
        return (null, "That area is not inside one monitor of the picture. Screen coordinates are physical pixels; look_at_screen gives each monitor's bounds.");
    }

    private static Rect? AreaArg(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out JsonElement a) || a.ValueKind != JsonValueKind.Object)
            return null;
        int Get(string field) => a.TryGetProperty(field, out JsonElement v) && v.TryGetInt32(out int n)
            ? n : throw new FormatException($"\"{name}\" needs X, Y, Width and Height (whole numbers, screen pixels).");
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

    // ================= look_at_screen =================

    private async Task<AiToolResult> LookAtScreen(JsonElement args, string app, CancellationToken cancel)
    {
        if (!_host.Settings.AiSeeScreen)
            return AiToolResult.Error(SeeScreenOff);
        AiAsk ask = Asking(args);
        Rect? area = AreaArg(args, "area");
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
            string id = KeepSnapshot(monitors);
            var images = new List<byte[]>();
            var described = new List<object>();
            // Shrinking and encoding take a moment on a large screen: not on the UI thread.
            string? problem = await Task.Run(() =>
            {
                if (area is Rect part)
                {
                    var (cut, why) = FromSnapshot(id, part);
                    if (cut == null)
                        return why;
                    using (cut)
                    {
                        var (jpeg, scale, w, h) = ForAi(cut);
                        images.Add(jpeg);
                        described.Add(new { Image = 1, part.X, part.Y, part.Width, part.Height, ImageWidth = w, ImageHeight = h, Scale = Math.Round(scale, 4) });
                    }
                    return null;
                }
                foreach (var (bounds, picture) in monitors)
                {
                    var (jpeg, scale, w, h) = ForAi(picture);
                    images.Add(jpeg);
                    described.Add(new
                    {
                        Image = images.Count, bounds.X, bounds.Y, bounds.Width, bounds.Height, ImageWidth = w, ImageHeight = h,
                        Scale = Math.Round(scale, 4),
                    });
                }
                return null;
            });
            if (problem != null)
                return AiToolResult.Error(problem);
            Log(app, AiActivityKind.Interaction, label, AiAnswer.Done, "");
            string text = JsonSerializer.Serialize(new
            {
                SnapshotId = id,
                Pictures = described,
                Coordinates = "A point at (px, py) in picture n is at X + px / Scale, Y + py / Scale on the screen (physical pixels). " +
                    "take_image and pick_pixel read this same picture with snapshot_id and screen coordinates, for the next " +
                    $"{SnapshotLife.TotalMinutes:0} minutes; area gives a part at full size, for exact coordinates. PuppyMacro's own windows are left out of it.",
            }, AnswerOptions);
            return new AiToolResult(text) { Images = images };
        }, cancel).ConfigureAwait(false);
    }

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
            var prepared = await Task.Run(() => PrepareClicks(recording));
            return RecordingAnswer(recording, prepared); // on the UI thread: the images go to the store there
        }, cancel).ConfigureAwait(false);
    }

    /// <summary>Off the UI thread: for each click the image around it (a PNG, none when flat) and its frame for the AI app.</summary>
    private List<(byte[]? Png, Rect Around, byte[]? Jpeg)> PrepareClicks(AiRecording recording)
    {
        bool see = _host.Settings.AiSeeScreen;
        var prepared = new List<(byte[]?, Rect, byte[]?)>();
        foreach (var click in recording.Clicks)
        {
            using var screen = click.Screen;
            byte[]? png = null, jpeg = null;
            Rect around = default;
            if (see && screen != null)
            {
                int half = ClickImageSide / 2;
                around = new Rect(click.X - half, click.Y - half, ClickImageSide, ClickImageSide).Intersect(click.MonitorBounds);
                if (around.Width >= MacroJson.AreaSize.Min && around.Height >= MacroJson.AreaSize.Min)
                {
                    using var cut = new Mat(screen, new Rect(around.X - click.MonitorBounds.X, around.Y - click.MonitorBounds.Y, around.Width, around.Height));
                    if (!OneColor(cut))
                        Cv2.ImEncode(".png", cut, out png);
                }
                jpeg = ForAi(screen).Jpeg;
            }
            prepared.Add((png, around, jpeg));
        }
        return prepared;
    }

    /// <summary>
    /// On the UI thread: the actions, and for each click the image around it (in the conditions' images, for an Image
    /// condition that waits for what was clicked) and its frame kept for get_recording_frame. Without Let AI apps see the
    /// screen: the actions only.
    /// </summary>
    private AiToolResult RecordingAnswer(AiRecording recording, List<(byte[]? Png, Rect Around, byte[]? Jpeg)> prepared)
    {
        string id = Guid.NewGuid().ToString("N")[..8];
        var clicks = new List<object>();
        var frames = new List<(int X, int Y, Rect Monitor, byte[]? Jpeg)>();
        for (int i = 0; i < recording.Clicks.Count; i++)
        {
            var click = recording.Clicks[i];
            var (png, around, jpeg) = prepared[i];
            string? imageId = null;
            if (png != null)
            {
                imageId = _host.Macros.Images.Add(png);
                _host.Macros.Images.HoldAlso(typeof(AiTools), imageId);
            }
            frames.Add((click.X, click.Y, click.MonitorBounds, jpeg));
            clicks.Add(new
            {
                Click = i + 1, click.X, click.Y, TimeMs = Math.Round(click.TimeMs),
                Image = imageId == null ? null : new { Id = imageId, around.X, around.Y, around.Width, around.Height },
            });
        }
        lock (_sync)
            _recording = (id, frames);
        return new AiToolResult(JsonSerializer.Serialize(new
        {
            RecordingId = id,
            Note = _host.Settings.AiSeeScreen
                ? "Actions are as PuppyMacro's recorder builds them (not saved). Each click has an image around it, taken a moment " +
                  "after the click, ready for an Image condition's Images (its Id, and X, Y where it was); get_recording_frame " +
                  "gives the screen at a click."
                : "Actions are as PuppyMacro's recorder builds them (not saved). Let AI apps see the screen is off: no pictures.",
            recording.Actions,
            Clicks = clicks,
        }, AnswerOptions));
    }

    private Task<AiToolResult> RecordingFrame(JsonElement args, string app)
    {
        if (!_host.Settings.AiSeeScreen)
            return Task.FromResult(AiToolResult.Error(SeeScreenOff));
        string id = Str(args, "recording_id") ?? "";
        int click = IntArg(args, "click") ?? 0;
        lock (_sync)
        {
            if (_recording is not { } recording || recording.Id != id)
                return Task.FromResult(AiToolResult.Error($"No recording \"{id}\": only the last record_macro is kept."));
            if (click < 1 || click > recording.Clicks.Count)
                return Task.FromResult(AiToolResult.Error($"\"click\" goes from 1 to {recording.Clicks.Count}."));
            var frame = recording.Clicks[click - 1];
            if (frame.Jpeg == null)
                return Task.FromResult(AiToolResult.Error("PuppyMacro had no picture of the screen at that click."));
            _log.Add(app, AiActivityKind.Read, Phrase.Of("Ai_ReadRecordingFrame", click.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            double scale = Math.Min(1, (double)MaxImageSide / Math.Max(frame.Monitor.Width, frame.Monitor.Height));
            return Task.FromResult(new AiToolResult(JsonSerializer.Serialize(new
            {
                Click = click, frame.X, frame.Y, Monitor = new { frame.Monitor.X, frame.Monitor.Y, frame.Monitor.Width, frame.Monitor.Height },
                Scale = Math.Round(scale, 4),
            }, AnswerOptions)) { Images = new[] { frame.Jpeg } });
        }
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
            AiTestRun? run = await _host.TestMacro(id, app, seconds);
            if (run == null)
                return AiToolResult.Error("PuppyMacro could not start the macro (another item started meanwhile, or it has no actions).");
            Log(app, AiActivityKind.Interaction, label, AiAnswer.Done, run.EndReason);
            return LastRun(id, $"The test ended: {run.EndReason}.", pictures: true);
        }, cancel).ConfigureAwait(false);
    }

    private Task<AiToolResult> GetLastRun(JsonElement args, string app)
    {
        Guid id = IdArg(args, "id");
        bool pictures = BoolArg(args, "pictures") ?? false;
        _log.Add(app, AiActivityKind.Read, Phrase.Of("Ai_ReadLastRun"));
        return Task.FromResult(LastRun(id, null, pictures));
    }

    /// <summary>A macro's last run log, its folder, and (with Let AI apps see the screen) its newest pictures.</summary>
    private AiToolResult LastRun(Guid id, string? first, bool pictures)
    {
        string? log = RunLog.Read(id);
        if (log == null)
            return new AiToolResult((first == null ? "" : first + " ") +
                "This macro has no log: PuppyMacro keeps the last run's log only for a macro with Debug on (a field of the " +
                "macro) or after test_macro, until PuppyMacro closes.");
        string folder = RunLog.FolderFor(id);
        var images = new List<byte[]>();
        string seen = "";
        if (pictures && _host.Settings.AiSeeScreen && Directory.Exists(folder))
        {
            var files = new DirectoryInfo(folder).GetFiles("*.png").OrderByDescending(f => f.LastWriteTimeUtc).Take(MaxRunPictures).Reverse().ToList();
            foreach (var file in files)
            {
                using var picture = Cv2.ImRead(file.FullName);
                if (!picture.Empty())
                    images.Add(ForAi(picture).Jpeg);
            }
            seen = files.Count == 0 ? "" : $" Pictures, in order: {string.Join(", ", files.Select(f => f.Name))}.";
        }
        else if (pictures && !_host.Settings.AiSeeScreen)
        {
            seen = " " + SeeScreenOff;
        }
        return new AiToolResult($"{(first == null ? "" : first + " ")}Log folder: {folder}.{seen}\n\n{log}") { Images = images };
    }
}
