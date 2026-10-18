using EasyScan.Core;
using OpenCvSharp;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace EasyScan.Desktop.Services;

public static class RuntimeProbe
{
    // Developer diagnostic: executes the shipped native decoders/exporters without opening a window.
    public static async Task<bool> RunAsync(string outputDirectory, string[] samples)
    {
        Directory.CreateDirectory(outputDirectory);
        try
        {
            var fixture = Path.Combine(outputDirectory, "runtime-fixture.png");
            using (var image = new Mat(300, 400, MatType.CV_8UC3, Scalar.All(230)))
            {
                Cv2.PutText(image, "easyScan", new Point(40, 160), HersheyFonts.HersheySimplex, 1.3, Scalar.All(40), 2);
                File.WriteAllBytes(fixture, image.ToBytes(".png"));
            }
            var loader = new ImageLoader(); var processor = new DocumentProcessor();
            var pages = new List<ExportPage>();
            var decoded = new List<object>();
            foreach (var path in new[] { fixture }.Concat(samples))
            {
                var source = (await loader.DescribeAsync(path))[0];
                using var image = await loader.LoadAsync(source);
                var detection = processor.DetectCrop(image);
                decoded.Add(new { Name = Path.GetFileName(path), image.Width, image.Height, detection.Crop.Area,
                    detection.Confidence, HasCurvedEdges = detection.CurvedEdges is not null });
                pages.Add(new(source, new ScanSettings { Crop = detection.Crop, CurvedEdges = detection.CurvedEdges }));
            }
            var outputs = new List<string>();
            foreach (var mode in Enum.GetValues<OutputColorMode>())
            {
                var result = await new ExportService(loader, processor).ExportAsync(pages,
                    new(Path.Combine(outputDirectory, mode.ToString()), ExportFormat.Png, Color: mode));
                if (result.Failures.Count != 0) throw new InvalidOperationException("Runtime image export failed.");
                outputs.AddRange(result.Files);
            }
            var pdf = await new ExportService(loader, processor).ExportAsync(pages,
                new(outputDirectory, Organization: ExportOrganization.CombinedPdf));
            if (pdf.Failures.Count != 0 || pdf.Files.Count != 1)
                throw new InvalidOperationException("Runtime PDF round trip failed.");
            var pdfPages = await loader.DescribeAsync(pdf.Files.Single());
            if (pdfPages.Count != pages.Count) throw new InvalidOperationException("Runtime PDF page count failed.");
            using var renderedPdf = await loader.LoadAsync(pdfPages[0], 600);
            if (renderedPdf.Empty()) throw new InvalidOperationException("Runtime PDF rendering failed.");
            var watermarked = await new ExportService(loader, processor).ExportAsync([pages[0]],
                new(Path.Combine(outputDirectory, "Watermark"), ExportFormat.Png, Watermark: new(true, "运行验证 Test")));
            if (watermarked.Failures.Count != 0 || watermarked.Files.Count != 1) throw new InvalidOperationException("Runtime Chinese watermark failed.");
            var continuous = await new ExportService(loader, processor).ExportAsync(pages,
                new(Path.Combine(outputDirectory, "Continuous"), Organization: ExportOrganization.CombinedPdf,
                    PdfLayout: new(PdfLayoutMode.Continuous), Watermark: new(true, "文档验证", WatermarkStyle.Tiled, WatermarkTarget.Paper)));
            if (continuous.Failures.Count != 0 || continuous.Files.Count != 1) throw new InvalidOperationException("Runtime paper watermark PDF failed.");
            var continuousPages = await loader.DescribeAsync(continuous.Files[0]);
            using var renderedContinuous = await loader.LoadAsync(continuousPages[0], 600);
            if (renderedContinuous.Empty()) throw new InvalidOperationException("Runtime continuous PDF rendering failed.");
            using var ocr = new OcrService(loader, processor);
            var text = await ocr.RecognizeAsync(pages[0]);
            if (!text.Text.Contains("easyScan", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Runtime offline OCR failed: " + text.Text);
            var searchable = await new ExportService(loader, processor, ocr).ExportAsync([pages[0]],
                new(Path.Combine(outputDirectory, "OCR"), ExportFormat.Pdf, Ocr: new(true, true)));
            if (searchable.Failures.Count != 0 || searchable.Files.Count != 2) throw new InvalidOperationException("Runtime searchable PDF/TXT failed.");
            File.WriteAllText(Path.Combine(outputDirectory, "runtime-report.json"), JsonSerializer.Serialize(new
            {
                Success = true, Runtime = RuntimeEnvironment.GetRuntimeDirectory(),
                Framework = RuntimeInformation.FrameworkDescription, Decoded = decoded, ImageOutputs = outputs.Count,
                PdfPages = pages.Count, PdfRenderVerified = true, WatermarkVerified = true, ContinuousPdfPages = continuousPages.Count,
                OfflineOcrVerified = true, SearchablePdfVerified = true, OcrModelVersion = ocr.Version
            }, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(outputDirectory, "runtime-report.json"), JsonSerializer.Serialize(new { Success = false, Error = ex.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            return false;
        }
    }
}
