namespace EasyScan.Core;

public enum PdfLayoutMode { OnePerPage, Continuous }
public enum PaperOrientation { Portrait, Landscape }
public enum WatermarkStyle { Diagonal, Tiled, Corner }
public enum WatermarkTarget { Scan, Paper }
public enum WatermarkColor { Gray, Black, White }
public enum WatermarkCorner { BottomRight, BottomLeft, TopRight, TopLeft }

public sealed record PdfLayoutSettings(PdfLayoutMode Mode = PdfLayoutMode.OnePerPage,
    PaperOrientation Orientation = PaperOrientation.Portrait, double MarginMm = 12, double ContentWidthPercent = 100);

public sealed record WatermarkSettings(bool Enabled = false, string Text = "", WatermarkStyle Style = WatermarkStyle.Diagonal,
    WatermarkTarget Target = WatermarkTarget.Scan, WatermarkColor Color = WatermarkColor.Gray,
    double Opacity = .20, double FontSizePercent = 4, double Angle = -30,
    WatermarkCorner Corner = WatermarkCorner.BottomRight)
{
    public void Validate()
    {
        if (!Enabled) return;
        if (string.IsNullOrWhiteSpace(Text) || Text.Length > 80 || Text.Any(char.IsControl))
            throw new ArgumentException("水印请输入 1～80 字的单行文字。");
        if (!double.IsFinite(Opacity) || Opacity is < .05 or > 1 || !double.IsFinite(FontSizePercent) || FontSizePercent is < 1 or > 12
            || !double.IsFinite(Angle) || Angle is < -90 or > 90) throw new ArgumentException("水印参数无效。");
    }
}
