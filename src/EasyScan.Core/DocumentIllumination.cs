using OpenCvSharp;

namespace EasyScan.Core;

/// <summary>
/// CPU adaptation of color-aware background (CVPR 2023) and low/high-frequency
/// separation (FSENet, ICCV 2023), not a reproduction of either learned network.
/// All estimation is bounded to a 512-pixel analysis image.
/// </summary>
internal sealed class DocumentIllumination : IDisposable
{
    internal Mat Background { get; }
    internal Mat RelativeTarget { get; }
    private DocumentIllumination(Mat background, Mat relativeTarget) { Background = background; RelativeTarget = relativeTarget; }

    internal static DocumentIllumination Estimate(Mat light, Mat a, Mat b, CancellationToken token)
    {
        var background = PhotoAnalysis.EstimateBackground(light);
        var relative = new Mat(light.Size(), MatType.CV_32FC1, Scalar.All(1));
        try
        {
            token.ThrowIfCancellationRequested();
            var width = light.Width; var height = light.Height;
            var pixels = light.AsRows<byte>(); var bases = background.AsRows<byte>();
            using var lab = new Mat(); Cv2.Merge(new[] { light, a, b }, lab);
            using var color = new Mat(); Cv2.CvtColor(lab, color, ColorConversionCodes.Lab2BGR);
            using var hsv = new Mat(); Cv2.CvtColor(color, hsv, ColorConversionCodes.BGR2HSV);
            var colors = hsv.AsRows<Vec3b>();
            var histogram = new int[256, 256]; var counts = new int[256];
            using var weights = new Mat(light.Size(), MatType.CV_32FC1, Scalar.All(0));
            using var weighted = new Mat(light.Size(), MatType.CV_32FC1, Scalar.All(0));
            var masks = weights.AsRows<float>(); var weightedRows = weighted.AsRows<float>();
            for (var y = 0; y < height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (var x = 0; x < width; x++)
                {
                    var value = pixels[y][x];
                    // A superset of background: reject dark strokes, retain shaded paper.
                    if (value < 24 || bases[y][x] - value > 12) continue;
                    var bin = (colors[y][x].Item0 / 12) * 16 + colors[y][x].Item1 / 32;
                    histogram[bin, value]++; counts[bin]++;
                    masks[y][x] = 1; weightedRows[y][x] = value;
                }
            }
            var total = counts.Sum();
            if (total < light.Total() * .15) return new(background, relative);
            using var sums = new Mat(); using var support = new Mat();
            // Masked low-frequency reconstruction keeps dense print out of the light field.
            Cv2.BoxFilter(weighted, sums, MatType.CV_32FC1, new Size(31, 31));
            Cv2.BoxFilter(weights, support, MatType.CV_32FC1, new Size(31, 31));
            var sumRows = sums.AsRows<float>(); var supportRows = support.AsRows<float>();
            var targets = new double[256]; var global = PhotoAnalysis.Percentile(background, .78);
            for (var bin = 0; bin < counts.Length; bin++)
            {
                if (counts[bin] < Math.Max(24, light.Total() * .01)) { targets[bin] = global; continue; }
                var wanted = counts[bin] * .95; var count = 0;
                for (var v = 0; v < 256; v++)
                { count += histogram[bin, v]; if (count >= wanted) { targets[bin] = v; break; } }
            }
            var references = relative.AsRows<float>();
            for (var y = 0; y < height; y++)
            {
                token.ThrowIfCancellationRequested();
                for (var x = 0; x < width; x++)
                {
                    var bin = (colors[y][x].Item0 / 12) * 16 + colors[y][x].Item1 / 32;
                    var chroma = colors[y][x].Item1;
                    // Reconstruct neutral paper only; never interpolate across printed color areas.
                    if (chroma < 24 && supportRows[y][x] > .45)
                        bases[y][x] = (byte)Math.Clamp(Math.Round(sumRows[y][x] / supportRows[y][x]), 0, 255);
                    // Common paper colors retain their own brightness; sparse ink follows its paper.
                    references[y][x] = (float)(chroma >= 24 ? Math.Clamp(targets[bin] / Math.Max(1, global), .55, 1.10) : 1);
                }
            }
            return new(background, relative);
        }
        catch { background.Dispose(); relative.Dispose(); throw; }
    }
    public void Dispose() { Background.Dispose(); RelativeTarget.Dispose(); }
}
