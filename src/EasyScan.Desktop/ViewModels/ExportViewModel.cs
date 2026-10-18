using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyScan.Core;
using EasyScan.Desktop.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;

namespace EasyScan.Desktop.ViewModels;

public sealed record Choice<T>(string Label, T Value);

public sealed partial class ExportViewModel : ObservableObject, IDisposable
{
    private sealed record PreviewSheet(string Name, bool IsPdf, (ExportPage Page, PaperPlacement Placement)[] Items);
    private readonly ImageLoader _loader;
    private readonly DocumentProcessor _processor;
    private readonly OcrService? _ocr;
    private readonly ExportPage[] _allPages, _selectedPages;
    private readonly IPreviewProvider _previews;
    private readonly PreviewCache? _ownedCache;
    private readonly PageSource? _preferred;
    private readonly SemaphoreSlim _previewGate = new(1, 1);
    private List<PreviewSheet> _sheets = [];
    private bool _dirty = true, _firstPreview = true, _disposed, _settingIndex;
    private Choice<ExportFormat> _lastImage;
    private CancellationTokenSource? _exportCancellation, _previewCancellation;
    public ExportResult? Result { get; private set; }
    public ObservableCollection<ExportFailure> Failures { get; } = [];
    public Choice<ExportFormat>[] Formats { get; } = [new("自动", ExportFormat.Automatic), new("JPG", ExportFormat.Jpeg), new("PNG", ExportFormat.Png), new("PDF", ExportFormat.Pdf)];
    public Choice<ExportFormat>[] ImageFormats => [Formats[1], Formats[2]];
    public Choice<ExportOrganization>[] Organizations { get; } = [new("按原文件分别导出", ExportOrganization.SeparateSources), new("合并为一个 PDF", ExportOrganization.CombinedPdf)];
    public Choice<bool>[] Scopes { get; }
    [ObservableProperty] private Choice<ExportFormat> format;
    [ObservableProperty] private Choice<ExportOrganization> organization;
    [ObservableProperty] private Choice<bool> scope;
    [ObservableProperty] private string folder;
    [ObservableProperty] private OutputColorMode colorMode;
    [ObservableProperty] private double binaryStrength;
    [ObservableProperty] private double jpegQuality = 95;
    [ObservableProperty] private BitmapSource? preview;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private int completed;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private bool isPreviewLoading;
    [ObservableProperty] private string previewError = "";
    [ObservableProperty] private int previewIndex;
    [ObservableProperty] private int previewCount;

    public bool CanConfigure => !IsBusy;
    public bool IsAutomatic { get => Format.Value == ExportFormat.Automatic; set { if (value) Format = Formats[0]; } }
    public bool IsImage { get => Format.Value is ExportFormat.Jpeg or ExportFormat.Png; set { if (value) Format = _lastImage; } }
    public bool IsPdfOutput { get => Format.Value == ExportFormat.Pdf; set { if (value) Format = Formats[3]; } }
    public Choice<ExportFormat> ImageFormat { get => IsImage ? Format : _lastImage; set { _lastImage = value; if (IsImage) Format = value; } }
    public bool CanChooseFormat => CanConfigure;
    public bool ShowOrganization => !IsImage;
    public bool IsPdf => IsPdfOutput || (IsAutomatic && ActivePages.Any(x => x.Source.Document.IsPdf));
    public bool ShowQuality => !IsPdfOutput && (IsImage && Format.Value == ExportFormat.Jpeg || IsAutomatic && ActivePages.Any(x => !x.Source.Document.IsPdf));
    public bool IsColor { get => ColorMode == OutputColorMode.Color; set { if (value) ColorMode = OutputColorMode.Color; } }
    public bool IsGrayscale { get => ColorMode == OutputColorMode.Grayscale; set { if (value) ColorMode = OutputColorMode.Grayscale; } }
    public bool IsBinary { get => ColorMode == OutputColorMode.Binary; set { if (value) ColorMode = OutputColorMode.Binary; } }
    public string ColorLabel => ColorMode switch { OutputColorMode.Color => "彩色", OutputColorMode.Grayscale => "灰度", _ => "黑白" };
    public int PageCount => ActivePages.Length;
    public bool HasFailures => Failures.Count > 0;
    public bool HasPreviewError => !string.IsNullOrEmpty(PreviewError);
    public string PreviewLabel => _sheets.Count == 0 ? "导出预览" : $"{_sheets[Math.Clamp(PreviewIndex, 0, _sheets.Count - 1)].Name} · {PreviewIndex + 1}/{PreviewCount}";
    public bool CanPreviousPreview => CanConfigure && !IsPreviewLoading && PreviewIndex > 0;
    public bool CanNextPreview => CanConfigure && !IsPreviewLoading && PreviewIndex + 1 < PreviewCount;
    private ExportPage[] ActivePages => Scope.Value ? _selectedPages : _allPages;
    private bool CanExport() => !IsBusy && !IsPreviewLoading && !HasPreviewError && !HasWatermarkError && PageCount > 0 && !string.IsNullOrWhiteSpace(Folder);

