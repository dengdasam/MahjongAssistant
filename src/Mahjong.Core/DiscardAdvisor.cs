namespace Mahjong.Core;

/// <summary>
/// Port of the original <c>discard_analysis</c> + <c>_risk_for_opponent</c>.
/// The opponent danger value is an ordinal heuristic with auditable evidence,
/// never a calibrated probability — same caveat as the original.
/// </summary>
public static class DiscardAdvisor
{
    private static readonly Dictionary<string, int> LevelScore = new()
    {
        ["低"] = 0,
        ["偏低"] = 1,
        ["中"] = 2,
        ["偏高"] = 3,
        ["高"] = 4,
    };

    public static IReadOnlyList<DiscardOption> Analyze(
        IReadOnlyList<int> hand,
        IReadOnlyList<int>? visible = null,
        IReadOnlyList<IReadOnlyList<int>>? opponentDiscards = null,
        int openMelds = 0,
        IReadOnlyList<IReadOnlyList<IReadOnlyList<int>>>? opponentMelds = null)
    {
        var logical = hand.Count + openMelds * 3;
        if (logical != 13 && logical != 14)
            throw new ArgumentException("concealed hand plus exposed melds must total 13 or 14 logical tiles", nameof(hand));

        visible ??= Array.Empty<int>();
        opponentDiscards ??= new IReadOnlyList<int>[] { Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>() };

        var vis = Tiles.Count(visible);
        var own = Tiles.Count(hand);
        var remaining = new int[Tiles.KindCount];
        for (var t = 0; t < Tiles.KindCount; t++)
        {
            remaining[t] = Math.Max(0, 4 - own[t] - vis[t]);
        }

        var rows = new List<DiscardOption>();
        foreach (var d in hand.Distinct().OrderBy(x => x))
        {
            var rest = new List<int>(hand);
            rest.Remove(d);

            var waits = logical == 14 ? HandAnalyzer.Waits(rest, openMelds) : new Dictionary<int, int>();
            var improving = HandAnalyzer.ImprovingTiles(logical == 14 ? rest : hand, visible, openMelds);
            var ukeire = improving.Values.Sum();

            var perOpponent = new List<OpponentRisk>();
            for (var i = 0; i < opponentDiscards.Count; i++)
            {
                var river = opponentDiscards[i];
                IReadOnlyList<IReadOnlyList<int>> melds =
                    opponentMelds is not null && i < opponentMelds.Count
                        ? opponentMelds[i]
                        : Array.Empty<IReadOnlyList<int>>();

                var risk = RiskForOpponent(d, river, melds, river.Count, vis);
                perOpponent.Add(new OpponentRisk(i + 1, risk.Level, risk.Evidence, risk.ScoreInternal));
            }

            var aggregate = perOpponent.Sum(o => LevelScore[o.Level]);
            var shanten = HandAnalyzer.Shanten(logical == 14 ? rest : hand, openMelds);
            var waitList = waits.Keys.Select(t => new TileCount(Tiles.Names[t], remaining[t])).ToList();
            var improvingList = improving.Select(kv => new TileCount(Tiles.Names[kv.Key], kv.Value)).ToList();
            var riskSummary = aggregate <= 2 ? "低" : aggregate <= 7 ? "中" : "高";

            rows.Add(new DiscardOption(d, Tiles.Names[d], shanten, waitList, improvingList, ukeire, riskSummary, perOpponent));
        }

        return rows
            .OrderBy(x => -x.Ukeire)
            .ThenBy(x => x.OpponentRisk.Sum(o => LevelScore[o.Level]))
            .ThenBy(x => x.Discard)
            .ToList();
    }

    /// <summary>Ordinal danger estimate with auditable evidence, never a probability.</summary>
    public static RiskResult RiskForOpponent(
        int tile,
        IReadOnlyList<int> river,
        IReadOnlyList<IReadOnlyList<int>> melds,
        int turnIndex,
        int[]? visible = null)
    {
        var played = Tiles.Count(river);
        var score = 0.0;
        var evidence = new List<string>();

        if (played[tile] > 0)
        {
            score -= 5.0;
            evidence.Add("该家已打过此牌：对该家有强安全证据");
        }

        if (tile < 27)
        {
            var suit = tile / 9;
            var rank = tile % 9 + 1;

            var suji = false;
            foreach (var x in river)
            {
                if (x < 27 && x / 9 == suit && Math.Abs(x % 9 + 1 - rank) == 3) { suji = true; break; }
            }
            if (suji)
            {
                score -= 0.7;
                evidence.Add("同花色筋线索：弱安全证据");
            }
            if (rank is 1 or 9)
            {
                score -= 0.25;
                evidence.Add("幺九牌通常连接度较低：弱结构线索");
            }
            if (rank is 3 or 7 && turnIndex >= 7)
            {
                score += 0.6;
                evidence.Add("三七牌后巡保留价值上升：本地口诀弱风险线索");
            }
        }

        if (melds.Count > 0)
        {
            score += 1.4;
            evidence.Add("该家有副露：手牌形状更集中");
        }

        score += Math.Min(Math.Max(turnIndex, 0), 12) * 0.12;

        if (turnIndex >= 7) evidence.Add("巡目偏后：整体风险上升");
        if (evidence.Count == 0) evidence.Add("缺少足够公开线索，按中性风险处理");

        var level = score <= -1.5 ? "低"
            : score < 0 ? "偏低"
            : score < 1.2 ? "中"
            : score < 2.4 ? "偏高"
            : "高";

        return new RiskResult(level, evidence, Math.Round(score, 2, MidpointRounding.ToEven));
    }
}
