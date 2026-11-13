using System.Text.Json;
using Mahjong.Core;
using Xunit;

namespace Mahjong.Tests;

/// <summary>
/// Acceptance tests: every expectation below was produced by running the
/// original (unmodified) Python module extracted from the shipped exe.
/// A green test run therefore means the C# port is behaviourally equivalent.
/// </summary>
public sealed class GoldenVectorTests
{
    private static readonly JsonDocument Doc = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden_vectors.json")));

    private static readonly JsonElement[] Cases = Doc.RootElement.GetProperty("cases")
        .EnumerateArray().ToArray();

    public static IEnumerable<object[]> CaseIds => Cases.Select(c => new object[] { c.GetProperty("id").GetString()! });

    private static JsonElement Case(string id) =>
        Cases.First(c => c.GetProperty("id").GetString() == id);

    private static List<int> Ints(JsonElement array) =>
        array.EnumerateArray().Select(x => x.GetInt32()).ToList();

    private static string JoinTiles(IEnumerable<TileCount> items) =>
        string.Join(";", items.Select(i => $"{i.Tile}:{i.Remaining}"));

    private static string JoinTiles(JsonElement array) =>
        string.Join(";", array.EnumerateArray()
            .Select(i => $"{i.GetProperty("tile").GetString()}:{i.GetProperty("remaining").GetInt32()}"));

    [Fact]
    public void TileNamesMatchOriginal()
    {
        var expected = Doc.RootElement.GetProperty("tile_names")
            .EnumerateArray().Select(x => x.GetString()!).ToArray();

        Assert.Equal(expected, Tiles.Names);
    }

    [Theory]
    [InlineData("1B", 18)]
    [InlineData("1C", 0)]
    [InlineData("1D", 9)]
    [InlineData("9B", 26)]
    [InlineData("EW", 27)]
    [InlineData("SW", 28)]
    [InlineData("WW", 29)]
    [InlineData("NW", 30)]
    [InlineData("RD", 31)]
    [InlineData("GD", 32)]
    [InlineData("WD", 33)]
    [InlineData("1F", -1)]
    [InlineData("4S", -1)]
    public void YoloLabelsMapToTiles(string label, int expected)
    {
        Assert.Equal(expected, Tiles.FromYoloLabel(label));
    }

    [Theory]
    [MemberData(nameof(CaseIds))]
    public void MatchesOriginalImplementation(string id)
    {
        var testCase = Case(id);
        var hand = Ints(testCase.GetProperty("hand"));
        var visible = Ints(testCase.GetProperty("visible"));
        var openMelds = testCase.GetProperty("open_melds").GetInt32();
        var expected = testCase.GetProperty("expected");

        Assert.Equal(expected.GetProperty("shanten").GetInt32(), HandAnalyzer.Shanten(hand, openMelds));
        Assert.Equal(expected.GetProperty("is_win").GetBoolean(), HandAnalyzer.IsWin(hand, openMelds));

        var logical = hand.Count + openMelds * 3;
        var thirteen = logical == 14 ? hand.Take(hand.Count - 1).ToList() : hand;

        Assert.Equal(
            JoinTiles(expected.GetProperty("waits")),
            JoinTiles(HandAnalyzer.Waits(thirteen, openMelds).Select(kv => new TileCount(Tiles.Names[kv.Key], kv.Value))));

        Assert.Equal(
            JoinTiles(expected.GetProperty("improving_tiles")),
            JoinTiles(HandAnalyzer.ImprovingTiles(thirteen, visible, openMelds)
                .Select(kv => new TileCount(Tiles.Names[kv.Key], kv.Value))));

        if (!expected.TryGetProperty("discard_analysis", out var expectedRows)) return;

        var rivers = testCase.GetProperty("rivers").EnumerateArray().Select(Ints).ToArray();
        var opponentMelds = testCase.GetProperty("opponent_melds").EnumerateArray()
            .Select(seat => (IReadOnlyList<IReadOnlyList<int>>)seat.EnumerateArray().Select(Ints).ToArray())
            .ToArray();

        var actual = DiscardAdvisor.Analyze(hand, visible, rivers, openMelds, opponentMelds);

        Assert.Equal(expectedRows.GetArrayLength(), actual.Count);

        var index = 0;
        foreach (var row in expectedRows.EnumerateArray())
        {
            var got = actual[index];
            Assert.Equal(row.GetProperty("discard").GetInt32(), got.Discard);
            Assert.Equal(row.GetProperty("tile").GetString(), got.Tile);
            Assert.Equal(row.GetProperty("shanten").GetInt32(), got.Shanten);
            Assert.Equal(JoinTiles(row.GetProperty("waits")), JoinTiles(got.Waits));
            Assert.Equal(JoinTiles(row.GetProperty("improving_tiles")), JoinTiles(got.ImprovingTiles));
            Assert.Equal(row.GetProperty("ukeire").GetInt32(), got.Ukeire);
            Assert.Equal(row.GetProperty("risk_summary").GetString(), got.RiskSummary);

            var expectedRisk = row.GetProperty("opponent_risk").EnumerateArray().ToArray();
            Assert.Equal(expectedRisk.Length, got.OpponentRisk.Count);
            for (var i = 0; i < expectedRisk.Length; i++)
            {
                var e = expectedRisk[i];
                var a = got.OpponentRisk[i];
                Assert.Equal(e.GetProperty("seat").GetInt32(), a.Seat);
                Assert.Equal(e.GetProperty("level").GetString(), a.Level);
                Assert.Equal(
                    e.GetProperty("evidence").EnumerateArray().Select(x => x.GetString()),
                    a.Evidence);
                Assert.True(
                    Math.Abs(e.GetProperty("score_internal").GetDouble() - a.ScoreInternal) < 0.011,
                    $"{id}/{got.Tile}/seat{a.Seat}: expected {e.GetProperty("score_internal").GetDouble()}, got {a.ScoreInternal}");
            }
            index++;
        }
    }

    [Fact]
    public void SnapshotAnalysisMatchesOriginal()
    {
        var snapshot = Doc.RootElement.GetProperty("snapshot_case");
        var input = snapshot.GetProperty("input");
        var hand = input.GetProperty("hand").EnumerateArray().Select(x => Tiles.IdOf(x.GetString()!)).ToList();
        var rivers = input.GetProperty("rivers").EnumerateArray()
            .Select(r => (IReadOnlyList<int>)r.EnumerateArray().Select(x => Tiles.IdOf(x.GetString()!)).ToArray())
            .ToArray();

        var expected = snapshot.GetProperty("expected");
        var expectedTop = expected.GetProperty("discard_recommendations").EnumerateArray()
            .Select(x => x.GetProperty("tile").GetString()!).ToArray();

        var visible = rivers.SelectMany(x => x).ToArray();
        var rows = DiscardAdvisor.Analyze(hand, visible, rivers);
        Assert.Equal(expectedTop, rows.Take(expectedTop.Length).Select(x => x.Tile));
    }
}
