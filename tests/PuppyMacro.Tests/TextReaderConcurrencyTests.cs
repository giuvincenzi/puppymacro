using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using PuppyMacro.Models;
using PuppyMacro.Services.Screen;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>
/// Windows' text recognition read from several threads at once, as two macros with Text conditions that run together do
/// (each on its own thread, with its own ConditionChecker, the same language).
/// </summary>
public class TextReaderConcurrencyTests
{
    [Fact]
    public async Task Two_macros_reading_text_at_the_same_time_both_read_it()
    {
        if (TextReader.Languages.Count == 0)
            return;
        using var picture = new Mat(200, 700, MatType.CV_8UC3, Scalar.All(255));
        Cv2.PutText(picture, "Open the CURRENCY tab", new Point(40, 120), HersheyFonts.HersheySimplex, 1.4, Scalar.All(0), 3);
        using var start = new Barrier(4);

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            start.SignalAndWait(TestContext.Current.CancellationToken);
            for (int i = 0; i < 5; i++)
            {
                using var copy = picture.Clone();
                Assert.NotNull(TextReader.Find(TextReader.Read(copy, null), "currency", TextMatch.Exact, false));
            }
        }, TestContext.Current.CancellationToken)).ToArray();

        // Each read must succeed: a COMException ("Another RecognizeAsync operation is already running") makes a running
        // macro end with "failed".
        await Task.WhenAll(readers);
    }
}
