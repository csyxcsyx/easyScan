using OpenCvSharp;

namespace EasyScan.Core;

public sealed class PhotoEnhancer
{
    public Mat Enhance(Mat image, ScanSettings settings, CancellationToken token = default)
    {
        using var balanced = settings.CorrectColorCast ? BalancePaper(image) : image.Clone();
        using var lab = new Mat(); Cv2.CvtColor(balanced, lab, ColorConversionCodes.BGR2Lab);
        var channels = Cv2.Split(lab);
        try
        {
            using var small = new Mat();
            var scale = Math.Min(1, 512d / Math.Max(image.Width, image.Height));
            Cv2.Resize(channels[0], small, new Size(Math.Max(1, (int)Math.Round(image.Width * scale)), Math.Max(1, (int)Math.Round(image.Height * scale))), interpolation: InterpolationFlags.Area);
            using var smallA = new Mat(); using var smallB = new Mat();
            Cv2.Resize(channels[1], smallA, small.Size(), interpolation: InterpolationFlags.Area);
            Cv2.Resize(channels[2], smallB, small.Size(), interpolation: InterpolationFlags.Area);
            using var illumination = DocumentIllumination.Estimate(small, smallA, smallB, token);
            var background = illumination.Background;
            var median = PhotoAnalysis.Percentile(background, .55);
            var target = PhotoAnalysis.Percentile(background, .78);
            var strength = Math.Clamp(settings.ShadowRemoval, 0, 1);
            if (settings.Preset == EnhancementPreset.WhitePaper) { strength = Math.Max(.65, strength); target = Math.Max(target, 238); }
            if (Math.Min(image.Width, image.Height) >= 32 && (strength > .001 || settings.HighlightRecovery > .001))
                CorrectIllumination(channels[0], background, illumination.RelativeTarget, target, strength, settings.HighlightRecovery, token);
            token.ThrowIfCancellationRequested();
            var gamma = 1d;
            if (settings.AutoExposure)
            {
                var intended = settings.Preset == EnhancementPreset.WhitePaper ? 238 : 208;
                if (median < intended - 10 || median > 242)
                    gamma = Math.Clamp(Math.Log(intended / 255d) / Math.Log(Math.Clamp(median, 25, 252) / 255d), .28, 1.12);
            }
            var contrast = 1 + Math.Clamp(settings.Contrast, -50, 50) / 100;
            var lut = Enumerable.Range(0, 256).Select(i =>
            {
                var mapped = 255 * Math.Pow(i / 255d, gamma);
                var inkProtection = gamma < 1 ? Math.Clamp(i / Math.Max(30, median * .60), 0, 1) : 1;
                var tone = i + (mapped - i) * inkProtection;
                return (byte)Math.Clamp(Math.Round((tone - 128) * contrast + 128 + Math.Clamp(settings.Brightness, -50, 50)), 0, 255);
            }).ToArray();
            using var lookup = new Mat(1, 256, MatType.CV_8UC1); lookup.SetArray(lut);
            Cv2.LUT(channels[0], lookup, channels[0]);
            EnhanceDetails(channels[0], settings, token);
            if (settings.ColorBoost > .001)
            {
                var boost = 1 + Math.Clamp(settings.ColorBoost, 0, 1) * .2;
                channels[1].ConvertTo(channels[1], MatType.CV_8UC1, boost, 128 * (1 - boost));
                channels[2].ConvertTo(channels[2], MatType.CV_8UC1, boost, 128 * (1 - boost));
            }
            Cv2.Merge(channels, lab);
            var output = new Mat(); Cv2.CvtColor(lab, output, ColorConversionCodes.Lab2BGR);
            try { PreserveInkHue(balanced, output, token); }
            catch { output.Dispose(); throw; }
            if (token.IsCancellationRequested) { output.Dispose(); token.ThrowIfCancellationRequested(); }
            return output;
        }
        finally { foreach (var channel in channels) channel.Dispose(); }
    }

    private static void EnhanceDetails(Mat light, ScanSettings settings, CancellationToken token)
    {
        if (Math.Min(light.Width, light.Height) < 5) return;
        var noise = Math.Clamp(settings.NoiseReduction, 0, 1);
        var detail = Math.Clamp(settings.DetailEnhancement, 0, 1);
        if (noise > .001)
        {
            using var filtered = new Mat();
            Cv2.BilateralFilter(light, filtered, 5, 4 + noise * 14, 2);
            Cv2.AddWeighted(light, 1 - noise, filtered, noise, 0, light);
        }
        if (detail <= .001) return;
        using var smooth = new Mat(); using var low = new Mat(); using var high = new Mat();
        Cv2.GaussianBlur(light, smooth, new Size(), Math.Clamp(Math.Max(light.Width, light.Height) / 2400d, .65, 1.4));
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.Erode(light, low, kernel); Cv2.Dilate(light, high, kernel);
        var pixels = light.AsRows<byte>(); var blurred = smooth.AsRows<byte>();
        var minima = low.AsRows<byte>(); var maxima = high.AsRows<byte>();
        var width = light.Width; var height = light.Height;
        for (var y = 0; y < height; y++)
        {
            if (y % 64 == 0) token.ThrowIfCancellationRequested();
            var row = pixels[y]; var blur = blurred[y]; var min = minima[y]; var max = maxima[y];
            for (var x = 0; x < width; x++)
            {
                var delta = row[x] - blur[x];
                if (Math.Abs(delta) < 2 + noise * 3 || max[x] - min[x] < 10) continue;
                var change = Math.Clamp(delta * detail * .85, -12, 12);
                row[x] = (byte)Math.Clamp(Math.Round(row[x] + change), min[x], max[x]);
            }
        }
    }

