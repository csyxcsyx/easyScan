using EasyScan.Desktop.ViewModels;
using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace EasyScan.Desktop.Views;
public partial class ExportDialog : Window
{
    public ExportViewModel ViewModel { get; }
    public ExportDialog(ExportViewModel viewModel)
    {
        InitializeComponent(); ViewModel = viewModel; DataContext = viewModel;
        SetResourceReference(StyleProperty, typeof(Window));
        viewModel.FolderRequested += OnFolderRequested;
        Width = Math.Min(1040, SystemParameters.WorkArea.Width * .92);
        Height = Math.Min(740, SystemParameters.WorkArea.Height * .92);
    }
    private void OnFolderRequested(object? sender, EventArgs e) => OnChooseFolder(this, new RoutedEventArgs());
    private void OnChooseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择保存位置", Multiselect = false };
        if (Directory.Exists(ViewModel.Folder)) dialog.InitialDirectory = ViewModel.Folder;
        if (dialog.ShowDialog(this) == true) ViewModel.Folder = dialog.FolderName;
    }
    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try { if (Directory.Exists(ViewModel.Folder)) Process.Start(new ProcessStartInfo(Path.GetFullPath(ViewModel.Folder)) { UseShellExecute = true }); }
        catch (Exception ex) { ViewModel.Status = "无法打开文件夹：" + ex.Message; }
    }
    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (ViewModel.IsBusy) { e.Cancel = true; ViewModel.CancelCommand.Execute(null); }
        else { ViewModel.FolderRequested -= OnFolderRequested; ViewModel.Dispose(); }
    }
}
