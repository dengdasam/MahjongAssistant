using System.Drawing;
using Mahjong.Core;

namespace Mahjong.Vision;

/// <summary>Tunables for <see cref="FrameAnalyzer"/>; mirrors the values in appsettings.json.</summary>
public sealed record DetectorOptions
{
    public float Confidence { get; init; } = 0.25f;
    public float Iou { get; init; } = 0.45f;

    /// <summary>Second-chance threshold used only when a frame gets too few tiles.</summary>
    public float LowConfidence { get; init; } = 0.12f;

    /// <summary>Re-run the hand band as a crop so tiles span more pixels.</summary>
    public bool AdaptiveRefine { get; init; } = true;

    /// <summary>Retry with <see cref="LowConfidence"/> when the hand is not 13/14 tiles.</summary>
    public bool DeepRetry { get; init; } = true;

    /// <summary>
    /// Tile width, in model-input pixels, that this network detects best.
    /// Measured with <c>Mahjong.Smoke scan</c>: detection falls off sharply above
    /// ~28 px, so frames whose tiles are smaller than this get cropped to match.
    /// </summary>
    public float TargetTilePixels { get; init; } = 22f;

    /// <summary>Minimum scale improvement before a cropped pass is worth the extra inference.</summary>
    public float MinScaleGain { get; init; } = 1.12f;

    public static DetectorOptions Default { get; } = new();
}

/// <summary>One inference pass over a frame.</summary>
public sealed record FrameAttempt(
    string Name,
    Rectangle? Roi,
    float Confidence,
    int Detections,
    int HandCount,
    float ConfidenceSum,
    long ElapsedMs,
    HandRowSelection Selection,
    IReadOnlyList<TileDetection> All)
{
    /// <summary>
    /// Average confidence of the tiles in the row. Used to tell a genuine 14-tile
    /// hand apart from 14 tiles scavenged at a very low threshold.
    /// </summary>
    public float MeanConfidence => HandCount > 0 ? ConfidenceSum / HandCount : 0f;

    public string Describe() =>
        $"{Name,-9} conf {Confidence:0.00} roi {(Roi is { } r ? $"{r.Width}x{r.Height}" : "full")} " +
        $"-> {Detections} 张 / 手牌 {HandCount} 张 · 均置信 {MeanConfidence:0.00} · {ElapsedMs} ms";
}

/// <summary>Result of analysing one frame, including every pass that was tried.</summary>
public sealed record FrameAnalysis(
    HandRowSelection Selection,
    IReadOnlyList<TileDetection> All,
    string Strategy,
    long ElapsedMs,
    IReadOnlyList<FrameAttempt> Attempts,
    EdgeCounts Counts)
{
    public IReadOnlyList<TileDetection> Hand => Selection.Row;
    public string Edge => Selection.Edge;
    public int Detections => All.Count;
}

/// <summary>
/// Runs the detector as a small ladder of passes and keeps the best one.
///
/// The model graph is fixed at 640×640, so the only way to give a tile more
/// pixels is to feed it less background. A portrait phone frame (768×1435)
/// letterboxes down to 0.446×, which leaves a 40 px tile at 17.8 px — below what
/// a stride-8 head can reliably resolve. Cropping the hand band first lifts that
/// to 0.83× (33 px) with the *same* weights.
///
/// The ladder is deliberately conservative: when the first pass already returns a
/// playable 13/14-tile hand, nothing else runs, so behaviour and latency stay
/// identical to the single-pass version.
/// </summary>
public static class FrameAnalyzer
{
    public static FrameAnalysis Analyze(Bitmap frame, TileDetector detector, DetectorOptions? options = null)
    {
        var opt = options ?? DetectorOptions.Default;
        var attempts = new List<FrameAttempt>();
        var total = System.Diagnostics.Stopwatch.StartNew();

        var primary = Run("full", null, opt.Confidence, frame, detector, opt.Iou);
        attempts.Add(primary);
        var best = primary;

        if (!IsPlayable(best.HandCount) && opt.DeepRetry)
        {
            var low = Run("full-low", null, opt.LowConfidence, frame, detector, opt.Iou);
            attempts.Add(low);
            best = Better(best, low);
        }

        if (!IsPlayable(best.HandCount) && opt.AdaptiveRefine)
        {
            var roi = ComputeRoi(best, frame.Width, frame.Height, detector.InputWidth, detector.InputHeight,
                opt.TargetTilePixels, opt.MinScaleGain);
            if (roi is { } region)
            {
                var refined = Run("roi", region, opt.Confidence, frame, detector, opt.Iou);
                attempts.Add(refined);
                best = Better(best, refined);

                if (!IsPlayable(best.HandCount) && opt.DeepRetry && best.HandCount < 13)
                {
                    var refinedLow = Run("roi-low", region, opt.LowConfidence, frame, detector, opt.Iou);
                    attempts.Add(refinedLow);
                    best = Better(best, refinedLow);
                }
            }
        }

        total.Stop();

        // Counts describe the whole frame, so always report the primary pass.
        return new FrameAnalysis(best.Selection, best.All, best.Name, total.ElapsedMilliseconds, attempts, primary.Selection.Counts);
    }

