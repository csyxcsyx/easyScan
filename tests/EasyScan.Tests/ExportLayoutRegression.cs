using EasyScan.Core;
using OpenCvSharp;
using PdfSharp.Pdf.IO;
using System.IO;

namespace EasyScan.Tests;

internal static class ExportLayoutRegression
{
    public static async Task RunAsync(string root, ImageLoader loader, DocumentProcessor processor, Action<bool, string> check)
    {
        var folder = Path.Combine(root, "文档水印回归"); Directory.CreateDirectory(folder);
        var planner = new PdfLayoutPlanner(); var first = planner.PlaceNext(800, 500); var second = planner.PlaceNext(400, 900);
        check(planner.PageCount == 2 && first.SheetIndex == 0 && second.SheetIndex == 1, "one-per-page PDF preserves one complete scan on every sheet");
        check(Math.Abs(first.Width / first.Height - 1.6) < .0001 && first.X >= 12 * PdfLayoutPlanner.PointsPerMm - .01 && second.Height <= planner.PageHeight - 24 * PdfLayoutPlanner.PointsPerMm + .01,
            "paper placement preserves aspect ratios and margins, including tall scans");
        var continuous = new PdfLayoutPlanner(new(PdfLayoutMode.Continuous));
        var positions = Enumerable.Range(0, 6).Select(_ => continuous.PlaceNext(800, 500)).ToArray();
        check(continuous.PageCount == 3 && positions[1].SheetIndex == 0 && positions[2].SheetIndex == 1 && positions[1].Y > positions[0].Y + positions[0].Height,
            "continuous layout fits two landscape scans per A4 sheet and starts a new sheet without splitting");
        var landscape = new PdfLayoutPlanner(new(Orientation: PaperOrientation.Landscape));
        check(landscape.PageWidth > landscape.PageHeight && Math.Abs(landscape.PageWidth - planner.PageHeight) < .01, "landscape orientation swaps the same A4 dimensions consistently");
        using var original = new Mat(500, 800, MatType.CV_8UC3, new Scalar(222, 241, 248));
        Cv2.PutText(original, "DOCUMENT 123", new Point(60, 220), HersheyFonts.HersheySimplex, 1.2, Scalar.All(35), 3);
        Cv2.Circle(original, new Point(620, 340), 55, new Scalar(40, 40, 170), 4);
        var sourcePath = Path.Combine(folder, "原图.png"); File.WriteAllBytes(sourcePath, original.ToBytes(".png"));
        var source = (await loader.DescribeAsync(sourcePath))[0]; var page = new ExportPage(source, new());
        using var master = processor.ProcessColor(original, page.Settings);
        var masterBytes = master.ToBytes(".png"); var composer = new WatermarkComposer();
        foreach (var style in Enum.GetValues<WatermarkStyle>())
        {
            foreach (var mode in Enum.GetValues<OutputColorMode>())
            {
                var wm = new WatermarkSettings(true, "仅用于测试 Test 123", style, Opacity: .45);
                using var converted = processor.ConvertColor(master, mode);
                using var marked = composer.Apply(converted, wm, mode);
                check(Cv2.Norm(marked, converted, NormTypes.L1) > 100, $"{style}/{mode}: Chinese and Latin watermark text renders visibly");
                if (mode == OutputColorMode.Binary)
                {
                    using var shades = new Mat(); Cv2.InRange(marked, Scalar.All(1), Scalar.All(254), shades);
                    check(Cv2.CountNonZero(shades) == 0, style + ": opacity retains strictly binary pixels");
                }
                var result = await new ExportService(loader, processor).ExportAsync([page], new(Path.Combine(folder, style + "-" + mode), ExportFormat.Png, Color: mode, Watermark: wm));
                using var reopened = await loader.LoadAsync((await loader.DescribeAsync(result.Files.Single()))[0]);
                using var expected = new Mat(); if (marked.Channels() == 1) Cv2.CvtColor(marked, expected, ColorConversionCodes.GRAY2BGR); else marked.CopyTo(expected);
                check(Cv2.Norm(reopened, expected, NormTypes.INF) == 0, $"{style}/{mode}: PNG output exactly matches watermark composition");
            }
        }
        check(master.ToBytes(".png").SequenceEqual(masterBytes), "watermark never modifies the retained color master");
        var invalid = false; try { composer.Apply(master, new(true, " "), OutputColorMode.Color); } catch (ArgumentException) { invalid = true; }
        check(invalid, "enabled empty watermark is rejected before export");
        var service = new ExportService(loader, processor);
        foreach (var mode in Enum.GetValues<PdfLayoutMode>())
        {
            var result = await service.ExportAsync(Enumerable.Repeat(page, 6).ToArray(), new(folder, ExportFormat.Pdf,
                ExportOrganization.CombinedPdf, PdfLayout: new(mode), Watermark: new(true, "测试文档", WatermarkStyle.Tiled, Opacity: .2)));
            using var document = PdfReader.Open(result.Files.Single(), PdfDocumentOpenMode.Import);
            check(document.PageCount == (mode == PdfLayoutMode.OnePerPage ? 6 : 3), mode + ": exported PDF page count matches the layout planner");
            check(document.Pages.Cast<PdfSharp.Pdf.PdfPage>().All(p => Math.Abs(p.Width.Point - planner.PageWidth) < .01 && Math.Abs(p.Height.Point - planner.PageHeight) < .01),
                mode + ": every exported PDF page has uniform A4 dimensions");
            using var raster = await loader.LoadAsync((await loader.DescribeAsync(result.Files.Single()))[0], 1000);
            var corner = raster.At<Vec3b>(5, 5); check(corner == new Vec3b(255,255,255), mode + ": PDF leaves a white paper background outside scan content");
        }
        foreach (var target in Enum.GetValues<WatermarkTarget>())
        {
            var wm = new WatermarkSettings(true, "仅用于测试", WatermarkStyle.Tiled, target, Opacity: .7);
            var result = await service.ExportAsync([page], new(Path.Combine(folder, target.ToString()), ExportFormat.Pdf, Watermark: wm));
            using var raster = await loader.LoadAsync((await loader.DescribeAsync(result.Files.Single()))[0], 1000);
            using var top = new Mat(raster, new Rect(0, 0, raster.Width, Math.Min(120, raster.Height)));
            using var gray = new Mat(); Cv2.CvtColor(top, gray, ColorConversionCodes.BGR2GRAY); using var ink = new Mat(); Cv2.InRange(gray, Scalar.All(0), Scalar.All(240), ink);
            check(target == WatermarkTarget.Scan ? Cv2.CountNonZero(ink) == 0 : Cv2.CountNonZero(ink) > 0,
                target + ": watermark respects scan-only versus full-paper coverage");
        }
        var snapshot = new ExportSettings(folder, ExportFormat.Pdf, PdfLayout: new(PdfLayoutMode.Continuous), Watermark: new(true, "预览测试", WatermarkStyle.Tiled));
        var preview = new ExportPreviewRenderer(processor).Render(Enumerable.Repeat(masterBytes, 6).ToArray(), snapshot, true, 0);
        check(preview.SheetCount == 3, "PDF preview and export share the same pagination");
        File.WriteAllBytes(Path.Combine(folder, "白纸连续预览.png"), preview.Bytes);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var canceled = false; try { using var ignored = composer.Apply(master, new(true, "取消验证"), OutputColorMode.Color, cancellation.Token); } catch (OperationCanceledException) { canceled = true; }
        check(canceled, "watermark composition honors cancellation");
    }
}
