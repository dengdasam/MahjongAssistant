using Mahjong.Core;

namespace Mahjong.Vision;

/// <summary>
/// Faithful port of the original <c>choose_hand</c>:
/// 1. keep detections inside a 20% band along each of the four edges;
/// 2. inside a band, cluster by centre with tolerance = 5.5% of that axis;
/// 3. pick the cluster with the most tiles, breaking ties by summed confidence;
/// 4. for the top/bottom edges, split the row wherever the horizontal gap
///    exceeds 1.8x the median tile width, then keep the best segment.
/// The edge with the most (tiles, confidence) wins, bottom edge first on a tie.
/// </summary>
public static class HandRowSelector
{
    private const float Band = 0.2f;
    private const float ToleranceRatio = 0.055f;
    private const float GapFactor = 1.8f;

    private const string Bottom = "下";
    private const string Top = "上";
    private const string Left = "左";
    private const string Right = "右";

    public static HandRowSelection Select(
        IReadOnlyList<TileDetection> detections,
        int imageWidth,
        int imageHeight)
    {
        var edges = new Dictionary<string, List<TileDetection>>
        {
            [Bottom] = detections.Where(d => d.Y + d.Height / 2f >= imageHeight * (1 - Band)).ToList(),
            [Top] = detections.Where(d => d.Y + d.Height / 2f <= imageHeight * Band).ToList(),
            [Left] = detections.Where(d => d.X + d.Width / 2f <= imageWidth * Band).ToList(),
            [Right] = detections.Where(d => d.X + d.Width / 2f >= imageWidth * (1 - Band)).ToList(),
        };

        var rows = edges.ToDictionary(
            pair => pair.Key,
            pair => Cluster(pair.Key, pair.Value, imageWidth, imageHeight));

        var best = rows
            .OrderByDescending(pair => pair.Value.Count)
            .ThenByDescending(pair => pair.Value.Sum(d => d.Confidence))
            .ThenByDescending(pair => pair.Key == Bottom)
            .First();

        // Cluster already returns the row in the original order (x for top/bottom, y for sides).
        var row = best.Value;
        var counts = new EdgeCounts(
            edges[Bottom].Count,
            edges[Top].Count,
            edges[Left].Count,
            edges[Right].Count);

        return new HandRowSelection(row, best.Key, counts);
    }

    private static List<TileDetection> Cluster(
        string edge,
        List<TileDetection> items,
        int imageWidth,
        int imageHeight)
    {
        if (items.Count == 0) return new List<TileDetection>();

        var useY = edge is Bottom or Top;
        var dimension = useY ? imageHeight : imageWidth;
        var tolerance = dimension * ToleranceRatio;

        float Center(TileDetection d) => useY ? d.Y + d.Height / 2f : d.X + d.Width / 2f;

        var best = new List<TileDetection>();
        foreach (var anchor in items)
        {
            var c = Center(anchor);
            var cluster = items.Where(item => Math.Abs(Center(item) - c) <= tolerance).ToList();
            if (IsBetter(cluster, best)) best = cluster;
        }

        if (!useY)
        {
            best.Sort((a, b) => a.Y.CompareTo(b.Y));
            return best;
        }

        best.Sort((a, b) => a.X.CompareTo(b.X));
        if (best.Count <= 1) return best;

        var widths = best.Select(d => d.Width).OrderBy(w => w).ToList();
        var gapLimit = widths[widths.Count / 2] * GapFactor;

        var segments = new List<List<TileDetection>> { new() { best[0] } };
        foreach (var item in best.Skip(1))
        {
            var previous = segments[^1][^1];
            var previousCenter = previous.X + previous.Width / 2f;
            var itemCenter = item.X + item.Width / 2f;
            if (itemCenter - previousCenter > gapLimit) segments.Add(new List<TileDetection> { item });
            else segments[^1].Add(item);
        }

        var winner = segments[0];
        foreach (var segment in segments.Skip(1))
        {
            if (IsBetter(segment, winner)) winner = segment;
        }

        return winner;
    }

    /// <summary>Strict comparison of (count, summed confidence), matching the original `&gt;`.</summary>
    private static bool IsBetter(List<TileDetection> candidate, List<TileDetection> current)
    {
        if (candidate.Count != current.Count) return candidate.Count > current.Count;
        return candidate.Sum(d => d.Confidence) > current.Sum(d => d.Confidence);
    }
}

public readonly record struct EdgeCounts(int Bottom, int Top, int Left, int Right);

public readonly record struct HandRowSelection(
    IReadOnlyList<TileDetection> Row,
    string Edge,
    EdgeCounts Counts);