    private static void PreserveInkHue(Mat original, Mat enhanced, CancellationToken token)
    {
        using var source = new Mat(); using var output = new Mat();
        Cv2.CvtColor(original, source, ColorConversionCodes.BGR2HSV);
        Cv2.CvtColor(enhanced, output, ColorConversionCodes.BGR2HSV);
        var sourceRows = source.AsRows<Vec3b>(); var outputRows = output.AsRows<Vec3b>();
        var width = source.Width; var height = source.Height;
        for (var y = 0; y < height; y++)
        {
            if (y % 64 == 0) token.ThrowIfCancellationRequested();
            var before = sourceRows[y]; var after = outputRows[y];
            for (var x = 0; x < width; x++)
                if (before[x].Item1 > 45)
                {
                    var pixel = after[x]; pixel.Item0 = before[x].Item0;
                    pixel.Item1 = (byte)Math.Max(pixel.Item1, before[x].Item1 * .85);
                    after[x] = pixel;
                }
        }
        Cv2.CvtColor(output, enhanced, ColorConversionCodes.HSV2BGR);
    }

    private static void CorrectIllumination(Mat light, Mat smallBackground, Mat relativeTarget, double target, double shadow, double highlights, CancellationToken token)
    {
        // Work on a small gain field and byte pixels. No full-resolution float fields.
        using var field = new Mat(); smallBackground.ConvertTo(field, MatType.CV_32FC1);
        using var desired = new Mat(); relativeTarget.ConvertTo(desired, MatType.CV_32FC1, target);
        Cv2.Add(field, Scalar.All(1), field);
        using var gain = new Mat(); Cv2.Divide(desired, field, gain);
        // Smooth correction, not the full-resolution text. This retains original high frequencies.
        Cv2.GaussianBlur(gain, gain, new Size(3, 3), .65);
        var width = light.Width; var height = light.Height;
        var pixels = light.AsRows<byte>();
        for (var y = 0; y < height; y += 256)
        {
            token.ThrowIfCancellationRequested();
            var count = Math.Min(256, height - y);
            using var inverse = new Mat(2, 3, MatType.CV_64FC1);
            var sx = gain.Width / (double)width; var sy = gain.Height / (double)height;
            inverse.SetArray(new[] { sx, 0d, .5 * sx - .5, 0d, sy, (y + .5) * sy - .5 });
            using var enlarged = new Mat();
            Cv2.WarpAffine(gain, enlarged, inverse, new Size(width, count),
                InterpolationFlags.Linear | InterpolationFlags.WarpInverseMap, BorderTypes.Replicate);
            var gains = enlarged.AsRows<float>();
            for (var row = 0; row < count; row++)
            {
                var values = pixels[y + row]; var scales = gains[row];
                for (var x = 0; x < width; x++)
                {
                    var raw = Math.Clamp(scales[x], .68f, 2.15f);
                    var weight = raw >= 1 ? shadow : Math.Clamp(highlights, 0, 1);
                    values[x] = (byte)Math.Clamp(Math.Round(values[x] * (1 + (raw - 1) * weight)), 0, 255);
                }
            }
        }
    }

    private static Mat BalancePaper(Mat image)
    {
        using var small = new Mat(); var scale = Math.Min(1, 300d / Math.Max(image.Width, image.Height));
        Cv2.Resize(image, small, new Size(Math.Max(1, (int)Math.Round(image.Width * scale)), Math.Max(1, (int)Math.Round(image.Height * scale))), interpolation: InterpolationFlags.Area);
        using var hsv = new Mat(); Cv2.CvtColor(small, hsv, ColorConversionCodes.BGR2HSV);
        using var paper = new Mat(); Cv2.InRange(hsv, new Scalar(0, 0, 120), new Scalar(180, 85, 255), paper);
        if (Cv2.CountNonZero(paper) < small.Width * small.Height * .10) return image.Clone();
        var mean = Cv2.Mean(small, paper); var average = (mean.Val0 + mean.Val1 + mean.Val2) / 3;
        var channels = Cv2.Split(image);
        try
        {
            for (var i = 0; i < 3; i++) channels[i].ConvertTo(channels[i], MatType.CV_8UC1, Math.Clamp(average / Math.Max(1, mean[i]), .82, 1.22));
            var output = new Mat(); Cv2.Merge(channels, output); return output;
        }
        finally { foreach (var channel in channels) channel.Dispose(); }
    }
}
