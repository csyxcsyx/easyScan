using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyScan.Core;
using EasyScan.Desktop.Services;
using OpenCvSharp;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;

namespace EasyScan.Desktop.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    public ImageLoader Loader { get; } = new();
    public DocumentProcessor Processor { get; } = new();
    public ObservableCollection<PageViewModel> Pages { get; } = [];
    private CancellationTokenSource? _importCancellation;
    private bool _disposed;
    private readonly HashSet<PageViewModel> _initializing = [];
    public PreviewCache PreviewCache { get; }

    [ObservableProperty] private PageViewModel? selectedPage;
    [ObservableProperty] private BitmapSource? originalPreview;
    [ObservableProperty] private BitmapSource? processedPreview;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isRendering;
    [ObservableProperty] private bool isCropping;
    [ObservableProperty] private string status = "准备就绪";
    [ObservableProperty] private int completedCount;
    [ObservableProperty] private double zoom = 1;
    [ObservableProperty] private OutputColorMode colorMode;
    [ObservableProperty] private double binaryStrength = 50;

    public byte[]? CurrentColorBytes { get; private set; }
    public bool HasPages => Pages.Count > 0;
    public bool CanEdit => SelectedPage?.IsReady == true;
    public bool HasPreview => ProcessedPreview is not null;
    public bool ShowUnavailable => HasPages && !HasPreview && !IsBusy && !IsRendering && !IsDetecting;
    public bool CanSwitchPreview => CanEdit && !IsCropping && !IsDetecting;
    public bool IsZoomed => Zoom > 1;
    public string CropActionText => IsCropping ? "完成裁剪" : "调整裁剪";
    public string CropToolbarText => IsCropping ? "完成" : "裁剪";
    public string QueueSummary => $"{Pages.Count} 页";
    public bool IsColor { get => ColorMode == OutputColorMode.Color; set { if (value) ColorMode = OutputColorMode.Color; } }
    public bool IsGrayscale { get => ColorMode == OutputColorMode.Grayscale; set { if (value) ColorMode = OutputColorMode.Grayscale; } }
    public bool IsBinary { get => ColorMode == OutputColorMode.Binary; set { if (value) ColorMode = OutputColorMode.Binary; } }

    public MainViewModel()
    {
        PreviewCache = new(Loader, Processor);
        Ocr = new(Loader, Processor);
        Pages.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasPages)); OnPropertyChanged(nameof(QueueSummary));
            OnPropertyChanged(nameof(ShowUnavailable));
            ExportCommand.NotifyCanExecuteChanged();
            NotifyWorkspaceCommands();
        };
    }

    public event EventHandler? ImportRequested;
    public event EventHandler? ExportRequested;
    public bool CanImport => !IsBusy;
    [RelayCommand(CanExecute = nameof(CanImport))] private void Import() => ImportRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand(CanExecute = nameof(CanExport))] private void Export() => ExportRequested?.Invoke(this, EventArgs.Empty);

    partial void OnSelectedPageChanged(PageViewModel? oldValue, PageViewModel? newValue)
    {
        _detectionCancellation?.Cancel();
        if (IsCropping) _cancelCrop = true;
        if (oldValue is not null) { oldValue.EndCropSession(false); oldValue.SettingsChanged -= OnSettingsChanged; oldValue.PropertyChanged -= OnPageStateChanged; }
        if (newValue is not null) { newValue.SettingsChanged += OnSettingsChanged; newValue.PropertyChanged += OnPageStateChanged; }
        IsCropping = false;
        Zoom = 1; PanX = 0; PanY = 0;
        OriginalPreview = null; ProcessedPreview = null; CurrentColorBytes = null;
        NotifySelection();
        _ = RefreshPreviewAsync();
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        NotifySelection();
        if (!IsCropping) _ = RefreshPreviewAsync(100);
    }

    private void NotifySelection()
    {
        NotifyOcrCommands();
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanSwitchPreview));
        UndoCommand.NotifyCanExecuteChanged(); RedoCommand.NotifyCanExecuteChanged();
        RotateLeftCommand.NotifyCanExecuteChanged(); RotateRightCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged(); ToggleCropCommand.NotifyCanExecuteChanged();
        NotifyWorkspaceCommands();
    }
    private bool CanUndo() => !IsCropping && !IsDetecting && !IsBusy && SelectedPage?.CanUndo == true;
    private bool CanRedo() => !IsCropping && !IsDetecting && !IsBusy && SelectedPage?.CanRedo == true;
    [RelayCommand(CanExecute = nameof(CanUndo))] private void Undo() => SelectedPage?.Undo();
    [RelayCommand(CanExecute = nameof(CanRedo))] private void Redo() => SelectedPage?.Redo();
    [RelayCommand(CanExecute = nameof(CanAdjust))] private void Reset() => SelectedPage?.Reset();
    [RelayCommand(CanExecute = nameof(CanAdjust))] private void RotateLeft() => Rotate(-1);
    [RelayCommand(CanExecute = nameof(CanAdjust))] private void RotateRight() => Rotate(1);
    private void Rotate(int turns) { if (SelectedPage is { } p) p.Edit(p.Settings with { QuarterTurns = p.Settings.QuarterTurns + turns }, "rotate"); }
    [RelayCommand(CanExecute = nameof(CanCrop))] private void ToggleCrop()
    {
        if (IsCropping) SelectedPage?.EndCropSession(true);
        else
        {
            SelectedPage?.BeginCropSession(); SelectedCropHandle = 0;
            ShowCurveHandles = SelectedPage?.CurvedEdges is not null;
        }
        IsCropping = !IsCropping;
    }
    [RelayCommand(CanExecute = nameof(CanZoomIn))] private void ZoomIn() => Zoom = Math.Clamp(Zoom * 1.15, .5, 8);
    [RelayCommand(CanExecute = nameof(CanZoomOut))] private void ZoomOut() => Zoom = Math.Clamp(Zoom / 1.15, .5, 8);
    [RelayCommand(CanExecute = nameof(CanZoom))] private void Fit() { Zoom = 1; PanX = 0; PanY = 0; }
    [RelayCommand] private void Cancel() => _importCancellation?.Cancel();

    [RelayCommand(CanExecute = nameof(CanRemove))] private void RemoveSelected()
    {
        if (IsBusy) return;
        var remove = Pages.Where(x => x.IsSelected).ToArray();
        if (remove.Length == 0 && SelectedPage is { } current) remove = [current];
        var index = SelectedPage is null ? 0 : Pages.IndexOf(SelectedPage);
        foreach (var page in remove) { Pages.Remove(page); PreviewCache.Remove(page.Source); }
        if (SelectedPage is null || !Pages.Contains(SelectedPage))
            SelectedPage = Pages.Count == 0 ? null : Pages[Math.Clamp(index, 0, Pages.Count - 1)];
        Status = $"已移除 {remove.Length} 页";
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))] private void MoveUp() => Move(-1);
    [RelayCommand(CanExecute = nameof(CanMoveDown))] private void MoveDown() => Move(1);
    private void Move(int delta)
    {
        if (IsBusy || SelectedPage is null) return;
        var index = Pages.IndexOf(SelectedPage);
        if (index + delta >= 0 && index + delta < Pages.Count) Pages.Move(index, index + delta);
        NotifyWorkspaceCommands();
    }

    [RelayCommand(CanExecute = nameof(CanAdjust))] private void ApplyToAll()
    {
        if (SelectedPage is not { IsReady: true } source) return;
        foreach (var page in Pages.Where(x => x.IsReady && x != source))
            page.Edit(page.Settings with { Preset = source.Settings.Preset, ShadowRemoval = source.Settings.ShadowRemoval,
                Brightness = source.Settings.Brightness, Contrast = source.Settings.Contrast, AutoExposure = source.Settings.AutoExposure,
                CorrectColorCast = source.Settings.CorrectColorCast, HighlightRecovery = source.Settings.HighlightRecovery, ColorBoost = source.Settings.ColorBoost,
                NoiseReduction = source.Settings.NoiseReduction, DetailEnhancement = source.Settings.DetailEnhancement }, "batch");
        Status = "已将增强设置应用到全部页面";
    }

    [RelayCommand(CanExecute = nameof(CanRetry))] private async Task RetryAsync()
    {
        if (IsBusy) return;
        using var cancellation = new CancellationTokenSource();
        _importCancellation = cancellation; IsBusy = true;
        try { foreach (var page in Pages.Where(x => !x.IsReady).ToArray()) await InitializePageAsync(page, cancellation.Token); }
        catch (OperationCanceledException) { Status = "已取消，可重试未完成页面"; }
        finally { IsBusy = false; _importCancellation = null; }
    }

    public async Task ImportPathsAsync(IEnumerable<string> paths)
    {
        if (IsBusy) { Status = "请等待当前导入完成"; return; }
        using var cancellation = new CancellationTokenSource();
        _importCancellation = cancellation; IsBusy = true; CompletedCount = 0;
        try
        {
            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (Pages.Any(x => x.Source.Document.Path.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))) continue;
                IReadOnlyList<PageSource> sources;
                try { sources = await Loader.DescribeAsync(path, cancellation.Token); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Pages.Add(new PageViewModel(new(new(Guid.NewGuid(), path, Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)), 0))
                    { State = "失败", Error = ExportService.FriendlyError(ex) });
                    continue;
                }
                var added = sources.Select(x => new PageViewModel(x)).ToArray();
                foreach (var page in added) Pages.Add(page);
                foreach (var page in added) await InitializePageAsync(page, cancellation.Token);
            }
            Status = ReadySummary + (Pages.Any(x => x.State == "待确认") ? $" · {Pages.Count(x => x.State == "待确认")} 页待确认" : "")
                + (Pages.Any(x => x.State == "失败") ? "，部分文件需要重试" : "");
        }
        catch (OperationCanceledException)
        {
            foreach (var page in Pages.Where(x => x.State is "排队中" or "处理中")) page.State = "已取消";
            Status = "已取消，可重试未完成页面";
        }
        finally { IsBusy = false; _importCancellation = null; }
    }

    private async Task InitializePageAsync(PageViewModel page, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _initializing.Add(page);
        page.State = "处理中"; Status = $"正在处理 {page.Name}";
        if (SelectedPage is null) SelectedPage = page;
        try
        {
            using var image = await Loader.LoadAsync(page.Source, 2400, cancellationToken: token);
            var originalBytes = await Task.Run(() => image.ToBytes(".png"), token);
            page.Thumbnail = BitmapHelper.FromBytes(originalBytes, 200);
            if (SelectedPage == page) { OriginalPreview = BitmapHelper.FromBytes(originalBytes); LoadingText = "正在识别与增强…"; }
            var detection = await Task.Run(() => Processor.DetectCrop(image, token), token);
            page.Initialize(detection, page.Source.Width > 0 ? page.Source.Width : image.Width,
                page.Source.Height > 0 ? page.Source.Height : image.Height);
            using (var paper = new PageRectifier().Rectify(image, page.Settings, token))
                page.Quality = await Task.Run(() => Processor.Analyze(paper), token);
            await PreviewCache.SeedAsync(new(page.Source, page.Settings), image, token);
            var preview = await PreviewCache.GetAsync(new(page.Source, page.Settings), OutputColorMode.Color, 0, token);
            page.Thumbnail = BitmapHelper.FromBytes(preview.ColorBytes, 200);
            _initializing.Remove(page);
            if (SelectedPage == page) { NotifySelection(); await RefreshPreviewAsync(); }
            CompletedCount++;
            NotifyWorkspaceCommands();
        }
        catch (OperationCanceledException) { page.State = "已取消"; throw; }
        catch (Exception ex) { page.State = "失败"; page.Error = ExportService.FriendlyError(ex); }
        finally { _initializing.Remove(page); if (SelectedPage == page) _ = RefreshPreviewAsync(); }
    }

    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanImport)); OnPropertyChanged(nameof(ShowUnavailable)); ImportCommand.NotifyCanExecuteChanged(); NotifyWorkspaceCommands(); }
    partial void OnIsRenderingChanged(bool value) { OnPropertyChanged(nameof(ShowUnavailable)); NotifyWorkspaceCommands(); }
    public void Dispose()
    {
        _disposed = true;
        _importCancellation?.Cancel(); _previewCancellation?.Cancel(); _detectionCancellation?.Cancel();
        _ocrCancellation?.Cancel(); Ocr.Dispose(); PreviewCache.Dispose();
    }
}
