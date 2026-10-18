using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace EasyScan.Desktop.Controls;

public sealed class BusyIndicator : Control
{
    public static readonly DependencyProperty IsBusyProperty = DependencyProperty.Register(nameof(IsBusy), typeof(bool), typeof(BusyIndicator), new PropertyMetadata(false, (d, _) => ((BusyIndicator)d).Update()));
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(BusyIndicator), new PropertyMetadata("正在加载…"));
    public bool IsBusy { get => (bool)GetValue(IsBusyProperty); set => SetValue(IsBusyProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    private readonly DispatcherTimer _delay = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private RotateTransform? _rotation;
    public BusyIndicator()
    {
        Visibility = Visibility.Collapsed; Focusable = false; IsHitTestVisible = false;
        _delay.Tick += (_, _) => { _delay.Stop(); if (IsBusy && IsVisible && SystemParameters.ClientAreaAnimation) _rotation?.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever }); };
        Loaded += (_, _) => Update(); Unloaded += (_, _) => Stop();
    }
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (GetTemplateChild("PART_Rotor") is FrameworkElement rotor) { _rotation = new(); rotor.RenderTransformOrigin = new(.5, .5); rotor.RenderTransform = _rotation; }
        Update();
    }
    private void Update() { Stop(); Visibility = IsBusy ? Visibility.Visible : Visibility.Collapsed; if (IsBusy) _delay.Start(); }
    private void Stop() { _delay.Stop(); _rotation?.BeginAnimation(RotateTransform.AngleProperty, null); }
}
