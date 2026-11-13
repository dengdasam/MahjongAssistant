using Mahjong.Core;
using Mahjong.Vision;
using Xunit;

namespace Mahjong.Tests;

/// <summary>
/// Covers the additions that sit on top of the ported detector: the multi-pass
/// ladder (pass selection + crop rule) and the frame stabiliser. The ported
/// behaviour itself is locked down by HandRowTests / GoldenVectorTests.
/// </summary>
public class OptimizationTests
{
    // ---------- hand stabiliser ----------

    [Fact]
    public void FirstReadingIsShownImmediately()
    {
        var stabilizer = new HandStabilizer(2);
        var shown = stabilizer.Push(new[] { 1, 2, 3 });

        Assert.Equal(new[] { 1, 2, 3 }, shown);
    }

    [Fact]
    public void SingleFrameBlipDoesNotReplaceTheShownHand()
    {
        var stabilizer = new HandStabilizer(2);
        stabilizer.Push(new[] { 1, 2, 3 });

        var afterBlip = stabilizer.Push(new[] { 1, 2, 9 });

        Assert.Equal(new[] { 1, 2, 3 }, afterBlip);
    }

    [Fact]
    public void RepeatedChangeIsAccepted()
    {
        var stabilizer = new HandStabilizer(2);
        stabilizer.Push(new[] { 1, 2, 3 });

        stabilizer.Push(new[] { 4, 5, 6 });
        var accepted = stabilizer.Push(new[] { 4, 5, 6 });

        Assert.Equal(new[] { 4, 5, 6 }, accepted);
    }

    [Fact]
    public void EmptyReadingClearsImmediately()
    {
        var stabilizer = new HandStabilizer(3);
        stabilizer.Push(new[] { 1, 2, 3 });

        var cleared = stabilizer.Push(Array.Empty<int>());

        Assert.Null(cleared);
        Assert.Null(stabilizer.Shown);
    }

    [Fact]
    public void RequiredHitsOneKeepsLegacyBehaviour()
    {
        var stabilizer = new HandStabilizer(1);
        stabilizer.Push(new[] { 1, 2, 3 });

        var changed = stabilizer.Push(new[] { 7, 8, 9 });

        Assert.Equal(new[] { 7, 8, 9 }, changed);
    }

    // ---------- pass ranking ----------

    [Theory]
    [InlineData(13, true)]
    [InlineData(14, true)]
    [InlineData(0, false)]
    [InlineData(12, false)]
    [InlineData(15, false)]
    public void PlayableHandsAreThirteenOrFourteen(int count, bool expected) =>
        Assert.Equal(expected, FrameAnalyzer.IsPlayable(count));

    // ---------- crop rule ----------

    [Fact]
    public void CropsSoTilesLandNearTheTargetSize()
    {
        // 768x1435 phone mirror: the whole frame letterboxes to 0.446x, leaving a 40 px
        // tile at 17.8 px. The crop should lift it back to roughly 22 px, not further.
        const int width = 768;
        const int height = 1435;
        const float target = 22f;
        var attempt = Attempt(Row(14, 40f, 55f, x0: 60f, y0: 1290f), width, height);

        var roi = FrameAnalyzer.ComputeRoi(attempt, width, height, 640, 640, target, 1.12f);

        Assert.NotNull(roi);
        var rect = roi!.Value;
        Assert.True(rect.Right <= width && rect.Bottom <= height);
        Assert.True(rect.Left >= 0 && rect.Top >= 0);

        var scale = Math.Min(640f / rect.Width, 640f / rect.Height);
        var tilePixels = 40f * scale;
        Assert.InRange(tilePixels, target - 2f, target + 2f);
    }

    [Fact]
    public void SkipsTheCropWhenTilesAreAlreadyBigEnough()
    {
        // 640x640 frame, 40 px tiles: the model already sees them larger than target,
        // and cropping would only make the network miss them.
        const int size = 640;
        var attempt = Attempt(Row(14, 40f, 55f, x0: 30f, y0: 560f), size, size);

        var roi = FrameAnalyzer.ComputeRoi(attempt, size, size, 640, 640, 22f, 1.12f);

        Assert.Null(roi);
    }

    [Fact]
    public void SkipsACropThatWouldCutTheRow()
    {
        // Very wide tiles on a narrow frame: reaching the target scale would clip tiles
        // off both ends, so the crop must be abandoned instead.
        var attempt = Attempt(Row(14, 120f, 160f, x0: 10f, y0: 1250f), 768, 1435);

        var roi = FrameAnalyzer.ComputeRoi(attempt, 768, 1435, 640, 640, 22f, 1.12f);

        Assert.Null(roi);
    }

    [Fact]
    public void ReturnsNullWhenNothingWasDetected()
    {
        var attempt = Attempt(Array.Empty<TileDetection>(), 768, 1435);

        Assert.Null(FrameAnalyzer.ComputeRoi(attempt, 768, 1435, 640, 640, 22f, 1.12f));
    }

    // ---------- helpers ----------

    private static IReadOnlyList<TileDetection> Row(int count, float tileWidth, float tileHeight, float x0, float y0)
    {
        var row = new List<TileDetection>(count);
        for (var i = 0; i < count; i++)
        {
            row.Add(new TileDetection(
                i % 34,
                "1B",
                0.8f,
                x0 + i * (tileWidth + 6f),
                y0,
                tileWidth,
                tileHeight));
        }

        return row;
    }

    private static FrameAttempt Attempt(IReadOnlyList<TileDetection> row, int width, int height)
    {
        var selection = HandRowSelector.Select(row, width, height);
        return new FrameAttempt("test", null, 0.25f, row.Count, row.Count, 1f, 0, selection, row);
    }
}
