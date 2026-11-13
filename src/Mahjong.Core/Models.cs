using System.Text.Json.Serialization;

namespace Mahjong.Core;

/// <summary>Shape mirrors the Python dicts so golden vectors can be compared field by field.</summary>
public sealed record TileCount(
    [property: JsonPropertyName("tile")] string Tile,
    [property: JsonPropertyName("remaining")] int Remaining);

public sealed record RiskResult(
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("evidence")] IReadOnlyList<string> Evidence,
    [property: JsonPropertyName("score_internal")] double ScoreInternal);

public sealed record OpponentRisk(
    [property: JsonPropertyName("seat")] int Seat,
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("evidence")] IReadOnlyList<string> Evidence,
    [property: JsonPropertyName("score_internal")] double ScoreInternal);

public sealed record DiscardOption(
    [property: JsonPropertyName("discard")] int Discard,
    [property: JsonPropertyName("tile")] string Tile,
    [property: JsonPropertyName("shanten")] int Shanten,
    [property: JsonPropertyName("waits")] IReadOnlyList<TileCount> Waits,
    [property: JsonPropertyName("improving_tiles")] IReadOnlyList<TileCount> ImprovingTiles,
    [property: JsonPropertyName("ukeire")] int Ukeire,
    [property: JsonPropertyName("risk_summary")] string RiskSummary,
    [property: JsonPropertyName("opponent_risk")] IReadOnlyList<OpponentRisk> OpponentRisk);

/// <summary>One YOLO detection mapped into the tile domain.</summary>
public sealed record TileDetection(int TileId, string Label, float Confidence, float X, float Y, float Width, float Height);
