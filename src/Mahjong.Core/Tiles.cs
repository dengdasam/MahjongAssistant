namespace Mahjong.Core;

/// <summary>
/// Tile encoding identical to the original Python module:
/// 0-8 万, 9-17 筒, 18-26 条, 27-33 东南西北中发白, 34-41 花牌.
/// Flowers (34-41) are removed from the hand and replaced by a draw.
/// </summary>
public static class Tiles
{
    public const int KindCount = 34;
    public const int NameCount = 42;
    public const int CopiesPerTile = 4;

    public static readonly int[] Orphans = { 0, 8, 9, 17, 18, 26, 27, 28, 29, 30, 31, 32, 33 };

    public static readonly string[] Names = BuildNames();

    private static string[] BuildNames()
    {
        var names = new List<string>(NameCount);
        for (var i = 1; i <= 9; i++) names.Add($"{i}万");
        for (var i = 1; i <= 9; i++) names.Add($"{i}筒");
        for (var i = 1; i <= 9; i++) names.Add($"{i}条");
        foreach (var c in "东南西北中发白") names.Add(c.ToString());
        foreach (var c in "春夏秋冬梅兰竹菊") names.Add(c.ToString());
        return names.ToArray();
    }

    public static int IdOf(string name) => Array.IndexOf(Names, name);

    /// <summary>
    /// Suit index for display grouping: 0 万, 1 筒, 2 条, 3 字, 4 花.
    /// </summary>
    public static int Suit(int tile) => tile switch
    {
        < 9 => 0,
        < 18 => 1,
        < 27 => 2,
        < 34 => 3,
        _ => 4,
    };

    /// <summary>
    /// Map a 42-class YOLO label to a tile id. Returns -1 for flowers / unknown labels.
    /// B=条, C=万, D=筒; F/S are the eight flower tiles; EW..WD are the honors.
    /// </summary>
    public static int FromYoloLabel(string label)
    {
        if (string.IsNullOrEmpty(label)) return -1;
        switch (label)
        {
            case "EW": return 27; // 东
            case "SW": return 28; // 南
            case "WW": return 29; // 西
            case "NW": return 30; // 北
            case "RD": return 31; // 中
            case "GD": return 32; // 发
            case "WD": return 33; // 白
        }
        if (label.Length >= 2 && char.IsDigit(label[0]))
        {
            var number = label[0] - '0';
            var suffix = label[1];
            return suffix switch
            {
                'C' when number is >= 1 and <= 9 => number - 1,        // 万
                'D' when number is >= 1 and <= 9 => 9 + number - 1,    // 筒
                'B' when number is >= 1 and <= 9 => 18 + number - 1,   // 条
                _ => -1                                                // 花牌 1F-4F / 1S-4S
            };
        }
        return -1;
    }

    /// <summary>Count physical tiles 0-33; flowers are ignored, as in the original.</summary>
    public static int[] Count(IEnumerable<int> tiles)
    {
        var counts = new int[KindCount];
        foreach (var t in tiles)
        {
            if (t >= 0 && t < KindCount) counts[t]++;
        }
        return counts;
    }

    public static bool IsOrphan(int tile) => Array.IndexOf(Orphans, tile) >= 0;
}
