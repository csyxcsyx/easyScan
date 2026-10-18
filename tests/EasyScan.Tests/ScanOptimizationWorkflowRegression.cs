using EasyScan.Core;
using EasyScan.Desktop;
using EasyScan.Desktop.Services;
using EasyScan.Desktop.ViewModels;
using EasyScan.Desktop.Views;
using OpenCvSharp;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Window = System.Windows.Window;

namespace EasyScan.Tests;

internal static class ScanOptimizationWorkflowRegression
{
    public static async Task RunAsync(string folder, string input, OcrPageResult fixture, ImageLoader loader, DocumentProcessor processor,
        IOcrEngine native, Mat clean, Action<bool, string> check, Action<Window, string, int, int, int> render)
    {
        using (var cancellation = new CancellationTokenSource())
        {
            var canceled = false;
            try
            {
                await native.RecognizeAsync(clean, new InlineProgress(p =>
                { if (p.Stage == "正在检测文字…") cancellation.CancelAfter(20); }), cancellation.Token);
            }
            catch (OperationCanceledException) { canceled = true; }
            check(canceled, "in-flight OCR cancels without publishing a partial result");
            check((await native.RecognizeAsync(clean)).Text.Contains("扫描清晰度测试"), "native OCR remains usable after cancellation");
        }
        var corrupt = Path.Combine(folder, "corrupt-models"); Directory.CreateDirectory(corrupt);
        File.WriteAllText(Path.Combine(corrupt, "det.onnx"), "corrupt");
        using (var engine = new OnnxOcrEngine(corrupt))
        {
            var rejected = false;
            try { await engine.RecognizeAsync(clean); } catch (InvalidDataException ex) { rejected = ex.Message.Contains("校验失败"); }
            check(rejected, "corrupt OCR assets fail hash validation before native loading");
        }
        var copy = Path.Combine(folder, "源文件版本.png"); File.Copy(input, copy, true);
        var page = new ExportPage((await loader.DescribeAsync(copy))[0], new());
        var fake = new TestEngine(fixture);
        using (var service = new OcrService(loader, processor, fake))
        {
            await service.RecognizeAsync(page);
            File.SetLastWriteTimeUtc(copy, File.GetLastWriteTimeUtc(copy).AddSeconds(3));
            await service.RecognizeAsync(page);
            check(fake.Calls == 2, "source version changes invalidate OCR cache");
        }
        var gated = new TestEngine(fixture, pause: true);
        using (var service = new OcrService(loader, processor, gated))
        {
            var pending = service.RecognizeAsync(page);
            await gated.Started.Task;
            File.SetLastWriteTimeUtc(copy, File.GetLastWriteTimeUtc(copy).AddSeconds(3)); gated.Release.TrySetResult(true);
            var rejected = false;
            try { await pending; } catch (IOException ex) { rejected = ex.Message.Contains("原文件发生变化"); }
            check(rejected, "changing a file during OCR rejects the obsolete result");
        }
        using (var service = new OcrService(loader, processor, new TestEngine(fixture, failAt: 2)))
        {
            var other = new ExportPage((await loader.DescribeAsync(input))[0], new());
            var result = await new ExportService(loader, processor, service).ExportAsync([page, other],
                new(Path.Combine(folder, "部分OCR失败"), ExportFormat.Pdf, ExportOrganization.CombinedPdf, Ocr: new(true, true)));
            check(result.Files.Count == 0 && result.Failures.Count == 1, "merged PDF and TXT stay unsaved when a later page OCR fails");
        }
        using (var cache = new PreviewCache(loader, processor, Path.Combine(folder, "tiny-budget"), 1024, 1024))
        {
            var frame = await cache.GetAsync(page, OutputColorMode.Color, 0, maxDimension: 3200);
            check(frame.Final.PixelWidth > 0 && cache.MemoryBytes <= 1024 && cache.DiskBytes <= 1024, "a large current preview cannot exceed configured cache budgets");
            check((await cache.GetAsync(page, OutputColorMode.Grayscale, 0, maxDimension: 3200)).Final.PixelWidth > 0,
                "evicting an oversize current entry still allows later preview generation");
        }

        var errors = new BindingErrors(); System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            using var vm = new MainViewModel(); await vm.ImportPathsAsync([input, copy]);
            check(vm.Pages.All(p => p.OcrState == "未识别"), "import does not automatically load models or run OCR");
            var pending = vm.RecognizeCurrentCommand.ExecuteAsync(null);
            check(vm.IsOcrBusy && !vm.RecognizeAllCommand.CanExecute(null) && vm.CancelOcrCommand.CanExecute(null),
                "OCR exposes cancellable busy state without starting duplicate batches");
            await pending;
            check(vm.SelectedPage!.HasCurrentOcr && vm.CanViewOcr && vm.SelectedPage.IsReady, "current-page OCR enables text viewing while preserving scan readiness");
            pending = vm.RecognizeAllCommand.ExecuteAsync(null); vm.CancelOcrCommand.Execute(null); await pending;
            check(!vm.IsOcrBusy && vm.OcrProgressText.Contains("已取消") && vm.Pages[0].HasCurrentOcr, "batch cancel preserves previously completed OCR results");
            vm.Pages[1].IsSelected = true; await vm.RecognizeSelectedCommand.ExecuteAsync(null);
            check(vm.Pages[1].HasCurrentOcr, "selected-page OCR retries and populates its own result");
            var window = new MainWindow(vm);
            render(window, "扫描优化-工作台.png", 1280, 760, 96);
            var tools = (DependencyObject)window.FindName("ToolsPanel");
            Find<ScrollViewer>(tools)!.ScrollToEnd();
            render(window, "扫描优化-OCR面板.png", 1280, 760, 96);
            render(window, "扫描优化-OCR面板-窄窗口.png", 900, 600, 144);
            render(new OcrResultWindow(vm.Pages[1]), "OCR-文字结果-200%.png", 760, 580, 192);
            using var export = new ExportViewModel(loader, processor, [page], [page], null, OutputColorMode.Color, 50, ocr: vm.Ocr);
            export.IsPdfOutput = true; export.ExportOcrText = true; export.SearchablePdf = true; await export.UpdatePreviewAsync();
            var dialog = new ExportDialog(export); render(dialog, "扫描优化-OCR导出.png", 1040, 700, 96);
            Find<ScrollViewer>(Find<ExportSettingsPanel>((DependencyObject)dialog.Content)!)!.ScrollToEnd();
            render(dialog, "扫描优化-OCR导出设置.png", 1040, 700, 96);
            check(export.ExportCommand.CanExecute(null), "OCR export options remain configurable without starting recognition during preview");
            check(errors.Errors.Count == 0, "new OCR views have no WPF binding errors: " + string.Join(" | ", errors.Errors));
        }
        finally { System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Remove(errors); }
    }
    private sealed class InlineProgress(Action<OcrProgress> action) : IProgress<OcrProgress> { public void Report(OcrProgress value) => action(value); }
    private sealed class TestEngine(OcrPageResult fixture, bool pause = false, int failAt = 0) : IOcrEngine
    {
        public int Calls;
        public string Version => "test-1";
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<OcrPageResult> RecognizeAsync(Mat image, IProgress<OcrProgress>? progress = null, CancellationToken token = default)
        {
            Calls++; if (Calls == failAt) throw new InvalidOperationException("模拟识别失败");
            Started.TrySetResult(true); if (pause) await Release.Task.WaitAsync(token);
            return fixture with { Width = image.Width, Height = image.Height };
        }
        public void Dispose() { }
    }
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
    private static T? Find<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) if (Find<T>(VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
        return null;
    }
}
