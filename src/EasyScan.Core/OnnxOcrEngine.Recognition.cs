using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace EasyScan.Core;

public sealed partial class OnnxOcrEngine
{
    private sealed class TextLine(Mat crop, Point2f[] quad, int index) : IDisposable
    {
        public Mat Crop { get; } = crop;
        public Point2f[] Quad { get; set; } = quad;
        public int Index { get; } = index;
        public List<LineCharacter> Characters { get; } = [];
        public void Dispose() => Crop.Dispose();
    }
    private sealed record LineCharacter(string Text, double Score, double Start, double End);
    private sealed record Segment(TextLine Line, int Start, int End)
    {
        public int Width => Math.Max(320, (int)Math.Ceiling((End - Start) * 48d / Line.Crop.Height));
    }

    private List<OcrTextBlock> ReadLines(Mat image, List<Point2f[]> boxes, IProgress<OcrProgress>? progress, CancellationToken token)
    {
        var blocks = new List<OcrTextBlock>();
        // Bound both crop lifetime and padded recognition width; preserve detector reading order.
        for (var first = 0; first < boxes.Count; first += 4)
        {
            var lines = new List<TextLine>();
            try
            {
                for (var i = first; i < Math.Min(first + 4, boxes.Count); i++)
                {
                    token.ThrowIfCancellationRequested();
                    var quad = boxes[i].ToArray();
                    var width = Math.Max(2, (int)Math.Round(Math.Max(quad[0].DistanceTo(quad[1]), quad[3].DistanceTo(quad[2]))));
                    var height = Math.Max(2, (int)Math.Round(Math.Max(quad[0].DistanceTo(quad[3]), quad[1].DistanceTo(quad[2]))));
                    using var transform = Cv2.GetPerspectiveTransform(quad, Rectangle(width, height));
                    var crop = new Mat();
                    try
                    {
                        Cv2.WarpPerspective(image, crop, transform, new Size(width, height), InterpolationFlags.Cubic, BorderTypes.Replicate);
                        if (height > width * 1.5)
                        {
                            Cv2.Rotate(crop, crop, RotateFlags.Rotate90Counterclockwise);
                            quad = [quad[1], quad[2], quad[3], quad[0]];
                        }
                        lines.Add(new(crop, quad, i));
                    }
                    catch { crop.Dispose(); throw; }
                }
                var shape = _classifier!.InputMetadata.First().Value.Dimensions;
                var h = shape[2] > 0 ? shape[2] : 80; var w = shape[3] > 0 ? shape[3] : 160;
                // Very long lines must not be squeezed into a tiny classifier thumbnail.
                // Three local views vote; ambiguous windows cannot force an orientation.
                var votes = new double[lines.Count];
                for (var view = 0; view < 3; view++)
                {
                    var active = lines.Where(l => view == 0 || l.Crop.Width > l.Crop.Height * 32).ToArray();
                    if (active.Length == 0) break;
                    var windows = new List<Mat>();
                    try
                    {
                        foreach (var line in active)
                        {
                            var span = line.Crop.Width > line.Crop.Height * 32 ? line.Crop.Height * 4 : line.Crop.Width;
                            var left = (line.Crop.Width - span) * view / 2;
                            windows.Add(new(line.Crop, new Rect(left, 0, span, line.Crop.Height)));
                        }
                        var classification = BatchTensor(windows, h, w, out _);
                        RunOutput(_classifier, classification, token, prediction =>
                        {
                            for (var i = 0; i < active.Length; i++)
                                if (Math.Max(prediction[i, 0], prediction[i, 1]) > .9f)
                                    votes[lines.IndexOf(active[i])] += prediction[i, 1] - prediction[i, 0];
                            return 0;
                        });
                    }
                    finally { foreach (var window in windows) window.Dispose(); }
                }
                for (var i = 0; i < lines.Count; i++)
                    if (votes[i] > .8)
                    {
                        var line = lines[i]; Cv2.Rotate(line.Crop, line.Crop, RotateFlags.Rotate180);
                        var q = line.Quad; line.Quad = [q[2], q[3], q[0], q[1]];
                    }
                var pending = new List<Segment>();
                foreach (var line in lines.OrderBy(l => l.Crop.Width / (double)l.Crop.Height))
                    foreach (var segment in SplitLine(line, token))
                    {
                        if (pending.Count > 0 && (pending.Count == 4 || Math.Max(segment.Width, pending.Max(s => s.Width)) * (pending.Count + 1) > 2048))
                        { RecognizeSegments(pending, token); pending.Clear(); }
                        pending.Add(segment);
                    }
                if (pending.Count > 0) RecognizeSegments(pending, token);
                foreach (var line in lines.OrderBy(l => l.Index))
                {
                    var block = ToBlock(line, image);
                    if (block is { Confidence: >= .5 } && !string.IsNullOrWhiteSpace(block.Text)) blocks.Add(block);
                    progress?.Report(new("正在识别文字…", line.Index + 1, boxes.Count));
                }
            }
            finally { foreach (var line in lines) line.Dispose(); }
        }
        return blocks;
    }

