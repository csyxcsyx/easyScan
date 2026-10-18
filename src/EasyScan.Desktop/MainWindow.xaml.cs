using EasyScan.Core;
using EasyScan.Desktop.ViewModels;
using EasyScan.Desktop.Views;
using Microsoft.Win32;
using System.ComponentModel;
using System.Windows;

namespace EasyScan.Desktop;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    private bool _queueHidden, _toolsHidden, _compact;

    public MainWindow() : this(new MainViewModel()) { }
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        SetResourceReference(StyleProperty, typeof(Window));
        ViewModel = viewModel; DataContext = viewModel;
        viewModel.ImportRequested += OnImportRequested;
        viewModel.ExportRequested += OnExportRequested;
        viewModel.OcrResultRequested += page => new OcrResultWindow(page) { Owner = this }.Show();
        Width = Math.Min(1320, SystemParameters.WorkArea.Width * .92);
        Height = Math.Min(840, SystemParameters.WorkArea.Height * .92);
        DragLeave += (_, _) => DropOverlay.Visibility = Visibility.Collapsed;
    }

    public async void ImportStartupFiles(string[] paths) => await ViewModel.ImportPathsAsync(paths);
    private async void OnImportRequested(object? sender, EventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "导入图片或 PDF", Multiselect = true,
            Filter = "扫描文件|*.jpg;*.jpeg;*.png;*.heic;*.heif;*.pdf|所有文件|*.*" };
        if (dialog.ShowDialog(this) == true) await ViewModel.ImportPathsAsync(dialog.FileNames);
    }

    private async void OnExportRequested(object? sender, EventArgs e)
    {
        if (ViewModel.IsBusy) { ViewModel.Status = "请等待导入完成后导出"; return; }
        var ready = ViewModel.Pages.Where(x => x.IsReady).ToArray();
        if (ready.Length == 0) { ViewModel.Status = "没有可导出的页面，请先重试失败项"; return; }
        var missing = ViewModel.Pages.Count - ready.Length;
        if (missing > 0 && MessageBox.Show(this, $"有 {missing} 页未能处理。跳过这些页，导出其余 {ready.Length} 页？",
            "部分页面不可用", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await ViewModel.RefreshPreviewAsync();
        var all = ready.Select(x => new ExportPage(x.Source, x.Settings)).ToArray();
        var selected = ready.Where(x => x.IsSelected).Select(x => new ExportPage(x.Source, x.Settings)).ToArray();
        if (selected.Length == 0 && ViewModel.SelectedPage is { IsReady: true } current) selected = [new(current.Source, current.Settings)];
        var vm = new ExportViewModel(ViewModel.Loader, ViewModel.Processor, all, selected,
            ViewModel.CurrentColorBytes, ViewModel.ColorMode, ViewModel.BinaryStrength, ViewModel.PreviewCache, ViewModel.SelectedPage?.Source, ViewModel.Ocr);
        var dialog = new ExportDialog(vm) { Owner = this };
        dialog.ShowDialog();
        ViewModel.ColorMode = vm.ColorMode; ViewModel.BinaryStrength = vm.BinaryStrength;
        if (vm.Result is { } result) ViewModel.Status = $"已导出 {result.Files.Count} 个文件" + (result.Cancelled ? "，任务已取消" : result.Failures.Count > 0 ? "，部分文件失败" : "");
        vm.Dispose();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var valid = e.Data.GetDataPresent(DataFormats.FileDrop) && !ViewModel.IsBusy;
        e.Effects = valid ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.Visibility = valid ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }
    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await ViewModel.ImportPathsAsync(paths);
    }
    private void OnToggleQueue(object sender, RoutedEventArgs e) { _queueHidden = !_queueHidden; UpdatePanels(); }
    private void OnToggleTools(object sender, RoutedEventArgs e) { _toolsHidden = !_toolsHidden; UpdatePanels(); }
    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (QueueColumn is null) return;
        SetLayoutWidth(ActualWidth);
    }
    public void SetLayoutWidth(double width)
    {
        var compact = width < 1120;
        if (compact != _compact) { _compact = compact; _queueHidden = compact; UpdatePanels(); }
    }
    private void UpdatePanels()
    {
        QueueColumn.Width = new GridLength(_queueHidden ? 0 : _compact ? 190 : 232);
        ToolsColumn.Width = new GridLength(_toolsHidden ? 0 : _compact ? 248 : 272);
        QueuePanel.Visibility = _queueHidden ? Visibility.Collapsed : Visibility.Visible;
        ToolsPanel.Visibility = _toolsHidden ? Visibility.Collapsed : Visibility.Visible;
    }
    private void OnWindowClosing(object? sender, CancelEventArgs e) => ViewModel.Dispose();
}
