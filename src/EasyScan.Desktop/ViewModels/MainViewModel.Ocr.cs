using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyScan.Core;

namespace EasyScan.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    public OcrService Ocr { get; private set; } = null!;
    private CancellationTokenSource? _ocrCancellation;
    [ObservableProperty] private bool isOcrBusy;
    [ObservableProperty] private string ocrProgressText = "";
    public bool CanRecognizeAll => !IsOcrBusy && !IsBusy && !IsCropping && !IsDetecting && Pages.Any(x => x.IsReady);
    public bool CanRecognize => CanRecognizeAll && SelectedPage?.IsReady == true;
    public bool CanViewOcr => SelectedPage?.HasCurrentOcr == true;
    public event Action<PageViewModel>? OcrResultRequested;

    [RelayCommand(CanExecute = nameof(CanRecognize))]
    private Task RecognizeCurrentAsync() => RecognizePagesAsync([SelectedPage!]);
    [RelayCommand(CanExecute = nameof(CanRecognize))]
    private Task RecognizeSelectedAsync()
    {
        var selected = Pages.Where(x => x.IsReady && x.IsSelected).ToArray();
        return RecognizePagesAsync(selected.Length == 0 ? [SelectedPage!] : selected);
    }
    [RelayCommand(CanExecute = nameof(CanRecognizeAll))]
    private Task RecognizeAllAsync() => RecognizePagesAsync(Pages.Where(x => x.IsReady).ToArray());
    [RelayCommand(CanExecute = nameof(IsOcrBusy))] private void CancelOcr() => _ocrCancellation?.Cancel();
    [RelayCommand(CanExecute = nameof(CanViewOcr))] private void ViewOcr() { if (SelectedPage is { } page) OcrResultRequested?.Invoke(page); }

    public async Task RecognizePagesAsync(IReadOnlyList<PageViewModel> pages)
    {
        if (IsOcrBusy || _disposed) return;
        using var cancellation = new CancellationTokenSource(); _ocrCancellation = cancellation;
        IsOcrBusy = true; var done = 0; var failed = 0;
        try
        {
            foreach (var page in pages)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (!Pages.Contains(page) || !page.IsReady) continue;
                var settings = page.Settings; page.OcrState = "正在识别"; page.OcrError = "";
                try
                {
                    var file = new System.IO.FileInfo(page.Source.Document.Path);
                    var sourceVersion = (file.Length, file.LastWriteTimeUtc.Ticks);
                    var progress = new Progress<OcrProgress>(p =>
                    {
                        if (_disposed || _ocrCancellation != cancellation || cancellation.IsCancellationRequested) return;
                        OcrProgressText = $"{done + 1}/{pages.Count} · {p.Stage}" + (p.Total > 0 ? $" {p.Completed}/{p.Total}" : "");
                    });
                    var result = await Ocr.RecognizeAsync(new(page.Source, settings), progress, cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (Pages.Contains(page)) page.SetOcrResult(result, settings, sourceVersion);
                }
                catch (OperationCanceledException) { page.OcrState = "已取消，可重试"; throw; }
                catch (Exception ex) { failed++; page.OcrState = "识别失败，可重试"; page.OcrError = ExportService.FriendlyError(ex); }
                done++; NotifyOcrCommands();
            }
            OcrProgressText = $"文字识别完成 · {done - failed} 页成功" + (failed > 0 ? $" · {failed} 页失败" : "");
        }
        catch (OperationCanceledException) { OcrProgressText = "已取消文字识别，已完成结果保留"; }
        finally { _ocrCancellation = null; IsOcrBusy = false; NotifyOcrCommands(); }
    }
    private void NotifyOcrCommands()
    {
        foreach (var name in new[] { nameof(CanRecognize), nameof(CanRecognizeAll), nameof(CanViewOcr) }) OnPropertyChanged(name);
        RecognizeCurrentCommand.NotifyCanExecuteChanged(); RecognizeSelectedCommand.NotifyCanExecuteChanged();
        RecognizeAllCommand.NotifyCanExecuteChanged(); ViewOcrCommand.NotifyCanExecuteChanged(); CancelOcrCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsOcrBusyChanged(bool value) => NotifyOcrCommands();
}
