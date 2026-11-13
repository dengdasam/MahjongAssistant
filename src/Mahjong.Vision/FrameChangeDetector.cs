using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Mahjong.Vision;

/// <summary>
/// Cheap "did the hand area actually change?" check, so the app stops running the
/// model while the screen is idle.
///
/// Inference costs 250–800 ms per pass; a mahjong hand sits still for most of the
/// game, so the majority of that work is redundant. The frame is shrunk to a tiny
/// grid and compared cell-by-cell against the previous sample: a static region
/// yields bit-identical captures, so the test is both cheap (~1 ms) and safe.
///
/// Deliberately conservative:
/// <list type="bullet">
///   <item>only the last known hand area is sampled, not the whole screen (a
///   blinking clock elsewhere must not count as a change);</item>
///   <item>after <see cref="MaxSkip"/> consecutive skips one inference is forced,
///   so slow drift can never hide the truth indefinitely.</item>
/// </list>
/// </summary>
public sealed class FrameChangeDetector
{
    private readonly int _cols;
    private readonly int _rows;
    private readonly int _maxSkip;
    private readonly double _changedCellRatio;
    private readonly int _cellDelta;

    private byte[]? _previous;
    private int _skipped;

    public FrameChangeDetector(
        int cols = 48,
        int rows = 48,
        int maxSkip = 20,
        double changedCellRatio = 0.03,
        int cellDelta = 10)
    {
        _cols = Math.Max(4, cols);
        _rows = Math.Max(4, rows);
        _maxSkip = Math.Max(1, maxSkip);
        _changedCellRatio = Math.Clamp(changedCellRatio, 0.001, 1.0);
        _cellDelta = Math.Max(1, cellDelta);
    }

    /// <summary>Consecutive frames skipped since the last real analysis.</summary>
    public int Skipped => _skipped;

    public int MaxSkip => _maxSkip;

    /// <summary>
    /// True when <paramref name="frame"/> differs from the previous sample inside
    /// <paramref name="region"/>. The first call always reports a change, and a
    /// forced refresh happens every <see cref="MaxSkip"/> skips.
    /// </summary>
    public bool HasChanged(Bitmap frame, Rectangle region)
    {
        var sample = Sample(frame, region);
        if (sample is null) return true;

        if (_previous is null || _previous.Length != sample.Length)
        {
            _previous = sample;
            _skipped = 0;
            return true;
        }

        if (_skipped >= _maxSkip)
        {
            _previous = sample;
            _skipped = 0;
            return true;
        }

        var changed = 0;
        for (var i = 0; i < sample.Length; i++)
        {
            if (Math.Abs(sample[i] - _previous[i]) > _cellDelta) changed++;
        }

        _previous = sample;

        if (changed >= Math.Max(1, (int)(sample.Length * _changedCellRatio)))
        {
            _skipped = 0;
            return true;
        }

        _skipped++;
        return false;
    }

    public void Reset()
    {
        _previous = null;
        _skipped = 0;
    }

    /// <summary>Grey-level grid for the region, or null when the region is unusable.</summary>
    private byte[]? Sample(Bitmap frame, Rectangle region)
    {
        region = Rectangle.Intersect(region, new Rectangle(0, 0, frame.Width, frame.Height));
        if (region.Width < 8 || region.Height < 8) return null;

        using var thumb = new Bitmap(_cols, _rows, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(thumb))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(frame, new Rectangle(0, 0, _cols, _rows), region, GraphicsUnit.Pixel);
        }

        var data = thumb.LockBits(new Rectangle(0, 0, _cols, _rows), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var stride = Math.Abs(data.Stride);
            var buffer = new byte[stride * _rows];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

            var result = new byte[_cols * _rows];
            for (var y = 0; y < _rows; y++)
            {
                var row = y * stride;
                for (var x = 0; x < _cols; x++)
                {
                    var i = row + x * 3;
                    // Rec. 601 luma, integer maths
                    result[y * _cols + x] = (byte)((buffer[i + 2] * 77 + buffer[i + 1] * 150 + buffer[i] * 29) >> 8);
                }
            }

            return result;
        }
        finally
        {
            thumb.UnlockBits(data);
        }
    }
}
