using System.Collections.Concurrent;

namespace Mahjong.Core;

/// <summary>
/// Direct port of the original <c>mahjong_probability</c> module (CPython 3.13 bytecode).
/// Behaviour is verified against golden vectors produced by the original module.
/// </summary>
public static class HandAnalyzer
{
    private static readonly ConcurrentDictionary<(UInt128 Counts, int Needed), bool> MeldableCache = new();
    private static readonly ConcurrentDictionary<(UInt128 Counts, int OpenMelds), int> ShantenCache = new();

    /// <summary>Standard hand + seven pairs + thirteen orphans, exactly as the original.</summary>
    public static bool IsWin(IReadOnlyList<int> hand, int openMelds = 0, bool sevenPairs = true, bool thirteenOrphans = true)
    {
        if (hand.Count + openMelds * 3 != 14) return false;

        var counts = Tiles.Count(hand);
        if (counts.Max() > 4) return false;

        if (openMelds == 0 && sevenPairs && counts.Count(x => x >= 2) == 7) return true;
        if (openMelds == 0 && thirteenOrphans && IsThirteenOrphans(counts)) return true;

        for (var pair = 0; pair < Tiles.KindCount; pair++)
        {
            if (counts[pair] < 2) continue;
            counts[pair] -= 2;
            var ok = Meldable(counts, 4 - openMelds);
            counts[pair] += 2;
            if (ok) return true;
        }
        return false;
    }

    private static bool IsThirteenOrphans(int[] counts)
    {
        foreach (var t in Tiles.Orphans)
        {
            if (counts[t] == 0) return false;
        }
        if (!Tiles.Orphans.Any(t => counts[t] >= 2)) return false;
        if (counts.Sum() != 14) return false;
        for (var t = 0; t < Tiles.KindCount; t++)
        {
            if (counts[t] != 0 && !Tiles.IsOrphan(t)) return false;
        }
        return true;
    }

    /// <summary>Can the concealed part be split into exactly <paramref name="needed"/> melds with nothing left over?</summary>
    private static bool Meldable(int[] counts, int needed)
    {
        var key = (PackCounts(counts), needed);
        if (MeldableCache.TryGetValue(key, out var cached)) return cached;

        var memo = new Dictionary<UInt128, bool>();
        bool Rec(int[] c, int n)
        {
            var localKey = PackCounts(c) | ((UInt128)(uint)n << 110);
            if (memo.TryGetValue(localKey, out var known)) return known;

            bool result;
            if (n == 0)
            {
                result = true;
                foreach (var x in c)
                {
                    if (x != 0) { result = false; break; }
                }
            }
            else
            {
                result = false;
                var i = Array.FindIndex(c, x => x > 0);
                if (i >= 0)
                {
                    if (c[i] >= 3)
                    {
                        var a = (int[])c.Clone();
                        a[i] -= 3;
                        if (Rec(a, n - 1)) result = true;
                    }
                    if (!result && i < 27 && i % 9 <= 6 && c[i + 1] > 0 && c[i + 2] > 0)
                    {
                        var a = (int[])c.Clone();
                        a[i]--; a[i + 1]--; a[i + 2]--;
                        if (Rec(a, n - 1)) result = true;
                    }
                }
            }
            memo[localKey] = result;
            return result;
        }

        var answer = Rec((int[])counts.Clone(), needed);
        MeldableCache[key] = answer;
        return answer;
    }

    /// <summary>Tiles that complete the hand. Values are physical copies left (4 - own count).</summary>
    public static Dictionary<int, int> Waits(IReadOnlyList<int> hand, int openMelds = 0)
    {
        var counts = Tiles.Count(hand);
        var result = new Dictionary<int, int>();
        for (var t = 0; t < Tiles.KindCount; t++)
        {
            if (counts[t] >= 4) continue;
            var next = new List<int>(hand.Count + 1);
            next.AddRange(hand);
            next.Add(t);
            if (IsWin(next, openMelds)) result[t] = 4 - counts[t];
        }
        return result;
    }

