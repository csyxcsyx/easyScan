namespace EasyScan.Core;

public enum OutputColorMode { Color, Grayscale, Binary }
public enum EnhancementPreset { Natural, WhitePaper }
public enum ExportFormat { Automatic, Jpeg, Png, Pdf }
public enum ExportOrganization { SeparateSources, CombinedPdf }

public sealed record SourceDocument(Guid Id, string Path, bool IsPdf)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
}

public sealed record PageSource(SourceDocument Document, int PageIndex, int Width = 0, int Height = 0)
{
    public string DisplayName => Document.IsPdf
        ? $"{System.IO.Path.GetFileName(Document.Path)} · 第 {PageIndex + 1} 页"
        : System.IO.Path.GetFileName(Document.Path);
}

public readonly record struct NormalizedPoint(double X, double Y);

public sealed record CurvedEdges(NormalizedPoint Top, NormalizedPoint Right, NormalizedPoint Bottom, NormalizedPoint Left)
{
    public NormalizedPoint[] Points => [Top, Right, Bottom, Left];
    public static CurvedEdges Straight(CropQuad crop)
    {
        var p = crop.Points;
        var mids = Enumerable.Range(0, 4).Select(i => new NormalizedPoint((p[i].X + p[(i + 1) % 4].X) / 2, (p[i].Y + p[(i + 1) % 4].Y) / 2)).ToArray();
        return new(mids[0], mids[1], mids[2], mids[3]);
    }
    public CurvedEdges WithPoint(int index, NormalizedPoint value)
    {
        var p = Points; p[index] = value; return new(p[0], p[1], p[2], p[3]);
    }
    public bool IsValidFor(CropQuad crop)
    {
        var corners = crop.Points;
        var smallest = Enumerable.Range(0, 4).Min(i => Math.Sqrt(Math.Pow(corners[i].X - corners[(i + 1) % 4].X, 2) + Math.Pow(corners[i].Y - corners[(i + 1) % 4].Y, 2)));
        return Points.Select((p, i) =>
        {
            var a = corners[i]; var b = corners[(i + 1) % 4];
            var length = Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
            var distance = Math.Sqrt(Math.Pow(p.X - (a.X + b.X) / 2, 2) + Math.Pow(p.Y - (a.Y + b.Y) / 2, 2));
            return double.IsFinite(p.X) && double.IsFinite(p.Y) && p.X is >= 0 and <= 1 && p.Y is >= 0 and <= 1 && distance < Math.Min(length * .18, smallest * .18);
        }).All(x => x);
    }
    public double Deviation(CropQuad crop) => Points.Zip(Straight(crop).Points)
        .Max(x => Math.Sqrt(Math.Pow(x.First.X - x.Second.X, 2) + Math.Pow(x.First.Y - x.Second.Y, 2)));
    public static NormalizedPoint Evaluate(NormalizedPoint a, NormalizedPoint b, NormalizedPoint mid, double t)
    {
        var bow = 4 * t * (1 - t);
        return new(a.X + (b.X - a.X) * t + (mid.X - (a.X + b.X) / 2) * bow,
            a.Y + (b.Y - a.Y) * t + (mid.Y - (a.Y + b.Y) / 2) * bow);
    }
}

public sealed record PhotoQuality(bool LowLight, bool BrightExposure, bool UnevenLight, double GlareFraction)
{
    public bool HasIssues => LowLight || BrightExposure || UnevenLight || GlareFraction > .003;
    public string Summary => string.Join(" · ", new[] { LowLight ? "低光" : "", BrightExposure ? "曝光偏亮" : "",
        UnevenLight ? "光线不均" : "", GlareFraction > .003 ? "局部反光" : "" }.Where(x => x.Length > 0));
}

public sealed record CropQuad(NormalizedPoint TopLeft, NormalizedPoint TopRight,
    NormalizedPoint BottomRight, NormalizedPoint BottomLeft)
{
    public static CropQuad FullFrame { get; } = new(new(0, 0), new(1, 0), new(1, 1), new(0, 1));
    public NormalizedPoint[] Points => [TopLeft, TopRight, BottomRight, BottomLeft];
    public double Area => Math.Abs(Points.Select((p, i) =>
        p.X * Points[(i + 1) % 4].Y - p.Y * Points[(i + 1) % 4].X).Sum()) / 2;

    public bool IsValid
    {
        get
        {
            var p = Points;
            if (Area < .01 || p.Any(x => !double.IsFinite(x.X) || !double.IsFinite(x.Y)
                || x.X < 0 || x.X > 1 || x.Y < 0 || x.Y > 1)) return false;
            // A consistent winding rejects crossing edges and concave quadrilaterals.
            var crosses = Enumerable.Range(0, 4).Select(i =>
                (p[(i + 1) % 4].X - p[i].X) * (p[(i + 2) % 4].Y - p[(i + 1) % 4].Y)
                - (p[(i + 1) % 4].Y - p[i].Y) * (p[(i + 2) % 4].X - p[(i + 1) % 4].X)).ToArray();
            return crosses.All(x => x > .00001);
        }
    }

    public CropQuad WithPoint(int index, NormalizedPoint value)
    {
        var p = Points;
        p[index] = value;
        return new(p[0], p[1], p[2], p[3]);
    }
}

public sealed record ScanSettings
{
    public CropQuad Crop { get; init; } = CropQuad.FullFrame;
    public CurvedEdges? CurvedEdges { get; init; }
    public bool FlattenCurvedEdges { get; init; } = true;
    public double EdgeInset { get; init; }
    public EnhancementPreset Preset { get; init; }
    public double ShadowRemoval { get; init; } = .35;
    public double Brightness { get; init; }
    public double Contrast { get; init; }
    public bool AutoExposure { get; init; } = true;
    public bool CorrectColorCast { get; init; }
    public double HighlightRecovery { get; init; } = .45;
    public double ColorBoost { get; init; } = .2;
    public double NoiseReduction { get; init; } = .15;
    public double DetailEnhancement { get; init; } = .2;
    public int QuarterTurns { get; init; }
}

public sealed record CropDetection(CropQuad Crop, double Confidence, string Description, CurvedEdges? CurvedEdges = null);
public sealed record ExportPage(PageSource Source, ScanSettings Settings);
public sealed record ExportSettings(string Folder, ExportFormat Format = ExportFormat.Automatic,
    ExportOrganization Organization = ExportOrganization.SeparateSources,
    OutputColorMode Color = OutputColorMode.Color, double BinaryStrength = .5,
    int JpegQuality = 95, int Dpi = 300, PdfLayoutSettings? PdfLayout = null, WatermarkSettings? Watermark = null,
    OcrExportSettings? Ocr = null);
public sealed record ExportProgress(int Completed, int Total, string FileName, string Stage = "");
public sealed record ExportFailure(string FileName, string Message);
public sealed record ExportResult(IReadOnlyList<string> Files, IReadOnlyList<ExportFailure> Failures, bool Cancelled);
