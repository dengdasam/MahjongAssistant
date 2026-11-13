using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using Mahjong.Core;
using Mahjong.Vision;

namespace Mahjong.Smoke;

/// <summary>
/// Headless check that the ONNX path works end to end: load the model, run a
/// synthetic frame through it, and report shape / timing / detections.
/// Usage: Mahjong.Smoke [assetsDir]
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] is "windows" or "win")
        {
            ListWindows(args.Skip(1).ToArray());
            return 0;
        }

        if (args.Length > 0 && args[0] is "capture" or "cap")
        {
            return Capture(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0] is "bench" or "frame")
        {
            return Bench(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0] is "scan")
        {
            return Scan(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && args[0] is "static" or "idle")
        {
            return Static(args.Skip(1).ToArray());
        }

        var assets = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "assets");

        var model = Path.Combine(assets, "weights.onnx");
        var classes = Path.Combine(assets, "class_names.txt");

        Console.WriteLine($"assets : {assets}");
        if (!File.Exists(model) || !File.Exists(classes))
        {
            Console.WriteLine("missing weights.onnx or class_names.txt");
            return 2;
        }

        Console.WriteLine($"model  : {new FileInfo(model).Length / 1024 / 1024.0:F1} MB");

        var loadWatch = Stopwatch.StartNew();
        TileDetector detector;
        try
        {
            detector = new TileDetector(model, classes);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"load failed: {ex.GetType().Name}: {ex.Message}");
            return 3;
        }
        loadWatch.Stop();
        Console.WriteLine($"loaded : {loadWatch.ElapsedMilliseconds} ms, {TileDetector.ClassCount} classes");

        using (detector)
        using (var frame = SyntheticFrame(1280, 720))
        {
            frame.Save(Path.Combine(Path.GetTempPath(), "mahjong_smoke_frame.png"), ImageFormat.Png);
            Console.WriteLine($"frame  : {frame.Width}x{frame.Height} (written to %TEMP%\\mahjong_smoke_frame.png)");

            var runs = new List<long>();
            IReadOnlyList<TileDetection> detections = Array.Empty<TileDetection>();
            for (var i = 0; i < 3; i++)
            {
                var watch = Stopwatch.StartNew();
                detections = detector.Detect(frame, 0.25f, 0.45f);
                watch.Stop();
                runs.Add(watch.ElapsedMilliseconds);
            }

            Console.WriteLine($"infer  : {string.Join(" / ", runs)} ms, avg {runs.Average():F0} ms");
            Console.WriteLine($"detect : {detections.Count} boxes");
            foreach (var d in detections.Take(16))
            {
                var name = d.TileId >= 0 ? Tiles.Names[d.TileId] : "?";
                Console.WriteLine(
                    $"  {d.Label,-3} -> {name,-4} conf {d.Confidence:F3}  box {d.X,5:F0},{d.Y,5:F0}  {d.Width,4:F0}x{d.Height,4:F0}");
            }

            CheckRoiEquivalence(detector, frame, detections);
        }

        Console.WriteLine("smoke ok");
        return 0;
    }

    /// <summary>
    /// Grabs the matched table window, saves the raw frame plus an annotated copy,
    /// and reports every detector pass. Usage: Mahjong.Smoke capture [outDir] [keywords...]
    /// </summary>
    private static int Capture(string[] args)
    {
        var outDir = args.Length > 0 && !args[0].Contains('麻') ? args[0] : Directory.GetCurrentDirectory();
        Directory.CreateDirectory(outDir);

        var keywords = args.Skip(1).ToArray();
        var found = NativeWindow.FindGameWindow(keywords.Length > 0 ? keywords : DefaultKeywords);
        if (found is null)
        {
            Console.WriteLine("no matching window — open the table first (run `windows` to see candidates)");
            return 4;
        }

        Console.WriteLine($"window : {found.Title} ({found.ClientWidth}x{found.ClientHeight}) hwnd=0x{found.Handle.ToInt64():X}");

        using var bitmap = ScreenCapture.CaptureClientArea(found.Handle);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var rawPath = Path.Combine(outDir, $"frame_{stamp}.png");
        bitmap.Save(rawPath, ImageFormat.Png);
        Console.WriteLine($"saved  : {rawPath}");

        using var detector = new TileDetector(AssetsPath("weights.onnx"), AssetsPath("class_names.txt"));
        PrintAnalysis(bitmap, detector);

        return 0;
    }

    /// <summary>Runs the full pass ladder on a saved PNG. Usage: Mahjong.Smoke bench frame.png</summary>
    private static int Bench(string[] args)
    {
        if (args.Length == 0 || !File.Exists(args[0]))
        {
            Console.WriteLine("usage: Mahjong.Smoke bench <frame.png>");
            return 4;
        }

        using var bitmap = new Bitmap(args[0]);
        using var detector = new TileDetector(AssetsPath("weights.onnx"), AssetsPath("class_names.txt"));
        PrintAnalysis(bitmap, detector);
        return 0;
    }

    /// <summary>
    /// Proves the ROI code path is pure plumbing: detecting inside a region must give
    /// exactly the same boxes as detecting on an already-cropped bitmap, just shifted.
    /// </summary>
    private static void CheckRoiEquivalence(TileDetector detector, Bitmap frame, IReadOnlyList<TileDetection> detections)
    {
        if (detections.Count == 0)
        {
            Console.WriteLine("roi    : skipped (no tiles to frame a region around)");
            return;
        }

        var pad = 24;
        var left = Math.Max(0, (int)detections.Min(d => d.X) - pad);
        var top = Math.Max(0, (int)detections.Min(d => d.Y) - pad);
        var right = Math.Min(frame.Width, (int)detections.Max(d => d.X + d.Width) + pad);
        var bottom = Math.Min(frame.Height, (int)detections.Max(d => d.Y + d.Height) + pad);
        var roi = Rectangle.FromLTRB(left, top, right, bottom);

        using var cropped = frame.Clone(roi, frame.PixelFormat);

        var inside = detector.Detect(frame, 0.25f, 0.45f, roi);
        var shifted = detector.Detect(cropped, 0.25f, 0.45f);

        var same = inside.Count == shifted.Count;
        if (same)
        {
            for (var i = 0; i < inside.Count; i++)
            {
                var a = inside[i];
                var b = shifted[i];
                if (a.TileId != b.TileId ||
                    Math.Abs(a.Confidence - b.Confidence) > 1e-4f ||
                    Math.Abs(a.X - (b.X + roi.X)) > 0.51f ||
                    Math.Abs(a.Y - (b.Y + roi.Y)) > 0.51f)
                {
                    same = false;
                    break;
                }
            }
        }

        Console.WriteLine(same
            ? $"roi    : equivalence ok ({inside.Count} boxes, offset {roi.X},{roi.Y} applied)"
            : $"roi    : MISMATCH (roi {inside.Count} vs crop {shifted.Count})");
    }

    /// <summary>
    /// Sweeps the effective scale and reports how many tiles each one finds.
    /// The model graph is fixed at 640, so the only lever is how much of the frame
    /// gets cropped. Answers "what tile pixel size does this model actually like?"
    /// with numbers instead of guesses. Usage: Mahjong.Smoke scan frame.png
    /// </summary>
    private static int Scan(string[] args)
    {
        if (args.Length == 0 || !File.Exists(args[0]))
        {
            Console.WriteLine("usage: Mahjong.Smoke scan <frame.png>");
            return 4;
        }

        using var frame = new Bitmap(args[0]);
        using var detector = new TileDetector(AssetsPath("weights.onnx"), AssetsPath("class_names.txt"));

        var probe = detector.Detect(frame, 0.12f, 0.45f);
        if (probe.Count == 0)
        {
            Console.WriteLine("no tiles found at low confidence — nothing to scan");
            return 5;
        }

        var centreX = (int)probe.Average(d => d.X + d.Width / 2f);
        var centreY = (int)probe.Average(d => d.Y + d.Height / 2f);
        var tileWidth = probe.Select(d => d.Width).OrderBy(w => w).ElementAt(probe.Count / 2);
        Console.WriteLine(
            $"frame {frame.Width}x{frame.Height}, {probe.Count} tiles, median tile {tileWidth:0}px, row centre {centreX},{centreY}");

        var input = detector.InputWidth;
        foreach (var scale in new[] { 0.35f, 0.45f, 0.55f, 0.65f, 0.75f, 0.9f, 1.1f, 1.35f })
        {
            var side = (int)Math.Round(input / scale);
            var cropWidth = Math.Min(frame.Width, side);
            var cropHeight = Math.Min(frame.Height, side);
            if (cropWidth < frame.Width / 6 || cropHeight < frame.Height / 8) continue;

            var left = Math.Clamp(centreX - cropWidth / 2, 0, frame.Width - cropWidth);
            var top = Math.Clamp(centreY - cropHeight / 2, 0, frame.Height - cropHeight);
            var roi = new Rectangle(left, top, cropWidth, cropHeight);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var hits = detector.Detect(frame, 0.25f, 0.45f, roi);
            watch.Stop();

            var actual = detector.ScaleFor(roi.Width, roi.Height);
            Console.WriteLine(
                $"  scale {actual:0.000}  roi {roi.Width,4}x{roi.Height,-4}  tile~{tileWidth * actual,5:0.0}px  -> {hits.Count,2} tiles  {watch.ElapsedMilliseconds,4} ms");
        }

        return 0;
    }

    /// <summary>
    /// Measures the idle-frame gate. Shows what one change check costs versus one
    /// inference pass, i.e. how much work a static screen now skips.
    /// Usage: Mahjong.Smoke static [frame.png]
    /// </summary>
    private static int Static(string[] args)
    {
        Bitmap frame;
        if (args.Length > 0 && File.Exists(args[0]))
        {
            frame = new Bitmap(args[0]);
        }
        else
        {
            frame = new Bitmap(1280, 720);
            using var g = Graphics.FromImage(frame);
            g.Clear(Color.FromArgb(18, 92, 60));
        }

        using (frame)
        using (var detector = new TileDetector(AssetsPath("weights.onnx"), AssetsPath("class_names.txt")))
        {
            var region = new Rectangle(0, (int)(frame.Height * 0.70), frame.Width, (int)(frame.Height * 0.30));
            var gate = new FrameChangeDetector(maxSkip: 20);

            // Warm up, then time 50 identical frames.
            gate.HasChanged(frame, region);
            var watch = Stopwatch.StartNew();
            var changes = 0;
            for (var i = 0; i < 50; i++)
                if (gate.HasChanged(frame, region)) changes++;
            watch.Stop();
            var perCheck = watch.Elapsed.TotalMilliseconds / 50;

            var inferWatch = Stopwatch.StartNew();
            var hits = detector.Detect(frame, 0.25f, 0.45f);
            inferWatch.Stop();

            Console.WriteLine($"frame     : {frame.Width}x{frame.Height}, gate region {region.Width}x{region.Height}");
            Console.WriteLine($"gate      : {perCheck:F2} ms / frame");
            Console.WriteLine($"inference : {inferWatch.ElapsedMilliseconds} ms ({hits.Count} tiles)");

            // Every (maxSkip + 1) frames the gate deliberately forces one refresh.
            var expected = 50 / (gate.MaxSkip + 1);
            Console.WriteLine($"skipped   : {50 - changes}/50 frames (expected {50 - expected}, the rest are forced refreshes)");
            Console.WriteLine($"saving    : ~{(50 - changes) * inferWatch.ElapsedMilliseconds / 50.0:F0} ms average per frame -> " +
                              $"~{(50 - changes) * 100 / 50}% of inference work removed while idle");
        }

        return 0;
    }

    private static void PrintAnalysis(Bitmap bitmap, TileDetector detector)
    {        Console.WriteLine($"frame  : {bitmap.Width}x{bitmap.Height}, base scale {detector.ScaleFor(bitmap.Width, bitmap.Height):0.000}");

        var analysis = FrameAnalyzer.Analyze(bitmap, detector);
        Console.WriteLine($"total  : {analysis.ElapsedMs} ms in {analysis.Attempts.Count} pass(es), winner = {analysis.Strategy}");
        foreach (var attempt in analysis.Attempts)
            Console.WriteLine("  " + attempt.Describe());

        Console.WriteLine(analysis.Hand.Count > 0
            ? "hand   : " + string.Join(" ", analysis.Hand.Select(d => Tiles.Names[d.TileId]))
            : "hand   : (none)");

        var annotated = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath("frame.png"))!,
            $"annotated_{DateTime.Now:yyyyMMdd-HHmmss}.png");
        SaveAnnotated(bitmap, analysis, annotated);
        Console.WriteLine($"markup : {annotated}");
    }

    private static void SaveAnnotated(Bitmap bitmap, FrameAnalysis analysis, string path)
    {
        using var canvas = new Bitmap(bitmap);
        using var graphics = Graphics.FromImage(canvas);
        using var chosen = new Pen(Color.FromArgb(255, 210, 60), 2.2f);
        using var other = new Pen(Color.FromArgb(120, 200, 255), 1.2f);
        using var font = new Font("Consolas", 11f, FontStyle.Bold);

        var hand = new HashSet<TileDetection>(analysis.Hand);
        foreach (var detection in analysis.All)
        {
            var pen = hand.Contains(detection) ? chosen : other;
            graphics.DrawRectangle(pen, detection.X, detection.Y, detection.Width, detection.Height);
            graphics.DrawString(
                $"{detection.Label} {detection.Confidence:0.00}",
                font,
                Brushes.White,
                detection.X,
                Math.Max(0, detection.Y - 15));
        }

        canvas.Save(path, ImageFormat.Png);
    }

    private static string AssetsPath(string file)
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "assets", file),
            Path.Combine(AppContext.BaseDirectory, file),
        };

        // Walk up to the repo root so `bench` also works straight from bin/.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            candidates.Add(Path.Combine(dir.FullName, "src", "Mahjong.Desktop", "assets", file));
            candidates.Add(Path.Combine(dir.FullName, "MahjongAssistant.CSharp", "src", "Mahjong.Desktop", "assets", file));
        }

        return candidates.FirstOrDefault(File.Exists) ?? Path.Combine(AppContext.BaseDirectory, file);
    }

    private static readonly string[] DefaultKeywords =
        { "微乐", "麻将", "微信", "wechat", "mahjong", "mumu", "模拟器", "夜神", "雷电" };

    /// <summary>Lists visible windows and reports which one auto mode would pick.</summary>
    private static void ListWindows(string[] keywords)
    {
        var terms = keywords.Length > 0 ? keywords : DefaultKeywords;

        var windows = NativeWindow.ListVisibleWindows();
        Console.WriteLine($"visible windows: {windows.Count}, keywords: {string.Join(",", terms)}");
        foreach (var w in windows)
        {
            var title = w.Title.ToLowerInvariant();
            var process = w.ProcessName.ToLowerInvariant();
            var hit = terms.Any(t => title.Contains(t.ToLowerInvariant()) || process.Contains(t.ToLowerInvariant()));
            Console.WriteLine($"  {(hit ? "*" : " ")} {w.ClientWidth,5}x{w.ClientHeight,-5} {w.ProcessName,-22} {w.Title}");
        }

        var picked = NativeWindow.FindGameWindow(terms);
        Console.WriteLine(picked is null
            ? "auto pick: none"
            : $"auto pick: {picked.Title} ({picked.ClientWidth}x{picked.ClientHeight})");
    }

    /// <summary>Draws a hand-like row of 14 pale tiles so the graph gets real pixels.</summary>
    private static Bitmap SyntheticFrame(int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(18, 92, 60));

        const int tileWidth = 52;
        const int tileHeight = 76;
        const int gap = 6;
        var totalWidth = 14 * tileWidth + 13 * gap;
        var startX = (width - totalWidth) / 2;
        var y = height - tileHeight - 40;

        for (var i = 0; i < 14; i++)
        {
            var x = startX + i * (tileWidth + gap);
            using var face = new SolidBrush(i % 3 == 0 ? Color.FromArgb(250, 246, 232) : Color.FromArgb(240, 236, 222));
            graphics.FillRectangle(face, x, y, tileWidth, tileHeight);
            using var border = new Pen(Color.FromArgb(120, 120, 110), 1.5f);
            graphics.DrawRectangle(border, x, y, tileWidth, tileHeight);
            using var mark = new SolidBrush(i % 2 == 0 ? Color.FromArgb(180, 40, 40) : Color.FromArgb(30, 60, 140));
            graphics.FillRectangle(mark, x + tileWidth / 2 - 10, y + tileHeight / 2 - 12, 20, 24);
        }

        return bitmap;
    }
}
