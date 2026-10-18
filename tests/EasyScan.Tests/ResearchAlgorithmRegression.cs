using EasyScan.Core;
using OpenCvSharp;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace EasyScan.Tests;

internal static class ResearchAlgorithmRegression
{
    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        var folder = Path.Combine(root, "research-algorithms"); Directory.CreateDirectory(folder);
        var processor = new DocumentProcessor();
        foreach (var (width, height) in new[] { (3, 3), (37, 731), (1919, 2177), (3200, 2101) })
        {
            using var input = new Mat(height, width, MatType.CV_8UC3);
            var random = new Random(width); var data = new Vec3b[width * height];
            for (var i = 0; i < data.Length; i++) { var v = (byte)random.Next(120, 256); data[i] = new(v, v, v); }
            input.SetArray(data);
            for (var y = 255; y < height; y += 256) Cv2.Line(input, new(0, y), new(width - 1, y), Scalar.All(160), 1);
            using var expected = ReferenceBinary(input);
            using var actual = processor.ConvertColor(input, OutputColorMode.Binary);
            check(Cv2.Norm(expected, actual, NormTypes.INF) == 0, $"striped Sauvola exactly matches full-image baseline at {width}x{height}, including seams");
        }
        using (var input = new Mat(800, 1000, MatType.CV_8UC3, Scalar.All(220)))
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel(); var canceled = false;
            try { using var result = processor.ConvertColor(input, OutputColorMode.Binary, cancellationToken: cts.Token); }
            catch (OperationCanceledException) { canceled = true; }
            check(canceled, "binary conversion honors cancellation before allocating output");
        }
        var settings = new ScanSettings { AutoExposure = false, ShadowRemoval = 1, HighlightRecovery = 0, ColorBoost = 0, NoiseReduction = 0, DetailEnhancement = 0 };
        using (var paper = new Mat(600, 1000, MatType.CV_8UC3, Scalar.All(235)))
        {
            Cv2.Rectangle(paper, new Rect(0, 300, 1000, 300), new Scalar(225, 193, 172), -1);
            using var output = processor.ProcessColor(paper, settings);
            using var white = new Mat(output, new Rect(50, 50, 900, 200));
            using var blue = new Mat(output, new Rect(50, 350, 900, 200));
            using var expectedBlue = new Mat(paper, new Rect(50, 350, 900, 200));
            var before = Cv2.Mean(expectedBlue); var after = Cv2.Mean(blue);
            check(Math.Abs(after.Val1 - before.Val1) < 12 && Math.Abs(after.Val2 - before.Val2) < 12,
                "color-aware background preserves printed panel brightness instead of forcing white-paper luminance");
            check(Cv2.Mean(white).Val2 - after.Val2 > 35, "color paper and white paper retain distinct luminance");
            File.WriteAllBytes(Path.Combine(folder, "color-paper-input.png"), paper.ToBytes(".png"));
            File.WriteAllBytes(Path.Combine(folder, "color-paper-output.png"), output.ToBytes(".png"));
        }
        using (var narrow = new Mat(2, 10000, MatType.CV_8UC3, Scalar.All(200)))
        {
            using var enhanced = processor.ProcessColor(narrow, settings);
            check(enhanced.Size() == narrow.Size() && double.IsFinite(processor.Analyze(narrow).GlareFraction),
                "extreme aspect ratios retain at least one analysis pixel without resizing to zero");
        }
        VerifyProjectiveCoordinates(check);
        using var engine = new OnnxOcrEngine();
        using (var blank = new Mat(64, 64, MatType.CV_8UC3, Scalar.All(255))) await engine.RecognizeAsync(blank);
        var text = string.Join(" ", Enumerable.Repeat("AB111100002222", 24));
        var size = Cv2.GetTextSize(text, HersheyFonts.HersheySimplex, 1, 2, out _);
        using var longLine = new Mat(60, size.Width + 24, MatType.CV_8UC3, Scalar.All(255));
        Cv2.PutText(longLine, text, new(12, 40), HersheyFonts.HersheySimplex, 1, Scalar.All(35), 2, LineTypes.AntiAlias);
        var blocks = RecognizeKnownLines(engine, longLine, [Quad(longLine.Width, longLine.Height)], CancellationToken.None);
        var recognized = string.Join("", blocks.Select(b => b.Text));
        var cer = ErrorRate(text, recognized);
        Console.WriteLine($"LONG LINE CER: {cer:P2}: {recognized}");
        check(cer <= .01, "segmented ultra-long line preserves repeated digits with CER <= 1%");
        check(blocks.Count == 1 && blocks[0].Characters.All(c => c.Polygon.All(p => p.X is >= 0 and <= 1 && p.Y is >= 0 and <= 1)),
            "segmented characters map back into original line bounds");
        check(blocks[0].Characters.Zip(blocks[0].Characters.Skip(1)).All(pair => pair.First.Polygon.Average(p => p.X) <= pair.Second.Polygon.Average(p => p.X)),
            "overlap merging preserves spatial character order");
        File.WriteAllBytes(Path.Combine(folder, "long-line.png"), longLine.ToBytes(".png"));
        using var rotated = new Mat(); Cv2.Rotate(longLine, rotated, RotateFlags.Rotate180);
        var turned = RecognizeKnownLines(engine, rotated, [Quad(rotated.Width, rotated.Height)], CancellationToken.None);
        check(ErrorRate(text, string.Concat(turned.Select(b => b.Text))) <= .08, "180-degree direction correction retains long-line sequence and coordinates");
        // Known boxes isolate recognition/batching from the detector's separate resize policy.
        using var batch = new Mat(400, 1200, MatType.CV_8UC3, Scalar.All(255));
        var expectedLines = new[] { "A 11112222", "B 00001111 123456", "C 22223333", "D 44445555" };
        var boxes = new List<Point2f[]>();
        for (var i = 0; i < expectedLines.Length; i++)
        {
            Cv2.PutText(batch, expectedLines[i], new(20, i * 90 + 60), HersheyFonts.HersheySimplex, 1, Scalar.All(30), 2, LineTypes.AntiAlias);
            var box = Quad(650 + i * 80, 70).Select(p => new Point2f(p.X + 10, p.Y + i * 90 + 10)).ToArray(); boxes.Add(box);
        }
        var batched = RecognizeKnownLines(engine, batch, boxes, CancellationToken.None);
        Console.WriteLine("MIXED BATCH: " + string.Join(" | ", batched.Select(b => b.Text)));
        var singleText = string.Concat(boxes.Select(box => string.Concat(RecognizeKnownLines(engine, batch, [box], CancellationToken.None).Select(b => b.Text))));
        var batchText = string.Concat(batched.Select(b => b.Text));
        check(batched.Count == 4 && batched.Select(b => b.Text[0]).SequenceEqual("ABCD") &&
            ErrorRate(string.Join("", expectedLines), batchText) <= ErrorRate(string.Join("", expectedLines), singleText) + .02,
            "mixed-width batch returns original reading order and retains repeated characters");
        File.WriteAllText(Path.Combine(folder, "metrics.json"), JsonSerializer.Serialize(new { longLineCer = cer, longLineCharacters = text.Count(c => !char.IsWhiteSpace(c)), recognized }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void VerifyProjectiveCoordinates(Action<bool, string> check)
    {
        var type = typeof(OnnxOcrEngine); var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var lineType = type.GetNestedType("TextLine", BindingFlags.NonPublic)!;
        var characterType = type.GetNestedType("LineCharacter", BindingFlags.NonPublic)!;
        using var crop = new Mat(101, 101, MatType.CV_8UC3, Scalar.All(255));
        Point2f[] quad = [new(40, 30), new(260, 70), new(220, 240), new(70, 180)];
        using var line = (IDisposable)Activator.CreateInstance(lineType, flags, null, [crop, quad, 0], null)!;
        var characters = (System.Collections.IList)lineType.GetProperty("Characters")!.GetValue(line)!;
        characters.Add(Activator.CreateInstance(characterType, flags, null, ["A", .99d, 25d, 75d], null)!);
        using var page = new Mat(300, 320, MatType.CV_8UC3, Scalar.All(255));
        var block = (OcrTextBlock)type.GetMethod("ToBlock", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [line, page])!;
        using var transform = Cv2.GetPerspectiveTransform(Quad(101, 101), quad);
        var expected = Cv2.PerspectiveTransform(new Point2f[] { new(25, 0), new(75, 0), new(75, 100), new(25, 100) }, transform);
        var worst = block.Characters[0].Polygon.Zip(expected).Max(p => Math.Abs(p.First.X * 320 - p.Second.X) + Math.Abs(p.First.Y * 300 - p.Second.Y));
        check(worst < .001, "character quadrilaterals follow inverse homography on nonparallel perspective edges");
    }

    private static List<OcrTextBlock> RecognizeKnownLines(OnnxOcrEngine engine, Mat image, List<Point2f[]> boxes, CancellationToken token)
    {
        try { return (List<OcrTextBlock>)typeof(OnnxOcrEngine).GetMethod("ReadLines", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(engine, [image, boxes, null, token])!; }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }
    private static Point2f[] Quad(int width, int height) => [new(0, 0), new(width - 1, 0), new(width - 1, height - 1), new(0, height - 1)];
    private static double ErrorRate(string expected, string actual)
    {
        var a = new string(expected.Where(c => !char.IsWhiteSpace(c)).ToArray()); var b = new string(actual.Where(c => !char.IsWhiteSpace(c)).ToArray());
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var next = new int[b.Length + 1]; next[0] = i;
            for (var j = 1; j <= b.Length; j++) next[j] = Math.Min(previous[j] + 1, Math.Min(next[j - 1] + 1, previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1)));
            previous = next;
        }
        return previous[^1] / (double)Math.Max(1, a.Length);
    }
    // Frozen 1.3 algorithm serves as independent reference for halo/border equivalence.
    private static Mat ReferenceBinary(Mat image)
    {
        using var gray = new Mat(); using var smooth = new Mat(); using var light = new Mat(); using var mean = new Mat();
        using var squares = new Mat(); using var variance = new Mat(); using var meanSquared = new Mat();
        Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY); Cv2.BilateralFilter(gray, smooth, 5, 10, 2);
        var block = Math.Min(Math.Clamp((int)Math.Round(51d * Math.Max(gray.Width, gray.Height) / 1600), 3, 201), Math.Min(gray.Width, gray.Height)); if (block % 2 == 0) block--;
        smooth.ConvertTo(light, MatType.CV_32FC1); Cv2.Blur(light, mean, new Size(block, block)); Cv2.Multiply(light, light, squares); Cv2.Blur(squares, variance, new Size(block, block));
        Cv2.Multiply(mean, mean, meanSquared); Cv2.Subtract(variance, meanSquared, variance); Cv2.Max(variance, 0, variance); Cv2.Sqrt(variance, variance);
        Cv2.Multiply(variance, Scalar.All(1d / 128), variance); Cv2.Subtract(variance, Scalar.All(1), variance); Cv2.Multiply(variance, Scalar.All(.35 - .28 * .5), variance);
        Cv2.Add(variance, Scalar.All(1), variance); Cv2.Multiply(mean, variance, mean);
        using var original = new Mat(); gray.ConvertTo(original, MatType.CV_32FC1); var output = new Mat(); Cv2.Compare(original, mean, output, CmpTypes.GT); return output;
    }
}
