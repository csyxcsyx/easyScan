using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyScan.Core;

namespace EasyScan.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private CancellationTokenSource? _detectionCancellation;
    [ObservableProperty] private bool isDetecting;
    [ObservableProperty] private bool showCurveHandles;
    [ObservableProperty] private int selectedCropHandle;
    public string[] CropHandleNames { get; } = ["左上角", "右上角", "右下角", "左下角", "上边", "右边", "下边", "左边"];
    public bool CanAdjust => CanEdit && !IsCropping && !IsDetecting && !IsBusy;
    public bool CanDetect => CanEdit && !IsDetecting && !IsBusy;
    public bool CanCrop => CanEdit && !IsDetecting && !IsBusy;
    public bool CanExport => HasPages && !IsBusy && !IsCropping && !IsDetecting && !IsRendering;
    public bool CanRetry => !IsBusy && !IsDetecting && Pages.Any(x => !x.IsReady);
    public bool CanRemove => HasPages && !IsBusy && !IsDetecting;
    public bool HasSelection => SelectedPage is not null;
    public bool CanMoveUp => CanRemove && SelectedPage is { } page && Pages.IndexOf(page) > 0;
    public bool CanMoveDown => CanRemove && SelectedPage is { } page && Pages.IndexOf(page) < Pages.Count - 1;
    public string ReadySummary => $"{Pages.Count(x => x.IsReady)} / {Pages.Count} 页可导出";

    [RelayCommand(CanExecute = nameof(CanDetect))]
    private async Task AutoDetectAsync()
    {
        var page = SelectedPage!;
        using var cancellation = new CancellationTokenSource(); _detectionCancellation = cancellation;
        IsDetecting = true; Status = "正在识别纸张…";
        try
        {
            using var image = await Loader.LoadAsync(page.Source, 2400, cancellationToken: cancellation.Token);
            var detection = await Task.Run(() => Processor.DetectCrop(image, cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            page.ApplyDetection(detection);
            using var cropped = new PageRectifier().Rectify(image, page.Settings, cancellation.Token);
            page.Quality = await Task.Run(() => Processor.Analyze(cropped), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (SelectedPage == page) ShowCurveHandles = detection.CurvedEdges is not null;
            Status = detection.Confidence < .65 ? "边界不明确，可手动调整" : "已重新识别纸张";
        }
        catch (OperationCanceledException) { if (!_disposed && SelectedPage == page) Status = "已取消纸张识别"; }
        catch (Exception ex) { Status = "识别失败：" + ExportService.FriendlyError(ex); }
        finally { _detectionCancellation = null; IsDetecting = false; }
    }
    [RelayCommand(CanExecute = nameof(CanCrop))] private void FullFrame()
    {
        if (SelectedPage is not { } page) return;
        page.SetBoundary(CropQuad.FullFrame, null); page.EdgeInset = 0;
    }
    [RelayCommand] private void CancelCrop()
    {
        if (!IsCropping) return;
        _cancelCrop = true;
        _detectionCancellation?.Cancel();
        SelectedPage?.EndCropSession(false); IsCropping = false; Status = "已取消本次裁剪调整";
    }
    [RelayCommand] private void StraightenEdges()
    {
        if (SelectedPage is { } page) page.SetBoundary(page.Crop, null);
    }
    partial void OnSelectedCropHandleChanged(int value)
    {
        if (value < 0 || value > 7) { SelectedCropHandle = Math.Clamp(value, 0, 7); return; }
        if (value >= 4) ShowCurveHandles = true;
    }
    partial void OnShowCurveHandlesChanged(bool value)
    {
        if (!value && SelectedCropHandle >= 4) SelectedCropHandle = 0;
    }
    partial void OnIsDetectingChanged(bool value) => NotifyWorkspaceCommands();

    private void NotifyWorkspaceCommands()
    {
        NotifyOcrCommands();
        OnPropertyChanged(nameof(IsPreviewBusy)); OnPropertyChanged(nameof(PreviewBusyText)); NotifyViewport();
        foreach (var property in new[] { nameof(CanAdjust), nameof(CanDetect), nameof(CanCrop), nameof(CanSwitchPreview), nameof(CanExport), nameof(CanRetry), nameof(CanRemove),
            nameof(CanMoveUp), nameof(CanMoveDown), nameof(HasSelection), nameof(ReadySummary) }) OnPropertyChanged(property);
        AutoDetectCommand.NotifyCanExecuteChanged(); ExportCommand.NotifyCanExecuteChanged(); RetryCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged(); MoveUpCommand.NotifyCanExecuteChanged(); MoveDownCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged(); RedoCommand.NotifyCanExecuteChanged();
        ToggleCropCommand.NotifyCanExecuteChanged(); FullFrameCommand.NotifyCanExecuteChanged(); ResetCommand.NotifyCanExecuteChanged();
        RotateLeftCommand.NotifyCanExecuteChanged(); RotateRightCommand.NotifyCanExecuteChanged(); ApplyToAllCommand.NotifyCanExecuteChanged();
    }
}
