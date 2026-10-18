using EasyScan.Core;
using EasyScan.Desktop.ViewModels;
using EasyScan.Desktop.Controls;
using System.Windows.Controls;

namespace EasyScan.Desktop.Views;
public partial class PreviewPane : UserControl
{
    public PreviewPane() => InitializeComponent();
    private void OnBoundaryCommitted(object? sender, BoundaryChangedEventArgs boundary)
    {
        if (DataContext is MainViewModel vm) vm.SelectedPage?.SetBoundary(boundary.Crop, boundary.Edges);
    }
}