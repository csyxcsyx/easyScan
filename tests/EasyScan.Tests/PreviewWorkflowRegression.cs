using EasyScan.Core;
using EasyScan.Desktop;
using EasyScan.Desktop.Controls;
using EasyScan.Desktop.Services;
using EasyScan.Desktop.ViewModels;
using EasyScan.Desktop.Views;
using OpenCvSharp;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Window = System.Windows.Window;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Rect = System.Windows.Rect;

namespace EasyScan.Tests;

internal static class PreviewWorkflowRegression
{
    public static async Task RunAsync(string root, Action<Window, string, int, int, int> render, Action<bool, string> check)
    {
        var cacheRoot = Path.Combine(root, "缓存验证"); Directory.CreateDirectory(cacheRoot);
        var foreign = Path.Combine(cacheRoot, "非缓存目录"); Directory.CreateDirectory(foreign);
        Directory.SetLastWriteTimeUtc(foreign, DateTime.UtcNow.AddDays(-2));
        var loader = new ImageLoader(); var processor = new DocumentProcessor();
        var input = Path.Combine(root, "场景回归/深色桌面-输入.png");
        var otherInput = Path.Combine(root, "场景回归/浅色桌面-输入.png");
        var page = new ExportPage((await loader.DescribeAsync(input))[0], new());
        var other = new ExportPage((await loader.DescribeAsync(otherInput))[0], new());
        string session;
        using (var cache = new PreviewCache(loader, processor, cacheRoot, memoryLimit: 1))
        {
            session = cache.SessionDirectory;
            await cache.GetAsync(page, OutputColorMode.Color, 0); await cache.GetAsync(other, OutputColorMode.Color, 0);
            var count = cache.ProcessingCount; var decodes = cache.DecodeCount;
            await cache.GetAsync(page, OutputColorMode.Color, 0);
            check(cache.ProcessingCount == count && cache.DecodeCount == decodes, "memory eviction restores disk previews without decoding the source or enhancing again");
            await cache.GetAsync(page, OutputColorMode.Grayscale, 0); await cache.GetAsync(page, OutputColorMode.Binary, .5);
            check(cache.ProcessingCount == count, "grayscale and binary reuse the cached color master");
            await cache.GetAsync(page with { Settings = page.Settings with { Brightness = 10 } }, OutputColorMode.Color, 0);
            check(cache.ProcessingCount == count + 1 && cache.DecodeCount == decodes, "settings change enhances only its page and reuses the orientation-correct original preview");
            var canceled = false; using var cts = new CancellationTokenSource(); cts.Cancel();
            try { await cache.GetAsync(page with { Settings = page.Settings with { Brightness = 20 } }, OutputColorMode.Color, 0, cts.Token); }
            catch (OperationCanceledException) { canceled = true; }
            check(canceled && cache.ProcessingCount == count + 1, "canceled preview work does not process or publish a new result");
            check(Directory.Exists(foreign), "cache cleanup leaves unrelated directories untouched");
        }
        check(!Directory.Exists(session), "normal shutdown removes its own session preview files");
        var changedPath = Path.Combine(cacheRoot, "源文件变化.png"); File.Copy(input, changedPath, true);
        var changedPage = new ExportPage((await loader.DescribeAsync(changedPath))[0], new());
        using (var changedCache = new PreviewCache(loader, processor, cacheRoot))
        {
            await changedCache.GetAsync(changedPage, OutputColorMode.Color, 0);
            File.SetLastWriteTimeUtc(changedPath, DateTime.UtcNow.AddMinutes(1));
            await changedCache.GetAsync(changedPage, OutputColorMode.Color, 0);
            check(changedCache.ProcessingCount == 2 && changedCache.DecodeCount == 2, "source file version changes invalidate both original and processed previews");
        }
        using var vm = new MainViewModel(); var window = new MainWindow(vm);
        await vm.ImportPathsAsync([input, otherInput]); await vm.RefreshPreviewAsync();
        check(vm.PreviewCache.ProcessingCount == 2, "import seeds one processed preview per page without duplicate processing");
        var first = vm.Pages[0]; var second = vm.Pages[1]; var originalFrame = vm.ProcessedPreview;
        var initialCount = vm.PreviewCache.ProcessingCount;
        vm.SelectedPage = second; await vm.RefreshPreviewAsync();
        var watch = Stopwatch.StartNew(); vm.SelectedPage = first; await vm.RefreshPreviewAsync(); watch.Stop();
        Console.WriteLine($"Cached return to page: {watch.Elapsed.TotalMilliseconds:F1} ms");
        check(vm.PreviewCache.ProcessingCount == initialCount && ReferenceEquals(vm.ProcessedPreview, originalFrame), "returning to a viewed page binds the same cached bitmap immediately");
        first.Brightness = 7; var obsolete = vm.RefreshPreviewAsync(150);
        vm.SelectedPage = second; await vm.RefreshPreviewAsync(); var latest = vm.ProcessedPreview;
        await obsolete;
        check(vm.SelectedPage == second && ReferenceEquals(vm.ProcessedPreview, latest), "an obsolete delayed render cannot replace the newly selected page");
        first.Undo(); vm.SelectedPage = first; await vm.RefreshPreviewAsync();
        vm.ShowComparison = true; vm.ShowOriginal = true;
        check(vm.ShowOriginal && !vm.ShowComparison && vm.PreviewMode == PreviewMode.Original, "clicking original exits comparison and keeps preview selection exclusive");
        vm.IsResult = true; check(vm.IsResult && !vm.ShowOriginal, "result view exits original view explicitly");
        vm.ShowComparison = true; render(window, "界面-联动对比.png", 1280, 760, 96);
        var viewers = All<ImageViewport>((DependencyObject)window.Content).ToArray();
        var left = viewers.First(x => ReferenceEquals(x.Source, vm.OriginalPreview));
        var right = viewers.First(x => x.Visibility == Visibility.Visible && ReferenceEquals(x.Source, vm.ProcessedPreview));
        var anchor = new Point(left.ActualWidth * .6, left.ActualHeight * .45); var bounds = left.ImageBounds;
        var xBefore = (anchor.X - bounds.X) / bounds.Width;
        left.ZoomAt(anchor, 2); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        check(vm.Zoom == 2 && right.Zoom == 2 && Math.Abs((anchor.X - left.ImageBounds.X) / left.ImageBounds.Width - xBefore) < .0001,
            "comparison zoom is linked and keeps the point under the pointer fixed");
        left.SetPan(.2, -.15); Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        check(Math.Abs(right.PanX - .2) < .0001 && Math.Abs(right.PanY + .15) < .0001, "comparison pans both sides in both directions");
        render(window, "界面-放大对比.png", 1280, 760, 96);
        vm.Zoom = 8; check(!vm.ZoomInCommand.CanExecute(null), "zoom-in disables at its maximum");
        vm.Zoom = .5; check(!vm.ZoomOutCommand.CanExecute(null), "zoom-out disables at its minimum");
        vm.FitCommand.Execute(null); check(vm.Zoom == 1 && vm.PanX == 0 && vm.PanY == 0, "fit resets scale and panning together");
        vm.ToggleCropCommand.Execute(null); check(!vm.CanZoom, "crop does not expose ineffective preview zoom actions");
        vm.CancelCropCommand.Execute(null); check(vm.ShowComparison, "canceling crop restores the previous comparison view");
        vm.ToggleCropCommand.Execute(null); vm.ToggleCropCommand.Execute(null); check(vm.IsResult, "confirming crop returns to the processed result");
        await vm.RefreshPreviewAsync();
        var expander = All<Expander>((DependencyObject)window.Content).First(); expander.ApplyTemplate();
        var header = All<ToggleButton>(expander).First(); header.ApplyTemplate();
        var body = (Border)header.Template.FindName("Body", header); var defaultBrush = body.Background;
        expander.IsExpanded = true; render(window, "界面-展开参数.png", 1280, 760, 96);
        check(Equals(body.Background, defaultBrush) && header.IsChecked == true, "expanded disclosure uses the same soft surface instead of a persistent pressed background");
        expander.IsExpanded = false;
        var help = All<HelpIcon>((DependencyObject)window.Content).First(x => x.Topic == "shadow");
        help.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent }); await Task.Delay(280);
        var firstHelp = HelpPopupService.OpenContent(window);
        check(firstHelp is not null, "help opens after its hover delay");
        SaveElement(firstHelp!, Path.Combine(root, "界面-帮助浮层.png"));
        var nextHelp = All<HelpIcon>((DependencyObject)window.Content).First(x => x.Topic == "contrast");
        nextHelp.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
        check(HelpPopupService.OpenContent(window) == firstHelp && All<TextBlock>(firstHelp!).Any(x => x.Text == "对比度"), "moving to another help icon immediately replaces the old explanation in one popup");
        nextHelp.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
        await Task.Delay(120); check(HelpPopupService.OpenContent(window) is not null, "help remains briefly when the pointer leaves");
        await Task.Delay(280); check(HelpPopupService.OpenContent(window) is null, "help closes after the leave grace period");
        vm.IsRendering = true; render(window, "界面-加载反馈.png", 1280, 760, 96);
        check(All<BusyIndicator>((DependencyObject)window.Content).Any(x => x.IsBusy), "loading state exposes a visible progress indicator without clearing existing content");
        vm.IsRendering = false;
        var exports = vm.Pages.Select(x => new ExportPage(x.Source, x.Settings)).ToArray();
        using var export = new ExportViewModel(loader, processor, exports, [exports[1]], null, OutputColorMode.Color, 50, vm.PreviewCache, exports[1].Source);
        var dialog = new ExportDialog(export); await export.UpdatePreviewAsync();
        check(export.PreviewLabel.Contains(second.Name), "export preview follows the preferred source instead of mismatching its page identity");
        export.IsPdfOutput = true; export.Organization = export.Organizations[1]; export.LayoutMode = export.LayoutModes[1];
        export.WatermarkEnabled = true; check(export.HasWatermarkError && !export.ExportCommand.CanExecute(null), "empty custom watermark explains its error and disables export");
        export.WatermarkText = "仅用于测试 Test"; export.WatermarkStyle = export.WatermarkStyles[1]; await export.UpdatePreviewAsync();
        check(!export.HasWatermarkError && export.Preview is not null && export.ExportCommand.CanExecute(null), "PDF layout and custom watermark preview become exportable after valid text");
        render(dialog, "界面-白纸水印导出.png", 1040, 740, 96);
        All<ScrollViewer>((DependencyObject)dialog.Content).First(x => x.Content is StackPanel).ScrollToEnd();
        render(dialog, "界面-水印选项.png", 1040, 740, 96);
        var folderBox = All<TextBox>((DependencyObject)dialog.Content).First(x => System.Windows.Automation.AutomationProperties.GetName(x) == "保存位置");
        check(All<ScrollViewer>(folderBox).First().ViewportHeight >= folderBox.FontSize + 3.5,
            "fixed destination textbox leaves enough visible height for a complete line of text");
        var combo = All<ComboBox>((DependencyObject)dialog.Content).First(x => ReferenceEquals(x.ItemsSource, export.Organizations));
        combo.ApplyTemplate(); combo.IsDropDownOpen = true; Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
        check(popup.Child is Border { Background: not null, Effect: not null }, "dropdown has an opaque independent menu surface with a separating shadow");
        SaveElement((FrameworkElement)popup.Child, Path.Combine(root, "界面-下拉菜单.png")); combo.IsDropDownOpen = false;
        export.Scope = export.Scopes[1]; await export.UpdatePreviewAsync();
        check(export.PageCount == 1 && export.PreviewCount == 1 && export.PreviewLabel.StartsWith("合并文档"), "changing export scope rebuilds preview navigation for only the active pages");
        export.IsImage = true; export.ImageFormat = export.ImageFormats[1]; await export.UpdatePreviewAsync();
        check(export.Format.Value == ExportFormat.Png && !export.ShowQuality && !export.IsPdf, "explicit PNG image export hides JPG quality and white-paper options");
        render(dialog, "界面-图片水印导出.png", 1040, 740, 96);
        export.IsPdfOutput = true; export.Organization = export.Organizations[1]; export.IsAutomatic = true; await export.UpdatePreviewAsync();
        check(export.IsAutomatic && export.Organization.Value == ExportOrganization.SeparateSources && !export.IsPdf,
            "switching from combined PDF to automatic resets incompatible organization and reflects the actual output kind");
    }
    private static IEnumerable<T> All<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) foreach (var child in All<T>(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
    private static void SaveElement(FrameworkElement element, string path)
    {
        element.Measure(new Size(340, 1000)); var width = Math.Max(1, (int)Math.Ceiling(element.DesiredSize.Width)); var height = Math.Max(1, (int)Math.Ceiling(element.DesiredSize.Height));
        element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(path); encoder.Save(output);
    }
}