    /// <summary>Minimum tiles from tenpai; -1 means complete. Supports standard, chiitoi, kokushi.</summary>
    public static int Shanten(IReadOnlyList<int> hand, int openMelds = 0)
    {
        var logical = hand.Count + openMelds * 3;
        if (logical != 13 && logical != 14)
            throw new ArgumentException("concealed hand plus exposed melds must total 13 or 14 logical tiles", nameof(hand));

        var counts = Tiles.Count(hand);
        var cacheKey = (PackCounts(counts), openMelds);
        if (ShantenCache.TryGetValue(cacheKey, out var cached)) return cached;

        var memo = new Dictionary<UInt128, int>();
        int Dfs(int[] c, int melds, int taatsu, int pair)
        {
            var key = PackCounts(c)
                      | ((UInt128)(uint)melds << 102)
                      | ((UInt128)(uint)taatsu << 106)
                      | ((UInt128)(uint)pair << 110);
            if (memo.TryGetValue(key, out var known)) return known;

            var i = Array.FindIndex(c, x => x > 0);
            int result;
            if (i < 0)
            {
                var m = Math.Min(melds, 4 - openMelds);
                var t = Math.Min(taatsu, 4 - openMelds - m);
                result = 8 - 2 * m - t - pair;
            }
            else
            {
                var best = 99;
                int[] Take(params int[] indices)
                {
                    var a = (int[])c.Clone();
                    foreach (var j in indices) a[j]--;
                    return a;
                }

                if (c[i] >= 3) best = Math.Min(best, Dfs(Take(i, i, i), melds + 1, taatsu, pair));
                if (i < 27 && i % 9 <= 6 && c[i + 1] > 0 && c[i + 2] > 0)
                    best = Math.Min(best, Dfs(Take(i, i + 1, i + 2), melds + 1, taatsu, pair));
                if (pair == 0 && c[i] >= 2) best = Math.Min(best, Dfs(Take(i, i), melds, taatsu, 1));

                if (taatsu < 4 - openMelds)
                {
                    if (c[i] >= 2) best = Math.Min(best, Dfs(Take(i, i), melds, taatsu + 1, pair));
                    if (i < 27)
                    {
                        if (i % 9 <= 7 && c[i + 1] > 0) best = Math.Min(best, Dfs(Take(i, i + 1), melds, taatsu + 1, pair));
                        if (i % 9 <= 6 && c[i + 2] > 0) best = Math.Min(best, Dfs(Take(i, i + 2), melds, taatsu + 1, pair));
                    }
                }

                best = Math.Min(best, Dfs(Take(i), melds, taatsu, pair));
                result = best;
            }
            memo[key] = result;
            return result;
        }

        var regular = Dfs(counts, 0, 0, 0);
        if (openMelds == 0)
        {
            var pairs = counts.Count(x => x >= 2);
            var distinct = counts.Count(x => x > 0);
            regular = Math.Min(regular, 6 - pairs + Math.Max(0, 7 - distinct));

            var kinds = Tiles.Orphans.Count(t => counts[t] > 0);
            var hasPair = Tiles.Orphans.Any(t => counts[t] >= 2) ? 1 : 0;
            regular = Math.Min(regular, 13 - kinds - hasPair);
        }

        ShantenCache[cacheKey] = regular;
        return regular;
    }

    /// <summary>Physical tile counts that lower shanten after drawing (13-tile state).</summary>
    public static Dictionary<int, int> ImprovingTiles(IReadOnlyList<int> hand, IReadOnlyList<int>? visible = null, int openMelds = 0)
    {
        var baseline = Shanten(hand, openMelds);
        var own = Tiles.Count(hand);
        var seen = Tiles.Count(visible ?? Array.Empty<int>());
        var result = new Dictionary<int, int>();

        for (var t = 0; t < Tiles.KindCount; t++)
        {
            var left = 4 - own[t] - seen[t];
            if (left <= 0) continue;
            var next = new List<int>(hand.Count + 1);
            next.AddRange(hand);
            next.Add(t);
            if (Shanten(next, openMelds) < baseline) result[t] = left;
        }
        return result;
    }

    internal static UInt128 PackCounts(int[] counts)
    {
        UInt128 value = 0;
        for (var i = 0; i < Tiles.KindCount; i++)
        {
            value |= (UInt128)(uint)counts[i] << (i * 3);
        }
        return value;
    }
}
