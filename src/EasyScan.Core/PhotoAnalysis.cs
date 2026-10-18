using OpenCvSharp;

namespace EasyScan.Core;

public static class PhotoAnalysis
{
    public static PhotoQuality Analyze(Mat image)
    {
        using var small = new Mat();
        var scale = Math.Min(1, 512d / Math.Max(image.Width, image.Height));
        Cv2.Resize(image, small, new Size(Math.Max(1, (int)Math.Round(image.Width * scale)), Math.Max(1, (int)Math.Round(image.Height * scale))), interpolation: InterpolationFlags.Area);
        using var lab = new Mat(); Cv2.CvtColor(small, lab, ColorConversionCodes.BGR2Lab);
        var channels = Cv2.Split(lab);
        try
        {
            using var background = EstimateBackground(channels[0]);
            var median = Percentile(background, .5); var low = Percentile(background, .12); var high = Percentile(background, .88);
            using var broad = new Mat(); Cv2.GaussianBlur(channels[0], broad, new Size(), Math.Max(8, Math.Min(small.Width, small.Height) * .08));
            var light = channels[0].AsRows<byte>(); var field = broad.AsRows<byte>();
            var width = small.Width; var height = small.Height;
            var glare = 0;
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    if (light[y][x] >= 244 && light[y][x] - field[y][x] > 12 && light[y][x] - median > 12) glare++;
            return new(median < 150, median > 235, high - low > 36, glare / (double)(small.Width * small.Height));
        }
        finally { foreach (var channel in channels) channel.Dispose(); }
    }

    internal static Mat EstimateBackground(Mat light)
    {
        var background = new Mat();
        var size = Math.Clamp(Math.Min(light.Width, light.Height) / 12, 9, 41);
        if (size % 2 == 0) size++;
        using var fine = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(size, size));
        using var broadKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(size * 2 + 1, size * 2 + 1));
        Cv2.MorphologyEx(light, fine, MorphTypes.Close, kernel);
        Cv2.MorphologyEx(light, background, MorphTypes.Close, broadKernel);
        // Broad closing fills dense ink and seals; the fine field retains real lighting changes.
        Cv2.AddWeighted(fine, .35, background, .65, 0, background);
        Cv2.GaussianBlur(background, background, new Size(), Math.Max(2, Math.Min(light.Width, light.Height) / 28d));
        return background;
    }

    internal static double Percentile(Mat image, double percentile)
    {
        var histogram = new int[256]; var rows = image.AsRows<byte>();
        var height = image.Height;
        for (var y = 0; y < height; y++)
            foreach (var value in rows[y]) histogram[value]++;
        var wanted = Math.Max(1, (int)(image.Width * image.Height * percentile)); var sum = 0;
        for (var i = 0; i < 256; i++) { sum += histogram[i]; if (sum >= wanted) return i; }
        return 255;
    }
}
