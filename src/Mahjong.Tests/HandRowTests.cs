using System.Globalization;
using System.Text.Json;
using Mahjong.Core;
using Mahjong.Vision;
using Xunit;

namespace Mahjong.Tests;

/// <summary>
/// Diffs the C# port of <c>choose_hand</c> against vectors produced by running
/// the original .pyc code object, so the hand-row heuristic is verified rather
/// than re-invented.
/// </summary>
public class HandRowTests
{
    private static readonly string VectorPath =
        Path.Combine(AppContext.BaseDirectory, "hand_row_vectors.json");

    public static TheoryData<int> CaseIndexes()
    {
        var data = new TheoryData<int>();
        var count = JsonDocument.Parse(File.ReadAllText(VectorPath))
            .RootElement.GetProperty("cases").GetArrayLength();
        for (var i = 0; i < count; i++) data.Add(i);
        return data;
    }

    [Theory]
    [MemberData(nameof(CaseIndexes))]
    public void MatchesOriginalChooseHand(int index)
    {
        var root = JsonDocument.Parse(File.ReadAllText(VectorPath)).RootElement;
        var testCase = root.GetProperty("cases")[index];

        var width = testCase.GetProperty("width").GetInt32();
        var height = testCase.GetProperty("height").GetInt32();

        var detections = testCase.GetProperty("detections").EnumerateArray()
            .Select(d =>
            {
                var box = d.GetProperty("bbox");
                return new TileDetection(
                    0,
                    string.Empty,
                    (float)d.GetProperty("confidence").GetDouble(),
                    (float)box[0].GetDouble(),
                    (float)box[1].GetDouble(),
                    (float)box[2].GetDouble(),
                    (float)box[3].GetDouble());
            })
            .ToList();

        var selection = HandRowSelector.Select(detections, width, height);
        var expected = testCase.GetProperty("expected");

        Assert.Equal(expected.GetProperty("edge").GetString(), selection.Edge);
        Assert.Equal(Describe(expected.GetProperty("row")), Describe(selection.Row));

        var counts = expected.GetProperty("counts");
        Assert.Equal(counts.GetProperty("下").GetInt32(), selection.Counts.Bottom);
        Assert.Equal(counts.GetProperty("上").GetInt32(), selection.Counts.Top);
        Assert.Equal(counts.GetProperty("左").GetInt32(), selection.Counts.Left);
        Assert.Equal(counts.GetProperty("右").GetInt32(), selection.Counts.Right);
    }

    private static string Describe(JsonElement boxes) =>
        string.Join("|", boxes.EnumerateArray().Select(b =>
            string.Join(",", b.EnumerateArray().Select(v => v.GetDouble().ToString("F3", CultureInfo.InvariantCulture)))));

    private static string Describe(IReadOnlyList<TileDetection> detections) =>
        string.Join("|", detections.Select(d => string.Join(",",
            new[] { d.X, d.Y, d.Width, d.Height }.Select(v => v.ToString("F3", CultureInfo.InvariantCulture)))));
}
