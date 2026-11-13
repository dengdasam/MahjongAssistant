using System.Drawing;
using Mahjong.Core;
using Mahjong.Vision;
using Xunit;

namespace Mahjong.Tests;

/// <summary>
/// The idle-frame gate decides whether to skip a 250–800 ms inference pass, so it
/// must never claim "nothing changed" on a real change, and must not keep skipping
/// forever when the screen is genuinely static.
/// </summary>
public sealed class FrameChangeTests
{
    private static Bitmap Frame(int width = 320, int height = 240, Color? fill = null)
    {
        var bitmap = new Bitmap(width, height);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(fill ?? Color.FromArgb(18, 92, 60));
        return bitmap;
    }

    private static Rectangle Whole(Bitmap bitmap) => new(0, 0, bitmap.Width, bitmap.Height);

    [Fact]
    public void FirstFrameAlwaysCountsAsChanged()
    {
        using var frame = Frame();
        var gate = new FrameChangeDetector();

        Assert.True(gate.HasChanged(frame, Whole(frame)));
        Assert.Equal(0, gate.Skipped);
    }

    [Fact]
    public void IdenticalFramesAreSkipped()
    {
        using var frame = Frame();
        var gate = new FrameChangeDetector();

        gate.HasChanged(frame, Whole(frame));
        Assert.False(gate.HasChanged(frame, Whole(frame)));
        Assert.False(gate.HasChanged(frame, Whole(frame)));
        Assert.Equal(2, gate.Skipped);
    }

    [Fact]
    public void ADrawnTileCountsAsAChange()
    {
        using var frame = Frame();
        var gate = new FrameChangeDetector();
        gate.HasChanged(frame, Whole(frame));

        using (var g = Graphics.FromImage(frame))
        {
            g.FillRectangle(Brushes.White, 40, 40, 60, 80);
        }

        Assert.True(gate.HasChanged(frame, Whole(frame)));
        Assert.Equal(0, gate.Skipped);
    }

    [Fact]
    public void ChangesOutsideTheRegionAreIgnored()
    {
        using var frame = Frame();
        var watch = new Rectangle(0, 160, 320, 80);
        var gate = new FrameChangeDetector();
        gate.HasChanged(frame, watch);

        // Timer / chat area above the hand strip keeps animating.
        using (var g = Graphics.FromImage(frame))
        {
            g.FillRectangle(Brushes.Orange, 0, 0, 320, 40);
        }

        Assert.False(gate.HasChanged(frame, watch));
    }

    [Fact]
    public void ForcesARefreshAfterTheSkipBudget()
    {
        using var frame = Frame();
        var gate = new FrameChangeDetector(maxSkip: 3);
        gate.HasChanged(frame, Whole(frame));

        // Exactly maxSkip frames may be skipped...
        Assert.False(gate.HasChanged(frame, Whole(frame)));
        Assert.False(gate.HasChanged(frame, Whole(frame)));
        Assert.False(gate.HasChanged(frame, Whole(frame)));
        Assert.Equal(3, gate.Skipped);

        // ...then one inference is forced even though nothing moved.
        Assert.True(gate.HasChanged(frame, Whole(frame)));
        Assert.Equal(0, gate.Skipped);
    }

    [Fact]
    public void ResetForgetsThePreviousFrame()
    {
        using var frame = Frame();
        var gate = new FrameChangeDetector();
        gate.HasChanged(frame, Whole(frame));
        Assert.False(gate.HasChanged(frame, Whole(frame)));

        gate.Reset();

        Assert.True(gate.HasChanged(frame, Whole(frame)));
    }

    [Fact]
    public void DegenerateRegionCountsAsChanged()
    {
        using var frame = Frame();
        var gate = new FrameChangeDetector();
        gate.HasChanged(frame, Whole(frame));

        // A 2x2 region (window collapsed) must never be treated as "no change".
        Assert.True(gate.HasChanged(frame, new Rectangle(0, 0, 2, 2)));
    }

    [Theory]
    [InlineData(0, 0)]      // 1万
    [InlineData(8, 0)]      // 9万
    [InlineData(9, 1)]      // 1筒
    [InlineData(17, 1)]     // 9筒
    [InlineData(18, 2)]     // 1条
    [InlineData(26, 2)]     // 9条
    [InlineData(27, 3)]     // 东
    [InlineData(33, 3)]     // 白
    [InlineData(34, 4)]     // 花
    public void SuitGroupsTheHandForDisplay(int tile, int suit)
    {
        Assert.Equal(suit, Tiles.Suit(tile));
    }

    [Fact]
    public void ReportsPendingWhileAChangeIsUnconfirmed()
    {
        var stabilizer = new HandStabilizer(requiredHits: 2);

        stabilizer.Push(new[] { 0, 0, 0 });
        Assert.False(stabilizer.HasPending); // first reading is shown at once

        stabilizer.Push(new[] { 1, 1, 1 });
        Assert.True(stabilizer.HasPending);  // a frame gate must not skip here

        stabilizer.Push(new[] { 1, 1, 1 });
        Assert.False(stabilizer.HasPending); // confirmed, safe to skip again
    }

    // ---------- card placement ----------

    private static NativeWindow.Rect Rect(int left, int top, int width, int height) =>
        new() { Left = left, Top = top, Right = left + width, Bottom = top + height };

    [Fact]
    public void CardSitsRightOfTheTableAt100Percent()
    {
        var table = Rect(100, 50, 1000, 800);
        var work = Rect(0, 0, 1920, 1040);

        var (left, top) = OverlayGeometry.Beside(table, work, dpi: 96, cardWidth: 220);

        Assert.Equal(1112, left, 1);          // table.Right + 12
        Assert.Equal(140, top, 1);            // table.Top + 90
    }

    [Fact]
    public void CardIsConvertedToDipsAt150Percent()
    {
        // Same physical geometry, but WPF expects 1/1.5 of the pixel value.
        var table = Rect(150, 75, 1500, 1200);
        var work = Rect(0, 0, 2880, 1560);

        var (left, top) = OverlayGeometry.Beside(table, work, dpi: 144, cardWidth: 220);

        Assert.Equal((1650 + 12) * 96.0 / 144, left, 1);
        Assert.Equal((75 + 90) * 96.0 / 144, top, 1);
    }

    [Fact]
    public void CardFlipsToTheLeftWhenTheTableTouchesTheRightEdge()
    {
        var table = Rect(900, 40, 1010, 800);   // ends at x=1910, only 10px left
        var work = Rect(0, 0, 1920, 1040);

        var (left, _) = OverlayGeometry.Beside(table, work, dpi: 96, cardWidth: 220);

        Assert.Equal(900 - 220 - 12, left, 1);
        Assert.True(left >= 0);
    }
}
