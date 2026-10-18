using EasyScan.Core;
using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EasyScan.Desktop.Controls;

public sealed class BoundaryChangedEventArgs(CropQuad crop, CurvedEdges? edges) : EventArgs
{
    public CropQuad Crop { get; } = crop;
    public CurvedEdges? Edges { get; } = edges;
}

public sealed class CropEditor : FrameworkElement
{
    public static readonly DependencyProperty ImageProperty = DependencyProperty.Register(nameof(Image), typeof(BitmapSource), typeof(CropEditor), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CropProperty = DependencyProperty.Register(nameof(Crop), typeof(CropQuad), typeof(CropEditor), new FrameworkPropertyMetadata(CropQuad.FullFrame, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty EdgesProperty = DependencyProperty.Register(nameof(Edges), typeof(CurvedEdges), typeof(CropEditor), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShowCurvesProperty = DependencyProperty.Register(nameof(ShowCurves), typeof(bool), typeof(CropEditor), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectedHandleProperty = DependencyProperty.Register(nameof(SelectedHandle), typeof(int), typeof(CropEditor), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public BitmapSource? Image { get => (BitmapSource?)GetValue(ImageProperty); set => SetValue(ImageProperty, value); }
    public CropQuad Crop { get => (CropQuad)GetValue(CropProperty); set => SetValue(CropProperty, value); }
    public CurvedEdges? Edges { get => (CurvedEdges?)GetValue(EdgesProperty); set => SetValue(EdgesProperty, value); }
    public bool ShowCurves { get => (bool)GetValue(ShowCurvesProperty); set => SetValue(ShowCurvesProperty, value); }
    public int SelectedHandle { get => (int)GetValue(SelectedHandleProperty); set => SetValue(SelectedHandleProperty, value); }
    public event EventHandler<CropQuad>? CropCommitted;
    public event EventHandler<BoundaryChangedEventArgs>? BoundaryCommitted;
    private bool _dragging;
    private CropQuad? _beforeDrag;
    private CurvedEdges? _beforeEdges;

    public CropEditor() { Focusable = true; Cursor = Cursors.Cross; }
    protected override AutomationPeer OnCreateAutomationPeer() => new CropAutomationPeer(this);
    private sealed class CropAutomationPeer(CropEditor owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => "CropEditor";
        protected override string GetNameCore() => "纸张边界，按 1 到 4 选择角点，5 到 8 选择弯边，方向键微调，Shift 加大步长";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;
    }

    private Rect ImageRect()
    {
        if (Image is null || ActualWidth < 1 || ActualHeight < 1) return Rect.Empty;
        var scale = Math.Min(Math.Max(1, ActualWidth - 48) / Image.PixelWidth, Math.Max(1, ActualHeight - 48) / Image.PixelHeight);
        var width = Image.PixelWidth * scale; var height = Image.PixelHeight * scale;
        return new((ActualWidth - width) / 2, (ActualHeight - height) / 2, width, height);
    }
    private static Point ToScreen(NormalizedPoint p, Rect r) => new(r.Left + p.X * r.Width, r.Top + p.Y * r.Height);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var rect = ImageRect();
        if (Image is null || rect.IsEmpty) return;
        dc.DrawImage(Image, rect);
        var mids = (Edges ?? CurvedEdges.Straight(Crop)).Points;
        var handles = ShowCurves ? Crop.Points.Concat(mids).ToArray() : Crop.Points;
        var points = handles.Select(x => ToScreen(x, rect)).ToArray();
        var polygon = new StreamGeometry();
        using (var context = polygon.Open())
        {
            context.BeginFigure(points[0], true, true);
            for (var side = 0; side < 4; side++)
                for (var i = 1; i <= 30; i++) context.LineTo(ToScreen(CurvedEdges.Evaluate(Crop.Points[side], Crop.Points[(side + 1) % 4], mids[side], i / 30d), rect), true, true);
        }
        var shade = new GeometryGroup { FillRule = FillRule.EvenOdd };
        shade.Children.Add(new RectangleGeometry(rect)); shade.Children.Add(polygon);
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)), null, shade);
        dc.DrawGeometry(null, new Pen(Brushes.White, 2), polygon);
        for (var i = 0; i < points.Length; i++)
        {
            if (i < 4) dc.DrawEllipse(Brushes.White, new Pen(Brushes.Gray, 1), points[i], 7, 7);
            else dc.DrawRoundedRectangle(Brushes.White, new Pen(Brushes.Gray, 1), new Rect(points[i].X - 6, points[i].Y - 6, 12, 12), 3, 3);
            if (SelectedHandle == i)
            {
                dc.DrawEllipse(null, new Pen(SystemParameters.HighContrast ? SystemColors.HighlightBrush : Brushes.DimGray, 3), points[i], 12, 12);
                dc.DrawEllipse(null, new Pen(Brushes.White, 1), points[i], 10, 10);
            }
            var text = new FormattedText((i + 1).ToString(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 12, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var x = Math.Clamp(points[i].X + (i is 1 or 2 ? -24 : 14), rect.Left, rect.Right - 14);
            var y = Math.Clamp(points[i].Y + (i is 2 or 3 ? -22 : 10), rect.Top, rect.Bottom - 18);
            dc.DrawRoundedRectangle(Brushes.DimGray, null, new Rect(x - 4, y - 1, 20, 18), 4, 4);
            dc.DrawText(text, new Point(x + 2, y));
        }
        if (_dragging || IsKeyboardFocusWithin) DrawMagnifier(dc, handles[Math.Clamp(SelectedHandle, 0, handles.Length - 1)], rect);
    }

    private void DrawMagnifier(DrawingContext dc, NormalizedPoint point, Rect imageRect)
    {
        if (Image is null || Image.PixelWidth < 40 || Image.PixelHeight < 40) return;
        var size = 40; var x = Math.Clamp((int)(point.X * (Image.PixelWidth - 1)) - size / 2, 0, Image.PixelWidth - size);
        var y = Math.Clamp((int)(point.Y * (Image.PixelHeight - 1)) - size / 2, 0, Image.PixelHeight - size);
        var patch = new CroppedBitmap(Image, new Int32Rect(x, y, size, size));
        var anchor = ToScreen(point, imageRect);
        var target = new Rect(anchor.X > ActualWidth / 2 ? 20 : ActualWidth - 124, 20, 104, 104);
        dc.DrawRoundedRectangle(Brushes.White, new Pen(Brushes.Gray, 1), target, 8, 8);
        var inside = new Rect(target.X + 5, target.Y + 5, 94, 94); dc.DrawImage(patch, inside);
        var crossX = inside.X + ((point.X * (Image.PixelWidth - 1) - x) / size) * inside.Width;
        var crossY = inside.Y + ((point.Y * (Image.PixelHeight - 1) - y) / size) * inside.Height;
        var pen = new Pen(Brushes.DimGray, 1);
        dc.DrawLine(pen, new Point(crossX - 8, crossY), new Point(crossX + 8, crossY));
        dc.DrawLine(pen, new Point(crossX, crossY - 8), new Point(crossX, crossY + 8));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var rect = ImageRect(); if (rect.IsEmpty) return;
        Focus();
        var mouse = e.GetPosition(this);
        var handles = ShowCurves ? Crop.Points.Concat((Edges ?? CurvedEdges.Straight(Crop)).Points) : Crop.Points;
        var candidates = handles.Select((p, i) => (Index: i, Distance: (ToScreen(p, rect) - mouse).Length)).OrderBy(x => x.Distance).ToArray();
        if (candidates[0].Distance > 28) { InvalidateVisual(); return; }
        SetCurrentValue(SelectedHandleProperty, candidates[0].Index); _beforeDrag = Crop; _beforeEdges = Edges; _dragging = true;
        CaptureMouse(); e.Handled = true; InvalidateVisual();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        var rect = ImageRect(); var p = e.GetPosition(this);
        MoveHandle(new(Math.Clamp((p.X - rect.X) / rect.Width, 0, 1), Math.Clamp((p.Y - rect.Y) / rect.Height, 0, 1)), false);
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false; ReleaseMouseCapture();
        if (Crop.IsValid && (Edges?.IsValidFor(Crop) ?? true)) Commit();
        else RestoreDrag();
        e.Handled = true;
    }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragging) { _dragging = false; RestoreDrag(); }
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Image is null) return;
        if (e.Key is >= Key.D1 and <= Key.D8) { SetCurrentValue(SelectedHandleProperty, e.Key - Key.D1); if (SelectedHandle >= 4) SetCurrentValue(ShowCurvesProperty, true); e.Handled = true; InvalidateVisual(); return; }
        if (e.Key == Key.Escape && _dragging) { _dragging = false; ReleaseMouseCapture(); RestoreDrag(); e.Handled = true; return; }
        var dx = e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0;
        var dy = e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0;
        if (dx == 0 && dy == 0) return;
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10d : 1d;
        var p = SelectedHandle < 4 ? Crop.Points[SelectedHandle] : (Edges ?? CurvedEdges.Straight(Crop)).Points[SelectedHandle - 4];
        MoveHandle(new(Math.Clamp(p.X + dx * step / Image.PixelWidth, 0, 1), Math.Clamp(p.Y + dy * step / Image.PixelHeight, 0, 1)), true);
        e.Handled = true;
    }
    private void MoveHandle(NormalizedPoint point, bool commit)
    {
        var crop = Crop; var edges = Edges;
        if (SelectedHandle < 4)
        {
            crop = crop.WithPoint(SelectedHandle, point);
            if (edges is not null)
            {
                var oldMid = CurvedEdges.Straight(Crop).Points; var newMid = CurvedEdges.Straight(crop).Points;
                var moved = edges.Points.Select((p, i) => new NormalizedPoint(Math.Clamp(p.X + newMid[i].X - oldMid[i].X, 0, 1), Math.Clamp(p.Y + newMid[i].Y - oldMid[i].Y, 0, 1))).ToArray();
                edges = new(moved[0], moved[1], moved[2], moved[3]);
            }
        }
        else edges = (edges ?? CurvedEdges.Straight(crop)).WithPoint(SelectedHandle - 4, point);
        if (!crop.IsValid || (edges is not null && !edges.IsValidFor(crop))) return;
        SetCurrentValue(CropProperty, crop); SetCurrentValue(EdgesProperty, edges);
        if (commit) Commit();
    }
    private void Commit() { CropCommitted?.Invoke(this, Crop); BoundaryCommitted?.Invoke(this, new(Crop, Edges)); }
    private void RestoreDrag() { SetCurrentValue(CropProperty, _beforeDrag ?? CropQuad.FullFrame); SetCurrentValue(EdgesProperty, _beforeEdges); }
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnGotKeyboardFocus(e); InvalidateVisual(); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { base.OnLostKeyboardFocus(e); InvalidateVisual(); }
}
