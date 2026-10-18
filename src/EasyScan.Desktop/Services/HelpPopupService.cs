using EasyScan.Desktop.Controls;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace EasyScan.Desktop.Services;

public static class HelpPopupService
{
    private static readonly ConditionalWeakTable<Window, Presenter> Presenters = new();
    public static void Show(HelpIcon icon, bool immediate)
    {
        if (Window.GetWindow(icon) is { } window) Presenters.GetValue(window, w => new(w)).Show(icon, immediate);
    }
    public static void Leave(HelpIcon icon) { if (Window.GetWindow(icon) is { } window && Presenters.TryGetValue(window, out var presenter)) presenter.Leave(icon); }
    public static FrameworkElement? OpenContent(Window window) => Presenters.TryGetValue(window, out var presenter) && presenter.Popup.IsOpen ? presenter.Content : null;

    private sealed class Presenter
    {
        public Popup Popup { get; } = new() { AllowsTransparency = true, StaysOpen = true, Focusable = false, Placement = PlacementMode.Right, HorizontalOffset = 4 };
        public Border Content { get; }
        private readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 6) };
        private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 20 };
        private readonly DispatcherTimer _open = new() { Interval = TimeSpan.FromMilliseconds(250) };
        private readonly DispatcherTimer _close = new() { Interval = TimeSpan.FromMilliseconds(350) };
        private HelpIcon? _current;
        public Presenter(Window window)
        {
            Content = new() { Width = 300, Padding = new(14), Margin = new(8), CornerRadius = new(8), BorderThickness = new(1) };
            Content.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush"); Content.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            _title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); _text.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryBrush");
            Content.Resources[SystemFonts.MessageFontFamilyKey] = window.FontFamily;
            var stack = new StackPanel(); stack.Children.Add(_title); stack.Children.Add(_text); Content.Child = stack;
            if (!SystemParameters.HighContrast && SystemParameters.DropShadow) Content.Effect = new DropShadowEffect { Color = Colors.Black, Opacity = .14, BlurRadius = 12, ShadowDepth = 2 };
            Popup.Child = Content;
            _open.Tick += (_, _) => { _open.Stop(); Popup.IsOpen = true; };
            _close.Tick += (_, _) => Close();
            Content.MouseEnter += (_, _) => _close.Stop(); Content.MouseLeave += (_, _) => Leave();
            window.Deactivated += (_, _) => Close(); window.Closed += (_, _) => Close();
            window.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && Popup.IsOpen) { Close(); e.Handled = true; } };
            window.PreviewMouseDown += (_, e) => { if (e.OriginalSource is DependencyObject d && !IsInsideIcon(d)) Close(); };
        }
        private static bool IsInsideIcon(DependencyObject source)
        {
            for (var current = source; current is not null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
                if (current is HelpIcon) return true;
            return false;
        }
        public void Show(HelpIcon icon, bool immediate)
        {
            var wasOpen = Popup.IsOpen;
            if (immediate && _current == icon && wasOpen) { Close(); return; }
            Close(); _current = icon;
            var help = HelpTopics.Get(icon.Topic); _title.Text = help.Title; _text.Text = help.Text; Popup.PlacementTarget = icon;
            if (immediate || wasOpen) Popup.IsOpen = true; else _open.Start();
        }
        public void Leave(HelpIcon? icon = null) { if (icon is not null && icon != _current) return; _open.Stop(); if (Popup.IsOpen) _close.Start(); }
        private void Close() { _open.Stop(); _close.Stop(); Popup.IsOpen = false; }
    }
}