    private static FrameAttempt Run(
        string name,
        Rectangle? roi,
        float confidence,
        Bitmap frame,
        TileDetector detector,
        float iou)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var all = detector.Detect(frame, confidence, iou, roi);
        var selection = HandRowSelector.Select(all, frame.Width, frame.Height);
        watch.Stop();

        return new FrameAttempt(
            name,
            roi,
            confidence,
            all.Count,
            selection.Row.Count,
            selection.Row.Sum(d => d.Confidence),
            watch.ElapsedMilliseconds,
            selection,
            all);
    }

    public static bool IsPlayable(int handCount) => handCount is 13 or 14;

    /// <summary>
    /// Rank passes so the ladder can only improve on the single-pass result.
    /// A playable 13/14-tile hand always outranks an unplayable one; among playable
    /// passes the score is <c>count + 4 × mean confidence</c>, which stops a low
    /// threshold from winning by inventing tiles at 0.12 confidence (one extra tile
    /// only outweighs a confidence drop of 0.25).
    /// </summary>
    private static FrameAttempt Better(FrameAttempt current, FrameAttempt candidate)
    {
        var a = Rank(current);
        var b = Rank(candidate);
        if (b > a + 1e-4f) return candidate;
        if (b < a - 1e-4f) return current;
        return candidate.ConfidenceSum > current.ConfidenceSum ? candidate : current;
    }

    private static float Rank(FrameAttempt attempt) =>
        IsPlayable(attempt.HandCount)
            ? 100f + attempt.HandCount + 4f * attempt.MeanConfidence
            : attempt.HandCount * 0.5f;

    /// <summary>
    /// Crop the frame so the hand's tiles land near <paramref name="targetTilePixels"/>
    /// in the model input — the size this network was actually trained on.
    ///
    /// Measured with <c>Mahjong.Smoke scan</c>: detection collapses as tiles grow
    /// (23 px → 8–12 tiles, 34 px → 0), so blindly zooming in is worse than useless.
    /// Returns null when tiles are already big enough, when the crop would cut the
    /// row, or when the gain is too small to justify another inference.
    /// </summary>
    public static Rectangle? ComputeRoi(
        FrameAttempt attempt,
        int frameWidth,
        int frameHeight,
        int inputWidth,
        int inputHeight,
        float targetTilePixels,
        float minScaleGain)
    {
        float ScaleFor(int width, int height) =>
            Math.Min((float)inputWidth / Math.Max(1, width), (float)inputHeight / Math.Max(1, height));

        var row = attempt.Selection.Row;
        var reference = row.Count > 0 ? row : attempt.All;
        if (reference.Count == 0) return null;

        var tileWidth = Median(reference.Select(d => d.Width));
        var tileHeight = Median(reference.Select(d => d.Height));
        if (tileWidth <= 0 || tileHeight <= 0) return null;

        var currentScale = ScaleFor(frameWidth, frameHeight);
        var desiredScale = targetTilePixels / tileWidth;
        if (desiredScale < currentScale * minScaleGain) return null;

        // A crop of this size is the largest one that still yields the desired scale.
        var maxWidth = (int)Math.Floor(inputWidth / desiredScale);
        var maxHeight = (int)Math.Floor(inputHeight / desiredScale);
        if (maxWidth < 32 || maxHeight < 32) return null;

        var rowLeft = reference.Min(d => d.X);
        var rowRight = reference.Max(d => d.X + d.Width);
        var rowTop = reference.Min(d => d.Y);
        var rowBottom = reference.Max(d => d.Y + d.Height);

        // Margin so a tile the first pass missed still falls inside the crop.
        var neededWidth = (int)Math.Ceiling(rowRight - rowLeft + 2 * tileWidth * 1.2f);
        var neededHeight = (int)Math.Ceiling(rowBottom - rowTop + 2 * tileHeight * 1.2f);
        if (neededWidth > maxWidth || neededHeight > maxHeight) return null;

        var width = Math.Min(frameWidth, Math.Max(neededWidth, maxWidth));
        var height = Math.Min(frameHeight, Math.Max(neededHeight, maxHeight));

        var centreX = (rowLeft + rowRight) / 2f;
        var centreY = (rowTop + rowBottom) / 2f;
        var left = Math.Clamp((int)Math.Round(centreX - width / 2f), 0, Math.Max(0, frameWidth - width));
        var top = Math.Clamp((int)Math.Round(centreY - height / 2f), 0, Math.Max(0, frameHeight - height));

        var rect = new Rectangle(left, top, width, height);
        return ScaleFor(rect.Width, rect.Height) < currentScale * minScaleGain ? null : rect;
    }

    private static float Median(IEnumerable<float> values)
    {
        var ordered = values.OrderBy(v => v).ToArray();
        if (ordered.Length == 0) return 0f;
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 1 ? ordered[middle] : (ordered[middle - 1] + ordered[middle]) / 2f;
    }
}
