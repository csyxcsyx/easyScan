using EasyScan.Core;
using EasyScan.Desktop;
using EasyScan.Desktop.Controls;
using EasyScan.Desktop.ViewModels;
using EasyScan.Desktop.Views;
using OpenCvSharp;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Point = OpenCvSharp.Point;
using Size = OpenCvSharp.Size;
using Window = System.Windows.Window;
using Rect = OpenCvSharp.Rect;
using ImageMagick;
using PdfSharp.Pdf;

namespace EasyScan.Tests;

internal static class Program
{
    private static int _passed;
    private static readonly ImageLoader Loader = new();
    private static readonly DocumentProcessor Processor = new();
    private static string _artifacts = "";

    [STAThread]
    public static int Main(string[] args)
    {
        _artifacts = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/verification");
        Directory.CreateDirectory(_artifacts);
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/easyScan;component/Themes/Controls.xaml") });
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var exit = 0;
        Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
        {
            try { await RunAsync(args.Skip(1).ToArray()); Console.WriteLine($"PASS: {_passed} assertions. Artifacts: {_artifacts}"); }
            catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; }
            finally { application.Shutdown(exit); }
        });
        application.Run();
        return exit;
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("FAIL: " + message);
        _passed++; Console.WriteLine("PASS: " + message);
    }

    private static async Task RunAsync(string[] samples)
    {
        if (samples.FirstOrDefault() == "--research-only") { await ResearchAlgorithmRegression.RunAsync(_artifacts, Check); return; }
        if (samples.FirstOrDefault() == "--scan-only") { await ScanOptimizationRegression.RunAsync(_artifacts, Check, Render); return; }
        if (samples.FirstOrDefault() == "--scene-only") { SceneRegression.Run(_artifacts, Check); return; }
        if (samples.FirstOrDefault() == "--export-only") { await ExportLayoutRegression.RunAsync(_artifacts, Loader, Processor, Check); return; }
        if (samples.FirstOrDefault() == "--workflow-only") { SceneRegression.Run(_artifacts, Check); await PreviewWorkflowRegression.RunAsync(_artifacts, Render, Check); return; }
        using var synthetic = new Mat(600, 900, MatType.CV_8UC3, Scalar.All(48));
        Point[] corners = [new(90, 75), new(680, 40), new(740, 500), new(55, 525)];
        Cv2.FillConvexPoly(synthetic, corners, new Scalar(225, 231, 235));
        Cv2.PutText(synthetic, "EASYSCAN", new Point(160, 220), HersheyFonts.HersheySimplex, 2, Scalar.All(30), 4);
        Cv2.Circle(synthetic, new Point(560, 400), 50, new Scalar(40, 40, 190), 4);
        var syntheticPath = Path.Combine(_artifacts, "透视测试.png");
        File.WriteAllBytes(syntheticPath, synthetic.ToBytes(".png"));
        var detection = Processor.DetectCrop(synthetic);
        Check(detection.Confidence >= .65, "synthetic page detected confidently");
        Check(detection.Crop.Area is > .45 and < .65, "outer page crop excludes dark tabletop");
        var expected = corners.Select(x => new NormalizedPoint(x.X / 899d, x.Y / 599d)).ToArray();
        Check(detection.Crop.Points.Zip(expected).All(x => Math.Abs(x.First.X - x.Second.X) < .025 && Math.Abs(x.First.Y - x.Second.Y) < .025), "perspective corners within 2.5% of ground truth");
        using var straight = Processor.ProcessColor(synthetic, new ScanSettings { Crop = detection.Crop, ShadowRemoval = 0 });
        Check(straight.Width is > 600 and < 720 && straight.Height is > 430 and < 500, "perspective output has expected dimensions");
        Check(!new CropQuad(new(0,0), new(1,1), new(1,0), new(0,1)).IsValid, "crossing crop edges rejected");
        using var rotated = Processor.ProcessColor(synthetic, new ScanSettings { ShadowRemoval = 0, QuarterTurns = -1 });
        Check(rotated.Width == 600 && rotated.Height == 900, "left rotation preserves full resolution");
        using var insetFrame = new PageRectifier().Rectify(synthetic, new ScanSettings { EdgeInset = .01 });
        Check(insetFrame.Width == 888 && insetFrame.Height == 588, "manual edge inset also applies to a full-frame crop");

        using var color = Processor.ConvertColor(straight, OutputColorMode.Color);
        using var gray = Processor.ConvertColor(straight, OutputColorMode.Grayscale);
        using var binary = Processor.ConvertColor(straight, OutputColorMode.Binary);
        Check(color.Channels() == 3 && gray.Channels() == 1 && binary.Channels() == 1, "color modes have expected channel counts");
        Check(Cv2.Norm(color, straight, NormTypes.INF) == 0, "color conversion preserves the color master");
        using var nonbinary = new Mat();
        Cv2.InRange(binary, Scalar.All(1), Scalar.All(254), nonbinary);
        Check(Cv2.CountNonZero(nonbinary) == 0, "binary output contains only black and white pixels");
        using (var tiny = new Mat(2, 2, MatType.CV_8UC3, Scalar.All(200)))
            Check(Processor.DetectCrop(tiny).Crop == CropQuad.FullFrame, "tiny images safely retain the full frame");

        using var shadow = new Mat(400, 800, MatType.CV_8UC3);
        var shadowPixels = new Vec3b[400 * 800];
        for (var y = 0; y < 400; y++)
            for (var x = 0; x < 800; x++)
            {
                var light = (byte)(130 + 95 * x / 799d);
                shadowPixels[y * 800 + x] = new(light, light, light);
            }
        shadow.SetArray(shadowPixels);
        Cv2.PutText(shadow, "SHADOW", new Point(100, 200), HersheyFonts.HersheySimplex, 1.6, Scalar.All(35), 3);
        using var corrected = Processor.ProcessColor(shadow, new ScanSettings { ShadowRemoval = .65 });
        double Difference(Mat image)
        {
            using var left = new Mat(image, new Rect(20, 20, 60, 60)); using var right = new Mat(image, new Rect(720, 20, 60, 60));
            return Math.Abs(Cv2.Mean(right).Val0 - Cv2.Mean(left).Val0);
        }
        var improvement = 1 - Difference(corrected) / Difference(shadow);
        Console.WriteLine($"Shadow gradient improvement: {improvement:P1}");
        Check(improvement > .2, "illumination correction reduces synthetic shadow gradient by more than 20%");
        File.WriteAllBytes(Path.Combine(_artifacts, "阴影处理.png"), corrected.ToBytes(".png"));

        var source = (await Loader.DescribeAsync(syntheticPath))[0];
        Check(source.Width == 900 && source.Height == 600, "source metadata preserves original pixel dimensions");
        var orientedPath = Path.Combine(_artifacts, "方向测试.jpg");
        using (var oriented = new MagickImage(synthetic.ToBytes(".jpg")))
        {
            var exif = new ExifProfile(); exif.SetValue(ExifTag.Orientation, (ushort)6);
            oriented.SetProfile(exif); oriented.Orientation = ImageMagick.Orientation.RightTop; oriented.Write(orientedPath);
        }
        var orientedSource = (await Loader.DescribeAsync(orientedPath))[0];
        using (var upright = await Loader.LoadAsync(orientedSource))
            Check(upright.Width == 600 && upright.Height == 900 && orientedSource.Width == 600, "EXIF orientation agrees between metadata and decoded image");
        var transparentPath = Path.Combine(_artifacts, "透明图片.png");
        using (var transparent = new Mat(40, 40, MatType.CV_8UC4, Scalar.All(0)))
            File.WriteAllBytes(transparentPath, transparent.ToBytes(".png"));
        using (var flattened = await Loader.LoadAsync((await Loader.DescribeAsync(transparentPath))[0]))
            Check(Cv2.Mean(flattened).Val0 == 255, "transparent input is composited on white paper");
        var exportPage = new ExportPage(source, new ScanSettings { Crop = detection.Crop });
        var exportService = new ExportService(Loader, Processor);
        var outputFolder = Path.Combine(_artifacts, "导出验证");
        foreach (var mode in Enum.GetValues<OutputColorMode>())
        {
            foreach (var format in new[] { ExportFormat.Jpeg, ExportFormat.Png, ExportFormat.Pdf })
            {
                var result = await exportService.ExportAsync([exportPage], new(Path.Combine(outputFolder, mode.ToString()), format, Color: mode));
                Check(result.Files.Count == 1 && result.Failures.Count == 0, $"{mode} / {format} exports successfully");
                var reread = (await Loader.DescribeAsync(result.Files[0]))[0];
                using var output = await Loader.LoadAsync(reread);
                Check(output.Width > 500 && output.Height > 400, $"{mode} / {format} reopens at full resolution");
                if (mode != OutputColorMode.Color)
                {
                    var channels = Cv2.Split(output);
                    try { Check(Cv2.Norm(channels[0], channels[1], NormTypes.INF) == 0 && Cv2.Norm(channels[1], channels[2], NormTypes.INF) == 0, $"{mode} / {format} actually stores neutral output"); }
                    finally { foreach (var channel in channels) channel.Dispose(); }
                }
            }
        }
        var combined = await exportService.ExportAsync([exportPage, exportPage], new(outputFolder, Organization: ExportOrganization.CombinedPdf));
        var pdfPages = await Loader.DescribeAsync(combined.Files.Single());
        Check(pdfPages.Count == 2, "combined PDF preserves page count");
        var perSourcePdf = await exportService.ExportAsync(pdfPages.Reverse().Select(x => new ExportPage(x, new ScanSettings())).ToArray(), new(outputFolder));
        Check((await Loader.DescribeAsync(perSourcePdf.Files.Single())).Count == 2, "automatic PDF output groups original source and includes every page");
        var pageImages = await exportService.ExportAsync(pdfPages.Select(x => new ExportPage(x, new ScanSettings())).ToArray(), new(outputFolder, ExportFormat.Png));
        Check(pageImages.Files.Count == 2 && Path.GetFileName(pageImages.Files[0]).Contains("-p001"), "PDF page image names include page numbers");
        var mixed = await exportService.ExportAsync([exportPage, .. pdfPages.Select(x => new ExportPage(x, new ScanSettings()))], new(outputFolder));
        Check(mixed.Files.Count == 2 && mixed.Files.Any(x => x.EndsWith(".jpg")) && mixed.Files.Any(x => x.EndsWith(".pdf")), "mixed automatic job exports image and source PDF separately");
        var lockedPath = Path.Combine(_artifacts, "加密文档.pdf");
        using (var locked = new PdfDocument())
        {
            locked.AddPage(); locked.SecuritySettings.UserPassword = "test-only-password"; locked.Save(lockedPath);
        }
        var protectedRejected = false;
        try { await Loader.DescribeAsync(lockedPath); }
        catch (Exception) { protectedRejected = true; }
        Check(protectedRejected, "encrypted PDF is rejected without treating it as an empty document");
        var again = await exportService.ExportAsync([exportPage], new(outputFolder, ExportFormat.Png));
        var duplicate = await exportService.ExportAsync([exportPage], new(outputFolder, ExportFormat.Png));
        Check(again.Files[0] != duplicate.Files[0] && File.Exists(again.Files[0]), "existing outputs are never overwritten");
        var broken = new ExportPage(new(new(Guid.NewGuid(), Path.Combine(_artifacts, "missing.png"), false), 0), new());
        var partial = await exportService.ExportAsync([broken, exportPage], new(outputFolder, ExportFormat.Png));
        Check(partial.Files.Count == 1 && partial.Failures.Count == 1, "one failed image does not prevent other exports");
        var incompletePdf = await exportService.ExportAsync([exportPage, broken], new(outputFolder, Organization: ExportOrganization.CombinedPdf));
        Check(incompletePdf.Files.Count == 0 && incompletePdf.Failures.Count == 1, "PDF containing a failed page is not silently truncated");
        using var cancellation = new CancellationTokenSource();
        var cancelled = await exportService.ExportAsync([exportPage, exportPage], new(outputFolder, ExportFormat.Png),
            new InlineProgress<ExportProgress>(_ => cancellation.Cancel()), cancellation.Token);
        Check(cancelled.Cancelled && cancelled.Files.Count == 1, "cancel preserves completed independent files");
        Check(!Directory.GetFiles(outputFolder, "*.tmp", SearchOption.AllDirectories).Any(), "export cleans temporary files");

        var pageVm = new PageViewModel(source);
        pageVm.Initialize(detection, 900, 600);
        pageVm.Brightness = 12; pageVm.Brightness = 22;
        pageVm.Undo(); Check(pageVm.Brightness == 0, "slider edits coalesce into one undo operation");
        pageVm.Redo(); Check(pageVm.Brightness == 22, "redo restores enhancement settings");
        pageVm.SetCrop(CropQuad.FullFrame); pageVm.Undo();
        Check(pageVm.Crop == detection.Crop, "crop adjustment can be undone");

        SceneRegression.Run(_artifacts, Check);
        await ExportLayoutRegression.RunAsync(_artifacts, Loader, Processor, Check);
        await VerifyUiAsync(samples.Length > 0 ? samples : [syntheticPath]);
        await ScanOptimizationRegression.RunAsync(_artifacts, Check, Render);
        await ResearchAlgorithmRegression.RunAsync(_artifacts, Check);
    }

    private static async Task VerifyUiAsync(string[] samples)
    {
        var errors = new BindingErrorListener();
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        using var vm = new MainViewModel();
        var window = new MainWindow(vm);
        Render(window, "界面-空状态.png", 1280, 760);
        await vm.ImportPathsAsync(samples);
        Check(vm.Pages.Count == samples.Length && vm.Pages.All(x => x.IsReady), "reference files import into the UI queue");
        await vm.RefreshPreviewAsync();
        Check(vm.OriginalPreview is not null && vm.ProcessedPreview is not null, "real processing populates UI preview");
        foreach (var page in vm.Pages)
        {
            using var original = await Loader.LoadAsync(page.Source);
            Check(original.Width == page.Source.Width && original.Height == page.Source.Height, $"{Path.GetExtension(page.Source.Document.Path)} decoded through bundled Magick.NET at original dimensions");
            Console.WriteLine($"Crop: {page.Name}; area={page.Crop.Area:F3}; state={page.State}; {page.CropDescription}");
            if (Path.GetFileName(page.Source.Document.Path).Equals("RCBC.HEIC", StringComparison.OrdinalIgnoreCase))
            {
                Check(page.Crop == CropQuad.FullFrame, "already filled HEIC certificate retains ornamental border and both seals");
            }
            using var processed = Processor.ProcessColor(original, page.Settings);
            var name = Path.GetFileNameWithoutExtension(page.Source.Document.Path);
            File.WriteAllBytes(Path.Combine(_artifacts, name + "-自然彩色.png"), processed.ToBytes(".png"));
            using var bw = Processor.ConvertColor(processed, OutputColorMode.Binary);
            File.WriteAllBytes(Path.Combine(_artifacts, name + "-黑白.png"), bw.ToBytes(".png"));
        }
        Render(window, "界面-工作台.png", 1280, 760);
        Render(window, "界面-窄窗口.png", 900, 600);
        vm.ShowComparison = true;
        Render(window, "界面-对比.png", 1280, 760);
        vm.ToggleCropCommand.Execute(null);
        Render(window, "界面-裁剪.png", 1280, 760);
        var cropControl = FindVisual<CropEditor>((DependencyObject)window.Content)!;
        Check(cropControl is not null, "crop editor is present in the rendered UI");
        var before = vm.SelectedPage!.Crop;
        var settingsBefore = vm.SelectedPage.Settings;
        Check(!vm.CanExport && !vm.CanAdjust && !vm.UndoCommand.CanExecute(null), "crop session disables export, enhancement and historical undo");
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, new FakePresentationSource(), 0, Key.Right) { RoutedEvent = Keyboard.KeyDownEvent };
        cropControl!.RaiseEvent(key);
        Check(key.Handled && vm.SelectedPage.Crop.TopLeft.X > before.TopLeft.X, "crop corner supports keyboard adjustment");
        vm.CancelCropCommand.Execute(null);
        Check(vm.SelectedPage.Settings == settingsBefore && !vm.IsCropping, "cancel restores the complete crop session snapshot");
        vm.ToggleCropCommand.Execute(null);
        cropControl.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new FakePresentationSource(), 0, Key.D5) { RoutedEvent = Keyboard.KeyDownEvent });
        cropControl.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new FakePresentationSource(), 0, Key.Up) { RoutedEvent = Keyboard.KeyDownEvent });
        var editedSettings = vm.SelectedPage.Settings;
        Check(editedSettings.CurvedEdges is not null && editedSettings != settingsBefore, "edge midpoint supports keyboard adjustment");
        vm.ToggleCropCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        Check(vm.SelectedPage.Settings == settingsBefore, "all crop adjustments undo as one operation");
        vm.RedoCommand.Execute(null);
        Check(vm.SelectedPage.Settings == editedSettings, "redo restores corners and curved edges together");
        vm.UndoCommand.Execute(null); vm.ShowComparison = false;
        vm.IsGrayscale = true; await vm.RefreshPreviewAsync();
        var grayPixels = new FormatConvertedBitmap(vm.ProcessedPreview!, PixelFormats.Bgr24, null, 0);
        var pixels = new byte[grayPixels.PixelWidth * grayPixels.PixelHeight * 3]; grayPixels.CopyPixels(pixels, grayPixels.PixelWidth * 3, 0);
        Check(Enumerable.Range(0, pixels.Length / 3).All(i => pixels[i * 3] == pixels[i * 3 + 1] && pixels[i * 3 + 1] == pixels[i * 3 + 2]), "grayscale preview has neutral channels");
        vm.IsBinary = true; await vm.RefreshPreviewAsync();
        vm.IsColor = true; await vm.RefreshPreviewAsync();
        Check(vm.CurrentColorBytes is not null && vm.IsColor, "switching back from binary restores the color preview");
        var exports = vm.Pages.Select(x => new ExportPage(x.Source, x.Settings)).ToArray();
        using var exportVm = new ExportViewModel(Loader, Processor, exports, [exports[0]], vm.CurrentColorBytes, vm.ColorMode, vm.BinaryStrength);
        var dialog = new ExportDialog(exportVm);
        await exportVm.UpdatePreviewAsync();
        Check(exportVm.ExportCommand.CanExecute(null), "export command is enabled when pages and destination are available");
        Render(dialog, "界面-导出彩色.png", 1040, 700);
        exportVm.IsBinary = true; await exportVm.UpdatePreviewAsync();
        Render(dialog, "界面-导出黑白.png", 1040, 700);
        exportVm.Folder = Path.Combine(_artifacts, "界面导出");
        exportVm.Format = exportVm.Formats.Single(x => x.Value == ExportFormat.Png);
        await exportVm.ExportCommand.ExecuteAsync(null);
        Check(exportVm.Result?.Files.Count == exports.Length, "export dialog command writes all selected job pages");
        exportVm.Scope = exportVm.Scopes.Single(x => x.Value);
        await exportVm.ExportCommand.ExecuteAsync(null);
        Check(exportVm.Result?.Files.Count == 1, "selected-page export scope exports only the selection");
        var badFolder = Path.Combine(_artifacts, "not-a-folder.txt"); File.WriteAllText(badFolder, "fixture");
        exportVm.Folder = badFolder;
        await exportVm.ExportCommand.ExecuteAsync(null);
        Check(exportVm.Result?.Files.Count == 0 && exportVm.HasFailures && !exportVm.IsBusy, "failed destination clears old success result and leaves a recoverable UI");
        Render(dialog, "界面-导出失败.png", 1040, 700);
        foreach (var dpi in new[] { 96, 144, 192 }) Render(window, $"界面-{dpi / 96d:P0}.png", 1280, 760, dpi);
        await InteractionRegression.RunAsync(_artifacts, Render, Check);
        await PreviewWorkflowRegression.RunAsync(_artifacts, Render, Check);
        Check(errors.Errors.Count == 0, "WPF binding diagnostics contain no errors: " + string.Join(" | ", errors.Errors.Take(3)));
        PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
    }

    private static void Render(Window window, string name, int width, int height, int dpi = 96)
    {
        window.Width = width; window.Height = height;
        if (window is MainWindow main) main.SetLayoutWidth(width);
        var content = (FrameworkElement)window.Content;
        content.Measure(new System.Windows.Size(width, height)); content.Arrange(new System.Windows.Rect(0, 0, width, height)); content.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
        var bitmap = new RenderTargetBitmap(width * dpi / 96, height * dpi / 96, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(_artifacts, name)); encoder.Save(output);
    }
    private static T? FindVisual<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindVisual<T>(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
        return null;
    }
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
    private sealed class BindingErrorListener : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
    private sealed class FakePresentationSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
}
