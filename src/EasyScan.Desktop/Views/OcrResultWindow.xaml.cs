using EasyScan.Desktop.ViewModels;
using Microsoft.Win32;
using System.IO;
using System.Windows;

namespace EasyScan.Desktop.Views;

public partial class OcrResultWindow : Window
{
    private readonly PageViewModel _page;
    public OcrResultWindow(PageViewModel page)
    {
        InitializeComponent(); _page = page; DataContext = page;
        SetResourceReference(StyleProperty, typeof(Window));
    }
    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try { if (_page.HasCurrentOcr) Clipboard.SetText(_page.OcrText); }
        catch (System.Runtime.InteropServices.ExternalException) { MessageBox.Show(this, "剪贴板正在被其他程序占用，请重试。", "复制文字"); }
    }
    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (!_page.HasCurrentOcr) return;
        var dialog = new SaveFileDialog { Title = "保存识别文字", Filter = "文本文档|*.txt", DefaultExt = ".txt", FileName = Path.GetFileNameWithoutExtension(_page.Name) + "-文字.txt" };
        if (dialog.ShowDialog(this) != true) return;
        try { await File.WriteAllTextAsync(dialog.FileName, _page.OcrText, new System.Text.UTF8Encoding(false)); }
        catch (Exception ex) { MessageBox.Show(this, "保存失败：" + EasyScan.Core.ExportService.FriendlyError(ex), "保存文字"); }
    }
}