    public ExportViewModel(ImageLoader loader, DocumentProcessor processor, ExportPage[] allPages, ExportPage[] selectedPages,
        byte[]? basePreview, OutputColorMode colorMode, double binaryStrength, IPreviewProvider? previews = null, PageSource? preferred = null, OcrService? ocr = null)
    {
        _loader = loader; _processor = processor; _allPages = allPages; _selectedPages = selectedPages; _preferred = preferred;
        _ocr = ocr;
        _previews = previews ?? (_ownedCache = new(loader, processor));
        format = Formats[0]; _lastImage = Formats[1]; organization = Organizations[0];
        Scopes = [new($"全部页面（{allPages.Length} 页）", false), new($"选中页面（{selectedPages.Length} 页）", true)]; scope = Scopes[0];
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        folder = Path.Combine(string.IsNullOrEmpty(pictures) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : pictures, "easyScan");
        this.colorMode = colorMode; this.binaryStrength = binaryStrength;
        Failures.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasFailures));
        _ = UpdatePreviewAsync();
    }

    private void RequestPreview(bool replan = false) { _dirty |= replan; _ = UpdatePreviewAsync(); }
    public async Task UpdatePreviewAsync()
    {
        _previewCancellation?.Cancel();
        var cancellation = new CancellationTokenSource(); _previewCancellation = cancellation; var token = cancellation.Token;
        var acquired = false; IsPreviewLoading = true; PreviewError = "";
        try
        {
            await _previewGate.WaitAsync(token); acquired = true;
            var settings = CreateSettings(Folder);
            if (HasWatermarkError) settings = settings with { Watermark = new() };
            if (_dirty)
            {
                var sheets = new List<PreviewSheet>();
                var groups = Organization.Value == ExportOrganization.CombinedPdf ? [ActivePages] : ActivePages.GroupBy(x => x.Source.Document.Id).Select(g => g.ToArray()).ToArray();
                foreach (var group in groups)
                {
                    var pdf = settings.Organization == ExportOrganization.CombinedPdf || settings.Format == ExportFormat.Pdf || settings.Format == ExportFormat.Automatic && group[0].Source.Document.IsPdf;
                    if (!pdf)
                    {
                        sheets.AddRange(group.Select(p => new PreviewSheet(p.Source.DisplayName, false, [(p, new(0, 0, 0, 0, 0))]))); continue;
                    }
                    var planner = new PdfLayoutPlanner(settings.PdfLayout); var items = new List<(ExportPage, PaperPlacement)>();
                    foreach (var page in group)
                    {
                        var size = await _previews.GetSizeAsync(page, token); items.Add((page, planner.PlaceNext(size.Width, size.Height)));
                    }
                    sheets.AddRange(items.GroupBy(x => x.Item2.SheetIndex).Select(g => new PreviewSheet(settings.Organization == ExportOrganization.CombinedPdf ? "合并文档" : group[0].Source.Document.Name, true, g.ToArray())));
                }
                token.ThrowIfCancellationRequested(); _sheets = sheets; _dirty = false;
                var index = Math.Clamp(PreviewIndex, 0, Math.Max(0, sheets.Count - 1));
                if (_firstPreview && _preferred is not null)
                {
                    var preferred = sheets.FindIndex(x => x.Items.Any(i => i.Page.Source.Document.Id == _preferred.Document.Id && i.Page.Source.PageIndex == _preferred.PageIndex));
                    if (preferred >= 0) index = preferred;
                }
                _firstPreview = false; SetIndex(index); PreviewCount = sheets.Count;
            }
            if (_sheets.Count == 0) { Preview = null; return; }
            var sheet = _sheets[Math.Clamp(PreviewIndex, 0, _sheets.Count - 1)];
            var images = new List<PaperPreviewImage>();
            foreach (var item in sheet.Items)
            {
                var frame = await _previews.GetAsync(item.Page, OutputColorMode.Color, 0, token);
                images.Add(new(frame.ColorBytes, item.Placement));
            }
            var bytes = await Task.Run(() => sheet.IsPdf ? new PaperPreviewRenderer(_processor).Render(images, settings, token)
                : new ExportPreviewRenderer(_processor).Render(images.Select(x => x.ColorBytes).ToArray(), settings, false, 0, token).Bytes, token);
            token.ThrowIfCancellationRequested(); if (!_disposed) Preview = BitmapHelper.FromBytes(bytes);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed) PreviewError = "预览失败：" + ExportService.FriendlyError(ex); }
        finally
        {
            if (acquired) _previewGate.Release();
            if (_previewCancellation == cancellation) { IsPreviewLoading = false; _previewCancellation = null; NotifyPreview(); }
            cancellation.Dispose();
        }
    }

    [RelayCommand(CanExecute = nameof(CanPreviousPreview))] private void PreviousPreview() => PreviewIndex--;
    [RelayCommand(CanExecute = nameof(CanNextPreview))] private void NextPreview() => PreviewIndex++;
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        using var cancellation = new CancellationTokenSource(); _exportCancellation = cancellation;
        IsBusy = true; Completed = 0; Failures.Clear(); Result = null; Status = "正在导出…";
        try
        {
            var settings = CreateSettings(Path.GetFullPath(Folder));
            var progress = new Progress<ExportProgress>(p =>
            {
                if (_exportCancellation != cancellation || cancellation.IsCancellationRequested || _disposed) return;
                Completed = p.Completed; Status = $"{p.Completed}/{p.Total} · {p.FileName}" + (p.Stage.Length > 0 ? " · " + p.Stage : "");
            });
            Result = await new ExportService(_loader, _processor, _ocr).ExportAsync(ActivePages, settings, progress, cancellation.Token);
            foreach (var failure in Result.Failures) Failures.Add(failure);
            Status = Result.Cancelled ? $"已取消，保留 {Result.Files.Count} 个已完成文件" : Result.Failures.Count > 0 ? $"已导出 {Result.Files.Count} 个文件，{Result.Failures.Count} 页失败" : $"已导出 {Result.Files.Count} 个文件";
        }
        catch (Exception ex) { var failure = new ExportFailure("保存位置", ExportService.FriendlyError(ex)); Failures.Add(failure); Result = new([], [failure], false); Status = "导出失败：" + failure.Message; }
        finally { _exportCancellation = null; IsBusy = false; }
    }
    [RelayCommand] private void Cancel() => _exportCancellation?.Cancel();
    private void NotifyPreview()
    {
        foreach (var property in new[] { nameof(PreviewLabel), nameof(CanPreviousPreview), nameof(CanNextPreview), nameof(HasPreviewError) }) OnPropertyChanged(property);
        PreviousPreviewCommand.NotifyCanExecuteChanged(); NextPreviewCommand.NotifyCanExecuteChanged(); ExportCommand.NotifyCanExecuteChanged();
    }
    partial void OnColorModeChanged(OutputColorMode value) { OnPropertyChanged(nameof(IsColor)); OnPropertyChanged(nameof(IsGrayscale)); OnPropertyChanged(nameof(IsBinary)); OnPropertyChanged(nameof(ColorLabel)); RequestPreview(); }
    partial void OnBinaryStrengthChanged(double value) => RequestPreview();
    private void SetIndex(int value) { _settingIndex = true; try { PreviewIndex = value; } finally { _settingIndex = false; } }
    partial void OnPreviewIndexChanged(int value) { PreviewZoom = 1; PreviewPanX = 0; PreviewPanY = 0; if (!_settingIndex) RequestPreview(); }
    partial void OnFormatChanged(Choice<ExportFormat> value)
    {
        if (value.Value is ExportFormat.Jpeg or ExportFormat.Png) _lastImage = value;
        if (value.Value != ExportFormat.Pdf && Organization.Value == ExportOrganization.CombinedPdf) Organization = Organizations[0];
        foreach (var property in new[] { nameof(IsAutomatic), nameof(IsImage), nameof(IsPdfOutput), nameof(ImageFormat), nameof(ShowQuality), nameof(ShowOrganization), nameof(IsPdf) }) OnPropertyChanged(property);
        RequestPreview(true);
    }
    partial void OnOrganizationChanged(Choice<ExportOrganization> value) { if (value.Value == ExportOrganization.CombinedPdf && !IsPdfOutput) Format = Formats[3]; RequestPreview(true); }
    partial void OnScopeChanged(Choice<bool> value) { OnPropertyChanged(nameof(PageCount)); OnPropertyChanged(nameof(IsPdf)); OnPropertyChanged(nameof(ShowQuality)); SetIndex(0); RequestPreview(true); }
    partial void OnFolderChanged(string value) => ExportCommand.NotifyCanExecuteChanged();
    partial void OnIsPreviewLoadingChanged(bool value) => NotifyPreview();
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanConfigure)); OnPropertyChanged(nameof(CanChooseFormat)); NotifyPreview(); }
    public void Dispose() { _disposed = true; _previewCancellation?.Cancel(); _exportCancellation?.Cancel(); _ownedCache?.Dispose(); }
    public event EventHandler? FolderRequested;
    [RelayCommand] private void ChooseFolder() => FolderRequested?.Invoke(this, EventArgs.Empty);
}
