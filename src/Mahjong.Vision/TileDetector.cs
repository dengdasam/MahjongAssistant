using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Mahjong.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Mahjong.Vision;

/// <summary>
/// YOLOv8-style 42-class tile detector. Pre/post-processing mirrors the original
/// <c>yolo_screen_detect.detect</c>: letterbox to the model input with 114 padding,
/// BGR→RGB, /255, global NMS, coordinates mapped back to the screenshot.
/// </summary>
public sealed class TileDetector : IDisposable
{
    public const int ClassCount = 42;
    private const float Pad = 114f / 255f;

    private readonly InferenceSession _session;
    private readonly string[] _classNames;
    private readonly string _inputName;

    public TileDetector(string modelPath, string classesPath)
    {
        _classNames = File.ReadAllLines(classesPath)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToArray();
        if (_classNames.Length != ClassCount)
            throw new InvalidOperationException($"expected {ClassCount} class labels, got {_classNames.Length}");

        var options = new SessionOptions();
        _session = new InferenceSession(modelPath, options);

        var input = _session.InputMetadata.First();
        _inputName = input.Key;
        var dims = input.Value.Dimensions;
        InputHeight = dims.Length > 2 && dims[2] > 0 ? dims[2] : 640;
        InputWidth = dims.Length > 3 && dims[3] > 0 ? dims[3] : 640;
    }

    /// <summary>Model input width (the ONNX graph is fixed, usually 640).</summary>
    public int InputWidth { get; }

    /// <summary>Model input height (the ONNX graph is fixed, usually 640).</summary>
    public int InputHeight { get; }

    /// <summary>
    /// Scale the frame would get when letterboxed whole. Cropping a region of
    /// interest raises this, which is the only way to gain tile pixels without
    /// re-exporting the model with dynamic axes.
    /// </summary>
    public float ScaleFor(int width, int height) =>
        Math.Min((float)InputWidth / Math.Max(1, width), (float)InputHeight / Math.Max(1, height));

    public IReadOnlyList<TileDetection> Detect(Bitmap image, float confidence = 0.25f, float iou = 0.45f) =>
        Detect(image, confidence, iou, null);

    /// <summary>
    /// Detect inside <paramref name="roi"/> (frame coordinates). Returned boxes are
    /// always in full-frame coordinates, so callers see no difference.
    /// </summary>
    public IReadOnlyList<TileDetection> Detect(Bitmap image, float confidence, float iou, Rectangle? roi)
    {
        Bitmap? crop = null;
        var source = image;
        var offsetX = 0;
        var offsetY = 0;

        if (roi is { } region)
        {
            region = Rectangle.Intersect(region, new Rectangle(0, 0, image.Width, image.Height));
            if (region.Width >= 16 && region.Height >= 16 &&
                (region.Width < image.Width || region.Height < image.Height))
            {
                crop = image.Clone(region, image.PixelFormat);
                source = crop;
                offsetX = region.X;
                offsetY = region.Y;
            }
        }

        try
        {
            var (tensor, scale, dx, dy) = Preprocess(source);
            var inputs = new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) };
            using var results = _session.Run(inputs);
            var output = results[0].AsTensor<float>();

            var dims = output.Dimensions;
            var channels = dims[1];
            var count = dims[2];
            var transposed = channels > count; // [1, N, 4+nc] instead of [1, 4+nc, N]
            if (transposed) (channels, count) = (count, channels);

            var classCount = channels - 4;
            var candidates = new List<Candidate>();
            for (var i = 0; i < count; i++)
            {
                var best = 0f;
                var bestClass = -1;
                for (var c = 0; c < classCount; c++)
                {
                    var value = transposed ? output[0, i, 4 + c] : output[0, 4 + c, i];
                    if (value > best) { best = value; bestClass = c; }
                }
                if (best <= confidence) continue;

                var cx = transposed ? output[0, i, 0] : output[0, 0, i];
                var cy = transposed ? output[0, i, 1] : output[0, 1, i];
                var bw = transposed ? output[0, i, 2] : output[0, 2, i];
                var bh = transposed ? output[0, i, 3] : output[0, 3, i];
                candidates.Add(new Candidate(best, bestClass, cx, cy, bw, bh));
            }

