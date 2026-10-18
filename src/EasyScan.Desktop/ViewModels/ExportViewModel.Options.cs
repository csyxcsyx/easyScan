using CommunityToolkit.Mvvm.ComponentModel;
using EasyScan.Core;

namespace EasyScan.Desktop.ViewModels;

public sealed partial class ExportViewModel
{
    public Choice<PdfLayoutMode>[] LayoutModes { get; } = [new("每页一张", PdfLayoutMode.OnePerPage), new("连续排版", PdfLayoutMode.Continuous)];
    public Choice<PaperOrientation>[] Orientations { get; } = [new("A4 竖向", PaperOrientation.Portrait), new("A4 横向", PaperOrientation.Landscape)];
    public Choice<WatermarkStyle>[] WatermarkStyles { get; } = [new("斜向居中", Core.WatermarkStyle.Diagonal), new("斜向平铺", Core.WatermarkStyle.Tiled), new("角落署名", Core.WatermarkStyle.Corner)];
    public Choice<WatermarkTarget>[] WatermarkTargets { get; } = [new("扫描图", Core.WatermarkTarget.Scan), new("整张白纸", Core.WatermarkTarget.Paper)];
    public Choice<WatermarkColor>[] WatermarkColors { get; } = [new("灰色", Core.WatermarkColor.Gray), new("黑色", Core.WatermarkColor.Black), new("白色", Core.WatermarkColor.White)];
    public Choice<WatermarkCorner>[] WatermarkCorners { get; } = [new("右下角", Core.WatermarkCorner.BottomRight), new("左下角", Core.WatermarkCorner.BottomLeft), new("右上角", Core.WatermarkCorner.TopRight), new("左上角", Core.WatermarkCorner.TopLeft)];
    [ObservableProperty] private Choice<PdfLayoutMode> layoutMode = new("每页一张", PdfLayoutMode.OnePerPage);
    [ObservableProperty] private Choice<PaperOrientation> orientation = new("A4 竖向", PaperOrientation.Portrait);
    [ObservableProperty] private double marginMm = 12;
    [ObservableProperty] private double contentWidth = 100;
    [ObservableProperty] private bool exportOcrText;
    [ObservableProperty] private bool searchablePdf;
    [ObservableProperty] private bool watermarkEnabled;
    [ObservableProperty] private string watermarkText = "";
    [ObservableProperty] private Choice<WatermarkStyle> watermarkStyle = new("斜向居中", Core.WatermarkStyle.Diagonal);
    [ObservableProperty] private Choice<WatermarkTarget> watermarkTarget = new("扫描图", Core.WatermarkTarget.Scan);
    [ObservableProperty] private Choice<WatermarkColor> watermarkColor = new("灰色", Core.WatermarkColor.Gray);
    [ObservableProperty] private Choice<WatermarkCorner> watermarkCorner = new("右下角", Core.WatermarkCorner.BottomRight);
    [ObservableProperty] private double watermarkOpacity = 20;
    [ObservableProperty] private double watermarkSize = 4;
    [ObservableProperty] private double watermarkAngle = -30;
    [ObservableProperty] private double previewZoom = 1;
    [ObservableProperty] private double previewPanX;
    [ObservableProperty] private double previewPanY;
    private double _slant = -30;
    public bool IsCornerWatermark => WatermarkStyle.Value == Core.WatermarkStyle.Corner;
    public bool IsAngledWatermark => !IsCornerWatermark;
    public string WatermarkError
    {
        get { try { WatermarkOptions().Validate(); return ""; } catch (ArgumentException ex) { return ex.Message; } }
    }
    public bool HasWatermarkError => !string.IsNullOrEmpty(WatermarkError);

    private WatermarkSettings WatermarkOptions() => new(WatermarkEnabled, WatermarkText.Trim(), WatermarkStyle.Value,
        WatermarkTarget.Value, WatermarkColor.Value, WatermarkOpacity / 100, WatermarkSize, WatermarkAngle, WatermarkCorner.Value);
    private ExportSettings CreateSettings(string folder) => new(folder, Format.Value, Organization.Value, ColorMode, BinaryStrength / 100,
        (int)Math.Round(JpegQuality), 300, new(LayoutMode.Value, Orientation.Value, MarginMm, ContentWidth), WatermarkOptions(), new(ExportOcrText, IsPdf && SearchablePdf));
    private void WatermarkChanged()
    {
        OnPropertyChanged(nameof(WatermarkError)); OnPropertyChanged(nameof(HasWatermarkError)); ExportCommand.NotifyCanExecuteChanged(); RequestPreview();
    }
    partial void OnLayoutModeChanged(Choice<PdfLayoutMode> value) => RequestPreview(true);
    partial void OnOrientationChanged(Choice<PaperOrientation> value) => RequestPreview(true);
    partial void OnMarginMmChanged(double value) => RequestPreview(true);
    partial void OnContentWidthChanged(double value) => RequestPreview(true);
    partial void OnWatermarkEnabledChanged(bool value) => WatermarkChanged();
    partial void OnWatermarkTextChanged(string value) => WatermarkChanged();
    partial void OnWatermarkStyleChanged(Choice<WatermarkStyle> value)
    {
        OnPropertyChanged(nameof(IsCornerWatermark)); OnPropertyChanged(nameof(IsAngledWatermark));
        if (IsCornerWatermark) WatermarkAngle = 0; else WatermarkAngle = _slant;
        WatermarkChanged();
    }
    partial void OnWatermarkTargetChanged(Choice<WatermarkTarget> value) => WatermarkChanged();
    partial void OnWatermarkColorChanged(Choice<WatermarkColor> value) => WatermarkChanged();
    partial void OnWatermarkCornerChanged(Choice<WatermarkCorner> value) => WatermarkChanged();
    partial void OnWatermarkOpacityChanged(double value) => WatermarkChanged();
    partial void OnWatermarkSizeChanged(double value) => WatermarkChanged();
    partial void OnWatermarkAngleChanged(double value) { if (!IsCornerWatermark) _slant = value; WatermarkChanged(); }
}
