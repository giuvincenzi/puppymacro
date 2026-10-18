using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using PuppyMacro.Models;
using Xunit;
using static PuppyMacro.E2E.Playback;

namespace PuppyMacro.E2E;

/// <summary>
/// Macros that look at the screen: an If with a pixel's color or an image shown in the target window, its Then and Else,
/// the wait, and a click where the image was found. make e2e category=Macros
/// </summary>
[Trait("Category", "Macros")]
public class ConditionTests
{
    /// <summary>The target window's background (TargetWindow.NewForm).</summary>
    private const string Background = "#182838";

    private static MacroAction If(MacroIf block, double delayMs = 0) => new() { Type = MacroActionType.If, DelayMs = delayMs, If = block };

    [Fact]
    public void A_pixel_condition_runs_Then_when_the_color_is_there_and_Else_when_it_is_not()
    {
        Point point = TargetWindow.PointAt(0.5, 0.5);
        MacroCondition Pixel(string color) => new() { Kind = ConditionKind.Pixel, X = point.X, Y = point.Y, Color = color, Tolerance = 5 };
        MacroDefinition macro = Macro("E2E pixel", Vk.F13,
            If(new MacroIf { Conditions = { Pixel(Background) }, Then = { Press(Vk.F15) }, Else = { Press(Vk.F16) } }),
            If(new MacroIf { Conditions = { Pixel("#FF0000") }, Then = { Press(Vk.F17) }, Else = { Press(Vk.F22) } }, delayMs: 100),
            Press(Vk.F23, delayMs: 100)); // the end
        using var app = new AppSession(seed => seed.Macros.Add(macro));
        using var target = new TargetWindow();

        target.Tap(Vk.F13);
        target.WaitFor(t => t.Ups(Vk.F23).Count == 1, 15, "the macro did not finish");

        Assert.True(target.Downs(Vk.F15).Count == 1, $"Then did not run for the background's color. Received: {target.Describe()}");
        Assert.Empty(target.Downs(Vk.F16));
        Assert.Empty(target.Downs(Vk.F17));
        Assert.True(target.Downs(Vk.F22).Count == 1, $"Else did not run for a color that is not there. Received: {target.Describe()}");
    }

    [Fact]
    public void An_image_is_waited_for_and_clicked_where_it_appears_and_a_missing_one_runs_Else_after_the_wait()
    {
        using Bitmap picture = Pattern(96, 64, seed: 7);
        byte[] png = Png(picture);
        string id = Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant();
        Point at = TargetWindow.PointAt(0.4, 0.4);
        var shown = new MacroCondition
        {
            Kind = ConditionKind.Image, Name = "E2E pattern", Similarity = 90, Area = SearchArea.Around,
            Images = new List<ConditionImage> { new() { Id = id, X = at.X, Y = at.Y } },
        };
        // The image appears about a second after the macro starts: Up to 8 s waits for it.
        MacroDefinition waits = Macro("E2E image", Vk.F13,
            If(new MacroIf
            {
                Conditions = { shown },
                Wait = ConditionWait.UpTo,
                WaitSeconds = 8,
                Then = { new MacroAction { Type = MacroActionType.Click, Vk = Vk.LButton, FoundId = shown.Id, FoundOffsetX = 5, DelayMs = 50 } },
                Else = { Press(Vk.F16) },
            }),
            Press(Vk.F23, delayMs: 100));
        // Somewhere it is not: after 1 s, Else.
        var missing = new MacroCondition
        {
            Kind = ConditionKind.Image, Name = "E2E missing", Similarity = 90, Area = SearchArea.Around,
            Images = new List<ConditionImage> { new() { Id = id, X = TargetWindow.PointAt(0.8, 0.1).X, Y = TargetWindow.PointAt(0.8, 0.1).Y } },
        };
        MacroDefinition gone = Macro("E2E image missing", Vk.F14,
            If(new MacroIf { Conditions = { missing }, Wait = ConditionWait.UpTo, WaitSeconds = 1, Then = { Press(Vk.F17) }, Else = { Press(Vk.F22) } }));
        using var app = new AppSession(seed =>
        {
            seed.Macros.Add(waits);
            seed.Macros.Add(gone);
            seed.Images[id] = png;
        });
        using var target = new TargetWindow();

        target.Tap(Vk.F13);
        TargetWindow.Quiet(1);
        Assert.Empty(target.Downs(Vk.LButton)); // still waiting
        target.ShowPicture(picture, at);
        target.WaitFor(t => t.Ups(Vk.F23).Count == 1, 15, "the macro did not finish");

        TargetEvent click = Assert.Single(target.Downs(Vk.LButton));
        Near(new Point(at.X + 48 + 5, at.Y + 32), click.Position, "the click where the image was found, 5 px to the right");
        Assert.Empty(target.Downs(Vk.F16));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        target.Tap(Vk.F14);
        target.WaitFor(t => t.Ups(Vk.F22).Count == 1, 10, "Else did not run for an image that is not there");
        Assert.Empty(target.Downs(Vk.F17));
        Assert.True(clock.ElapsedMilliseconds >= 900, $"Else ran after {clock.ElapsedMilliseconds} ms, before the 1 s wait was over");
    }

    /// <summary>A picture of colored squares, the same for a seed: detail an image search finds in one place only.</summary>
    internal static Bitmap Pattern(int width, int height, int seed)
    {
        var random = new Random(seed);
        var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bitmap);
        for (int y = 0; y < height; y += 8)
        {
            for (int x = 0; x < width; x += 8)
            {
                using var brush = new SolidBrush(Color.FromArgb(random.Next(256), random.Next(256), random.Next(256)));
                g.FillRectangle(brush, x, y, 8, 8);
            }
        }
        return bitmap;
    }

    private static byte[] Png(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}
