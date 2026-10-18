using OpenCvSharp;

namespace EasyScan.Core;

public sealed record PaperPreviewImage(byte[] ColorBytes, PaperPlacement Placement);

public sealed class PaperPreviewRenderer(DocumentProcessor processor)
{
    public byte[] Render(IReadOnlyList<PaperPreviewImage> images, ExportSettings settings, CancellationToken token = default)
    {
        var layout = new PdfLayoutPlanner(settings.PdfLayout); var scale = 1000 / layout.PageWidth;
        using var paper = new Mat((int)Math.Round(layout.PageHeight * scale), 1000,
            settings.Color == OutputColorMode.Color ? MatType.CV_8UC3 : MatType.CV_8UC1, Scalar.All(255));
        var watermark = settings.Watermark ?? new(); var composer = new WatermarkComposer();
        foreach (var item in images)
        {
            token.ThrowIfCancellationRequested();
            using var color = Cv2.ImDecode(item.ColorBytes, ImreadModes.Color);
            using var converted = processor.ConvertColor(color, settings.Color, settings.BinaryStrength, token);
            using var image = watermark.Target == WatermarkTarget.Scan ? composer.Apply(converted, watermark, settings.Color, token) : converted.Clone();
            var p = item.Placement;
            var rect = new Rect((int)Math.Round(p.X * scale), (int)Math.Round(p.Y * scale), Math.Max(1, (int)Math.Round(p.Width * scale)), Math.Max(1, (int)Math.Round(p.Height * scale)));
            rect.Width = Math.Min(rect.Width, paper.Width - rect.X); rect.Height = Math.Min(rect.Height, paper.Height - rect.Y);
            using var resized = new Mat(); Cv2.Resize(image, resized, rect.Size, interpolation: settings.Color == OutputColorMode.Binary ? InterpolationFlags.Nearest : InterpolationFlags.Area);
            using var target = new Mat(paper, rect); resized.CopyTo(target);
        }
        using var final = watermark.Target == WatermarkTarget.Paper ? composer.Apply(paper, watermark, settings.Color, token) : paper.Clone();
        return final.ToBytes(".png");
    }
}
