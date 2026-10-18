using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EasyScan.Desktop.Controls;

public sealed class ImageViewport : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(BitmapSource), typeof(ImageViewport), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(nameof(Zoom), typeof(double), typeof(ImageViewport), new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnTransformChanged, (_, v) => Math.Clamp((double)v, .5, 8)));
    public static readonly DependencyProperty PanXProperty = DependencyProperty.Register(nameof(PanX), typeof(double), typeof(ImageViewport), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public static readonly DependencyProperty PanYProperty = DependencyProperty.Register(nameof(PanY), typeof(double), typeof(ImageViewport), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public BitmapSource? Source { get => (BitmapSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public double Zoom { get => (double)GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public double PanX { get => (double)GetValue(PanXProperty); set => SetValue(PanXProperty, value); }
    public double PanY { get => (double)GetValue(PanYProperty); set => SetValue(PanYProperty, value); }
    private Point? _dragStart;
    private double _startX, _startY;
    private bool _keyboardFocus;
    public ImageViewport() { Focusable = true; ClipToBounds = true; }
    private static void OnTransformChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (ImageViewport)d;
        if (view.Zoom <= 1) { view.SetCurrentValue(PanXProperty, 0d); view.SetCurrentValue(PanYProperty, 0d); }
        view.Cursor = view.Zoom > 1 ? Cursors.Hand : Cursors.Arrow;
    }
    private Size FitSize()
    {
        if (Source is null || ActualWidth <= 0 || ActualHeight <= 0) return new();
        var scale = Math.Min(Math.Max(1, ActualWidth - 40) / Source.PixelWidth, Math.Max(1, ActualHeight - 40) / Source.PixelHeight);
        return new(Source.PixelWidth * scale, Source.PixelHeight * scale);
    }
    public Rect ImageBounds
    {
        get
        {
            var fit = FitSize(); var w = fit.Width * Zoom; var h = fit.Height * Zoom;
            return new((ActualWidth - w) / 2 + PanX * w, (ActualHeight - h) / 2 + PanY * h, w, h);
        }
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (Source is not null) dc.DrawImage(Source, ImageBounds);
        if (_keyboardFocus && IsKeyboardFocusWithin)
            dc.DrawRoundedRectangle(null, new Pen((Brush?)TryFindResource("FocusBrush") ?? Brushes.Gray, 2), new Rect(2, 2, Math.Max(0, ActualWidth - 4), Math.Max(0, ActualHeight - 4)), 5, 5);
    }
    public void ZoomAt(Point point, double factor)
    {
        if (Source is null) return;
        var fit = FitSize(); var before = Zoom; var after = Math.Clamp(before * factor, .5, 8);
        if (fit.Width <= 0 || fit.Height <= 0) return;
        var sourceX = (point.X - ActualWidth / 2) / (fit.Width * before) - PanX;
        var sourceY = (point.Y - ActualHeight / 2) / (fit.Height * before) - PanY;
        SetCurrentValue(ZoomProperty, after);
        if (after > 1) SetPan((point.X - ActualWidth / 2) / (fit.Width * after) - sourceX, (point.Y - ActualHeight / 2) / (fit.Height * after) - sourceY);
    }
    public void SetPan(double x, double y)
    {
        var fit = FitSize();
        var boundX = fit.Width > 0 ? Math.Max(0, .5 + (ActualWidth / 2 - 48) / (fit.Width * Zoom)) : 0;
        var boundY = fit.Height > 0 ? Math.Max(0, .5 + (ActualHeight / 2 - 48) / (fit.Height * Zoom)) : 0;
        SetCurrentValue(PanXProperty, Zoom <= 1 ? 0d : Math.Clamp(x, -boundX, boundX));
        SetCurrentValue(PanYProperty, Zoom <= 1 ? 0d : Math.Clamp(y, -boundY, boundY));
    }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e); if (!IsEnabled || Source is null) return;
        ZoomAt(e.GetPosition(this), Math.Pow(1.15, e.Delta / 120d)); e.Handled = true;
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e); if (Source is null) return;
        _keyboardFocus = false; Focus();
        if (e.ClickCount == 2) { SetCurrentValue(ZoomProperty, 1d); SetPan(0, 0); e.Handled = true; return; }
        if (Zoom <= 1) return;
        _dragStart = e.GetPosition(this); _startX = PanX; _startY = PanY; CaptureMouse(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); if (_dragStart is not { } start) return;
        var fit = FitSize(); var point = e.GetPosition(this);
        if (fit.Width > 0 && fit.Height > 0) SetPan(_startX + (point.X - start.X) / (fit.Width * Zoom), _startY + (point.Y - start.Y) / (fit.Height * Zoom));
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { base.OnMouseLeftButtonUp(e); if (_dragStart is null) return; _dragStart = null; ReleaseMouseCapture(); e.Handled = true; }
    protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); _dragStart = null; }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); _keyboardFocus = InputManager.Current.MostRecentInputDevice is KeyboardDevice; InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); _keyboardFocus = true;
        if (e.Key is Key.Add or Key.OemPlus) { ZoomAt(new(ActualWidth / 2, ActualHeight / 2), 1.15); e.Handled = true; }
        else if (e.Key is Key.Subtract or Key.OemMinus) { ZoomAt(new(ActualWidth / 2, ActualHeight / 2), 1 / 1.15); e.Handled = true; }
        else if (e.Key == Key.Home) { SetCurrentValue(ZoomProperty, 1d); SetPan(0, 0); e.Handled = true; }
        else if (Zoom > 1 && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            SetPan(PanX + (e.Key == Key.Left ? .04 : e.Key == Key.Right ? -.04 : 0), PanY + (e.Key == Key.Up ? .04 : e.Key == Key.Down ? -.04 : 0)); e.Handled = true;
        }
        InvalidateVisual();
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new ViewportPeer(this);
    private sealed class ViewportPeer(ImageViewport owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => "ImageViewport";
        protected override string GetNameCore() => "图片预览，滚轮缩放，放大后左键拖动，双击或 Home 适应窗口";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
    }
}