    private static List<Segment> SplitLine(TextLine line, CancellationToken token)
    {
        var image = line.Crop;
        var limit = Math.Max(2, (int)Math.Floor(2048d * image.Height / 48));
        var overlap = Math.Max(1, (int)Math.Floor(192d * image.Height / 48));
        using var gray = new Mat(); Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
        var rows = gray.AsRows<byte>();
        var imageWidth = image.Width; var imageHeight = image.Height;
        var result = new List<Segment>();
        var start = 0;
        while (start < imageWidth)
        {
            token.ThrowIfCancellationRequested();
            var end = Math.Min(imageWidth, start + limit);
            if (end < imageWidth)
            {
                // Search the final quarter for a quiet column before cutting through ink.
                var best = end; var score = double.MaxValue;
                for (var x = start + limit * 3 / 4; x < end; x++)
                {
                    double sum = 0;
                    for (var y = imageHeight / 5; y < imageHeight * 4 / 5; y++) sum += 255 - rows[y][x];
                    if (sum < score) { score = sum; best = x; }
                }
                end = Math.Max(start + overlap + 1, best);
            }
            result.Add(new(line, start, end));
            if (end == imageWidth) break;
            start = end - overlap;
        }
        return result;
    }

    private void RecognizeSegments(List<Segment> segments, CancellationToken token)
    {
        var crops = new List<Mat>();
        try
        {
            foreach (var segment in segments)
                crops.Add(new(segment.Line.Crop, new Rect(segment.Start, 0, segment.End - segment.Start, segment.Line.Crop.Height)));
            var width = segments.Max(s => s.Width);
            var tensor = BatchTensor(crops, 48, width, out var contentWidths);
            RunOutput(_recognizer!, tensor, token, prediction =>
            {
                var shape = prediction.Dimensions; var steps = shape[^2]; var classes = shape[^1];
                if (classes != _characters.Length || shape.Length != 3 || shape[0] != segments.Count)
                    throw new InvalidDataException("OCR 模型与字符字典或批量维度不匹配。");
                // Consume native output in its owning scope, without an entire output copy.
                var data = prediction.ToDenseTensor().Buffer.Span;
                for (var n = 0; n < segments.Count; n++)
                {
                    token.ThrowIfCancellationRequested();
                    var runs = new List<(int Label, int Start, int End, double Score)>();
                    var visibleSteps = steps * contentWidths[n] / (double)width;
                    for (var t = 0; t < Math.Min(steps, (int)Math.Ceiling(visibleSteps)); t++)
                    {
                        if (t % 32 == 0) token.ThrowIfCancellationRequested();
                        var offset = (n * steps + t) * classes; var label = 0; var score = data[offset];
                        for (var c = 1; c < classes; c++) if (data[offset + c] > score) { label = c; score = data[offset + c]; }
                        if (label == 0) continue;
                        if (runs.Count > 0 && runs[^1].Label == label && runs[^1].End == t - 1)
                        { var last = runs[^1]; runs[^1] = (label, last.Start, t, Math.Max(score, last.Score)); }
                        else runs.Add((label, t, t, score));
                    }
                    var segment = segments[n];
                    var centers = runs.Select(r => Math.Clamp((r.Start + r.End + 1) / 2d / visibleSteps, 0, 1)).ToArray();
                    var decoded = new List<LineCharacter>();
                    for (var i = 0; i < runs.Count; i++)
                    {
                        var left = i == 0 ? Math.Max(0, runs[i].Start / visibleSteps) : (centers[i - 1] + centers[i]) / 2;
                        var right = i == runs.Count - 1 ? Math.Min(1, (runs[i].End + 1) / visibleSteps) : (centers[i] + centers[i + 1]) / 2;
                        decoded.Add(new(_characters[runs[i].Label], runs[i].Score,
                            segment.Start + left * (segment.End - segment.Start), segment.Start + right * (segment.End - segment.Start)));
                    }
                    MergeOverlap(segment, decoded);
                }
                return 0;
            });
        }
        finally { foreach (var crop in crops) crop.Dispose(); }
    }

