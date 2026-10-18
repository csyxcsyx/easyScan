namespace EasyScan.Core;

public sealed record PaperPlacement(int SheetIndex, double X, double Y, double Width, double Height);

public sealed class PdfLayoutPlanner
{
    private readonly PdfLayoutSettings _settings;
    private readonly double _margin, _availableWidth, _availableHeight;
    private int _sheet = -1;
    private double _nextY;
    public double PageWidth { get; }
    public double PageHeight { get; }
    public int PageCount => _sheet + 1;
    public const double PointsPerMm = 72d / 25.4;

    public PdfLayoutPlanner(PdfLayoutSettings? settings = null)
    {
        _settings = settings ?? new();
        if (!double.IsFinite(_settings.MarginMm) || _settings.MarginMm is < 0 or > 30
            || !double.IsFinite(_settings.ContentWidthPercent) || _settings.ContentWidthPercent is < 30 or > 100)
            throw new ArgumentException("PDF 页边距或内容宽度无效。");
        PageWidth = (_settings.Orientation == PaperOrientation.Portrait ? 210 : 297) * PointsPerMm;
        PageHeight = (_settings.Orientation == PaperOrientation.Portrait ? 297 : 210) * PointsPerMm;
        _margin = _settings.MarginMm * PointsPerMm;
        _availableWidth = PageWidth - 2 * _margin; _availableHeight = PageHeight - 2 * _margin;
    }

    public PaperPlacement PlaceNext(int width, int height)
    {
        if (width < 1 || height < 1) throw new ArgumentException("扫描图尺寸无效。");
        var scale = Math.Min(_availableWidth * _settings.ContentWidthPercent / 100 / width, _availableHeight / height);
        var w = width * scale; var h = height * scale;
        if (_settings.Mode == PdfLayoutMode.OnePerPage)
        {
            _sheet++; return new(_sheet, (PageWidth - w) / 2, (PageHeight - h) / 2, w, h);
        }
        if (_sheet < 0 || _nextY + h > PageHeight - _margin + .01) { _sheet++; _nextY = _margin; }
        var result = new PaperPlacement(_sheet, (PageWidth - w) / 2, _nextY, w, h);
        _nextY += h + 6 * PointsPerMm;
        return result;
    }
}
