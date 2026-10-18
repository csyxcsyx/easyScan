using System.Windows;
using System.Windows.Media;

namespace EasyScan.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length >= 2 && e.Args[0] == "--verify-runtime")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = VerifyRuntimeAsync(e.Args[1], e.Args.Skip(2).ToArray());
            return;
        }
        ApplyAccessibilityTheme();
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        if (e.Args.Length > 0) window.ImportStartupFiles(e.Args);
    }

    private async Task VerifyRuntimeAsync(string outputDirectory, string[] samples)
    {
        var success = await Services.RuntimeProbe.RunAsync(outputDirectory, samples);
        Shutdown(success ? 0 : 1);
    }

    public void ApplyAccessibilityTheme()
    {
        if (!SystemParameters.HighContrast) return;
        Resources["SurfaceBrush"] = SystemColors.WindowBrush;
        Resources["ShellBrush"] = SystemColors.ControlBrush;
        Resources["CanvasBrush"] = SystemColors.WindowBrush;
        Resources["TextBrush"] = SystemColors.WindowTextBrush;
        Resources["SecondaryBrush"] = SystemColors.WindowTextBrush;
        Resources["BorderBrush"] = SystemColors.WindowTextBrush;
        Resources["ControlBorderBrush"] = SystemColors.WindowTextBrush;
        Resources["FocusBrush"] = SystemColors.HighlightBrush;
        Resources["SelectionBrush"] = SystemColors.HighlightBrush;
        Resources["SelectionTextBrush"] = SystemColors.HighlightTextBrush;
        Resources["HoverBrush"] = SystemColors.ControlLightBrush;
        Resources["PressedBrush"] = SystemColors.ControlDarkBrush;
        Resources["PrimaryBrush"] = SystemColors.HighlightBrush;
        Resources["OnPrimaryBrush"] = SystemColors.HighlightTextBrush;
        Resources["ButtonBrush"] = SystemColors.ControlBrush;
        Resources["GroupBrush"] = SystemColors.ControlBrush;
    }
}
