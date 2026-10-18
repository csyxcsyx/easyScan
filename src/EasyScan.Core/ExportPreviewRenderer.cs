using OpenCvSharp;
namespace EasyScan.Core;

public sealed record ExportPreview(byte[] Bytes, int SheetCount);
public sealed class ExportPreviewRenderer(DocumentProcessor processor)
{
    public ExportPreview Render(IReadOnlyList<byte[]> colorImages, ExportSettings settings, bool pdf, int index, CancellationToken token = default)
    {
        if (colorImages.Count == 0) throw new ArgumentException("没有可预览的扫描图。");
        if (!pdf)
        {
            using var color = Cv2.ImDecode(colorImages[Math.Clamp(index, 0, colorImages.Count - 1)], ImreadModes.Color);
            using var converted = processor.ConvertColor(color, settings.Color, settings.BinaryStrength, token);
            using var marked = new WatermarkComposer().Apply(converted, settings.Watermark ?? new(), settings.Color, token);
            return new(marked.ToBytes(".png"), colorImages.Count);
        }
        var planner = new PdfLayoutPlanner(settings.PdfLayout);
        var placements = new List<PaperPlacement>();
        foreach (var bytes in colorImages)
        {
            token.ThrowIfCancellationRequested(); using var image = Cv2.ImDecode(bytes, ImreadModes.Color);
            placements.Add(planner.PlaceNext(image.Width, image.Height));
        }
        index = Math.Clamp(index, 0, planner.PageCount - 1);
        var selected = colorImages.Select((bytes, i) => new PaperPreviewImage(bytes, placements[i])).Where(x => x.Placement.SheetIndex == index).ToArray();
        return new(new PaperPreviewRenderer(processor).Render(selected, settings, token), planner.PageCount);
    }
}