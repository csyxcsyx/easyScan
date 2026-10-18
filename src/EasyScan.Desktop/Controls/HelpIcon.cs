using EasyScan.Desktop.Services;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace EasyScan.Desktop.Controls;

public sealed class HelpIcon : Button
{
    public static readonly DependencyProperty TopicProperty = DependencyProperty.Register(nameof(Topic), typeof(string), typeof(HelpIcon), new PropertyMetadata("", (d, _) => ((HelpIcon)d).SetAccessibleText()));
    public string Topic { get => (string)GetValue(TopicProperty); set => SetValue(TopicProperty, value); }
    public HelpIcon()
    {
        SetResourceReference(StyleProperty, typeof(Button));
        Width = 24; Height = 24; MinHeight = 24; Padding = new(3); Background = Brushes.Transparent;
        var glyph = new TextBlock { Text = "?", FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryBrush");
        var circle = new Border { Width = 14, Height = 14, CornerRadius = new(7), BorderThickness = new(1), Child = glyph };
        circle.SetResourceReference(Border.BorderBrushProperty, "SecondaryBrush"); Content = circle;
        MouseEnter += (_, _) => HelpPopupService.Show(this, false);
        MouseLeave += (_, _) => HelpPopupService.Leave(this);
        Click += (_, _) => HelpPopupService.Show(this, true);
        SetAccessibleText();
    }
    private void SetAccessibleText()
    {
        var help = HelpTopics.Get(Topic);
        System.Windows.Automation.AutomationProperties.SetName(this, help.Title + "说明");
        System.Windows.Automation.AutomationProperties.SetHelpText(this, help.Text);
    }
}

public sealed class HelpLabel : StackPanel
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(HelpLabel), new PropertyMetadata(""));
    public static readonly DependencyProperty TopicProperty = DependencyProperty.Register(nameof(Topic), typeof(string), typeof(HelpLabel), new PropertyMetadata(""));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string Topic { get => (string)GetValue(TopicProperty); set => SetValue(TopicProperty, value); }
    public HelpLabel()
    {
        Orientation = Orientation.Horizontal;
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(Text)) { Source = this });
        var help = new HelpIcon { Margin = new(3, 0, 0, 0) };
        help.SetBinding(HelpIcon.TopicProperty, new Binding(nameof(Topic)) { Source = this });
        Children.Add(label); Children.Add(help);
    }
}
