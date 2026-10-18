using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;

// Separate processes load either frozen baseline or current Core; no implementation is copied.
var corePath = Path.GetFullPath(args[0]);
var reportPath = Path.GetFullPath(args[1]);
var mode = args.ElementAtOrDefault(2) ?? "binary";
var count = int.Parse(args.ElementAtOrDefault(3) ?? "7");
Cv2.SetNumThreads(4);
var context = new AssemblyLoadContext("algorithm", isCollectible: true);
var assembly = context.LoadFromAssemblyPath(corePath);
if (!Path.GetFullPath(assembly.Location).Equals(corePath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Wrong benchmark assembly");
var processorType = assembly.GetType("EasyScan.Core.DocumentProcessor")!;
var processor = Activator.CreateInstance(processorType)!;
var settingsType = assembly.GetType("EasyScan.Core.ScanSettings")!;
var settings = Activator.CreateInstance(settingsType)!;
foreach (var (name, value) in new (string, object)[] { ("AutoExposure", false), ("ShadowRemoval", 1d), ("HighlightRecovery", 0d), ("ColorBoost", 0d), ("NoiseReduction", 0d), ("DetailEnhancement", 0d) })
    settingsType.GetProperty(name)!.SetValue(settings, value);
var convert = processorType.GetMethod("ConvertColor")!;
var enhance = processorType.GetMethod("ProcessColor")!;
Mat Binary(Mat image)
{
    var parameters = new List<object> { image, Enum.Parse(assembly.GetType("EasyScan.Core.OutputColorMode")!, "Binary"), .5d };
    if (convert.GetParameters().Length == 4) parameters.Add(CancellationToken.None);
    return (Mat)convert.Invoke(processor, parameters.ToArray())!;
}
Mat Enhance(Mat image) => (Mat)enhance.Invoke(processor, [image, settings, CancellationToken.None])!;
var measurements = new List<object>();
if (mode == "binary" || mode == "enhance")
{
    foreach (var (width, height) in new[] { (3264, 2448), (4000, 3000), (6000, 4000) })
    {
        using var image = new Mat(height, width, MatType.CV_8UC3, Scalar.All(230));
        for (var y = 40; y < height - 20; y += 45) Cv2.PutText(image, "EASYSCAN 111100002222 .,:;", new(30, y), HersheyFonts.HersheySimplex, .7, Scalar.All(155), 1);
        using (var warmup = mode == "binary" ? Binary(image) : Enhance(image)) { }
        var times = new List<double>(); string hash = "";
        for (var i = 0; i < count; i++)
        {
            var watch = Stopwatch.StartNew(); using var result = mode == "binary" ? Binary(image) : Enhance(image);
            times.Add(watch.Elapsed.TotalMilliseconds);
            if (i == 0) hash = Convert.ToHexString(SHA256.HashData(result.ToBytes(".png")));
        }
        times.Sort();
        measurements.Add(new { width, height, count, medianMs = times[times.Count / 2], p95Ms = times[(int)Math.Ceiling(times.Count * .95) - 1], hash,
            peakMiB = Process.GetCurrentProcess().PeakWorkingSet64 / 1048576d });
    }
}
else if (mode == "quality")
{
    using var truth = new Mat(900, 1600, MatType.CV_8UC3, Scalar.All(235));
    Cv2.Rectangle(truth, new Rect(0, 450, 1600, 450), new Scalar(225, 193, 172), -1);
    for (var y = 70; y < 860; y += 45) Cv2.PutText(truth, "CERTIFICATE 111100002222", new(70, y), HersheyFonts.HersheySimplex, .65, Scalar.All(100), 1);
    Cv2.Circle(truth, new(1330, 700), 90, new Scalar(155, 160, 215), 4);
    using var shadow = truth.Clone();
    Shade(shadow);
    using var output = Enhance(shadow);
    double Error(Mat value, Rect area)
    {
        using var actual = new Mat(value, area); using var expected = new Mat(truth, area);
        return Cv2.Norm(actual, expected, NormTypes.L1) / (area.Width * area.Height * 3);
    }
    var prefix = Path.Combine(Path.GetDirectoryName(reportPath)!, Path.GetFileNameWithoutExtension(reportPath));
    Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
    File.WriteAllBytes(prefix + "-input.png", shadow.ToBytes(".png"));
    File.WriteAllBytes(prefix + "-truth.png", truth.ToBytes(".png"));
    File.WriteAllBytes(prefix + "-output.png", output.ToBytes(".png"));
    measurements.Add(new { colorMae = Error(output, new Rect(50, 480, 1100, 370)), whiteMae = Error(output, new Rect(50, 30, 1100, 370)),
        beforeColorMae = Error(shadow, new Rect(50, 480, 1100, 370)), beforeWhiteMae = Error(shadow, new Rect(50, 30, 1100, 370)) });
}
else if (mode == "batch")
{
    using var image = new Mat(3000, 4000, MatType.CV_8UC3, Scalar.All(225));
    var working = new List<double>(); var times = new List<double>();
    for (var i = 0; i < count; i++)
    {
        var watch = Stopwatch.StartNew();
        using (var output = Enhance(image)) using (var binary = Binary(output)) { }
        times.Add(watch.Elapsed.TotalMilliseconds);
        if (i % 5 == 4) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        using var process = Process.GetCurrentProcess(); process.Refresh(); working.Add(process.WorkingSet64 / 1048576d);
    }
    measurements.Add(new { count, times, workingMiB = working, peakMiB = Process.GetCurrentProcess().PeakWorkingSet64 / 1048576d });
}
else if (mode == "real")
{
    using var image = Cv2.ImRead(Path.GetFullPath(args[4]));
    var watch = Stopwatch.StartNew(); using var output = Enhance(image);
    measurements.Add(new { image.Width, image.Height, milliseconds = watch.Elapsed.TotalMilliseconds });
    File.WriteAllBytes(Path.ChangeExtension(reportPath, ".png"), output.ToBytes(".png"));
}
else if (mode == "numeric")
{
    using var input = new Mat(2100, 6400, MatType.CV_8UC3, Scalar.All(255));
    // 255^2 * 201^2 exceeds Int32; squared-window statistics must not overflow.
    using var output = Binary(input);
    var white = Cv2.CountNonZero(output);
    if (white != input.Total()) throw new InvalidDataException("Squared-box accumulation overflow on white paper");
    measurements.Add(new { width = input.Width, height = input.Height, whitePixels = white, squaredWindowSum = 255L * 255 * 201 * 201, passed = true });
}
else if (mode == "holdout")
{
    // Fixed, independent evaluation fixtures; never used to choose thresholds.
    var colors = new[] { new Scalar(225, 193, 172), new Scalar(185, 215, 180), new Scalar(170, 215, 230), new Scalar(215, 180, 220) };
    for (var index = 0; index < 12; index++)
    {
        using var truth = new Mat(720, 1200, MatType.CV_8UC3, Scalar.All(235));
        Cv2.Rectangle(truth, new Rect(0, 360, 1200, 360), colors[index % 4], -1);
        for (var y = 40; y < 700; y += 34) Cv2.PutText(truth, "PRINT 11002233,.;", new(30, y), HersheyFonts.HersheySimplex, .5, Scalar.All(95), 1);
        using var input = truth.Clone(); var rows = input.AsRows<Vec3b>();
        for (var y = 0; y < 720; y++) for (var x = 0; x < 1200; x++)
        {
            var factor = (index / 4) switch
            {
                0 => .55 + .45 * Math.Min(1, x / 800d),
                1 => x < 450 ? .62 : 1,
                _ => 1 - .45 * Math.Exp(-Math.Pow((x - 450) / 230d, 2))
            };
            var pixel = rows[y][x]; for (var c = 0; c < 3; c++) pixel[c] = (byte)Math.Round(pixel[c] * factor); rows[y][x] = pixel;
        }
        using var output = Enhance(input);
        using var outColor = new Mat(output, new Rect(30, 390, 1100, 300)); using var gtColor = new Mat(truth, new Rect(30, 390, 1100, 300));
        measurements.Add(new { index, mae = Cv2.Norm(output, truth, NormTypes.L1) / (720 * 1200 * 3), colorMae = Cv2.Norm(outColor, gtColor, NormTypes.L1) / (1100 * 300 * 3) });
    }
}
else if (mode == "dibco")
{
    var data = Path.GetFullPath(args[4]);
    foreach (var file in Directory.GetFiles(Path.Combine(data, "dataset"), "*.bmp").Order())
    {
        using var input = Cv2.ImRead(file); using var truth = Cv2.ImRead(Path.Combine(data, "gt", Path.GetFileNameWithoutExtension(file) + "_gt.bmp"), ImreadModes.Grayscale);
        using var raw = Binary(input); using var color = Enhance(input); using var corrected = Binary(color);
        measurements.Add(new { file = Path.GetFileName(file), rawF1 = F1(raw, truth), enhancedF1 = F1(corrected, truth) });
    }
}
else if (mode is "ocr" or "ocr-long")
{
    var engineType = assembly.GetType("EasyScan.Core.OnnxOcrEngine")!;
    using var engine = (IDisposable)Activator.CreateInstance(engineType, [Path.GetFullPath("models/ocr")])!;
    var recognize = engineType.GetMethod("RecognizeAsync")!;
    async Task<object> Recognize(Mat image)
    {
        var task = (Task)recognize.Invoke(engine, [image, null, CancellationToken.None])!;
        await task; return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }
    using var image = Cv2.ImRead(Path.GetFullPath(args[4]));
    if (mode == "ocr")
    {
        var times = new List<double>(); var text = "";
        for (var i = 0; i <= count; i++)
        {
            var watch = Stopwatch.StartNew(); var result = await Recognize(image); times.Add(watch.Elapsed.TotalMilliseconds);
            text = (string)result.GetType().GetProperty("Text")!.GetValue(result)!;
        }
        measurements.Add(new { coldMs = times[0], warmMs = times.Skip(1), text, peakMiB = Process.GetCurrentProcess().PeakWorkingSet64 / 1048576d });
    }
    else
    {
        using var blank = new Mat(64, 64, MatType.CV_8UC3, Scalar.All(255)); await Recognize(blank);
        Point2f[] quad = [new(0, 0), new(image.Width - 1, 0), new(image.Width - 1, image.Height - 1), new(0, image.Height - 1)];
        var text = ""; var watch = Stopwatch.StartNew();
        var method = engineType.GetMethod("ReadLines", BindingFlags.Instance | BindingFlags.NonPublic);
        if (method is not null)
        {
            var result = (System.Collections.IEnumerable)method.Invoke(engine, [image, new List<Point2f[]> { quad }, null, CancellationToken.None])!;
            text = string.Concat(result.Cast<object>().Select(b => (string)b.GetType().GetProperty("Text")!.GetValue(b)!));
        }
        else
        {
            var result = engineType.GetMethod("ReadLine", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(engine, [image, quad, CancellationToken.None]);
            text = result is null ? "" : (string)result.GetType().GetProperty("Text")!.GetValue(result)!;
        }
        var expected = string.Join(" ", Enumerable.Repeat("AB111100002222", 24));
        measurements.Add(new { cer = Cer(expected, text), text, milliseconds = watch.Elapsed.TotalMilliseconds });
    }
}
Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
File.WriteAllText(reportPath, JsonSerializer.Serialize(new { corePath, loadedPath = assembly.Location, coreSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))), convertParameters = convert.GetParameters().Length, mode, threads = 4, measurements }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(reportPath);

static void Shade(Mat image)
{
    var width = image.Width; var height = image.Height; var rows = image.AsRows<Vec3b>();
    for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
    {
        var factor = .48 + .52 * Math.Min(1, x / (width * .7));
        var p = rows[y][x]; for (var c = 0; c < 3; c++) p[c] = (byte)Math.Round(p[c] * factor); rows[y][x] = p;
    }
}

static double F1(Mat binary, Mat truth)
{
    using var a = new Mat(); using var b = new Mat(); using var overlap = new Mat();
    Cv2.Compare(binary, 127, a, CmpTypes.LE); Cv2.Compare(truth, 127, b, CmpTypes.LE); Cv2.BitwiseAnd(a, b, overlap);
    return 2d * Cv2.CountNonZero(overlap) / Math.Max(1, Cv2.CountNonZero(a) + Cv2.CountNonZero(b));
}
static double Cer(string expected, string actual)
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
