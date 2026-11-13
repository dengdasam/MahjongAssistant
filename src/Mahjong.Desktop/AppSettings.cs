using System.IO;
using System.Text.Json;

namespace Mahjong.Desktop;

public sealed record AppSettings
{
    public string ModelPath { get; init; } = string.Empty;
    public string ClassesPath { get; init; } = string.Empty;
    public double Confidence { get; init; } = 0.25;
    public double Iou { get; init; } = 0.45;
    public int IntervalMs { get; init; } = 1000;

    /// <summary>Second-chance threshold, only used when a frame yields too few tiles.</summary>
    public double LowConfidence { get; init; } = 0.12;

    /// <summary>Crop the hand band and re-run the detector so tiles get more pixels.</summary>
    public bool AdaptiveRefine { get; init; } = true;

    /// <summary>Retry a frame with <see cref="LowConfidence"/> before giving up on it.</summary>
    public bool DeepRetry { get; init; } = true;

    /// <summary>Frames a changed hand must repeat before it replaces the shown one (1 = off).</summary>
    public int HandStableFrames { get; init; } = 2;

    /// <summary>
    /// Tile width in model-input pixels that the network detects best (measured ≈22).
    /// Frames with smaller tiles get cropped to match; bigger ones are left alone.
    /// </summary>
    public double TargetTilePixels { get; init; } = 22;

    /// <summary>Start reading as soon as a game window is found.</summary>
    public bool AutoStart { get; init; } = true;

    /// <summary>Title / process keywords used to auto-pick the table window.</summary>
    public string[] WindowKeywords { get; init; } =
    {
        "微乐", "麻将", "微信", "wechat", "mahjong", "mumu", "模拟器", "夜神", "雷电"
    };

    /// <summary>How often to look for the window again while auto mode is idle.</summary>
    public int AutoRescanMs { get; init; } = 3000;

    /// <summary>Delay before the first auto attach, giving the UI time to appear.</summary>
    public int AutoStartDelayMs { get; init; } = 700;

    /// <summary>
    /// Skip inference while the hand area is pixel-identical to the last frame.
    /// Saves the whole 250–800 ms pass when nothing on the table moved.
    /// </summary>
    public bool StaticSkip { get; init; } = true;

    /// <summary>Consecutive skips before one inference is forced anyway.</summary>
    public int StaticSkipMax { get; init; } = 20;

    /// <summary>Follow the table window when it is moved or resized.</summary>
    public bool FollowWindow { get; init; } = true;

    /// <summary>Diagnostic log path; relative paths resolve next to the exe.</summary>
    public string LogPath { get; init; } = "logs/mahjong-assistant.log";

    public static AppSettings Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var settings = new AppSettings();
        if (File.Exists(path))
        {
            try
            {
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AppSettings();
            }
            catch
            {
                settings = new AppSettings();
            }
        }

        return settings with
        {
            ModelPath = Resolve(settings.ModelPath, "assets/weights.onnx"),
            ClassesPath = Resolve(settings.ClassesPath, "assets/class_names.txt"),
            WindowKeywords = settings.WindowKeywords is { Length: > 0 }
                ? settings.WindowKeywords
                : new AppSettings().WindowKeywords,
            IntervalMs = Math.Clamp(settings.IntervalMs, 200, 10_000),
            AutoRescanMs = Math.Clamp(settings.AutoRescanMs, 500, 60_000),
            HandStableFrames = Math.Clamp(settings.HandStableFrames, 1, 10),
            StaticSkipMax = Math.Clamp(settings.StaticSkipMax, 1, 200),
            TargetTilePixels = Math.Clamp(settings.TargetTilePixels, 8, 120),
            Confidence = Math.Clamp(settings.Confidence, 0.02, 0.95),
            LowConfidence = Math.Clamp(settings.LowConfidence, 0.02, 0.95),
            Iou = Math.Clamp(settings.Iou, 0.05, 0.95),
        };

    }

    /// <summary>Detector knobs derived from the JSON so Vision stays settings-agnostic.</summary>
    public Mahjong.Vision.DetectorOptions ToDetectorOptions() => new()
    {
        Confidence = (float)Confidence,
        Iou = (float)Iou,
        LowConfidence = (float)LowConfidence,
        AdaptiveRefine = AdaptiveRefine,
        DeepRetry = DeepRetry,
        TargetTilePixels = (float)TargetTilePixels,
    };

    /// <summary>Relative paths are resolved against the executable directory.</summary>
    private static string Resolve(string value, string fallback)
    {
        var target = string.IsNullOrWhiteSpace(value) ? fallback : value;
        return Path.IsPathRooted(target) ? target : Path.Combine(AppContext.BaseDirectory, target);
    }
}