            var detections = new List<TileDetection>();
            foreach (var candidate in NonMaximumSuppression(candidates, iou))
            {
                var x1 = (candidate.Cx - candidate.Bw / 2f - dx) / scale + offsetX;
                var y1 = (candidate.Cy - candidate.Bh / 2f - dy) / scale + offsetY;
                var x2 = Math.Min(image.Width, (candidate.Cx + candidate.Bw / 2f - dx) / scale + offsetX);
                var y2 = Math.Min(image.Height, (candidate.Cy + candidate.Bh / 2f - dy) / scale + offsetY);
                if (x2 <= x1 || y2 <= y1) continue;

                var label = _classNames[candidate.Class];
                var tileId = Tiles.FromYoloLabel(label);
                if (tileId < 0) continue; // flowers are removed from the hand by the original too

                detections.Add(new TileDetection(tileId, label, candidate.Confidence, x1, y1, x2 - x1, y2 - y1));
            }

            return detections.OrderBy(d => d.Y).ThenBy(d => d.X).ToList();
        }
        finally
        {
            crop?.Dispose();
        }
    }

    private (DenseTensor<float> Tensor, float Scale, int Dx, int Dy) Preprocess(Bitmap image)
    {
        var width = image.Width;
        var height = image.Height;
        var scale = Math.Min((float)InputHeight / height, (float)InputWidth / width);
        var newWidth = (int)Math.Round(width * scale);
        var newHeight = (int)Math.Round(height * scale);
        var dx = (InputWidth - newWidth) / 2;
        var dy = (InputHeight - newHeight) / 2;

        var tensor = new DenseTensor<float>(new[] { 1, 3, InputHeight, InputWidth });
        tensor.Buffer.Span.Fill(Pad);

        using var resized = new Bitmap(newWidth, newHeight, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(resized))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(image, 0, 0, newWidth, newHeight);
        }

        var data = resized.LockBits(new Rectangle(0, 0, newWidth, newHeight), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var stride = Math.Abs(data.Stride);
            var buffer = new byte[stride * newHeight];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            var span = tensor.Buffer.Span;
            var plane = InputHeight * InputWidth;

            for (var y = 0; y < newHeight; y++)
            {
                var row = y * stride;
                var targetRow = (dy + y) * InputWidth + dx;
                for (var x = 0; x < newWidth; x++)
                {
                    var source = row + x * 3;
                    var target = targetRow + x;
                    span[target] = buffer[source + 2] / 255f;             // R
                    span[plane + target] = buffer[source + 1] / 255f;     // G
                    span[plane * 2 + target] = buffer[source] / 255f;     // B
                }
            }
        }
        finally
        {
            resized.UnlockBits(data);
        }

        return (tensor, scale, dx, dy);
    }

    private static IEnumerable<Candidate> NonMaximumSuppression(List<Candidate> candidates, float iouThreshold)
    {
        var remaining = candidates.OrderByDescending(c => c.Confidence).ToList();
        while (remaining.Count > 0)
        {
            var best = remaining[0];
            yield return best;
            remaining.RemoveAt(0);
            remaining.RemoveAll(other => IntersectionOverUnion(best, other) > iouThreshold);
        }
    }

    private static float IntersectionOverUnion(Candidate a, Candidate b)
    {
        var ax1 = a.Cx - a.Bw / 2f; var ay1 = a.Cy - a.Bh / 2f;
        var ax2 = a.Cx + a.Bw / 2f; var ay2 = a.Cy + a.Bh / 2f;
        var bx1 = b.Cx - b.Bw / 2f; var by1 = b.Cy - b.Bh / 2f;
        var bx2 = b.Cx + b.Bw / 2f; var by2 = b.Cy + b.Bh / 2f;

        var interWidth = Math.Max(0f, Math.Min(ax2, bx2) - Math.Max(ax1, bx1));
        var interHeight = Math.Max(0f, Math.Min(ay2, by2) - Math.Max(ay1, by1));
        var intersection = interWidth * interHeight;
        if (intersection <= 0) return 0f;

        var union = a.Bw * a.Bh + b.Bw * b.Bh - intersection;
        return union <= 0 ? 0f : intersection / union;
    }

    public void Dispose() => _session.Dispose();

    private sealed record Candidate(float Confidence, int Class, float Cx, float Cy, float Bw, float Bh);
}
