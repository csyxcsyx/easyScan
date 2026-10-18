using CommunityToolkit.Mvvm.ComponentModel;
using EasyScan.Core;
using EasyScan.Desktop.Services;
using System.ComponentModel;
using System.Windows.Media.Imaging;

namespace EasyScan.Desktop.ViewModels;

public enum PreviewMode { Result, Original, Compare }

public sealed partial class MainViewModel
{
    private CancellationTokenSource? _previewCancellation;
    private PreviewMode _beforeCrop;
    private bool _cancelCrop;
    [ObservableProperty] private PreviewMode previewMode;
    [ObservableProperty] private double panX;
    [ObservableProperty] private double panY;
    [ObservableProperty] private string loadingText = "正在读取图片…";
    public bool IsResult { get => PreviewMode == PreviewMode.Result; set { if (value) PreviewMode = PreviewMode.Result; } }
    public bool ShowOriginal { get => PreviewMode == PreviewMode.Original; set { if (value) PreviewMode = PreviewMode.Original; else if (ShowOriginal) PreviewMode = PreviewMode.Result; } }
    public bool ShowComparison { get => PreviewMode == PreviewMode.Compare; set { if (value) PreviewMode = PreviewMode.Compare; else if (ShowComparison) PreviewMode = PreviewMode.Result; } }
    public bool ShowSingle => !IsCropping && !ShowComparison;
    public bool ShowCompare => !IsCropping && ShowComparison;
    public BitmapSource? DisplayPreview => (ShowOriginal ? OriginalPreview : ProcessedPreview) ?? OriginalPreview ?? SelectedPage?.Thumbnail;
    public string ZoomLabel => $"{Zoom:0.0}×";
    public bool CanZoom => HasPreview && !IsCropping && !IsDetecting;
    public bool CanZoomIn => CanZoom && Zoom < 8;
    public bool CanZoomOut => CanZoom && Zoom > .5;
    public bool IsPreviewBusy => IsRendering || IsDetecting;
    public string PreviewBusyText => IsDetecting ? "正在识别纸张…" : LoadingText;

    private void OnPageStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PageViewModel.State)) return;
        if (SelectedPage?.State is "失败" or "已取消") IsRendering = false;
        NotifySelection();
    }

    public async Task RefreshPreviewAsync(int delay = 0)
    {
        _previewCancellation?.Cancel();
        var cancellation = new CancellationTokenSource(); _previewCancellation = cancellation;
        var token = cancellation.Token; var page = SelectedPage;
        try
        {
            if (_disposed || page is null) return;
            if (!page.IsReady || _initializing.Contains(page))
            {
                LoadingText = page.State == "排队中" ? "等待处理…" : "正在读取与处理…";
                IsRendering = _initializing.Contains(page) || page.State is "排队中" or "处理中";
                return;
            }
            var exportPage = new ExportPage(page.Source, page.Settings);
            var resolution = Zoom > 1.5 ? 3200 : 1600;
            if (PreviewCache.TryGet(exportPage, ColorMode, BinaryStrength / 100, out var cached, resolution)) { ApplyFrame(page, cached!); return; }
            IsRendering = true; LoadingText = HasPreview ? "正在更新预览…" : "正在生成预览…";
            if (delay > 0) await Task.Delay(delay, token);
            var frame = await PreviewCache.GetAsync(exportPage, ColorMode, BinaryStrength / 100, token, resolution);
            token.ThrowIfCancellationRequested();
            if (SelectedPage == page && !_disposed) ApplyFrame(page, frame);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (SelectedPage == page) Status = "预览失败：" + ExportService.FriendlyError(ex); }
        finally
        {
            if (_previewCancellation == cancellation)
            {
                IsRendering = page is not null && !_disposed && (_initializing.Contains(page) || page.State is "排队中" or "处理中");
                _previewCancellation = null;
            }
            cancellation.Dispose(); NotifyViewport();
        }
    }

    private void ApplyFrame(PageViewModel page, PreviewFrame frame)
    {
        OriginalPreview = frame.Original; ProcessedPreview = frame.Final; CurrentColorBytes = frame.ColorBytes;
        page.Thumbnail = BitmapHelper.FromBytes(frame.ColorBytes, 200); NotifyViewport();
    }
    private void NotifyViewport()
    {
        foreach (var property in new[] { nameof(DisplayPreview), nameof(CanZoom), nameof(CanZoomIn), nameof(CanZoomOut), nameof(HasPreview), nameof(ShowUnavailable) }) OnPropertyChanged(property);
        ZoomInCommand.NotifyCanExecuteChanged(); ZoomOutCommand.NotifyCanExecuteChanged(); FitCommand.NotifyCanExecuteChanged();
    }
    partial void OnPreviewModeChanged(PreviewMode value)
    {
        foreach (var property in new[] { nameof(IsResult), nameof(ShowOriginal), nameof(ShowComparison), nameof(ShowSingle), nameof(ShowCompare), nameof(DisplayPreview) }) OnPropertyChanged(property);
        NotifyViewport();
    }
    partial void OnOriginalPreviewChanged(BitmapSource? value) => NotifyViewport();
    partial void OnProcessedPreviewChanged(BitmapSource? value) => NotifyViewport();
    partial void OnZoomChanged(double value)
    {
        if (value < .5 || value > 8) { Zoom = Math.Clamp(value, .5, 8); return; }
        if (value <= 1) { PanX = 0; PanY = 0; }
        OnPropertyChanged(nameof(ZoomLabel)); OnPropertyChanged(nameof(IsZoomed)); NotifyViewport();
        if (CanZoom) _ = RefreshPreviewAsync(250);
    }
    partial void OnIsCroppingChanged(bool value)
    {
        if (value) _beforeCrop = PreviewMode;
        else { PreviewMode = _cancelCrop ? _beforeCrop : PreviewMode.Result; _cancelCrop = false; }
        foreach (var property in new[] { nameof(ShowSingle), nameof(ShowCompare), nameof(CanSwitchPreview), nameof(CropActionText), nameof(CropToolbarText) }) OnPropertyChanged(property);
        NotifyWorkspaceCommands(); NotifyViewport();
        if (!value) _ = RefreshPreviewAsync();
    }
    partial void OnColorModeChanged(OutputColorMode value)
    {
        OnPropertyChanged(nameof(IsColor)); OnPropertyChanged(nameof(IsGrayscale)); OnPropertyChanged(nameof(IsBinary)); _ = RefreshPreviewAsync();
    }
    partial void OnBinaryStrengthChanged(double value) => _ = RefreshPreviewAsync(100);
    partial void OnLoadingTextChanged(string value) => OnPropertyChanged(nameof(PreviewBusyText));
}
