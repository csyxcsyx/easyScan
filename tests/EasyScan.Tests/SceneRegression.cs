using EasyScan.Core;
using OpenCvSharp;
using System.IO;
using System.Text.Json;

namespace EasyScan.Tests;

internal static class SceneRegression
{
    public static void Run(string root, Action<bool, string> check)
    {
        var folder = Path.Combine(root, "场景回归"); Directory.CreateDirectory(folder);
        var processor = new DocumentProcessor(); var metrics = new List<object>();
        var corners = new CropQuad(new(.12, .13), new(.83, .08), new(.88, .86), new(.08, .91));
        foreach (var name in new[] { "深色桌面", "浅色桌面", "纹理桌面", "低光", "明亮环境", "弯曲纸张", "小证件" })
        {
            var crop = name == "小证件" ? new CropQuad(new(.3,.35), new(.66,.32), new(.69,.63), new(.28,.67)) : corners;
            var edges = name == "弯曲纸张" ? CurvedEdges.Straight(crop) with
                { Top = new(.475,.18), Bottom = new(.48,.825), Left = new(.14,.52), Right = new(.815,.47) } : null;
            using var image = new Mat(600, 900, MatType.CV_8UC3, Scalar.All(name is "浅色桌面" or "明亮环境" ? 230 : name == "低光" ? 25 : 65));
            if (name == "纹理桌面")
                for (var x = 0; x < 900; x += 18) Cv2.Line(image, new Point(x, 0), new Point(x + 120, 599), new Scalar(85,110,145), 8);
            using var mask = new Mat(image.Size(), MatType.CV_8UC1, Scalar.All(0));
            var outline = Outline(crop, edges).Select(p => new Point((int)Math.Round(p.X * 899), (int)Math.Round(p.Y * 599))).ToArray();
            Cv2.FillPoly(mask, [outline], Scalar.All(255));
            image.SetTo(Scalar.All(name == "低光" ? 108 : name == "明亮环境" ? 252 : 244), mask);
            // The hard silhouette ground truth is independent of the detector's output.
            Cv2.PutText(image, "DOCUMENT SCAN", new Point(name == "小证件" ? 310 : 220, name == "小证件" ? 300 : 220), HersheyFonts.HersheySimplex, name == "小证件" ? .45 : 1.2, Scalar.All(30), 2);
            File.WriteAllBytes(Path.Combine(folder, name + "-输入.png"), image.ToBytes(".png"));
            var result = processor.DetectCrop(image);
            var settings = new ScanSettings { Crop = result.Crop, CurvedEdges = result.CurvedEdges, EdgeInset = .0015, AutoExposure = false, ShadowRemoval = 0, HighlightRecovery = 0, ColorBoost = 0 };
            using var expectedColor = new Mat(); Cv2.CvtColor(mask, expectedColor, ColorConversionCodes.GRAY2BGR);
            using var maskOutput = new PageRectifier().Rectify(expectedColor, settings);
            using var gray = new Mat(); Cv2.CvtColor(maskOutput, gray, ColorConversionCodes.BGR2GRAY);
            using var backgroundPixels = new Mat(); Cv2.InRange(gray, Scalar.All(0), Scalar.All(180), backgroundPixels);
            var contamination = Cv2.CountNonZero(backgroundPixels) / (double)(gray.Width * gray.Height);
            Console.WriteLine($"SCENE {name}: area={result.Crop.Area:F3}, confidence={result.Confidence:F2}, background={contamination:P2}, curve={result.CurvedEdges?.Deviation(result.Crop):F4}");
            metrics.Add(new { Name = name, result.Confidence, Area = result.Crop.Area, BackgroundFraction = contamination, Curvature = result.CurvedEdges?.Deviation(result.Crop) ?? 0 });
            using var output = processor.ProcessColor(image, settings); File.WriteAllBytes(Path.Combine(folder, name + "-输出.png"), output.ToBytes(".png"));
            check(result.Crop != CropQuad.FullFrame && result.Confidence >= .65, name + ": detects the document rather than the full background");
            check(contamination < .025, name + ": background contamination below 2.5%");
            check(Math.Abs(result.Crop.Area - crop.Area) < .07, name + ": retains the document area");
            if (edges is not null) check(result.CurvedEdges is not null, "curved silhouette is detected and supplies correction controls");
        }
        using var uniform = new Mat(500, 700, MatType.CV_8UC3, Scalar.All(245));
        Cv2.Rectangle(uniform, new Rect(70,70,560,360), Scalar.All(80), 2);
        check(processor.DetectCrop(uniform).Crop == CropQuad.FullFrame, "already cropped document does not mistake a printed frame for the paper edge");
        using (var ornamental = new Mat(500, 700, MatType.CV_8UC3, Scalar.All(160)))
        {
            Cv2.Rectangle(ornamental, new Rect(35,25,630,450), Scalar.All(195), -1);
            Cv2.Rectangle(ornamental, new Rect(35,25,630,450), Scalar.All(65), 4);
            check(processor.DetectCrop(ornamental).Crop == CropQuad.FullFrame,
                "a broad darker border on similar paper stock falls back to full-frame confirmation");
        }

        foreach (var scene in new[] { "低光增强", "强光增强", "局部反光", "偏色" })
        {
            using var photo = new Mat(400, 700, MatType.CV_8UC3, scene == "偏色" ? new Scalar(160,205,225) : Scalar.All(scene == "低光增强" ? 94 : scene == "强光增强" ? 248 : 185));
            Cv2.PutText(photo, "REAL TEXT", new Point(90,190), HersheyFonts.HersheySimplex, 1.4, Scalar.All(scene == "低光增强" ? 25 : 48), 3);
            Cv2.Circle(photo, new Point(540, 270), 40, new Scalar(45,45,180), 4);
            if (scene == "局部反光") Cv2.Ellipse(photo, new Point(330,90), new Size(70,45), 0, 0, 360, Scalar.All(255), -1);
            using var enhanced = processor.ProcessColor(photo, new ScanSettings { CorrectColorCast = scene == "偏色", ShadowRemoval = .55, HighlightRecovery = .7 });
            var quality = processor.Analyze(photo);
            using var backgroundBefore = new Mat(photo, new Rect(20,250,100,80)); using var backgroundAfter = new Mat(enhanced, new Rect(20,250,100,80));
            var before = Cv2.Mean(backgroundBefore).Val0; var after = Cv2.Mean(backgroundAfter).Val0;
            Console.WriteLine($"LIGHT {scene}: {before:F1}->{after:F1}; quality={quality.Summary}; glare={quality.GlareFraction:P2}");
            if (scene == "低光增强") check(after > before + 40 && after < 240 && quality.LowLight, "automatic exposure lifts low-light paper without clipping it");
            if (scene == "强光增强") check(after < before && after > 200 && quality.BrightExposure, "bright exposure is gently compressed");
            if (scene == "局部反光")
            {
                using var peak = new Mat(enhanced, new Rect(320,80,20,20));
                check(Cv2.Mean(peak).Val0 < 250 && quality.GlareFraction > .003, "local reflection is detected and highlight intensity is reduced");
                using var independent = processor.ProcessColor(photo, new ScanSettings { ShadowRemoval = 0, HighlightRecovery = 1, AutoExposure = false });
                using var independentPeak = new Mat(independent, new Rect(320,80,20,20));
                check(Cv2.Mean(independentPeak).Val0 < 250, "highlight recovery also works when shadow removal is disabled");
            }
            if (scene == "偏色")
            {
                var b = Cv2.Mean(backgroundBefore); var a = Cv2.Mean(backgroundAfter);
                check(Math.Abs(a.Val2 - a.Val0) < Math.Abs(b.Val2 - b.Val0) * .5, "optional white balance reduces a warm lighting cast");
            }
            using var seal = new Mat(enhanced, new Rect(496,226,88,88)); var mean = Cv2.Mean(seal);
            check(mean.Val2 > mean.Val0 + 8, scene + ": red seal remains colored");
            using var inputHsv = new Mat(); using var outputHsv = new Mat();
            Cv2.CvtColor(photo, inputHsv, ColorConversionCodes.BGR2HSV); Cv2.CvtColor(enhanced, outputHsv, ColorConversionCodes.BGR2HSV);
            var inputInk = inputHsv.At<Vec3b>(270, 580); var outputInk = outputHsv.At<Vec3b>(270, 580);
            var hueDifference = Math.Abs(outputInk.Item0 - inputInk.Item0);
            check(outputInk.Item1 >= inputInk.Item1 * .7 && Math.Min(hueDifference, 180 - hueDifference) <= 6,
                scene + ": red ink retains hue and useful saturation after exposure correction");
            File.WriteAllBytes(Path.Combine(folder, scene + "-输入.png"), photo.ToBytes(".png")); File.WriteAllBytes(Path.Combine(folder, scene + "-输出.png"), enhanced.ToBytes(".png"));
        }
        File.WriteAllText(Path.Combine(folder, "metrics.json"), JsonSerializer.Serialize(metrics, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static IEnumerable<NormalizedPoint> Outline(CropQuad crop, CurvedEdges? edges)
    {
        var corners = crop.Points; var mid = (edges ?? CurvedEdges.Straight(crop)).Points;
        for (var side = 0; side < 4; side++)
            for (var i = 0; i < 50; i++) yield return CurvedEdges.Evaluate(corners[side], corners[(side + 1) % 4], mid[side], i / 50d);
    }
}
