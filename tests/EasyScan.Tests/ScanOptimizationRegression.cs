using EasyScan.Core;
using EasyScan.Desktop.Services;
using EasyScan.Desktop.ViewModels;
using EasyScan.Desktop.Views;
using OpenCvSharp;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Point = OpenCvSharp.Point;
using Rect = OpenCvSharp.Rect;
using Size = OpenCvSharp.Size;

namespace EasyScan.Tests;

internal static class ScanOptimizationRegression
{
    public static async Task RunAsync(string root, Action<bool, string> check, Action<System.Windows.Window, string, int, int, int> render)
    {
        var folder = Path.Combine(root, "扫描优化"); Directory.CreateDirectory(folder);
        var processor = new DocumentProcessor(); var loader = new ImageLoader();
        var metrics = new Dictionary<string, object>();
        VerifyGeometry(processor, check, metrics);
        VerifyStrokes(processor, check, metrics, folder);
        VerifyNoise(processor, check, metrics);
        using var clean = TextFixture();
        var input = Path.Combine(folder, "中英文-原图.png"); File.WriteAllBytes(input, clean.ToBytes(".png"));
        var page = new ExportPage((await loader.DescribeAsync(input))[0], new ScanSettings());
        using var engine = new OnnxOcrEngine();
        var watch = Stopwatch.StartNew();
        var recognized = await engine.RecognizeAsync(clean);
        var cleanMs = watch.ElapsedMilliseconds;
        Console.WriteLine("OCR CLEAN: " + recognized.Text.Replace(Environment.NewLine, " | "));
        const string expected = "扫描清晰度测试\n证书编号：20261015\n姓名：张三\nEASYSCAN OCR 1234567890\n浅色文字与彩色印章";
        var cleanCer = ErrorRate(expected, recognized.Text);
        check(cleanCer <= .08, $"offline Chinese/English/number OCR CER <= 8% (actual {cleanCer:P1})");
        check(recognized.Blocks.SelectMany(b => b.Characters).All(c => c.Polygon.All(p => p.X is >= 0 and <= 1 && p.Y is >= 0 and <= 1)), "OCR character coordinates stay in final scan bounds");
        using var shaded = Shade(clean);
        File.WriteAllBytes(Path.Combine(folder, "中英文-阴影输入.png"), shaded.ToBytes(".png"));
        var raw = await engine.RecognizeAsync(shaded);
        using var enhanced = processor.ProcessColor(shaded, new ScanSettings { ShadowRemoval = .8 });
        File.WriteAllBytes(Path.Combine(folder, "中英文-阴影优化.png"), enhanced.ToBytes(".png"));
        var improved = await engine.RecognizeAsync(enhanced);
        var rawCer = ErrorRate(expected, raw.Text); var enhancedCer = ErrorRate(expected, improved.Text);
        check(enhancedCer <= rawCer + .02 && enhancedCer <= .12, $"shadow correction preserves OCR accuracy ({rawCer:P1} -> {enhancedCer:P1})");
        using (var blank = new Mat(400, 600, MatType.CV_8UC3, Scalar.All(255)))
            check((await engine.RecognizeAsync(blank)).Blocks.Count == 0, "blank paper successfully produces empty OCR text");
        using (var missing = new OnnxOcrEngine(Path.Combine(folder, "absent-models")))
        {
            var failed = false;
            try { await missing.RecognizeAsync(clean); } catch (InvalidOperationException ex) { failed = ex.Message.Contains("OCR 模型"); }
            check(failed, "missing offline models explain recovery instead of reporting empty success");
        }
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel(); var canceled = false;
            try { await engine.RecognizeAsync(clean, token: cts.Token); } catch (OperationCanceledException) { canceled = true; }
            check(canceled, "OCR honors cancellation before native inference");
        }
        using var service = new OcrService(loader, processor);
        var cached = await service.RecognizeAsync(page);
        check(ReferenceEquals(cached, await service.RecognizeAsync(page)), "unchanged source/settings/models reuse text-only OCR cache");
        var changed = await service.RecognizeAsync(page with { Settings = page.Settings with { QuarterTurns = 2 } });
        check(!ReferenceEquals(cached, changed), "rotation invalidates OCR cache and keeps coordinates for the new scan");
        var vm = new PageViewModel(page.Source); vm.Initialize(new(CropQuad.FullFrame, .8, "整页"), clean.Width, clean.Height);
        vm.SetOcrResult(cached, vm.Settings);
        check(vm.HasCurrentOcr, "page exposes current OCR independently of scan readiness");
        vm.Brightness = 10;
        check(!vm.HasCurrentOcr && vm.OcrText == "" && vm.IsReady, "editing scan parameters hides stale text without disabling scanning");
        vm.Undo(); check(vm.HasCurrentOcr, "undo restores matching OCR result validity");
        render(new OcrResultWindow(vm), "OCR-文字结果.png", 760, 580, 96);
        render(new OcrResultWindow(vm), "OCR-文字结果-窄窗口.png", 480, 400, 144);
        await VerifyPdfAsync(folder, page, loader, processor, service, check);
        await VerifyPreviewAsync(folder, loader, processor, check);
        await ScanOptimizationWorkflowRegression.RunAsync(folder, input, recognized, loader, processor, engine, clean, check, render);
        metrics["OcrCleanCer"] = cleanCer; metrics["OcrShadowBeforeCer"] = rawCer; metrics["OcrShadowAfterCer"] = enhancedCer;
        metrics["OcrColdMilliseconds"] = cleanMs; metrics["PeakWorkingSetMiB"] = Process.GetCurrentProcess().PeakWorkingSet64 / 1048576d;
        metrics["ModelBytes"] = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "models", "ocr")).Sum(f => new FileInfo(f).Length);
        File.WriteAllText(Path.Combine(folder, "metrics.json"), JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(folder, "recognized.json"), JsonSerializer.Serialize(recognized, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void VerifyGeometry(DocumentProcessor processor, Action<bool, string> check, Dictionary<string, object> metrics)
    {
        using var image = new Mat(1800, 2600, MatType.CV_8UC3, Scalar.All(80));
        Point[] corners = [new(310, 260), new(2260, 170), new(2320, 1570), new(240, 1610)];
        Cv2.FillConvexPoly(image, corners, Scalar.All(235));
        Cv2.Rectangle(image, new Rect(650, 600, 1300, 650), Scalar.All(50), 8);
        var watch = Stopwatch.StartNew(); var detection = processor.DetectCrop(image); watch.Stop();
        var error = detection.Crop.Points.Zip(corners).Max(p => Math.Sqrt(Math.Pow(p.First.X * 2599 - p.Second.X, 2) + Math.Pow(p.First.Y * 1799 - p.Second.Y, 2))) / 1800;
        check(detection.Crop != CropQuad.FullFrame && error < .01, $"high-resolution corner refinement stays within 1% of short side ({error:P2})");
        using var colored = new Mat(600, 900, MatType.CV_8UC3, new Scalar(220, 225, 235));
        Cv2.Rectangle(colored, new Rect(130, 100, 630, 400), new Scalar(235, 230, 210), -1);
        var chroma = processor.DetectCrop(colored);
        check(chroma.Crop != CropQuad.FullFrame && chroma.Crop.Area is > .40 and < .55, "Lab candidate edges detect similar-luminance colored backgrounds");
        metrics["CornerErrorShortSide"] = error; metrics["DetectionMilliseconds2600px"] = watch.ElapsedMilliseconds;
    }

    private static void VerifyStrokes(DocumentProcessor processor, Action<bool, string> check, Dictionary<string, object> metrics, string folder)
    {
        using var image = new Mat(800, 3200, MatType.CV_8UC3, Scalar.All(245));
        using var truth = new Mat(image.Size(), MatType.CV_8UC1, Scalar.All(0));
        for (var y = 70; y < 730; y += 40)
        {
            Cv2.Line(image, new Point(80, y), new Point(3100, y), Scalar.All(155), 1);
            Cv2.Line(truth, new Point(80, y), new Point(3100, y), Scalar.All(255), 1);
        }
        using var binary = processor.ConvertColor(image, OutputColorMode.Binary);
        using var legacy = LegacyBinary(image);
        var current = ForegroundF1(binary, truth); var before = ForegroundF1(legacy, truth);
        check(current > .95 && current > before + .3, $"one-pixel stroke F1 improves over former Gaussian path ({before:F3} -> {current:F3})");
        check(Cv2.CountNonZero(binary) + Cv2.CountNonZero(truth) == image.Width * image.Height, "thin-stroke output introduces no surrounding background ink");
        File.WriteAllBytes(Path.Combine(folder, "细线-旧算法.png"), legacy.ToBytes(".png"));
        File.WriteAllBytes(Path.Combine(folder, "细线-新算法.png"), binary.ToBytes(".png"));
        metrics["ThinStrokeF1Before"] = before; metrics["ThinStrokeF1After"] = current;
    }

    private static Mat LegacyBinary(Mat image)
    {
        using var gray = new Mat(); using var smooth = new Mat(); using var light = new Mat(); using var mean = new Mat();
        using var variance = new Mat(); using var squared = new Mat(); using var meanSquared = new Mat();
        Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY); Cv2.GaussianBlur(gray, smooth, new Size(7, 7), 0);
        smooth.ConvertTo(light, MatType.CV_32FC1); Cv2.Blur(light, mean, new Size(103, 103));
        Cv2.Multiply(light, light, squared); Cv2.Blur(squared, variance, new Size(103, 103));
        Cv2.Multiply(mean, mean, meanSquared); Cv2.Subtract(variance, meanSquared, variance);
        Cv2.Max(variance, 0, variance); Cv2.Sqrt(variance, variance);
        Cv2.Multiply(variance, Scalar.All(1d / 128), variance); Cv2.Subtract(variance, Scalar.All(1), variance);
        Cv2.Multiply(variance, Scalar.All(.21), variance); Cv2.Add(variance, Scalar.All(1), variance); Cv2.Multiply(mean, variance, mean);
        var output = new Mat(); Cv2.Compare(light, mean, output, CmpTypes.GT); return output;
    }
    private static double ForegroundF1(Mat binary, Mat truth)
    {
        using var foreground = new Mat(); using var overlap = new Mat(); Cv2.BitwiseNot(binary, foreground); Cv2.BitwiseAnd(foreground, truth, overlap);
        return 2d * Cv2.CountNonZero(overlap) / Math.Max(1, Cv2.CountNonZero(foreground) + Cv2.CountNonZero(truth));
    }
    private static void VerifyNoise(DocumentProcessor processor, Action<bool, string> check, Dictionary<string, object> metrics)
    {
        using var noisy = new Mat(240, 400, MatType.CV_8UC3); var pixels = new Vec3b[240 * 400]; var random = new Random(42);
        for (var i = 0; i < pixels.Length; i++) { var v = (byte)(190 + random.Next(-10, 11)); pixels[i] = new(v, v, v); } noisy.SetArray(pixels);
        var settings = new ScanSettings { AutoExposure = false, ShadowRemoval = 0, HighlightRecovery = 0, ColorBoost = 0, DetailEnhancement = 0, NoiseReduction = 1 };
        using var reduced = processor.ProcessColor(noisy, settings);
        Cv2.MeanStdDev(noisy, out _, out var before); Cv2.MeanStdDev(reduced, out _, out var after);
        check(after.Val0 < before.Val0 * .7, "edge-preserving denoise reduces flat-paper noise by at least 30%");
        metrics["NoiseStdBefore"] = before.Val0; metrics["NoiseStdAfter"] = after.Val0;
    }
    private static Mat TextFixture()
    {
        var visual = new DrawingVisual();
        using (var graphics = visual.RenderOpen())
        {
            graphics.DrawRectangle(Brushes.White, null, new System.Windows.Rect(0, 0, 1200, 800));
            var lines = new[] { "扫描清晰度测试", "证书编号：20261015", "姓名：张三", "EASYSCAN OCR 1234567890", "浅色文字与彩色印章" };
            for (var i = 0; i < lines.Length; i++) graphics.DrawText(new FormattedText(lines[i], CultureInfo.GetCultureInfo("zh-CN"),
                System.Windows.FlowDirection.LeftToRight, new Typeface("Microsoft YaHei"), i == 0 ? 42 : 32,
                i == 4 ? new SolidColorBrush(Color.FromRgb(125, 125, 125)) : Brushes.Black, 1), new System.Windows.Point(80, 60 + i * 110));
            graphics.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(190, 45, 45)), 5), new System.Windows.Point(970, 620), 70, 70);
        }
        var bitmap = new RenderTargetBitmap(1200, 800, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = new MemoryStream(); encoder.Save(stream);
        return Cv2.ImDecode(stream.ToArray(), ImreadModes.Color);
    }
    private static Mat Shade(Mat clean)
    {
        var shaded = clean.Clone(); var rows = shaded.AsRows<Vec3b>(); var w = shaded.Width; var h = shaded.Height;
        for (var y = 0; y < h; y++) for (var x = 0; x < w; x++)
        { var p = rows[y][x]; var gain = .28 + .68 * x / w; rows[y][x] = new((byte)(p.Item0 * gain), (byte)(p.Item1 * gain), (byte)(p.Item2 * gain)); }
        return shaded;
    }
    private static double ErrorRate(string expected, string actual)
    {
        static string Clean(string s) => string.Concat(s.Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit));
        var a = Clean(expected); var b = Clean(actual); var prev = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        { var next = new int[b.Length + 1]; next[0] = i; for (var j = 1; j <= b.Length; j++) next[j] = Math.Min(Math.Min(next[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1)); prev = next; }
        return prev[^1] / (double)Math.Max(1, a.Length);
    }
    private static async Task VerifyPdfAsync(string folder, ExportPage page, ImageLoader loader, DocumentProcessor processor, OcrService ocr, Action<bool, string> check)
    {
        var service = new ExportService(loader, processor, ocr);
        var curved = page.Settings with { QuarterTurns = 1, CurvedEdges = new(new(.5, .015), new(.99, .5), new(.5, .985), new(.01, .5)) };
        var cases = new[]
        {
            (Name: "OnePerPage", Layout: new PdfLayoutSettings(), Color: OutputColorMode.Color, Pages: new[] { page, page }),
            (Name: "Continuous", Layout: new PdfLayoutSettings(PdfLayoutMode.Continuous), Color: OutputColorMode.Binary, Pages: new[] { page, page }),
            (Name: "RotatedCurved", Layout: new PdfLayoutSettings(PdfLayoutMode.Continuous, PaperOrientation.Landscape), Color: OutputColorMode.Grayscale, Pages: new[] { page with { Settings = curved }, page })
        };
        var validations = new List<object>();
        foreach (var example in cases)
        {
            var settings = new ExportSettings(Path.Combine(folder, example.Name), ExportFormat.Pdf, ExportOrganization.CombinedPdf, example.Color,
                PdfLayout: example.Layout, Watermark: new(true, "仅供测试", Target: WatermarkTarget.Paper));
            var plain = await service.ExportAsync(example.Pages, settings);
            var searchable = await service.ExportAsync(example.Pages, settings with { Ocr = new(true, true) });
            check(searchable.Failures.Count == 0 && searchable.Files.Count == 2, example.Name + ": searchable PDF and matching UTF-8 TXT export successfully " + string.Join(" | ", searchable.Failures.Select(f => f.Message)));
            var a = await loader.DescribeAsync(plain.Files.Single()); var b = await loader.DescribeAsync(searchable.Files.Single(f => f.EndsWith(".pdf")));
            check(a.Count == b.Count, example.Name + ": text layer preserves layout pagination");
            for (var i = 0; i < a.Count; i++)
            {
                using var before = await loader.LoadAsync(a[i], 1000); using var after = await loader.LoadAsync(b[i], 1000);
                check(Cv2.Norm(before, after, NormTypes.INF) == 0, example.Name + ": hidden text leaves rendered page pixels identical");
            }
            var planner = new PdfLayoutPlanner(example.Layout); var scans = new List<object>(); var texts = new List<string>();
            foreach (var source in example.Pages)
            {
                var result = await ocr.RecognizeAsync(source); scans.Add(new { Placement = planner.PlaceNext(result.Width, result.Height), Result = result }); texts.Add(result.Text);
            }
            validations.Add(new { File = searchable.Files.Single(f => f.EndsWith(".pdf")), Text = string.Join("\n", texts), Scans = scans });
        }
        File.WriteAllText(Path.Combine(folder, "pdf-checks.json"), JsonSerializer.Serialize(validations, new JsonSerializerOptions { WriteIndented = true }));
        using var missing = new OcrService(loader, processor, new OnnxOcrEngine(Path.Combine(folder, "missing-models")));
        var failure = await new ExportService(loader, processor, missing).ExportAsync([page], new(Path.Combine(folder, "失败"), ExportFormat.Pdf, Ocr: new(false, true)));
        check(failure.Files.Count == 0 && failure.Failures.Count == 1, "failed OCR never silently saves a non-searchable success PDF");
    }
    private static async Task VerifyPreviewAsync(string folder, ImageLoader loader, DocumentProcessor processor, Action<bool, string> check)
    {
        var path = Path.Combine(folder, "高清输入.png");
        using (var image = new Mat(1800, 3600, MatType.CV_8UC3, Scalar.All(245))) { Cv2.PutText(image, "HIGH RESOLUTION", new Point(100, 900), HersheyFonts.HersheySimplex, 3, Scalar.All(40), 3); File.WriteAllBytes(path, image.ToBytes(".png")); }
        var page = new ExportPage((await loader.DescribeAsync(path))[0], new());
        using var cache = new PreviewCache(loader, processor, Path.Combine(folder, "cache"));
        var normal = await cache.GetAsync(page, OutputColorMode.Color, 0);
        var high = await cache.GetAsync(page, OutputColorMode.Color, 0, maxDimension: 3200);
        check(normal.Original.PixelWidth == 1600 && high.Original.PixelWidth == 3200, "normal and HD preview cache keep distinct source resolutions");
        var count = cache.ProcessingCount;
        var again = await cache.GetAsync(page, OutputColorMode.Color, 0, maxDimension: 3200);
        check(cache.ProcessingCount == count && ReferenceEquals(high.Final, again.Final), "returning to HD preview reuses its cached bitmap");
    }
}
