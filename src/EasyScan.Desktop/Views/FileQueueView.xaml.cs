using EasyScan.Desktop.ViewModels;
using System.Windows.Controls;

namespace EasyScan.Desktop.Views;
public partial class FileQueueView : UserControl
{
    public FileQueueView() => InitializeComponent();
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var page in e.RemovedItems.OfType<PageViewModel>()) page.IsSelected = false;
        foreach (var page in e.AddedItems.OfType<PageViewModel>()) page.IsSelected = true;
    }
}
