using EasyScan.Core;
using EasyScan.Desktop;
using EasyScan.Desktop.ViewModels;
using OpenCvSharp;
using System.IO;
using System.Windows;
using Window = System.Windows.Window;

namespace EasyScan.Tests;

internal static class InteractionRegression
{
    public static async Task RunAsync(string root, Action<Window, string, int, int, int> render, Action<bool, string> check)
    {
        using var vm = new MainViewModel();
        await vm.ImportPathsAsync([Path.Combine(root, "场景回归/弯曲纸张-输入.png"), Path.Combine(root, "场景回归/小证件-输入.png")]);
        await vm.RefreshPreviewAsync();
        var window = new MainWindow(vm);
        var page = vm.Pages[0]; var other = vm.Pages[1];
        vm.SelectedPage = page;
        await vm.RefreshPreviewAsync();
        check(page.HasCurvedEdges && !other.HasCurvedEdges, "each imported page retains its independently detected geometry");
        var otherGeometry = other.Settings;
        page.HighlightRecovery = 75; page.CorrectColorCast = true; page.AutoExposure = false; page.ColorBoost = 15;
        vm.ApplyToAllCommand.Execute(null);
        check(other.Settings.Crop == otherGeometry.Crop && other.Settings.CurvedEdges == otherGeometry.CurvedEdges &&
            other.Settings.HighlightRecovery == .75 && other.Settings.CorrectColorCast && !other.Settings.AutoExposure,
            "batch enhancement copies new lighting controls without copying crop geometry");
        page.Undo();
        vm.ToggleCropCommand.Execute(null); vm.SelectedCropHandle = 4;
        render(window, "界面-弯边裁剪.png", 1280, 760, 96);
        var before = page.Settings;
        vm.FullFrameCommand.Execute(null);
        await vm.AutoDetectCommand.ExecuteAsync(null);
        check(page.HasCurvedEdges && !vm.IsDetecting, "redetection rebuilds curved boundaries and releases its busy state");
        vm.CancelCropCommand.Execute(null);
        check(page.Settings == before, "cancel also restores geometry changed by automatic redetection");
        vm.ToggleCropCommand.Execute(null); vm.FullFrameCommand.Execute(null);
        var detectionTask = vm.AutoDetectCommand.ExecuteAsync(null);
        vm.CancelCropCommand.Execute(null); await detectionTask;
        check(page.Settings == before && !vm.IsDetecting && !vm.IsCropping,
            "cancel during redetection prevents a late result from replacing restored settings");
        vm.ToggleCropCommand.Execute(null); vm.FullFrameCommand.Execute(null); vm.SelectedPage = other;
        check(page.Settings == before && !vm.IsCropping, "switching pages cancels unconfirmed crop edits");
        vm.MoveUpCommand.Execute(null);
        check(vm.Pages[0] == other && !vm.CanMoveUp && vm.CanMoveDown, "queue movement updates boundary command states");
        vm.SelectedPage = page; await vm.RefreshPreviewAsync();
        using var original = await vm.Loader.LoadAsync(page.Source);
        using var master = vm.Processor.ProcessColor(original, page.Settings);
        foreach (var mode in Enum.GetValues<OutputColorMode>())
        {
            var result = await new ExportService(vm.Loader, vm.Processor).ExportAsync([new(page.Source, page.Settings)],
                new(Path.Combine(root, "弯边导出", mode.ToString()), ExportFormat.Png, Color: mode));
            var source = (await vm.Loader.DescribeAsync(result.Files.Single()))[0];
            using var reopened = await vm.Loader.LoadAsync(source);
            using var expected = vm.Processor.ConvertColor(master, mode);
            using var expectedBgr = new Mat();
            if (expected.Channels() == 1) Cv2.CvtColor(expected, expectedBgr, ColorConversionCodes.GRAY2BGR);
            else expected.CopyTo(expectedBgr);
            check(reopened.Size() == master.Size() && Cv2.Norm(reopened, expectedBgr, NormTypes.INF) == 0,
                mode + ": curved full-resolution PNG matches the final processing pipeline exactly");
        }
    }
}