    private static void MergeOverlap(Segment segment, List<LineCharacter> next)
    {
        var existing = segment.Line.Characters;
        if (existing.Count == 0) { existing.AddRange(next); return; }
        var previous = existing.Where(c => c.End > segment.Start).ToArray();
        var incoming = next.Where(c => c.Start < previous.LastOrDefault()?.End).ToArray();
        // Spatially constrained LCS aligns overlap only. Equal neighbouring digits remain distinct.
        var dp = new int[previous.Length + 1, incoming.Length + 1];
        bool Matches(int i, int j) => previous[i].Text == incoming[j].Text &&
            Math.Abs((previous[i].Start + previous[i].End - incoming[j].Start - incoming[j].End) / 2) < segment.Line.Crop.Height * .6;
        for (var i = previous.Length - 1; i >= 0; i--)
            for (var j = incoming.Length - 1; j >= 0; j--)
                dp[i, j] = Matches(i, j) ? 1 + dp[i + 1, j + 1] : Math.Max(dp[i + 1, j], dp[i, j + 1]);
        var a = 0; var b = 0; var cut = previous.Length == 0 ? segment.Start : (segment.Start + previous[^1].End) / 2;
        while (a < previous.Length && b < incoming.Length)
        {
            if (Matches(a, b)) { cut = (previous[a].Start + previous[a].End + incoming[b].Start + incoming[b].End) / 4; break; }
            if (dp[a + 1, b] >= dp[a, b + 1]) a++; else b++;
        }
        existing.RemoveAll(c => (c.Start + c.End) / 2 >= cut);
        existing.AddRange(next.Where(c => (c.Start + c.End) / 2 >= cut));
    }

    private static OcrTextBlock? ToBlock(TextLine line, Mat image)
    {
        if (line.Characters.Count == 0) return null;
        // A projective inverse is essential: bilinear quad interpolation is not a homography.
        using var inverse = Cv2.GetPerspectiveTransform(Rectangle(line.Crop.Width, line.Crop.Height), line.Quad);
        var matrix = new double[3, 3];
        for (var y = 0; y < 3; y++) for (var x = 0; x < 3; x++) matrix[y, x] = inverse.At<double>(y, x);
        NormalizedPoint Map(double x, double y)
        {
            var z = matrix[2, 0] * x + matrix[2, 1] * y + matrix[2, 2];
            return new(Math.Clamp((matrix[0, 0] * x + matrix[0, 1] * y + matrix[0, 2]) / z / image.Width, 0, 1),
                Math.Clamp((matrix[1, 0] * x + matrix[1, 1] * y + matrix[1, 2]) / z / image.Height, 0, 1));
        }
        var chars = line.Characters.Select(c => new OcrCharacter(c.Text, c.Score,
            [Map(c.Start, 0), Map(Math.Min(line.Crop.Width - 1, c.End), 0), Map(Math.Min(line.Crop.Width - 1, c.End), line.Crop.Height - 1), Map(c.Start, line.Crop.Height - 1)])).ToArray();
        return new(string.Concat(chars.Select(c => c.Text)), chars.Average(c => c.Confidence),
            line.Quad.Select(p => Normalize(p, image)).ToArray(), chars);
    }

    private static DenseTensor<float> BatchTensor(IReadOnlyList<Mat> images, int height, int width, out int[] contentWidths)
    {
        var result = new DenseTensor<float>(new[] { images.Count, 3, height, width });
        contentWidths = new int[images.Count];
        var stride = 3 * height * width;
        for (var i = 0; i < images.Count; i++)
        {
            var single = Tensor(images[i], height, width, false, out contentWidths[i]);
            single.Buffer.Span.CopyTo(result.Buffer.Span.Slice(i * stride, stride));
        }
        return result;
    }
    private static Point2f[] Rectangle(int width, int height) => [new(0, 0), new(width - 1, 0), new(width - 1, height - 1), new(0, height - 1)];
    private static NormalizedPoint Normalize(Point2f p, Mat image) => new(p.X / image.Width, p.Y / image.Height);
}
